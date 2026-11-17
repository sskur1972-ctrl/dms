//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using System.Collections.Concurrent;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

using DMS.Web.Services;

namespace DMS.Web.Services.Office;

/// <summary>
/// Reads a WOPI client's discovery document.
///
/// <para><b>Why discovery rather than a configured URL</b></para>
/// Every WOPI client publishes <c>/hosting/discovery</c>: an XML map from file
/// extension and action to the URL that performs it. Reading it is what lets
/// one integration serve Collabora Online and, in principle, Office for the
/// web without a per-vendor branch, and what stops an editor upgrade from
/// silently breaking the button - if the map changes, we follow it.
///
/// <para><b>The urlsrc placeholders</b></para>
/// A urlsrc is a template, not a URL. Office for the web publishes things like
/// <c>.../wordeditorframe.aspx?&lt;ui=UI_LLCC&amp;&gt;&lt;rs=DC_LLCC&amp;&gt;</c>,
/// where each <c>&lt;...&gt;</c> is an OPTIONAL parameter the host may fill in.
/// A host that does not want them must remove them; leaving them in produces a
/// URL the editor rejects. That is what <see cref="CleanUrlSource"/> is for, and
/// it is the single most common thing to get wrong in a WOPI host.
/// </para>
/// </summary>
public sealed class WopiDiscoveryClient : IWopiDiscoveryClient
{
    public const string HttpClientName = "wopi-discovery";
    public const string InsecureHttpClientName = "wopi-discovery-insecure";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfigService _config;
    private readonly ILogger<WopiDiscoveryClient> _logger;

    /// <summary>
    /// Cached per (extension, action). A discovery document changes only when
    /// the editor is upgraded, and fetching it on every click would put a
    /// network round trip in front of opening a document.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Actions = new(StringComparer.OrdinalIgnoreCase);

    private static string? _sourceUrl;
    private static DateTime _fetchedUtc = DateTime.MinValue;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>How long a cached discovery document is trusted for.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    public WopiDiscoveryClient(IHttpClientFactory httpClientFactory, IConfigService config,
        ILogger<WopiDiscoveryClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<string?> GetActionUrlAsync(string extension, string action, bool refresh = false,
        CancellationToken ct = default)
    {
        await EnsureLoadedAsync(refresh, ct).ConfigureAwait(false);

        var ext = (extension ?? string.Empty).TrimStart('.').ToLowerInvariant();

        if (Actions.TryGetValue(Key(ext, action), out var url)) return url;

        // "edit" not offered but "view" is - a client configured read-only, or a
        // format it can only display. The caller decides what to do about it.
        if (!string.Equals(action, "view", StringComparison.OrdinalIgnoreCase) &&
            Actions.TryGetValue(Key(ext, "view"), out var viewUrl))
        {
            _logger.LogInformation(
                "The Office editor offers no '{Action}' action for .{Extension}; falling back to view.",
                action, ext);

            return viewUrl;
        }

        return null;
    }

    public async Task<(bool Reachable, string? Detail)> ProbeAsync(CancellationToken ct = default)
    {
        var discoveryUrl = await _config.GetStringAsync(ConfigKeys.WopiDiscoveryUrl, null, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(discoveryUrl))
            return (false, "No editor address is configured (office.wopi.discoveryUrl).");

        try
        {
            var document = await FetchAsync(discoveryUrl, ct).ConfigureAwait(false);
            var count = document.Descendants("action").Count();

            return count > 0
                ? (true, $"{count} editor actions published by {discoveryUrl}.")
                : (false, $"{discoveryUrl} answered, but published no actions. Is it a WOPI client?");
        }
        catch (Exception ex)
        {
            return (false, $"{discoveryUrl} could not be read: {ex.Message}");
        }
    }

    private async Task EnsureLoadedAsync(bool refresh, CancellationToken ct)
    {
        var configuredUrl=await _config.GetStringAsync(ConfigKeys.WopiDiscoveryUrl,null,ct);
        var stale = configuredUrl != _sourceUrl || DateTime.UtcNow - _fetchedUtc > CacheFor;
        if (!refresh && !stale && !Actions.IsEmpty) return;

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-checked inside the gate: several requests can arrive while the
            // first is fetching, and they should wait rather than all fetch.
            stale = configuredUrl != _sourceUrl || DateTime.UtcNow - _fetchedUtc > CacheFor;
            if (!refresh && !stale && !Actions.IsEmpty) return;

            var discoveryUrl = await _config.GetStringAsync(ConfigKeys.WopiDiscoveryUrl, null, ct)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(discoveryUrl))
            {
                _logger.LogWarning("The Office editor is enabled but office.wopi.discoveryUrl is empty.");
                return;
            }

            if (_sourceUrl != discoveryUrl) Actions.Clear();
            var document = await FetchAsync(discoveryUrl, ct).ConfigureAwait(false);

            var loaded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var element in document.Descendants("action"))
            {
                var name = element.Attribute("name")?.Value;
                var ext = element.Attribute("ext")?.Value;
                var urlSource = element.Attribute("urlsrc")?.Value;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ext) ||
                    string.IsNullOrWhiteSpace(urlSource))
                {
                    continue;
                }

                loaded[Key(ext, name)] = CleanUrlSource(urlSource);
            }

            if (loaded.Count == 0)
            {
                _logger.LogWarning("{Url} published no usable WOPI actions; the previous map is kept.",
                    discoveryUrl);
                return;
            }

            Actions.Clear();
            foreach (var pair in loaded) Actions[pair.Key] = pair.Value;

            _sourceUrl=discoveryUrl;
            _fetchedUtc = DateTime.UtcNow;

            _logger.LogInformation("Loaded {Count} WOPI actions from {Url}.", loaded.Count, discoveryUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the Office editor's discovery document failed.");
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<XDocument> FetchAsync(string discoveryUrl, CancellationToken ct)
    {
        var insecure = await _config.GetBoolAsync(ConfigKeys.WopiAllowInsecureTls, false, ct).ConfigureAwait(false);

        if (insecure)
        {
            _logger.LogWarning(
                "office.wopi.allowInsecureEditorTls is ON: the editor's TLS certificate is not being verified. " +
                "This is for an internal pilot only.");
        }

        var client = _httpClientFactory.CreateClient(insecure ? InsecureHttpClientName : HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(8);

        using var response = await client.GetAsync(discoveryUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // The discovery document comes from another server, so it is parsed with
        // DTDs prohibited and no resolver - an XML external entity in it would
        // otherwise be able to read files from this host.
        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null
        };

        using var reader = System.Xml.XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }

    /// <summary>
    /// Strips the optional <c>&lt;...&gt;</c> placeholders out of a urlsrc and
    /// tidies the query string that is left.
    /// </summary>
    /// <remarks>
    /// A urlsrc such as <c>https://host/we/frame.aspx?&lt;ui=UI_LLCC&amp;&gt;&lt;rs=DC_LLCC&amp;&gt;IsLicensedUser=1&amp;</c>
    /// becomes <c>https://host/we/frame.aspx?IsLicensedUser=1</c>. The
    /// placeholders are parameters the host MAY supply; a host that leaves them
    /// in sends the editor a URL containing angle brackets, which it rejects.
    /// </remarks>
    internal static string CleanUrlSource(string urlSource)
    {
        var builder = new System.Text.StringBuilder(urlSource.Length);
        var depth = 0;

        foreach (var character in urlSource)
        {
            if (character == '<') { depth++; continue; }
            if (character == '>') { if (depth > 0) depth--; continue; }
            if (depth == 0) builder.Append(character);
        }

        var cleaned = builder.ToString();

        // Whatever the placeholders left behind: a trailing separator, a doubled
        // ampersand, or a '?' with nothing after it.
        cleaned = cleaned.Replace("&&", "&", StringComparison.Ordinal).TrimEnd('&', '?');

        var question = cleaned.IndexOf('?');
        if (question >= 0 && question < cleaned.Length - 1)
        {
            var path = cleaned[..question];
            var query = cleaned[(question + 1)..].Trim('&');
            cleaned = query.Length > 0 ? $"{path}?{query}" : path;
        }

        return cleaned;
    }

    private static string Key(string extension, string action)
        => $"{extension.TrimStart('.').ToLowerInvariant()}|{action.ToLowerInvariant()}";
}
