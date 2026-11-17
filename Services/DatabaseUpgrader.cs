//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Applies the phase scripts (Database/03_... onwards) to the DMS database at start-up.
 *
 * The scripts are embedded in DMS.Web.dll (see DMS.Web.csproj), so the database the application really
 * uses (ConnectionStrings:DMSConnection) is always brought up to date - no manual script run needed.
 * Every script is idempotent and runs in its own transaction; it is applied only when its
 * schema_version row is missing. 02_dms_schema_and_seed.sql (the base schema) is NOT applied here.
 *
 * Switch off with "Database": { "AutoUpgrade": false } in appsettings.json.
 * The result (or the error) is logged and kept in Status, which the Administration screens show.
 */
using System.Reflection;
using Npgsql;

namespace DMS.Web.Services
{
    public static class DatabaseUpgrader
    {
        /// <summary>schema_version -> embedded script, in order.</summary>
        private static readonly (string Version, string Script)[] Scripts =
        {
            ("PG002", "03_phase2_masters.sql"),
            ("PG003", "04_phase3a_documents.sql"),
            ("PG004", "06_authoring_office.sql"),
            ("PG005", "07_phase3b_workflow.sql"),
            ("PG006", "08_phase3c_lifecycle.sql"),
            ("PG007", "09_phase4_rendition_share.sql"),
            ("PG008", "10_phase5a_scheduler_print.sql"),
            ("PG009", "11_phase5b_audit_reports_legacy.sql"),
        };

        /// <summary>Last start-up result; null = database is up to date.</summary>
        public static string? Problem { get; private set; }

        public static void Run(IConfiguration configuration, ILogger logger)
        {
            var connectionString = configuration.GetConnectionString("DMSConnection");
            if (!configuration.GetValue("Database:AutoUpgrade", true))
            {
                logger.LogInformation("Database auto-upgrade is switched off (Database:AutoUpgrade = false).");
                CheckOnly(connectionString, logger);
                return;
            }

            try
            {
                using var cn = new NpgsqlConnection(connectionString);
                cn.Open();

                if (!HasBaseSchema(cn))
                {
                    Problem = $"Database '{cn.Database}' on {cn.Host} does not contain the DMS schema (PG001). " +
                              "Run Database/01_create_database.sql and 02_dms_schema_and_seed.sql first.";
                    logger.LogError(Problem);
                    return;
                }

                foreach (var (version, script) in Scripts)
                {
                    if (HasVersion(cn, version)) continue;

                    logger.LogWarning("Database '{Db}' on {Host}: {Version} missing - applying {Script} ...", cn.Database, cn.Host, version, script);
                    var sql = ReadScript(script);
                    try
                    {
                        using var cmd = new NpgsqlCommand(sql, cn) { CommandTimeout = 600 };
                        cmd.ExecuteNonQuery();
                    }
                    catch (Exception ex)
                    {
                        // the script's own transaction is rolled back by the server; reset the session
                        try { using var rb = new NpgsqlCommand("ROLLBACK", cn); rb.ExecuteNonQuery(); } catch { /* none open */ }
                        var detail = ex is PostgresException pg
                            ? $"{pg.SqlState}: {pg.MessageText}{(string.IsNullOrEmpty(pg.Where) ? "" : " (" + pg.Where + ")")}"
                            : ex.Message;
                        Problem = $"{script} could not be applied to '{cn.Database}' on {cn.Host}: {detail}";
                        logger.LogError(ex, Problem);
                        return;
                    }

                    if (!HasVersion(cn, version))
                    {
                        Problem = $"{script} ran on '{cn.Database}' but {version} was not recorded.";
                        logger.LogError(Problem);
                        return;
                    }
                    logger.LogWarning("{Script} applied - database '{Db}' is now at {Version}.", script, cn.Database, version);
                }

                Problem = null;
                logger.LogInformation("DMS database '{Db}' on {Host} is up to date.", cn.Database, cn.Host);
            }
            catch (Exception ex)
            {
                Problem = "The DMS database could not be reached at start-up: " + ex.Message;
                logger.LogError(ex, Problem);
            }
        }

        private static void CheckOnly(string? connectionString, ILogger logger)
        {
            try
            {
                using var cn = new NpgsqlConnection(connectionString);
                cn.Open();
                var missing = Scripts.Where(s => !HasVersion(cn, s.Version)).Select(s => s.Script).ToList();
                Problem = missing.Count == 0 ? null
                    : $"Database '{cn.Database}' on {cn.Host} needs: {string.Join(", ", missing)} (auto-upgrade is off).";
                if (Problem != null) logger.LogWarning(Problem);
            }
            catch (Exception ex)
            {
                Problem = "The DMS database could not be reached at start-up: " + ex.Message;
                logger.LogError(ex, Problem);
            }
        }

        private static bool HasBaseSchema(NpgsqlConnection cn)
        {
            using var cmd = new NpgsqlCommand(
                "SELECT to_regclass('DMS.schema_version') IS NOT NULL AND to_regproc('DMS.sp_audit_append') IS NOT NULL", cn);
            return cmd.ExecuteScalar() is true && HasVersion(cn, "PG001");
        }

        private static bool HasVersion(NpgsqlConnection cn, string version)
        {
            using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM schema_version WHERE version = @v)", cn);
            cmd.Parameters.AddWithValue("v", version);
            return cmd.ExecuteScalar() is true;
        }

        private static string ReadScript(string fileName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"{fileName} is not embedded in {assembly.GetName().Name}.");
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            // the leading ROLLBACK is only for SQL editors with a stuck session
            return sql.Replace("\r\n", "\n").Replace("\nROLLBACK;\n", "\n");
        }
    }
}
