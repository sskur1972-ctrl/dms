using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Npgsql;

namespace DMS.Web.Services.Configuration;

public static class ConfigurationMigration
{
    public static void Run(IConfiguration configuration, string root)
    {
        using var connection = new NpgsqlConnection(configuration.GetConnectionString("DMSConnection"));
        connection.Open();
        using var transaction = connection.BeginTransaction();
        connection.Execute("LOCK TABLE dms.system_config IN SHARE ROW EXCLUSIVE MODE", transaction: transaction);
        var rows = connection.Query<Row>("SELECT config_key AS Key, config_value AS Value FROM dms.system_config ORDER BY updated_at DESC NULLS LAST, id DESC", transaction: transaction).ToList();
        var path = Path.Combine(root, "application.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var backup = Path.Combine(root, "App_Data", "configuration-backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
        Directory.CreateDirectory(backup);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Copy(path, Path.Combine(backup, "application.json"));
        var export = Path.Combine(backup, "system-config.json");
        File.WriteAllText(export, JsonSerializer.Serialize(rows));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(export, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // Consolidate the previous dotted OCR entries into the same structured AI section.
        if (json["Infrastructure"] is JsonObject infrastructure)
            foreach (var item in infrastructure.ToArray().Where(p => p.Key.StartsWith("ai.", StringComparison.OrdinalIgnoreCase)))
            {
                Set(json, InfrastructureSettings.ReadPath(item.Key), DecodeLegacyKey(item.Key, item.Value?.ToString(), configuration));
                infrastructure.Remove(item.Key);
            }
        foreach (var row in rows.GroupBy(r => r.Key.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Where(r => InfrastructureSettings.IsFileManaged(r.Key.Trim())))
        {
            var key = row.Key.Trim();
            var target = InfrastructureSettings.ReadPath(key);
            // Existing file/environment overrides retain their previous precedence.
            var value = configuration[target] ?? configuration["Infrastructure:" + key] ?? row.Value;
            Set(json, target, DecodeLegacyKey(key, value, configuration));
        }
        ConfigurationBootstrap.Save(path, json);
        var removed = connection.Execute("DELETE FROM dms.system_config a USING dms.system_config b WHERE lower(trim(a.config_key))=lower(trim(b.config_key)) AND (coalesce(a.updated_at,'-infinity'::timestamp),a.id)<(coalesce(b.updated_at,'-infinity'::timestamp),b.id)", transaction: transaction);
        foreach (var key in rows.Select(r => r.Key.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Where(InfrastructureSettings.IsFileManaged))
            connection.Execute("DELETE FROM dms.system_config WHERE lower(trim(config_key))=lower(@key)", new { key }, transaction);
        connection.Execute("CREATE UNIQUE INDEX IF NOT EXISTS ux_system_config_normalized_key ON dms.system_config(lower(trim(config_key)))", transaction: transaction);
        transaction.Commit();
        Console.WriteLine($"Configuration migrated; removed {removed} duplicate rows. Protected backup: {backup}. Restart the application.");
    }
    static string? DecodeLegacyKey(string key, string? value, IConfiguration configuration)
    {
        // One-time conversion of previously stored AI credentials, never runtime format detection.
        if (key.StartsWith("ai.", StringComparison.OrdinalIgnoreCase) && key.EndsWith(".apiKey", StringComparison.OrdinalIgnoreCase) && value?.StartsWith("ptpl:v1:", StringComparison.Ordinal) == true)
        {
            var plain = PTPLCRYPTORENGINE.AESCryptor.Decrypt(value[8..], configuration["Cipherkey"]);
            if (string.IsNullOrEmpty(plain)) throw new InvalidOperationException("An existing AI credential could not be migrated. Verify Cipherkey before retrying.");
            return plain;
        }
        return value;
    }
    static void Set(JsonObject json, string path, string? value)
    {
        var segments = path.Split(':');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (json[segments[i]] is not JsonObject child) json[segments[i]] = child = new JsonObject();
            json = child;
        }
        if (json[segments[^1]] is null) json[segments[^1]] = value;
    }
    sealed class Row { public string Key { get; set; } = ""; public string? Value { get; set; } }
}
