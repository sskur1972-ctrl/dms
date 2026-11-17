//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 2 - Advisory review rules (compliance assistant). The assistant is advisory only:
 * no rule can approve, block or change a document. Rules are seeded; QA edits wording, pattern,
 * severity and activation. The pattern is validated BEFORE it is stored - a pattern that does not
 * compile would silently never fire.
 */
using System.Text.RegularExpressions;
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Master;
using DMS.Web.CommonUtils;
using Microsoft.AspNetCore.Mvc;

namespace DMS.Web.Controllers
{
    public class ComplianceRuleController : DmsBaseController
    {
        private static readonly string[] Severities = { "INFO", "LOW", "MEDIUM", "HIGH" };

        public ComplianceRuleController(IUnitOfWork unitOfWork) : base(unitOfWork) { }

        public async Task<IActionResult> Index()
        {
            if (!HasPermission(AdminPermissions.ComplianceRule)) return NoPermission();
            return View(await _unitOfWork.ComplianceRule.GetListAsync(true));
        }

        [HttpGet]
        public async Task<IActionResult> Edit(long id)
        {
            if (!HasPermission(AdminPermissions.ComplianceRule)) return NoPermission();
            var model = await _unitOfWork.ComplianceRule.GetByIdAsync(id);
            if (model == null)
            {
                NotifyError("Rule not found.");
                return RedirectToAction(nameof(Index));
            }
            return View(model);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(ComplianceRule model, string? reason)
        {
            if (!HasPermission(AdminPermissions.ComplianceRule)) return NoPermission();
            var existing = await _unitOfWork.ComplianceRule.GetByIdAsync(model.id);
            if (existing == null) return NotFound();

            // code and kind are fixed by the seed - the engine is written for them
            model.rule_code = existing.rule_code;
            model.rule_kind = existing.rule_kind;
            if (!HasPattern(model.rule_kind)) model.pattern = existing.pattern;

            var errors = Validate(model).ToList();
            if (!ModelState.IsValid) errors.Insert(0, FirstModelError());
            if (errors.Count > 0)
            {
                NotifyError(string.Join(" ", errors));
                return View(model);
            }

            var result = await _unitOfWork.ComplianceRule.UpdateAsync(model, reason, CurrentUserId, ClientIp);
            if (!result.Success)
            {
                NotifyError(result.Message);
                return RedirectToAction(nameof(Edit), new { id = model.id });
            }
            NotifySuccess($"Rule {existing.rule_code} has been updated. The assistant remains advisory - no rule can approve, block or change a document.");
            return RedirectToAction(nameof(Index));
        }

        public static bool HasPattern(string? kind) =>
            (kind ?? "").ToUpperInvariant() is "TEXT_MUST_NOT_MATCH" or "REFERENCES_NOT_LINKED" or "TEXT_MUST_CONTAIN_ALL";

        /// <summary>Checks the pattern for the rule kind (regular expression / list of terms).</summary>
        public static IEnumerable<string> Validate(ComplianceRule rule)
        {
            if (!Severities.Contains((rule.severity ?? "").ToUpperInvariant()))
                yield return "Severity must be INFO, LOW, MEDIUM or HIGH.";

            var pattern = rule.pattern?.Trim() ?? "";
            switch ((rule.rule_kind ?? "").ToUpperInvariant())
            {
                case "TEXT_MUST_NOT_MATCH":
                case "REFERENCES_NOT_LINKED":
                    if (pattern.Length == 0)
                    {
                        yield return "A regular expression is required for this rule.";
                        yield break;
                    }
                    string? regexError = null;
                    try
                    {
                        _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    }
                    catch (ArgumentException ex)
                    {
                        regexError = "The pattern is not a valid regular expression: " + ex.Message;
                    }
                    if (regexError != null) yield return regexError;
                    break;

                case "TEXT_MUST_CONTAIN_ALL":
                    var terms = pattern.Split('|');
                    if (pattern.Length == 0 || terms.Any(t => t.Trim().Length == 0))
                        yield return "List the required terms separated by | (no empty terms), e.g. purpose|scope|procedure.";
                    break;
            }
        }
    }
}
