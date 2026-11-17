//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* WOPI host endpoints called by the Office editor (Collabora Online) - ported from Bcl.Dms WopiController.
 * Anonymous by cookie: every call is authorised by the signed access_token instead (see WopiService).
 *   GET  /wopi/files/{versionId}            CheckFileInfo
 *   GET  /wopi/files/{versionId}/contents   GetFile
 *   POST /wopi/files/{versionId}/contents   PutFile
 *   POST /wopi/files/{versionId}            LOCK / REFRESH_LOCK / UNLOCK / GET_LOCK (X-WOPI-Override)
 */
using DMS.Web.Services.Office;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace DMS.Web.Controllers
{
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    [Route("wopi")]
    [ApiController]
    [WopiRawBody]
    public sealed class WopiController : ControllerBase
    {
        private const string LockHeader = "X-WOPI-Lock";
        private const string OverrideHeader = "X-WOPI-Override";
        private const string OldLockHeader = "X-WOPI-OldLock";
        private const string LockFailureReasonHeader = "X-WOPI-LockFailureReason";

        private readonly WopiService _wopi;
        private readonly ILogger<WopiController> _logger;

        public WopiController(WopiService wopi, ILogger<WopiController> logger)
        {
            _wopi = wopi;
            _logger = logger;
        }

        private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();
        [HttpGet("attachments/{id:long}")]
        public async Task<IActionResult> CheckAttachment(long id,[FromQuery(Name="access_token")]string? token){
            var info=await _wopi.CheckAttachmentAsync(id,token);return info==null?Unauthorized():new JsonResult(info);
        }
        [HttpGet("attachments/{id:long}/contents")]
        public async Task<IActionResult> AttachmentContent(long id,[FromQuery(Name="access_token")]string? token){
            var stream=await _wopi.GetAttachmentAsync(id,token);if(stream==null)return Unauthorized();
            Response.Headers.CacheControl="no-store";return File(stream,"application/octet-stream");
        }

        [HttpGet("files/{id:long}")]
        public async Task<IActionResult> CheckFileInfo(long id, [FromQuery(Name = "access_token")] string? accessToken)
        {
            var (info, status) = await _wopi.CheckFileInfoAsync(id, accessToken);
            if (info == null) return StatusCode(status);
            Response.Headers["X-WOPI-HostEndpoint"] = "DMS";
            return new JsonResult(info);
        }

        [HttpGet("files/{id:long}/contents")]
        public async Task<IActionResult> GetFile(long id, [FromQuery(Name = "access_token")] string? accessToken)
        {
            var (content, status) = await _wopi.GetFileAsync(id, accessToken);
            if (content == null) return StatusCode(status);
            Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            Response.Headers.Pragma = "no-cache";
            return File(content, "application/octet-stream");
        }

        [HttpPost("files/{id:long}/contents")]
        [RequestSizeLimit(209_715_200)]
        public async Task<IActionResult> PutFile(long id, [FromQuery(Name = "access_token")] string? accessToken)
        {
            var result = await _wopi.PutFileAsync(id, accessToken, Request.Headers[LockHeader].FirstOrDefault(), Request.Body, Ip, HttpContext.RequestAborted);
            if (result.Saved)
            {
                if (!string.IsNullOrEmpty(result.Version)) Response.Headers["X-WOPI-ItemVersion"] = result.Version;
                return Ok();
            }
            if (result.Failure == WopiFailure.Conflict) Response.Headers[LockHeader] = result.CurrentLock ?? "";
            if (!string.IsNullOrEmpty(result.Message)) Response.Headers[LockFailureReasonHeader] = Sanitise(result.Message);
            _logger.LogWarning("WOPI PutFile refused for revision {VersionId}: {Failure} {Message}", id, result.Failure, result.Message);
            return result.Failure switch
            {
                WopiFailure.Unauthorized => Unauthorized(),
                WopiFailure.NotFound => NotFound(),
                WopiFailure.Conflict => Conflict(),
                WopiFailure.TooLarge => StatusCode(StatusCodes.Status413PayloadTooLarge),
                WopiFailure.NotImplemented => StatusCode(StatusCodes.Status501NotImplemented),
                _ => StatusCode(StatusCodes.Status500InternalServerError)
            };
        }

        [HttpPost("files/{id:long}")]
        public async Task<IActionResult> Operation(long id, [FromQuery(Name = "access_token")] string? accessToken)
        {
            var operation = (Request.Headers[OverrideHeader].FirstOrDefault() ?? string.Empty).ToUpperInvariant();
            var lockValue = Request.Headers[LockHeader].FirstOrDefault() ?? string.Empty;
            switch (operation)
            {
                case "LOCK":
                {
                    var oldLock = Request.Headers[OldLockHeader].FirstOrDefault();
                    if (!string.IsNullOrEmpty(oldLock)) await _wopi.UnlockAsync(id, accessToken, oldLock);
                    return LockOutcome(await _wopi.LockAsync(id, accessToken, lockValue, Ip));
                }
                case "REFRESH_LOCK":
                    return LockOutcome(await _wopi.RefreshLockAsync(id, accessToken, lockValue));
                case "UNLOCK":
                    return LockOutcome(await _wopi.UnlockAsync(id, accessToken, lockValue));
                case "GET_LOCK":
                {
                    var held = await _wopi.GetLockAsync(id, accessToken);
                    if (!held.Granted) return Unauthorized();
                    Response.Headers[LockHeader] = held.CurrentLock ?? string.Empty;
                    return Ok();
                }
                default:
                    // PUT_RELATIVE, RENAME_FILE, DELETE: a controlled record is not renamed, copied or deleted from an editor
                    _logger.LogInformation("The Office editor asked for {Operation} on revision {VersionId}; this host does not offer it.", operation, id);
                    return StatusCode(StatusCodes.Status501NotImplemented);
            }
        }

        private IActionResult LockOutcome(WopiLockResultDto result)
        {
            if (result.Granted)
            {
                if (!string.IsNullOrEmpty(result.CurrentLock)) Response.Headers[LockHeader] = result.CurrentLock;
                return Ok();
            }
            Response.Headers[LockHeader] = result.CurrentLock ?? string.Empty;
            if (!string.IsNullOrEmpty(result.HeldByUserName))
                Response.Headers[LockFailureReasonHeader] = Sanitise($"{result.HeldByUserName} has this revision open in the editor.");
            else if (!string.IsNullOrEmpty(result.Message))
                Response.Headers[LockFailureReasonHeader] = Sanitise(result.Message);
            return Conflict();
        }

        private static string Sanitise(string value)
        {
            var trimmed = value.Length > 200 ? value[..200] : value;
            var builder = new System.Text.StringBuilder(trimmed.Length);
            foreach (var c in trimmed)
            {
                if (c is '\r' or '\n' or '\0') { builder.Append(' '); continue; }
                builder.Append(c < 32 || c > 126 ? '?' : c);
            }
            return builder.ToString();
        }
    }

    /// <summary>
    /// The WOPI body is the raw file. Stop MVC from trying to read it as a form, which fails
    /// (and would buffer the whole file) when a client sends it as form-encoded.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class WopiRawBodyAttribute : Attribute, IResourceFilter
    {
        public void OnResourceExecuting(ResourceExecutingContext context)
        {
            context.ValueProviderFactories.RemoveType<FormValueProviderFactory>();
            context.ValueProviderFactories.RemoveType<JQueryFormValueProviderFactory>();
            context.ValueProviderFactories.RemoveType<FormFileValueProviderFactory>();
        }
        public void OnResourceExecuted(ResourceExecutedContext context) { }
    }
}
