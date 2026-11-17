using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.Json;
using Npgsql;
using PTPLCRYPTORENGINE;

namespace DMS.Web.Services.Configuration;

public static class ConfigurationBootstrap
{
    public static bool IsMaintenanceArgument(string value) => value is "--encrypt-dms-connection" or "--migrate-configuration" or "--verify-configuration";
    public static bool Initialize(WebApplicationBuilder builder, string[] args)
    {
        foreach (var source in builder.Configuration.Sources.OfType<JsonConfigurationSource>()
                     .Where(s => System.IO.Path.GetFileName(s.Path ?? "").StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)).ToArray())
            builder.Configuration.Sources.Remove(source);
        var file = builder.Environment.IsDevelopment() ? "appsettings.Development.json" : "appsettings.json";
        builder.Configuration.AddJsonFile("application.json", optional: false, reloadOnChange: false);
        var profile = builder.Configuration.GetSection("EnvironmentOverrides:" + builder.Environment.EnvironmentName);
        var prefix = profile.Path + ":";
        builder.Configuration.AddInMemoryCollection(profile.AsEnumerable().Where(p => p.Value != null)
            .Select(p => new KeyValuePair<string, string?>(p.Key[prefix.Length..], p.Value)).ToArray());
        builder.Configuration.AddJsonFile(file, optional: false, reloadOnChange: false);
        if (builder.Environment.IsDevelopment()) builder.Configuration.AddUserSecrets(typeof(ConfigurationBootstrap).Assembly, optional: true);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args.Where(a => !IsMaintenanceArgument(a)).ToArray());

        var selected = System.IO.Path.Combine(builder.Environment.ContentRootPath, file);
        if (args.Contains("--encrypt-dms-connection"))
        {
            var json = JsonNode.Parse(File.ReadAllText(selected))!.AsObject();
            var value = json["ConnectionStrings"]?["DMSConnection"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("DMSConnection is missing from the selected environment file.");
            // Maintenance input must be a valid plaintext connection string. This also prevents accidental double encryption.
            try { _ = new NpgsqlConnectionStringBuilder(value); }
            catch { throw new InvalidOperationException("The encryption command requires a plaintext connection string in the selected environment file."); }
            json["ConnectionStrings"]!["DMSConnection"] = AESCryptor.Encrypt(value, RequireKey(builder.Configuration));
            Save(selected, json);
            Console.WriteLine($"DMSConnection protected in {file}; no connection value was printed.");
            return false;
        }
        var stored = builder.Configuration.GetConnectionString("DMSConnection");
        if (!string.IsNullOrWhiteSpace(stored))
        {
            string plain;
            try { plain = AESCryptor.Decrypt(stored, RequireKey(builder.Configuration)); }
            catch { throw new InvalidOperationException("DMSConnection could not be decrypted. Check Cipherkey and the encrypted configuration."); }
            if (string.IsNullOrWhiteSpace(plain)) throw new InvalidOperationException("Decrypted DMSConnection is empty.");
            _ = new NpgsqlConnectionStringBuilder(plain);
            // Every existing EF, Dapper, WOPI, identity and backup consumer sees the same resolved value.
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DMSConnection"] = plain });
        }
        else throw new InvalidOperationException("DMSConnection is missing from the selected environment configuration.");
        if (args.Contains("--verify-configuration"))
        {
            _ = new NpgsqlConnectionStringBuilder(builder.Configuration.GetConnectionString("DMSConnection"));
            using var logs = new RollingFileLoggerProvider(builder.Configuration, builder.Environment.ContentRootPath);
            var logger = logs.CreateLogger("DMS.ConfigurationVerification");
            logger.LogInformation("Configuration verification passed for {Environment} using {File}.", builder.Environment.EnvironmentName, file);
            logger.LogError(new InvalidOperationException("Synthetic logging verification exception; this is not an application failure."), "Intentional error-log verification probe.");
            Console.WriteLine($"Configuration verified: {file}; connection parsed; application/error log probes queued. No database connection was opened.");
            return false;
        }
        if (args.Contains("--migrate-configuration"))
        {
            ConfigurationMigration.Run(builder.Configuration, builder.Environment.ContentRootPath);
            return false;
        }
        return true;
    }
    static string RequireKey(IConfiguration c) => !string.IsNullOrWhiteSpace(c["Cipherkey"]) ? c["Cipherkey"]! : throw new InvalidOperationException("Cipherkey is required.");
    internal static void Save(string path, JsonNode json)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, json.ToJsonString(new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + Environment.NewLine);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        File.Move(temporary, path, overwrite: true);
    }
}
