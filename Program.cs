using Microsoft.AspNetCore.DataProtection;
using DMS.DataAccess.Data;
using DMS.DataAccess.Repository;
using DMS.DataAccess.Repository.IRepository;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using System.Text.Json.Serialization;
using DMS.Web.Services.Configuration;
using DMS.Web.Services;
using DMS.Web.Services.Rendition;

var builder = WebApplication.CreateBuilder(args.Where(a => !ConfigurationBootstrap.IsMaintenanceArgument(a)).ToArray());
try
{
    if (!ConfigurationBootstrap.Initialize(builder, args)) return;
}
catch (Exception ex)
{
    try
    {
        using var startupLogs = new RollingFileLoggerProvider(builder.Configuration, builder.Environment.ContentRootPath);
        startupLogs.CreateLogger("DMS.Startup").LogCritical(ex, "DMS configuration initialization failed.");
    }
    catch { Console.Error.WriteLine("DMS startup logging failed; check configuration and log directory permissions."); }
    throw;
}
// The container owns the provider so shutdown drains the logging queue.
builder.Services.AddSingleton<ILoggerProvider>(_ => new RollingFileLoggerProvider(builder.Configuration, builder.Environment.ContentRootPath));

// -------------------- Kestrel Server Limits --------------------
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.MaxRequestHeadersTotalSize = 32768;
    serverOptions.Limits.MaxRequestLineSize = 32768;
});

// -------------------- Services --------------------

// MVC + TempData + JSON + Razor View Location
builder.Services.AddControllersWithViews(options => options.Filters.Add<DMS.Web.Services.SharedAdministrationFilter>())
    .AddSessionStateTempDataProvider()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
    })
    .AddRazorOptions(options =>
    {
        options.ViewLocationFormats.Clear();
        options.ViewLocationFormats.Add("/Views/{1}/{0}.cshtml");
        options.ViewLocationFormats.Add("/Views/Shared/{0}.cshtml");
        options.ViewLocationFormats.Add("/Views/Report/{1}/{0}.cshtml");
    });

// EF Core with PostgreSQL (DMS database)
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DMSConnection")));


// Form options for file upload
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueCountLimit = int.MaxValue;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartBodyLengthLimit = 104857600; // 100MB
    options.MemoryBufferThreshold = 64 * 1024;
});

// Model binding limits
builder.Services.Configure<MvcOptions>(options =>
{
    options.MaxModelBindingCollectionSize = 10000;
});

// Secure cookie policy
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.Secure = (builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("DocumentEditor:AllowHttpForDevelopment") ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always);
});

// Session config
builder.Services.AddSession(options =>
{
    // Cookies are shared across ports. DMS must not overwrite the portal's
    // session cookie: the portal uses that session to validate its login.
    options.Cookie.Name = ".DMS.Session";
    options.IdleTimeout = TimeSpan.FromMinutes(15);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = (builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("DocumentEditor:AllowHttpForDevelopment") ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always);
});
builder.Services.AddSingleton<DMS.Web.Services.Rendition.EuroOfficePdfConverter>();
//builder.Services.AddSingleton<DMS.Web.Services.EuroOffice.EuroOfficeJwtService>();
//builder.Services.AddSingleton<DMS.Web.Services.Rendition.EuroOfficePdfConverter>();
//  // Ensures IHttpClientFactory is registered
//builder.Services.AddSingleton<EuroOfficePdfConverter>();

// Mode-aware authentication follows the Common portal cookie when Portal mode is enabled.
var sharedAuth = builder.Configuration.GetSection("SharedAuthentication");
var authMode = sharedAuth["Mode"] ?? "Standalone";
if (!string.Equals(authMode, "Standalone", StringComparison.OrdinalIgnoreCase) &&
    !string.Equals(authMode, "Portal", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("SharedAuthentication:Mode must be Standalone or Portal.");
var standaloneMode = string.Equals(authMode, "Standalone", StringComparison.OrdinalIgnoreCase);
builder.Services.AddScoped<DMS.Web.CommonUtils.PasswordCipher>();
DMS.Web.Services.DmsAuthentication.AddDmsAuthentication(builder.Services, builder.Configuration, builder.Environment);

// Dependency Injection & utility services
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<DMS.Web.Services.FileStore>();
// -------------------- Phase 3a: in-application authoring + Office editor (Collabora / WOPI) --------------------
builder.Services.AddScoped<DMS.Web.Services.IConfigService, DMS.Web.Services.ConfigService>();
builder.Services.AddSingleton<DMS.Web.Services.ExternalIntegrationSecurity>();
builder.Services.AddSingleton<DMS.Web.Services.Authoring.IEditorHtmlSanitizer, DMS.Web.Services.Authoring.EditorHtmlSanitizer>();
builder.Services.AddSingleton<DMS.Web.Services.Authoring.IHtmlDocxWriter, DMS.Web.Services.Authoring.HtmlDocxWriter>();
builder.Services.AddScoped<DMS.Web.Services.Authoring.IFileStorageService, DMS.Web.Services.Authoring.FileStoreStorageAdapter>();
builder.Services.AddScoped<DMS.Web.Services.Authoring.IDocumentPreviewRenderer, DMS.Web.Services.Authoring.OpenXmlPreviewRenderer>();
builder.Services.AddScoped<DMS.Web.Services.Authoring.IDocumentTextExtractor, DMS.Web.Services.Authoring.OpenXmlDocumentTextExtractor>();
builder.Services.AddScoped<DMS.Web.Services.Authoring.DocumentAuthoringService>();

// WOPI access tokens are signed with Data Protection: the key ring must survive restarts (and be shared by every node)
var keyRingPath = builder.Configuration["Dms:Security:DataProtectionKeyPath"];
if (string.IsNullOrWhiteSpace(keyRingPath)) keyRingPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
Directory.CreateDirectory(keyRingPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
    .SetApplicationName("DMS");
builder.Services.AddScoped<DMS.Web.Services.Office.IWopiTokenProtector, DMS.Web.Services.Office.WopiTokenProtector>();
builder.Services.AddHttpClient(DMS.Web.Services.Office.WopiDiscoveryClient.HttpClientName);
builder.Services.AddHttpClient(DMS.Web.Services.Office.WopiDiscoveryClient.InsecureHttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
builder.Services.AddScoped<DMS.Web.Services.Office.IWopiDiscoveryClient, DMS.Web.Services.Office.WopiDiscoveryClient>();
builder.Services.AddScoped<DMS.Web.Services.Office.IWopiPreflightService, DMS.Web.Services.Office.WopiPreflightService>();
builder.Services.AddScoped<DMS.Web.Services.Office.WopiService>();
//builder.Services.AddScoped<DMS.Web.Services.DocumentEditor.IDocumentEditorProvider, DMS.Web.Services.DocumentEditor.CollaboraEditorProvider>();
builder.Services.AddScoped<DMS.Web.Services.DocumentEditor.IEditorProviderResolver, DMS.Web.Services.DocumentEditor.EditorProviderResolver>();
builder.Services.AddScoped<DMS.Web.Services.DocumentEditor.IDocumentEditorService, DMS.Web.Services.DocumentEditor.DocumentEditorService>();
builder.Services.Configure<DMS.Web.Services.DocumentEditor.EditorOptions>(builder.Configuration.GetSection("DocumentEditor"));
builder.Services.AddScoped<DMS.Web.Services.DocumentEditor.EditorSessionStore>();
builder.Services.AddScoped<DMS.Web.Services.DocumentEditor.TemplateEditorService>();
builder.Services.AddScoped<DMS.Web.Services.DocumentEditor.EditorAnchorService>();
builder.Services.AddScoped<DMS.Web.Services.DocumentEditor.EditorHealthService>();
builder.Services.AddScoped<DMS.Web.Services.DocumentEditor.EditorFileCommitService>();
builder.Services.AddScoped<DMS.Web.Services.DocumentEditor.EditorDownloadService>();
builder.Services.AddScoped<DMS.Web.Services.DocumentEditor.PageSectionService>();
builder.Services.AddScoped<DMS.Web.Services.DocumentEditor.IDocumentEditorProvider, DMS.Web.Services.DocumentEditor.OnlyOfficeEditorProvider>();
builder.Services.AddScoped<DMS.Web.Services.DocumentEditor.IDocumentEditorProvider, DMS.Web.Services.DocumentEditor.SpireEditorProvider>();
builder.Services.AddHttpClient("editor-download", c => c.Timeout = TimeSpan.FromMinutes(2)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient("editor-health", c => c.Timeout = TimeSpan.FromSeconds(8)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// Phase 4 - PDF rendition (LibreOffice + DMS stamper) and its background worker
builder.Services.AddSingleton<DMS.Web.Services.Rendition.LibreOfficeConverter>();
builder.Services.AddScoped<DMS.Web.Services.Rendition.RenditionProcessor>();
//builder.Services.AddScoped<DMS.Web.Services.Rendition.CollaboraPdfConverter>();
builder.Services.AddScoped<DMS.Web.Services.Rendition.OnlyOfficeFileConverter>();
builder.Services.AddHttpClient("rendition-conversion", c => c.Timeout = TimeSpan.FromMinutes(30)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHostedService<DMS.Web.Services.Rendition.RenditionWorker>();
// Phase 5a - controlled copies, scheduled jobs (SLA, automatic effective date, periodic review, e-mail)
builder.Services.AddScoped<DMS.Web.Services.Rendition.ControlledCopyRenderer>();
builder.Services.AddScoped<DMS.Web.Services.Scheduler.SchedulerJobs>();
builder.Services.AddHostedService<DMS.Web.Services.Scheduler.SchedulerWorker>();
// Phase 5b - legacy master import
builder.Services.AddScoped<DMS.Web.Services.Legacy.LegacyImportService>();
// Dms:Storage:Root
builder.Services.AddScoped<DMS.Web.Services.PgBackupRunner>();   // Administration > Backups
builder.Services.AddDetection();

builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddScoped<DMS.Web.Services.Intelligence.DocumentIntelligenceService>();
builder.Services.AddScoped<DMS.Web.Services.Intelligence.AiProviderClient>();
builder.Services.AddScoped<DMS.Web.Services.Intelligence.AiConfigurationService>();
builder.Services.AddScoped<DMS.Web.Services.Intelligence.RagService>();
builder.Services.AddHostedService<DMS.Web.Services.Intelligence.IntelligenceWorker>();
builder.Services.AddHttpClient("document-intelligence", c => { c.Timeout = TimeSpan.FromMinutes(10); c.MaxResponseContentBufferSize = 8 * 1024 * 1024; }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddCors();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("external-token", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});

//if (builder.Environment.IsDevelopment())
//{
//    builder.Services.AddDistributedMemoryCache();
//}
//else
//{

//var redisConnection =
//    builder.Configuration.GetConnectionString("Redis")
//    ?? throw new InvalidOperationException(
//        "Redis connection string is missing.");

//builder.Services.AddStackExchangeRedisCache(options =>
//{
//    options.Configuration = redisConnection;
//    options.InstanceName = "dms:";
//});

//builder.Services.Configure<DMS.Web.Caching.DmsRedisOptions>(
//builder.Configuration.GetSection("Dms:Redis"));

//builder.Services.AddScoped<DMS.Web.Caching.IDmsDocumentCache,
//                            DMS.Web.Caching.RedisDocumentCache>();
var redisConnection =
builder.Configuration.GetConnectionString("Redis")
?? throw new InvalidOperationException(
    "Redis connection string is missing.");

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = redisConnection;
    options.InstanceName = "dms:";
});

builder.Services.Configure<DMS.Web.Caching.DmsRedisOptions>(
    builder.Configuration.GetSection("Dms:Redis"));

builder.Services.AddScoped<
    DMS.Web.Caching.IDmsDocumentCache,
    DMS.Web.Caching.RedisDocumentCache>();

//builder.Services.AddHttpClient<EuroOfficePdfConverter>();

//}

if (!builder.Environment.IsDevelopment() && builder.Configuration.GetValue<bool>("DocumentEditor:AllowHttpForDevelopment"))
    throw new InvalidOperationException("HTTP editor development mode is forbidden outside Development.");

var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("DMS started in {Environment}; persistent file logging enabled.", app.Environment.EnvironmentName));

// Bring the DMS database (ConnectionStrings:DMSConnection) up to date: applies Database/03_... etc.
// when their schema_version row is missing (see Services/DatabaseUpgrader.cs).
// DMS.Web.Services.DatabaseUpgrader.Run(app.Configuration, app.Logger);

// -------------------- Middleware Pipeline --------------------

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.Use(async (context, next) =>
{
    try
    {
        await next();
        if (context.Response.StatusCode >= 400)
            app.Logger.LogWarning("HTTP {StatusCode} {Method} {Path}; trace {TraceIdentifier}", context.Response.StatusCode, context.Request.Method, context.Request.Path, context.TraceIdentifier);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Request failed: {Method} {Path}; trace {TraceIdentifier}", context.Request.Method, context.Request.Path, context.TraceIdentifier);
        throw;
    }
});

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();

// CORS must be before session & auth
app.UseCors(options =>
    options.WithOrigins("https://go.birlaopus.com/")
           .AllowAnyMethod()
           .AllowAnyHeader()
           .AllowCredentials());

app.UseCookiePolicy();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: standaloneMode
        ? "{controller=Account}/{action=Login}/{id?}"
        : "{controller=Home}/{action=Index}/{id?}");




app.MapGet("/api/internal/rendition-source/{token}", (string token) =>
    DMS.Web.Services.Rendition.EuroOfficePdfConverter.TryTakePendingFile(token, out var bytes)
        ? Results.File(bytes, "application/octet-stream")
        : Results.NotFound());

app.Run();
