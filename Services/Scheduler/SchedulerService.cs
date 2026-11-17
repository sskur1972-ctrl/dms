//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 5a - the scheduled jobs (ported from Bcl.Dms DmsSchedulerService / Hangfire recurring jobs):
*   SLA_SWEEP        warn before a stage is due, flag and tell at breach, escalate to QA after the grace days (URS-7.5, 7.6)
*   MAKE_EFFECTIVE   documents published with an effective date that has come become effective (URS-7.12)
*   PERIODIC_REVIEW  tell the owner a periodic review is coming due (URS-7.21)
*   EMAIL            send the queued notification e-mails over SMTP (System.Net.Mail)
*   BACKUP           the scheduled backup (backup.schedule.cron, local time) through PgBackupRunner
* The rules live in the database functions; this only runs them on a timer and records each run in scheduler_runs.
*/
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using DMS.DataAccess.Repository.IRepository;
using DMS.Web.Services.Rendition;

namespace DMS.Web.Services.Scheduler
{

    public static class NotificationEmailTemplate
    {
        private const string Green = "#087B68";
        private const RegexOptions CI = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // "Open: /Documents/Details/5" or "Open: https://..."
        private static readonly Regex OpenRx = new(
            @"\s*Open:\s*(?<url>https?://[^\s<>]+|/[A-Za-z][A-Za-z0-9/_\-?=&.]*)", CI);

        // document number such as SOP-QA-26-025 or SPEC-BULK-QA-26-001, optionally followed by "(title)"
        private static readonly Regex DocNumberRx = new(
            @"\b(?<number>[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d{2}-\d{2,})\b(?:\s*\((?<title>[^)]*)\))?",
            RegexOptions.CultureInvariant);

        // older wording: "Document X (title) ..." / "Approval of X (title) ..." where X is not in the usual number format
        private static readonly Regex LegacyDocRx = new(
            @"^\s*(?:Document\s+|Approval\s+of\s+|Authorization\s+of\s+)(?<number>\S+)\s+\((?<title>.*?)\)",
            CI | RegexOptions.Singleline);

        private static readonly Regex RevisionRx = new(
            @"Temporary revision:\s*(?<value>[^.\r\n]+)", CI);

        private const string DatePattern = @"\d{1,2}-[A-Za-z]{3}-\d{4}|\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{4}";

        private static readonly Regex DateRx = new(
            @"(?<lead>your action is due by|action is due by|due by|due on|due date:?|was due on|breached on|"
            + @"due for periodic review on|periodic review due on|effective on|effective from|effective date:?)\s*"
            + @"(?<value>" + DatePattern + ")", CI);

        private static readonly Regex OwnerRx = new(
            @"(?:circulated for review by|submitted by|created by|owned by|authored by|initiated by|requested by|"
            + @"raised by|rejected by|returned by|approved by|escalated by)\s+(?<owner>[^\r\n]+?)(?=\.(?:\s|$)|[\r\n]|$)",
            CI);

        // first match wins, so the more specific wordings come first
        private static readonly (Regex Pattern, string Label)[] Actions =
        {
            (new Regex(@"escalat",                                   CI), "Escalated - Action Required"),
            (new Regex(@"breach|overdue|exceeded|past due",          CI), "Overdue - Action Required"),
            (new Regex(@"periodic review",                           CI), "Periodic Review Due"),
            (new Regex(@"reject|returned|sent back|send back",       CI), "Returned for Rework"),
            (new Regex(@"now effective|became effective|made effective|has become effective", CI), "Document Effective"),
            (new Regex(@"has been approved|was approved|is approved",CI), "Approved"),
            (new Regex(@"circulated for review",                     CI), "Review Document"),
            (new Regex(@"authori[sz]ation",                          CI), "Authorization Required"),
            (new Regex(@"approval",                                  CI), "Approval Required"),
            (new Regex(@"reminder|due soon|about to be due|will be due|approaching", CI), "Reminder - Action Due Soon"),
        };

        private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

        public static string Build(string heading, string? bodyText, string footerNote, string? baseUrl)
        {
            var originalBody = bodyText ?? string.Empty;
            var body = originalBody;

            //// ---- the "Open:" link becomes the button ----
            //var openMatch = OpenRx.Match(body);
            //var documentUrl = openMatch.Success
            //    ? openMatch.Groups["url"].Value.TrimEnd('.', ',', ';', ')')
            //    : string.Empty;

            //if (documentUrl.StartsWith("/") && !string.IsNullOrWhiteSpace(baseUrl))
            //    documentUrl = baseUrl.TrimEnd('/') + documentUrl;

            //if (openMatch.Success)
            //    body = body.Remove(openMatch.Index, openMatch.Length).Trim();

            // ---- Remove the "Open: /Documents/Details/..." text from every email ----
            body = OpenRx.Replace(body, string.Empty).Trim();

            // ---- document number / title ----
            var docMatch = DocNumberRx.Match(body);
            if (!docMatch.Success) docMatch = LegacyDocRx.Match(body);

            var documentNumber = docMatch.Success ? docMatch.Groups["number"].Value.Trim() : string.Empty;
            var documentTitle = docMatch.Success ? docMatch.Groups["title"].Value.Trim() : string.Empty;

            // ---- other fields ----
            var revision = RevisionRx.Match(body) is { Success: true } rm ? rm.Groups["value"].Value.Trim() : string.Empty;

            var dueMatch = DateRx.Match(body);
            var dateValue = dueMatch.Success ? dueMatch.Groups["value"].Value.Trim() : string.Empty;
            var dateLabel = dueMatch.Success && dueMatch.Groups["lead"].Value.StartsWith("effective", StringComparison.OrdinalIgnoreCase)
                ? "Effective Date"
                : "Due Date";

            var owner = OwnerRx.Match(body) is { Success: true } om ? om.Groups["owner"].Value.Trim() : string.Empty;

            var action = string.Empty;
            foreach (var (pattern, label) in Actions)
                if (pattern.IsMatch(body)) { action = label; break; }

            // when the wording is not recognised, show the message itself so no information is lost
            var showMessage = string.IsNullOrEmpty(documentNumber) || string.IsNullOrEmpty(action);
            if (string.IsNullOrEmpty(action)) action = "Action Required";

            // ---- rows ----
            var rows = new StringBuilder();

            void AddRow(string label, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                rows.Append($@"
            <p style='margin:0 0 20px;
                      font-size:16px;
                      line-height:1.5;
                      color:#404040;'>
                <strong>{E(label)}:</strong> {E(value)}
            </p>");
            }

            AddRow("Document Number", documentNumber);
            AddRow("Document Title", documentTitle);
            AddRow("Temporary Revision", revision);
            AddRow("Due Action", action);
            AddRow(dateLabel, dateValue);
            AddRow("Owner / Circulated By", owner);

            if (showMessage)
            {
                var formatted = E(body)
                    .Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "<br/>");
                if (!string.IsNullOrWhiteSpace(formatted))
                    rows.Append($@"
            <div style='margin:0 0 20px;
                        font-family:Arial,Helvetica,sans-serif;
                        font-size:16px;
                        line-height:1.6;
                        color:#404040;
                        overflow-wrap:break-word;'>
                {formatted}
            </div>");
            }

            // ---- button ----
            //var buttonHtml = string.Empty;
            //if (!string.IsNullOrWhiteSpace(documentUrl))
            //{
            //    buttonHtml = $@"
            //<table role='presentation'
            //       align='center'
            //       cellpadding='0'
            //       cellspacing='0'
            //       style='margin:30px auto 0;'>
            //    <tr>
            //        <td align='center'
            //            bgcolor='{Green}'
            //            style='border-radius:5px;'>
            //            <a href='{E(documentUrl)}'
            //               style='display:inline-block;
            //                      padding:15px 32px;
            //                      color:#ffffff;
            //                      font-family:Arial,Helvetica,sans-serif;
            //                      font-size:16px;
            //                      font-weight:bold;
            //                      text-decoration:none;'>


            //            </a>
            //        </td>
            //    </tr>
            //</table>";
            //}


            var intro = string.IsNullOrEmpty(documentNumber)
                ? "You have received a notification from the Document Management System:"
                : "Please find the document details below:";

            return $@"
<!DOCTYPE html>
<html>
<body style='margin:0;padding:0;background:#ffffff;'>
    <table role='presentation'
           width='100%'
           cellpadding='0'
           cellspacing='0'
           style='background:#ffffff;'>
        <tr>
            <td align='center' style='padding:24px 12px;'>
                <table role='presentation'
                       width='680'
                       cellpadding='0'
                       cellspacing='0'
                       style='width:100%;
                              max-width:900px;
                              background:#ffffff;
                              font-family:Arial,Helvetica,sans-serif;'>
                    <tr>
                        <td align='center'
                            style='background:{Green};
                                   color:#ffffff;
                                   padding:24px 16px;
                                   font-size:30px;
                                   font-weight:bold;'>
                            {E(heading)}
                        </td>
                    </tr>
 
                    <tr>
                        <td style='padding:44px 24px 32px;
                                   color:#404040;
                                   font-family:Arial,Helvetica,sans-serif;
                                   font-size:16px;
                                   line-height:1.6;'>
 
                            <p style='margin:0 0 24px;'>
                                Hello,
                            </p>
 
                            <p style='margin:0 0 24px;
                                      font-size:16px;
                                      line-height:1.6;
                                      color:#404040;'>
                                {E(intro)}
                            </p>
 
                            {rows}
 
                            
 
                            <p style='margin:30px 0 0;
                                      font-size:16px;
                                      line-height:1.6;
                                      color:#404040;'>
                                Regards,<br/>
                                <strong>DMS</strong>
                            </p>
 
                        </td>
                    </tr>
 
                    <tr>
                        <td align='center'
                            style='background:#f0f0f0;
                                   color:#777777;
                                   font-family:Arial,Helvetica,sans-serif;
                                   font-size:12px;
                                   line-height:1.7;
                                   padding:20px 24px;'>
                            {E(footerNote)}<br/>
                            DMS &copy; {DateTime.Now.Year}
                            Your Company Pvt. Ltd.
                        </td>
                    </tr>
 
                </table>
            </td>
        </tr>
    </table>
</body>
</html>";
        }
    }










    public sealed record SchedulerJobResult(string Job, string Status, int Items, string? Message);

    public sealed class SchedulerJobs
    {
        public static readonly string[] JobCodes = { "SLA_SWEEP", "MAKE_EFFECTIVE", "PERIODIC_REVIEW", "EMAIL", "BACKUP" };

        private readonly IUnitOfWork _unitOfWork;
        private readonly IConfigService _config;
        private readonly PgBackupRunner _backup;
        private readonly ILogger<SchedulerJobs> _logger;

        public SchedulerJobs(IUnitOfWork unitOfWork, IConfigService config, PgBackupRunner backup, ILogger<SchedulerJobs> logger)
        {
            _unitOfWork = unitOfWork; _config = config; _backup = backup; _logger = logger;
        }

        /// <summary>Runs every job (or one) and records each in scheduler_runs. A failing job does not stop the others.</summary>
        public async Task<List<SchedulerJobResult>> RunAsync(string? onlyJob, CancellationToken ct)
        {
            var results = new List<SchedulerJobResult>();
            foreach (var job in JobCodes.Where(j => string.IsNullOrEmpty(onlyJob) || string.Equals(j, onlyJob, StringComparison.OrdinalIgnoreCase)))
            {
                ct.ThrowIfCancellationRequested();
                var started = DateTime.UtcNow;
                SchedulerJobResult result;
                try
                {
                    result = job switch
                    {
                        "SLA_SWEEP" => new(job, "OK", await _unitOfWork.Scheduler.SlaSweepAsync(), null),
                        "MAKE_EFFECTIVE" => await MakeEffectiveAsync(),
                        "PERIODIC_REVIEW" => new(job, "OK", await _unitOfWork.Scheduler.PeriodicReviewAsync(), null),
                        "BACKUP" => await BackupIfDueAsync(started, ct),
                        _ => await SendEmailsAsync(ct)
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Scheduler job {Job} failed", job);
                    result = new(job, "FAILED", 0, ex.Message);
                }
                results.Add(result);
                // an idle pass (nothing done, nothing wrong) is not worth a row every few minutes
                if (result.Items > 0 || result.Status != "OK" || !string.IsNullOrEmpty(onlyJob))
                    await _unitOfWork.Scheduler.LogRunAsync(job, started, result.Status, result.Items, result.Message);
            }
            return results;
        }

        private async Task<SchedulerJobResult> MakeEffectiveAsync()
        {
            var outcomes = await _unitOfWork.Scheduler.MakeEffectiveAsync();
            if (outcomes.Count == 0) return new("MAKE_EFFECTIVE", "OK", 0, null);
            var done = outcomes.Count(o => o.outcome == "EFFECTIVE");
            if (done > 0) RenditionWorker.Nudge();   // the EFFECTIVE controlled copy
            var text = string.Join("; ", outcomes.Select(o => $"{o.document_number}: {o.outcome}"));
            foreach (var o in outcomes.Where(o => o.outcome != "EFFECTIVE"))
                _logger.LogWarning("Automatic effective date: {Doc} {Outcome}", o.document_number, o.outcome);
            return new("MAKE_EFFECTIVE", done == outcomes.Count ? "OK" : "PARTIAL", done, text);
        }

        private async Task<SchedulerJobResult> SendEmailsAsync(CancellationToken ct)
     {
            if (!await _config.GetBoolAsync(ConfigKeys.EmailEnabled, false, ct))
                return new("EMAIL", "OK", 0, null);   // e-mail switched off: the queue waits, in-app notifications still work

            var host = await _config.GetStringAsync(ConfigKeys.SmtpHost, null, ct);
            if (string.IsNullOrWhiteSpace(host))
                return new("EMAIL", "FAILED", 0, "notification.email.enabled is true but notification.smtp.host is empty.");

            var batch = Math.Clamp(await _config.GetIntAsync(ConfigKeys.EmailBatchSize, 50, ct), 1, 500);
            var pending = await _unitOfWork.Scheduler.GetPendingEmailsAsync(batch);
            if (pending.Count == 0)
                return new("EMAIL", "OK", 0, null);

            var from = await _config.GetStringAsync(ConfigKeys.EmailFrom, "dms-noreply@localhost", ct);
            var fromName = await _config.GetStringAsync(ConfigKeys.EmailFromName, "DMS", ct);
            var baseUrl = (await _config.GetStringAsync(ConfigKeys.EmailBaseUrl, null, ct))?.TrimEnd('/');

            if (string.IsNullOrWhiteSpace(baseUrl))
                _logger.LogWarning("notification.email.baseUrl is empty: the 'View Document' links in e-mails will be relative and will not open.");

            using var client = new SmtpClient(host, await _config.GetIntAsync(ConfigKeys.SmtpPort, 25, ct))
            {
                EnableSsl = await _config.GetBoolAsync(ConfigKeys.SmtpEnableSsl, false, ct),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 10_000
            };

            var user = await _config.GetStringAsync(ConfigKeys.SmtpUser, null, ct);
            if (!string.IsNullOrWhiteSpace(user))
                client.Credentials = new NetworkCredential(user, await _config.GetStringAsync(ConfigKeys.SmtpPassword, "", ct));

            int sent = 0, failed = 0;
            string? lastError = null;

            foreach (var mail in pending)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var message = new MailMessage(new MailAddress(from!, fromName), new MailAddress(mail.recipient_email!))
                    {
                        Subject = (mail.subject ?? "DMS notification").Replace('\r', ' ').Replace('\n', ' '),
                        // ONE template for every notification (approval, review, SLA, periodic review ...)
                        Body = NotificationEmailTemplate.Build(
                            "Document Management System",
                            mail.body ?? string.Empty,
                            "This message was sent by the Document Management System. Do not reply.",
                            baseUrl),
                        IsBodyHtml = true,
                        BodyEncoding = System.Text.Encoding.UTF8,
                        SubjectEncoding = System.Text.Encoding.UTF8
                    };

                    await client.SendMailAsync(message, ct);
                    await _unitOfWork.Scheduler.MarkEmailAsync(mail.id, true, null);
                    sent++;
                }
                catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException or IOException)
                {
                    failed++;
                    lastError = ex.Message;
                    await _unitOfWork.Scheduler.MarkEmailAsync(mail.id, false, ex.Message);
                    _logger.LogWarning("E-mail notification {Id} to {To} not sent: {Error}", mail.id, mail.recipient_email, ex.Message);
                    if (ex is SmtpException { StatusCode: SmtpStatusCode.GeneralFailure }) break;   // server unreachable: try the rest next pass
                }
            }

            return new("EMAIL", failed == 0 ? "OK" : sent == 0 ? "FAILED" : "PARTIAL", sent,
                       failed == 0 ? $"{sent} sent" : $"{sent} sent, {failed} not sent: {lastError}");
        }

        /// <summary>The backup runs when a minute matching backup.schedule.cron (local time) has passed since the last BACKUP run.</summary>
        private async Task<SchedulerJobResult> BackupIfDueAsync(DateTime nowUtc, CancellationToken ct)
        {
            var cron = await _config.GetStringAsync("backup.schedule.cron", null, ct);
            if (string.IsNullOrWhiteSpace(cron) || cron.Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
                return new("BACKUP", "OK", 0, null);
            if (!CronSchedule.TryParse(cron, out var schedule))
                return new("BACKUP", "FAILED", 0, $"backup.schedule.cron '{cron}' is not a valid 5-field cron expression.");
            var tz = await _config.GetStringAsync(ConfigKeys.TimeZoneId, "Asia/Kolkata", ct);
            var interval = Math.Clamp(await _config.GetIntAsync(ConfigKeys.SchedulerIntervalMinutes, 5, ct), 1, 1440);
            var last = await _unitOfWork.Scheduler.GetLastRunAsync("BACKUP") ?? nowUtc.AddMinutes(-interval);
            var from = RenditionProcessor.ToLocal(last > nowUtc.AddDays(-1) ? last : nowUtc.AddDays(-1), tz);
            var to = RenditionProcessor.ToLocal(nowUtc, tz);
            if (!schedule.AnyMatchBetween(from, to)) return new("BACKUP", "OK", 0, null);

            var type = (await _config.GetStringAsync("backup.schedule.type", "COMBINED", ct))!.ToUpperInvariant();
            var result = await _backup.RunAsync(type, "SCHEDULED", null, "scheduler", ct);
            return new("BACKUP", result.Success ? "OK" : "FAILED", 1, result.Success ? $"{type} backup done" : result.Message);
        }

        // "Open: /Documents/Details/5" -> "Open: https://dms.company.local/Documents/Details/5"
        // (no longer used by the e-mail sender - NotificationEmailTemplate does this itself - kept in case other code calls it)
        internal static string AbsoluteLinks(string body, string? baseUrl)
            => string.IsNullOrEmpty(baseUrl) ? body : Regex.Replace(body, @"(?<=^|\s)(/[A-Za-z][A-Za-z0-9/_\-?=&.]*)", m => baseUrl + m.Value);
    }

    /// <summary>Minimal 5-field cron (minute hour day-of-month month day-of-week): *, n, a-b, lists and /step.</summary>
    public sealed class CronSchedule
    {
        private readonly bool[][] _fields;
        private readonly bool _domAny, _dowAny;
        private CronSchedule(bool[][] fields, bool domAny, bool dowAny) { _fields = fields; _domAny = domAny; _dowAny = dowAny; }

        public static bool TryParse(string expression, out CronSchedule schedule)
        {
            schedule = null!;
            var parts = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5) return false;
            var limits = new (int Min, int Max)[] { (0, 59), (0, 23), (1, 31), (1, 12), (0, 7) };
            var fields = new bool[5][];
            for (var i = 0; i < 5; i++)
            {
                fields[i] = new bool[limits[i].Max + 1];
                foreach (var item in parts[i].Split(','))
                {
                    var step = 1; var range = item;
                    var slash = item.IndexOf('/');
                    if (slash >= 0)
                    {
                        if (!int.TryParse(item[(slash + 1)..], out step) || step < 1) return false;
                        range = item[..slash];
                    }
                    int lo, hi;
                    if (range == "*") { lo = limits[i].Min; hi = limits[i].Max; }
                    else if (range.Contains('-'))
                    {
                        var ab = range.Split('-');
                        if (ab.Length != 2 || !int.TryParse(ab[0], out lo) || !int.TryParse(ab[1], out hi)) return false;
                    }
                    else
                    {
                        if (!int.TryParse(range, out lo)) return false;
                        hi = slash >= 0 ? limits[i].Max : lo;
                    }
                    if (lo < limits[i].Min || hi > limits[i].Max || lo > hi) return false;
                    for (var v = lo; v <= hi; v += step) fields[i][v] = true;
                }
            }
            if (fields[4][7]) fields[4][0] = true;   // 7 = Sunday
            schedule = new CronSchedule(fields, parts[2] == "*", parts[4] == "*");
            return true;
        }

        public bool Matches(DateTime t)
        {
            if (!_fields[0][t.Minute] || !_fields[1][t.Hour] || !_fields[3][t.Month]) return false;
            bool dom = _fields[2][t.Day], dow = _fields[4][(int)t.DayOfWeek];
            // standard cron: when both day fields are restricted, either may match
            return _domAny || _dowAny ? dom && dow : dom || dow;
        }

        /// <summary>True when a minute in (from, to] matches.</summary>
        public bool AnyMatchBetween(DateTime from, DateTime to)
        {
            var t = new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0).AddMinutes(1);
            for (; t <= to; t = t.AddMinutes(1)) if (Matches(t)) return true;
            return false;
        }
    }

    /// <summary>Runs <see cref="SchedulerJobs"/> every scheduler.intervalMinutes inside the web application.</summary>
    public sealed class SchedulerWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<SchedulerWorker> _logger;

        public SchedulerWorker(IServiceScopeFactory scopes, ILogger<SchedulerWorker> logger) { _scopes = scopes; _logger = logger; }

        public static DateTime? LastPassUtc { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
            while (!stoppingToken.IsCancellationRequested)
            {
                var wait = TimeSpan.FromMinutes(5);
                try
                {
                    using var scope = _scopes.CreateScope();
                    var config = scope.ServiceProvider.GetRequiredService<IConfigService>();
                    wait = TimeSpan.FromMinutes(Math.Clamp(await config.GetIntAsync(ConfigKeys.SchedulerIntervalMinutes, 5, stoppingToken), 1, 1440));
                    if (await config.GetBoolAsync(ConfigKeys.SchedulerEnabled, true, stoppingToken))
                    {
                        await scope.ServiceProvider.GetRequiredService<SchedulerJobs>().RunAsync(null, stoppingToken);
                        LastPassUtc = DateTime.UtcNow;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "The scheduler could not run; retrying later.");
                }
                try { await Task.Delay(wait, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }


    }
}
