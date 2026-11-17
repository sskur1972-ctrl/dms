//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using Microsoft.Extensions.Logging;

using DMS.Web.Services;

namespace DMS.Web.Services.Office;

/// <summary>
/// Checks a WOPI configuration before anybody tries to open a document with it.
///
/// <para><b>The problem this solves</b></para>
/// A WOPI integration has two directions and both have to work. The browser
/// loads the editor from the editor server; the editor server then calls back
/// into THIS application for the file, the locks and the save. When the second
/// direction is broken the symptom is a grey frame and no message anywhere -
/// not in the browser console, not in this application's log, because the call
/// never arrived.
///
/// <para>
/// Two configuration mistakes account for almost all of it. The first is a
/// <c>publicBaseUrl</c> the editor cannot resolve: <c>localhost</c> is the
/// classic, because inside a container <c>localhost</c> is the container. The
/// second is the editor's own host allow-list - Collabora's
/// <c>aliasgroup1</c> - not matching this application's public URL, which
/// Collabora reports as "unauthorized WOPI host" and nothing else does.
/// </para>
///
/// <para><b>What it deliberately does NOT claim</b></para>
/// It checks from THIS SERVER'S network position. It cannot see what the editor
/// can reach, and it cannot read the editor's allow-list. Every check says
/// which side it speaks for, and the two it cannot make are named in the report
/// rather than left as a false pass - a green tick that meant nothing would be
/// worse than no tick at all.
/// </summary>
public sealed class WopiPreflightService : IWopiPreflightService
{
    private readonly IConfigService _config;
    private readonly IWopiDiscoveryClient _discovery;
    private readonly IHttpClientFactory _httpClients;
    private readonly ILogger<WopiPreflightService> _logger;

    public WopiPreflightService(IConfigService config, IWopiDiscoveryClient discovery,
        IHttpClientFactory httpClients, ILogger<WopiPreflightService> logger)
    {
        _config = config;
        _discovery = discovery;
        _httpClients = httpClients;
        _logger = logger;
    }

    public async Task<WopiPreflightReportDto> RunAsync(CancellationToken ct = default)
    {
        var checks = new List<WopiPreflightCheckDto>();

        var enabled = await _config.GetBoolAsync(ConfigKeys.WopiEnabled, false, ct).ConfigureAwait(false);
        var editor = await _config.GetStringAsync(ConfigKeys.WopiEditor, "COLLABORA", ct).ConfigureAwait(false);
        var discoveryUrl = await _config.GetStringAsync(ConfigKeys.WopiDiscoveryUrl, string.Empty, ct)
            .ConfigureAwait(false);
        var publicBase = (await _config.GetStringAsync(ConfigKeys.WopiPublicBaseUrl, string.Empty, ct)
            .ConfigureAwait(false) ?? string.Empty).TrimEnd('/');
        var insecureTls = await _config.GetBoolAsync(ConfigKeys.WopiAllowInsecureTls, false, ct)
            .ConfigureAwait(false);
        var tokenMinutes = await _config.GetIntAsync(ConfigKeys.WopiTokenLifetimeMinutes, 600, ct)
            .ConfigureAwait(false);
        var lockMinutes = await _config.GetIntAsync(ConfigKeys.WopiLockMinutes, 30, ct).ConfigureAwait(false);

        var report = new WopiPreflightReportDto
        {
            Enabled = enabled,
            EditorName = editor,
            DiscoveryUrl = discoveryUrl,
            PublicBaseUrl = publicBase
        };

        // --- 1. the switch --------------------------------------------------
        checks.Add(enabled
            ? Pass("Office editor", $"Switched on, configured as {editor}.")
            : Warn("Office editor", "Switched off, so no document can be opened in it.",
                $"Set {ConfigKeys.WopiEnabled} to true once the checks below pass. " +
                "Leaving it off outside a trial is correct."));

        // --- 2. publicBaseUrl -----------------------------------------------
        checks.Add(WopiPreflightRules.CheckPublicBaseUrl(publicBase));

        // --- 3. discovery ---------------------------------------------------
        if (string.IsNullOrWhiteSpace(discoveryUrl))
        {
            checks.Add(Fail("Editor discovery", "No discovery URL is configured.",
                $"Set {ConfigKeys.WopiDiscoveryUrl} to the editor's discovery endpoint - " +
                "https://<editor-host>:9980/hosting/discovery for Collabora Online."));

            checks.Add(Skipped("Editable file types", "Discovery was not read."));
        }
        else
        {
            var probe = await _discovery.ProbeAsync(ct).ConfigureAwait(false);

            if (!probe.Reachable)
            {
                checks.Add(Fail("Editor discovery", $"Could not read {discoveryUrl}: {probe.Detail}",
                    insecureTls
                        ? "This server cannot reach the editor. Check the host name, the port, and any " +
                          "firewall between them."
                        : "This server cannot reach the editor. If it is using a self-signed certificate, " +
                          $"set {ConfigKeys.WopiAllowInsecureTls} to true for the trial only - it logs a " +
                          "warning on every use."));

                checks.Add(Skipped("Editable file types", "Discovery could not be read."));
            }
            else
            {
                checks.Add(Pass("Editor discovery", $"Read {discoveryUrl}."));

                report.DocxEditUrl = await _discovery.GetActionUrlAsync("docx", "edit", false, ct)
                    .ConfigureAwait(false);
                report.XlsxEditUrl = await _discovery.GetActionUrlAsync("xlsx", "edit", false, ct)
                    .ConfigureAwait(false);

                checks.Add(report.DocxEditUrl is { Length: > 0 }
                    ? Pass("Editable file types",
                        report.XlsxEditUrl is { Length: > 0 }
                            ? "The editor offers an edit action for .docx and .xlsx."
                            : "The editor offers an edit action for .docx. It offers none for .xlsx, so " +
                              "spreadsheets will not open in it.")
                    : Fail("Editable file types",
                        "Discovery was read but declares no edit action for .docx.",
                        "The endpoint answered but is not a WOPI client's discovery document, or the engine " +
                        "is a viewer-only build. Open the URL in a browser: you should see " +
                        "<wopi-discovery> with <action name=\"edit\" ext=\"docx\" urlsrc=\"...\">."));
            }
        }

        // --- 4. can this application be reached at its own public URL? ------
        checks.Add(await CheckLoopbackAsync(publicBase, ct).ConfigureAwait(false));

        // --- 5. TLS posture -------------------------------------------------
        checks.Add(insecureTls
            ? Warn("Editor certificate validation",
                "Certificate validation is DISABLED for calls to the editor.",
                "Acceptable for a trial with a self-signed certificate. Set " +
                $"{ConfigKeys.WopiAllowInsecureTls} back to false before this is used for real work, and " +
                "install a certificate the server trusts.")
            : Pass("Editor certificate validation", "Enabled."));

        // --- 6. lifetimes ---------------------------------------------------
        checks.Add(WopiPreflightRules.CheckLifetimes(tokenMinutes, lockMinutes));

        // --- 7. the two things this cannot check ----------------------------
        checks.Add(new WopiPreflightCheckDto
        {
            Name = "Can the EDITOR reach this application?",
            Outcome = WopiPreflightOutcome.Skipped,
            Detail = "Not checkable from here. The check above proves this SERVER can reach " +
                     $"{(publicBase.Length > 0 ? publicBase : "its own public URL")}; it says nothing about " +
                     "whether the editor can.",
            Remedy = "Open a document. If the frame stays grey and this application's log records no " +
                     "CheckFileInfo, the editor never called - and publicBaseUrl is the reason nine times " +
                     "out of ten. From a shell inside the editor's container: " +
                     "curl -k <publicBaseUrl>/wopi/files/1"
        });

        checks.Add(new WopiPreflightCheckDto
        {
            Name = "Does the editor's allow-list include this host?",
            Outcome = WopiPreflightOutcome.Skipped,
            Detail = "Not checkable from here - it is configuration inside the editor, which does not " +
                     "publish it.",
            Remedy = "Collabora refuses an unlisted host with \"unauthorized WOPI host\" and nothing else. " +
                     "aliasgroup1 is a REGULAR EXPRESSION matching this application's public URL, so the dots " +
                     "need escaping: aliasgroup1=https://dms\\.bluecross\\.local:443"
        });

        report.Checks = checks;

        _logger.LogInformation(
            "WOPI preflight: {Failures} failure(s), {Warnings} warning(s), editor {Editor}.",
            report.Failures, report.Warnings, editor);

        return report;
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// Calls this application's own WOPI endpoint at its configured public URL.
    ///
    /// <para>
    /// A tokenless call to a non-existent revision, so it never touches a real
    /// document. What matters is the SHAPE of the answer: 401 or 404 means DNS,
    /// TLS, the reverse proxy and the WOPI route all worked and the request
    /// reached the host's own authorisation. A connection error means the URL is
    /// wrong or unreachable. A 200 would mean the endpoint is answering without
    /// a token, which would be a serious defect and is reported as one.
    /// </para>
    /// </summary>
    private async Task<WopiPreflightCheckDto> CheckLoopbackAsync(string publicBase, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(publicBase) ||
            !Uri.TryCreate(publicBase, UriKind.Absolute, out _))
        {
            return Skipped("This application reachable at its public URL", "No usable public URL to try.");
        }

        var probe = $"{publicBase.TrimEnd('/')}/wopi/files/0";

        try
        {
            // The insecure client, deliberately: this check is about whether the
            // URL routes to us at all, and a self-signed certificate on our own
            // front door is a separate finding from an unreachable host.
            var client = _httpClients.CreateClient(WopiDiscoveryClient.InsecureHttpClientName);
            client.Timeout = TimeSpan.FromSeconds(10);

            using var response = await client.GetAsync(probe, ct).ConfigureAwait(false);

            return WopiPreflightRules.JudgeLoopback(probe, (int)response.StatusCode, response.ReasonPhrase);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail("This application reachable at its public URL", $"{probe} timed out.",
                "Nothing answered within ten seconds. Check the host name and port, and that this " +
                "application is listening on the address in publicBaseUrl and not only on localhost.");
        }
        catch (HttpRequestException ex)
        {
            return Fail("This application reachable at its public URL", $"{probe} could not be reached: {ex.Message}",
                "This is the address the EDITOR will call. If this server cannot reach it, the editor " +
                "certainly cannot.");
        }
    }

    private static WopiPreflightCheckDto Pass(string name, string detail)
        => new() { Name = name, Outcome = WopiPreflightOutcome.Pass, Detail = detail };

    private static WopiPreflightCheckDto Warn(string name, string detail, string? remedy = null)
        => new() { Name = name, Outcome = WopiPreflightOutcome.Warn, Detail = detail, Remedy = remedy };

    private static WopiPreflightCheckDto Fail(string name, string detail, string? remedy = null)
        => new() { Name = name, Outcome = WopiPreflightOutcome.Fail, Detail = detail, Remedy = remedy };

    private static WopiPreflightCheckDto Skipped(string name, string detail)
        => new() { Name = name, Outcome = WopiPreflightOutcome.Skipped, Detail = detail };
}
