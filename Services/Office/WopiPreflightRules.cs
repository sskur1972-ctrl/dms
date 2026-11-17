//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").

using DMS.Web.Services;

namespace DMS.Web.Services.Office;

/// <summary>
/// The parts of the Office-editor preflight that are a judgement about a
/// configured value rather than a network call, so they can be tested without
/// one.
/// </summary>
public static class WopiPreflightRules
{
    /// <summary>
    /// Judges <c>office.wopi.publicBaseUrl</c>.
    ///
    /// <para><b>Why this is worth its own function</b></para>
    /// It is the single most common failure in a WOPI integration, and the
    /// reason is that the setting is easy to read as "this application's
    /// address" when it means "this application's address <i>as the editor can
    /// reach it</i>". The browser's address bar says <c>localhost</c>, so
    /// <c>localhost</c> is what gets typed - and inside the editor's container
    /// <c>localhost</c> is the container, so the editor asks itself for the file
    /// and gets nothing. The frame stays grey and no log anywhere records a
    /// thing, because the call never left the container.
    /// </para>
    /// </summary>
    public static WopiPreflightCheckDto CheckPublicBaseUrl(string? publicBaseUrl)
    {
        const string name = "This application's public URL";

        var value = (publicBaseUrl ?? string.Empty).Trim().TrimEnd('/');

        if (value.Length == 0)
        {
            return Fail(name, "Not configured.",
                $"Set {ConfigKeys.WopiPublicBaseUrl} to this application's address AS THE EDITOR CAN REACH " +
                "IT. Not the browser's address, and not localhost.");
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return Fail(name, $"'{value}' is not an absolute URL.",
                "It must include the scheme and host, for example https://dms.bluecross.local");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return Fail(name, $"Scheme '{uri.Scheme}' is not http or https.",
                "Use https for anything beyond a local trial.");
        }

        var host = uri.Host;

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host is "127.0.0.1" or "::1" or "[::1]")
        {
            return Fail(name, $"'{value}' points at localhost.",
                "The EDITOR calls this URL, and localhost means the editor's own machine - inside a " +
                "container, the container itself. Use the address the editor can reach this application " +
                "on. On a single Windows machine with the editor in Docker Desktop that is " +
                "http://host.docker.internal:<port>; on a network it is the server's DNS name.");
        }

        if (host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            return Warn(name,
                $"'{value}' uses host.docker.internal, which only resolves inside a Docker container.",
                "Correct for a single-machine trial with the editor in Docker Desktop. It will not work " +
                "once the editor runs on another machine - use a DNS name then.");
        }

        return uri.Scheme == "https"
            ? Pass(name, $"{value} - absolute, https, not localhost.")
            : Warn(name, $"{value} is http, not https.",
                "Acceptable inside a trial network. A controlled document crossing an unencrypted hop " +
                "between this application and the editor is not acceptable for real work.");
    }

    /// <summary>
    /// Judges the access-token and edit-lock lifetimes against each other.
    ///
    /// <para>
    /// The token must outlive the lock. If it does not, an editing session can
    /// lose its authorisation while it still holds the document: the author
    /// cannot save, and cannot release the lock either, so the revision stays
    /// locked until the lock expires on its own and nobody can revise it.
    /// </para>
    /// </summary>
    public static WopiPreflightCheckDto CheckLifetimes(int tokenMinutes, int lockMinutes)
    {
        const string name = "Token and lock lifetimes";

        return tokenMinutes > lockMinutes
            ? Pass(name, $"Access token {tokenMinutes} min, edit lock {lockMinutes} min.")
            : Warn(name,
                $"The access token ({tokenMinutes} min) is not longer than the edit lock ({lockMinutes} min).",
                "Make the token longer than the lock, or an editing session can lose its authorisation " +
                "while it still holds the document - the author then cannot save and cannot release it " +
                "either, and the revision stays locked until the lock expires.");
    }

    /// <summary>
    /// What a tokenless call to this application's own WOPI endpoint should
    /// have answered.
    ///
    /// <para>
    /// 401 or 404 is the PASS: it means DNS, TLS, the reverse proxy and the
    /// WOPI route all worked and the request reached the host's own
    /// authorisation, which then refused it. A 200 is a FAILURE and a serious
    /// one - it would mean the WOPI endpoints answer without a token, and every
    /// controlled document in the system is readable by anyone who can guess a
    /// revision id.
    /// </para>
    /// </summary>
    public static WopiPreflightCheckDto JudgeLoopback(string probeUrl, int statusCode, string? reasonPhrase)
    {
        const string name = "This application reachable at its public URL";

        if (statusCode is 401 or 404)
        {
            return Pass(name,
                $"{probeUrl} answered {statusCode}, which is this host refusing a request with no token - " +
                "so the address, TLS and the WOPI route all work.");
        }

        if (statusCode == 200)
        {
            return Fail(name, $"{probeUrl} answered 200 to a request with NO access token.",
                "That would mean the WOPI endpoints are answering unauthenticated, which would expose " +
                "every controlled document. Do not enable the editor. Check that nothing in front of " +
                "this application is rewriting or caching /wopi.");
        }

        return Warn(name,
            $"{probeUrl} answered {statusCode} ({reasonPhrase}), which is neither 401 nor 404.",
            "Something answered, but perhaps not this application. Check what is listening at that " +
            "address - a reverse proxy or another site.");
    }

    internal static WopiPreflightCheckDto Pass(string name, string detail)
        => new() { Name = name, Outcome = WopiPreflightOutcome.Pass, Detail = detail };

    internal static WopiPreflightCheckDto Warn(string name, string detail, string? remedy = null)
        => new() { Name = name, Outcome = WopiPreflightOutcome.Warn, Detail = detail, Remedy = remedy };

    internal static WopiPreflightCheckDto Fail(string name, string detail, string? remedy = null)
        => new() { Name = name, Outcome = WopiPreflightOutcome.Fail, Detail = detail, Remedy = remedy };
}
