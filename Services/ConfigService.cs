//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Typed reader over system_config (Administration > System Configuration), same contract as the
 * old Bcl.Dms IConfigService so the ported authoring / Office editor code reads settings unchanged.
 * Values are read once per request (scoped) and cached for that request.
 */
using System.Globalization;
using DMS.DataAccess.Repository.IRepository;

namespace DMS.Web.Services
{
    public static class ConfigKeys
    {
        public const string EditorEnabled = "office.editor.enabled";
        public const string EditorProvider = "office.editor.provider";
        public const string AuthoringEnabled             = "authoring.enabled";
        public const string AuthoringMaxContentBytes     = "authoring.maxContentBytes";
        public const string AuthoringMaxImageBytes       = "authoring.maxImageBytes";
        public const string AuthoringMaxTotalImageBytes  = "authoring.maxTotalImageBytes";
        public const string AuthoringAutosaveSeconds     = "authoring.autosaveSeconds";
        public const string AuthoringDefaultPageSetup    = "authoring.defaultPageSetup";
        public const string AuthoringHeaderFooterEnabled = "authoring.headerFooterEnabled";
        public const string StorageMaxUploadMb           = "storage.maxUploadMb";
        public const string UploadAllowedExtensions      = "upload.allowedExtensions";

        public const string WopiEnabled              = "office.wopi.enabled";
        public const string WopiEditor               = "office.wopi.editor";
        public const string WopiDiscoveryUrl         = "office.wopi.discoveryUrl";
        public const string WopiPublicBaseUrl        = "office.wopi.publicBaseUrl";
        public const string WopiTokenLifetimeMinutes = "office.wopi.tokenLifetimeMinutes";
        public const string WopiLockMinutes          = "office.wopi.lockMinutes";
        public const string WopiAllowInsecureTls     = "office.wopi.allowInsecureEditorTls";

        public const string RenditionLibreOfficePath   = "rendition.libreOfficePath";
        public const string RenditionTimeoutSeconds    = "rendition.timeoutSeconds";
        public const string RenditionSignaturePage     = "rendition.signaturePage";
        public const string RenditionWorkerEnabled     = "rendition.workerEnabled";
        public const string RenditionBatchSize         = "rendition.queue.batchSize";
        public const string RenditionAllowManualUpload = "rendition.allowManualUpload";
        public const string TimeZoneId                 = "ui.timeZoneId";
        public const string ShareLinkEnabled           = "share.link.enabled";
        public const string ShareLinkDefaultDays       = "share.link.defaultDays";
        public const string ShareLinkMaxDays           = "share.link.maxDays";
        public const string ShareLinkFrameAncestors    = "share.link.frameAncestors";
        // Phase 5a - scheduler, e-mail, controlled print
        public const string SchedulerEnabled         = "scheduler.enabled";
        public const string SchedulerIntervalMinutes = "scheduler.intervalMinutes";
        public const string EmailEnabled             = "notification.email.enabled";
        public const string EmailFrom                = "notification.email.from";
        public const string EmailFromName            = "notification.email.fromName";
        public const string EmailBatchSize           = "notification.email.batchSize";
        public const string EmailBaseUrl             = "notification.email.baseUrl";
        public const string SmtpHost                 = "notification.smtp.host";
        public const string SmtpPort                 = "notification.smtp.port";
        public const string SmtpUser                 = "notification.smtp.user";
        public const string SmtpPassword             = "notification.smtp.password";
        public const string SmtpEnableSsl            = "notification.smtp.enableSsl";
        public const string PrintMaxCopies           = "print.maxCopiesPerRequest";
        public const string PrintDefaultReturnDays   = "print.defaultReturnDays";
    }

    public interface IConfigService
    {
        Task<string?> GetStringAsync(string key, string? fallback, CancellationToken ct = default);
        Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default);
        Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default);
        Task<double> GetDoubleAsync(string key, double fallback, CancellationToken ct = default);
    }

    public sealed class ConfigService : IConfigService
    {
        private readonly IUnitOfWork _unitOfWork;
        private Dictionary<string, string?>? _values;

        private readonly IConfiguration _settings;
        private readonly DMS.Web.CommonUtils.PasswordCipher _cipher;
        public ConfigService(IUnitOfWork unitOfWork, IConfiguration settings,DMS.Web.CommonUtils.PasswordCipher? cipher=null) { _unitOfWork = unitOfWork; _settings=settings;_cipher=cipher??new(settings,Microsoft.Extensions.Logging.Abstractions.NullLogger<DMS.Web.CommonUtils.PasswordCipher>.Instance); }
        private string? Override(string key) => _settings[DMS.Web.Services.Configuration.InfrastructureSettings.ReadPath(key)] ?? (key.StartsWith("ai.",StringComparison.OrdinalIgnoreCase) ? _settings[key.Replace('.',':')] : _settings[key switch {
            ConfigKeys.EditorEnabled => "DocumentEditor:Enabled",
            ConfigKeys.WopiEnabled => "DocumentEditor:Collabora:Enabled",
            ConfigKeys.WopiDiscoveryUrl => "DocumentEditor:Collabora:DiscoveryUrl",
            ConfigKeys.WopiPublicBaseUrl => "DocumentEditor:PublicBaseUrl",
            ConfigKeys.WopiTokenLifetimeMinutes => "DocumentEditor:SessionMinutes",
            _ => "__no_editor_override__"
        }]);

        private async Task<Dictionary<string, string?>> ValuesAsync()
            => _values ??= (await _unitOfWork.SystemConfig.GetListAsync())
                .GroupBy(c => c.config_key ?? "", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().config_value, StringComparer.OrdinalIgnoreCase);

        public async Task<string?> GetStringAsync(string key, string? fallback, CancellationToken ct = default){
            var configured = Override(key);
            var value=!string.IsNullOrWhiteSpace(configured) ? configured.Trim() : key.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) ? fallback : (await ValuesAsync()).TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;
            return value;
        }

        public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default)
            => bool.TryParse(await GetStringAsync(key, null, ct), out var b) ? b : fallback;

        public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default)
            => int.TryParse(await GetStringAsync(key, null, ct), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

        public async Task<double> GetDoubleAsync(string key, double fallback, CancellationToken ct = default)
            => double.TryParse(await GetStringAsync(key, null, ct), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    }
}
