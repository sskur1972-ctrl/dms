using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace DMS.Web.Services.Configuration;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(10000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    readonly Task writer;
    readonly string directory;
    readonly long maxBytes;
    readonly int retentionDays;
    readonly string[] secrets;
    public RollingFileLoggerProvider(IConfiguration configuration, string root)
    {
        directory = Path.GetFullPath(Path.Combine(root, configuration["Logging:File:Directory"] ?? "App_Data/logs"));
        Directory.CreateDirectory(directory);
        maxBytes = Math.Max(1024 * 1024, configuration.GetValue<long?>("Logging:File:MaxFileBytes") ?? 32 * 1024 * 1024);
        retentionDays = Math.Max(1, configuration.GetValue<int?>("Logging:File:RetentionDays") ?? 30);
        secrets = configuration.AsEnumerable().Where(p => p.Key.EndsWith("Password", StringComparison.OrdinalIgnoreCase) || p.Key.EndsWith("Secret", StringComparison.OrdinalIgnoreCase) || p.Key.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase) || p.Key == "Cipherkey" || p.Key == "ConnectionStrings:DMSConnection")
            .Select(p => p.Value).Where(v => !string.IsNullOrEmpty(v) && v.Length > 3).Cast<string>().Distinct().ToArray();
        writer = Task.Run(WriteAsync);
    }
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { queue.Writer.TryComplete(); writer.GetAwaiter().GetResult(); }
    string Redact(string text)
    {
        foreach (var secret in secrets) text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        text = Regex.Replace(text, @"(?i)\b(password|pwd)\s*=\s*[^;\r\n]*", "$1=[REDACTED]", RegexOptions.None, TimeSpan.FromSeconds(1));
        return Regex.Replace(text, @"(?i)(access_token|sig|api_key|token)=([^&\s]+)", "$1=[REDACTED]", RegexOptions.None, TimeSpan.FromSeconds(1));
    }
    async Task WriteAsync()
    {
        DateTime cleaned = DateTime.MinValue;
        await foreach (var e in queue.Reader.ReadAllAsync())
        {
            try
            {
                var json = JsonSerializer.Serialize(new { timestamp = e.Time, level = e.Level.ToString(), category = e.Category, eventId = e.EventId, traceId = e.TraceId, message = Redact(e.Message), exception = e.Exception == null ? null : Redact(e.Exception) }) + Environment.NewLine;
                await Append("application", e.Time.UtcDateTime, json);
                if (e.Level >= LogLevel.Error) await Append("errors", e.Time.UtcDateTime, json);
                if (cleaned.Date != e.Time.UtcDateTime.Date)
                {
                    foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
                        if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-retentionDays)) File.Delete(file);
                    cleaned = e.Time.UtcDateTime;
                }
            }
            catch { Console.Error.WriteLine("DMS file logging failed. Check the log directory permissions and available disk space."); }
        }
    }
    async Task Append(string prefix, DateTime date, string text)
    {
        var path = Path.Combine(directory, $"{prefix}-{date:yyyyMMdd}.jsonl");
        for (var n = 1; File.Exists(path) && new FileInfo(path).Length >= maxBytes; n++) path = Path.Combine(directory, $"{prefix}-{date:yyyyMMdd}-{n}.jsonl");
        await File.AppendAllTextAsync(path, text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
    }
    sealed record Entry(DateTimeOffset Time, LogLevel Level, string Category, int EventId, string? TraceId, string Message, string? Exception);
    sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public bool IsEnabled(LogLevel level) => level != LogLevel.None;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var entry = new Entry(DateTimeOffset.UtcNow, level, category, id.Id, System.Diagnostics.Activity.Current?.TraceId.ToString(), formatter(state, exception), exception?.ToString());
            try { owner.queue.Writer.WriteAsync(entry).AsTask().GetAwaiter().GetResult(); }
            catch (ChannelClosedException) { }
        }
    }
}
