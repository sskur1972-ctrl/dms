//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Phase 5b - URS-7.22 bulk import of legacy masters (Bcl.Dms ILegacyImportService / LegacyImportRequest).
 *
 * The package is ONE .zip: manifest.csv at its root and the master files (paths in the File column, relative to the
 * root of the zip). Each row becomes an EFFECTIVE document with its existing number and revision and the importer's
 * retrospective electronic signature (is_legacy_retro_signature) carrying the batch justification.
 *
 *   dry run  - everything is checked (manifest, master data, number clashes, the files, their format); nothing is written
 *              except the batch record and its row results.
 *   import   - every valid row is imported in its own transaction; a bad row is recorded and skipped, never half-done.
 */
using DMS.DataAccess.Repository.IRepository;
using DMS.Model.Documents;
using DMS.Model.Legacy;
using DMS.Web.Services.Rendition;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace DMS.Web.Services.Legacy
{
    public sealed class LegacyImportException : Exception
    {
        public LegacyImportException(string message) : base(message) { }
    }

    public sealed class LegacyImportService
    {
        public const string ManifestTemplate =
            "DocumentNumber,Title,DocumentType,Department,Revision,Owner,EffectiveFrom,File,PeriodicReviewDue,Keywords,LegacyReference,TrainingRequired\r\n" +
            "SOP-QA-001,Handling of deviations,SOP,QA,04,qa.controller,2024-03-15,masters/SOP-QA-001 rev 04.docx,,deviation;capa,PAPER-ARCHIVE-BOX-12,Y\r\n";

        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["documentnumber"] = "DocumentNumber",
            ["number"] = "DocumentNumber",
            ["docno"] = "DocumentNumber",
            ["title"] = "Title",
            ["documenttype"] = "DocumentType",
            ["type"] = "DocumentType",
            ["documenttypecode"] = "DocumentType",
            ["department"] = "Department",
            ["departmentcode"] = "Department",
            ["dept"] = "Department",
            ["revision"] = "Revision",
            ["version"] = "Revision",
            ["existingversionno"] = "Revision",
            ["rev"] = "Revision",
            ["owner"] = "Owner",
            ["ownerusername"] = "Owner",
            ["effectivefrom"] = "EffectiveFrom",
            ["effectivedate"] = "EffectiveFrom",
            ["file"] = "File",
            ["sourcefile"] = "File",
            ["sourcerelativepath"] = "File",
            ["path"] = "File",
            ["periodicreviewdue"] = "PeriodicReviewDue",
            ["reviewdue"] = "PeriodicReviewDue",
            ["keywords"] = "Keywords",
            ["legacyreference"] = "LegacyReference",
            ["legacysourcereference"] = "LegacyReference",
            ["reference"] = "LegacyReference",
            ["trainingrequired"] = "TrainingRequired",
            ["training"] = "TrainingRequired",
        };

        private static readonly string[] Required = { "DocumentNumber", "Title", "DocumentType", "Department", "Revision", "Owner", "EffectiveFrom", "File" };

        private readonly IUnitOfWork _unitOfWork;
        private readonly FileStore _fileStore;
        private readonly IConfigService _config;
        private readonly ILogger<LegacyImportService> _logger;

        public LegacyImportService(IUnitOfWork unitOfWork, FileStore fileStore, IConfigService config, ILogger<LegacyImportService> logger)
        {
            _unitOfWork = unitOfWork; _fileStore = fileStore; _config = config; _logger = logger;
        }

        /// <summary>Validates (dry run) or imports the package; returns the batch id. Package problems throw before anything is written.</summary>
        public async Task<long> RunAsync(
            Stream package,
            string packageName,
            LegacyImportForm form,
            bool signed,
            int actor,
            int? plantId,
            string? ip,
            CancellationToken ct)
        {
            // keep the package in a temporary file: it is read twice (hash + entries) and may be large
            var temp = Path.Combine(Path.GetTempPath(), "dms-legacy-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                string sha;
                await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
                {
                    await package.CopyToAsync(file, ct);
                    file.Position = 0;
                    sha = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
                }
                using var zip = OpenZip(temp);
                var entries = IndexEntries(zip);
                var rows = ReadManifest(zip, entries);
                await CheckRowsAsync(rows, entries);

                var meaning = await _config.GetStringAsync("legacy.import.signatureMeaning", "Approved by", ct);
                var start = await _unitOfWork.Compliance.StartLegacyBatchAsync(form.BatchReference, form.DryRun, form.Justification, meaning,
                                                                               Path.GetFileName(packageName), sha, rows.Count, actor, ip);
                if (!start.Success) throw new LegacyImportException(start.Message);
                var batchId = start.Id;

                var imported = 0;
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (row.Problems.Count > 0)
                    {
                        await _unitOfWork.Compliance.LogLegacyRowAsync(batchId, row.RowNo, row.DocumentNumber, false, null, string.Join("; ", row.Problems));
                        continue;
                    }
                    if (form.DryRun)
                    {
                        await _unitOfWork.Compliance.LogLegacyRowAsync(batchId, row.RowNo, row.DocumentNumber, true, null,
                            $"Valid - would be imported as {row.DocumentNumber} rev {row.Revision}, effective {row.EffectiveFrom:dd-MMM-yyyy}");
                        continue;
                    }
                    StoredFile? stored = null;
                    try
                    {
                        var entry = entries[Normalise(row.File)];
                        await using (var content = entry.Open())
                            stored = await _fileStore.SaveCheckedAsync(content, entry.Length, Path.GetFileName(entry.FullName), "documents", ct);
                        //var result = await _unitOfWork.Compliance.ImportLegacyDocumentAsync(batchId, row, stored, signed, actor, ip);
                        var result = await _unitOfWork.Compliance.ImportLegacyDocumentAsync(
    batchId,
    row,
    stored,
    signed,
    actor,
    ip,
    form.AccessScope,
    plantId);
                        if (!result.Success)
                        {
                            _fileStore.Archive(stored.RelativePath);
                            await _unitOfWork.Compliance.LogLegacyRowAsync(batchId, row.RowNo, row.DocumentNumber, false, null, result.Message);
                        }
                        else imported++;
                    }
                    catch (Exception ex) when (ex is FileStoreException or IOException or InvalidDataException)
                    {
                        if (stored != null) _fileStore.Archive(stored.RelativePath);
                        await _unitOfWork.Compliance.LogLegacyRowAsync(batchId, row.RowNo, row.DocumentNumber, false, null, ex.Message);
                    }
                }
                await _unitOfWork.Compliance.FinishLegacyBatchAsync(batchId, actor, ip);
                if (imported > 0) RenditionWorker.Nudge();
                _logger.LogInformation("Legacy import batch {Batch} ({Ref}, dry run {Dry}): {Rows} rows, {Imported} imported",
                                       batchId, form.BatchReference, form.DryRun, rows.Count, imported);
                return batchId;
            }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { /* temp file - removed by the OS later */ }
            }
        }

        private ZipArchive OpenZip(string path)
        {
            try { return ZipFile.OpenRead(path); }
            catch (InvalidDataException) { throw new LegacyImportException("The package is not a valid .zip file."); }
        }

        private static string Normalise(string path)
            => path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

        private static Dictionary<string, ZipArchiveEntry> IndexEntries(ZipArchive zip)
        {
            if (zip.Entries.Count > 20000) throw new LegacyImportException("The package has more than 20 000 entries. Split it into smaller batches.");
            var index = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue;   // a folder
                var key = Normalise(e.FullName);
                if (key.Split('/').Any(part => part == "..")) throw new LegacyImportException($"The package contains an unsafe path: {e.FullName}");
                index[key] = e;
            }
            return index;
        }

        private static List<LegacyManifestRow> ReadManifest(ZipArchive zip, Dictionary<string, ZipArchiveEntry> entries)
        {
            var manifest = entries.TryGetValue("manifest.csv", out var m) ? m
                : entries.Values.Where(e => !e.FullName.Replace('\\', '/').Contains('/') && e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                                .SingleOrDefault()
                  ?? throw new LegacyImportException("The package has no manifest.csv at its root.");
            if (manifest.Length > 20 * 1024 * 1024) throw new LegacyImportException("manifest.csv is larger than 20 MB.");
            string text;
            using (var reader = new StreamReader(manifest.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                text = reader.ReadToEnd();
            var records = ParseCsv(text);
            if (records.Count < 2) throw new LegacyImportException("manifest.csv has no rows under its header line.");

            var header = records[0].Select(h => Aliases.TryGetValue(new string(h.Where(char.IsLetterOrDigit).ToArray()), out var n) ? n : h.Trim()).ToList();
            var missing = Required.Where(r => !header.Contains(r)).ToList();
            if (missing.Count > 0) throw new LegacyImportException("manifest.csv is missing the column(s): " + string.Join(", ", missing) +
                                                                  ". Download the template for the expected layout.");
            string Get(List<string> rec, string col) { var i = header.IndexOf(col); return i >= 0 && i < rec.Count ? rec[i].Trim() : ""; }

            var rows = new List<LegacyManifestRow>();
            for (var i = 1; i < records.Count; i++)
            {
                var rec = records[i];
                if (rec.All(string.IsNullOrWhiteSpace)) continue;
                var row = new LegacyManifestRow
                {
                    RowNo = i + 1,   // the line in the file (the header is line 1)
                    DocumentNumber = Get(rec, "DocumentNumber"),
                    Title = Get(rec, "Title"),
                    DocumentType = Get(rec, "DocumentType"),
                    Department = Get(rec, "Department"),
                    Revision = Get(rec, "Revision"),
                    Owner = Get(rec, "Owner"),
                    File = Get(rec, "File"),
                    Keywords = NullIfEmpty(Get(rec, "Keywords")),
                    LegacyReference = NullIfEmpty(Get(rec, "LegacyReference")),
                    TrainingRequired = Get(rec, "TrainingRequired").ToUpperInvariant() is "Y" or "YES" or "TRUE" or "1"
                };
                var eff = Get(rec, "EffectiveFrom");
                if (TryDate(eff, out var e)) row.EffectiveFrom = e; else if (eff.Length > 0) row.Problems.Add($"effective date '{eff}' is not a date (use yyyy-mm-dd)");
                var due = Get(rec, "PeriodicReviewDue");
                if (TryDate(due, out var d)) row.PeriodicReviewDue = d; else if (due.Length > 0) row.Problems.Add($"periodic review date '{due}' is not a date (use yyyy-mm-dd)");
                rows.Add(row);
            }
            if (rows.Count == 0) throw new LegacyImportException("manifest.csv has no rows under its header line.");
            if (rows.Count > 5000) throw new LegacyImportException("A batch holds at most 5 000 masters. Split the manifest.");
            return rows;
        }



        public async Task<long> RunSingleAsync(
            Stream document,
            string fileName,
            long fileSize,
            SingleLegacyImportForm form,
            bool signed,
            int actor,
            int? plantId,
            string? ip,
            CancellationToken ct)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            if (form == null)
                throw new ArgumentNullException(nameof(form));

            if (string.IsNullOrWhiteSpace(fileName))
                throw new LegacyImportException("The document file name is required.");

            if (fileSize <= 0)
                throw new LegacyImportException("The uploaded document is empty.");

            // Validate the selected access scope.
            if (form.AccessScope != "PLANT" && form.AccessScope != "GLOBAL")
                throw new LegacyImportException("Select Plant or Global access scope.");

            // Plant-scoped documents require a valid plant ID.
            if (form.AccessScope == "PLANT" && (!plantId.HasValue || plantId.Value <= 0))
                throw new LegacyImportException("Your account is not mapped to a valid plant.");

            // Global documents must not be associated with a specific plant.
            if (form.AccessScope == "GLOBAL")
                plantId = null;

            // Create the manifest row from the manually entered form.
            var row = new LegacyManifestRow
            {
                RowNo = 2,
                DocumentNumber = form.DocumentNumber?.Trim() ?? string.Empty,
                Title = form.Title?.Trim() ?? string.Empty,
                DocumentType = form.DocumentType?.Trim() ?? string.Empty,
                Department = form.Department?.Trim() ?? string.Empty,
                Revision = form.Revision?.Trim() ?? string.Empty,
                Owner = form.Owner?.Trim() ?? string.Empty,
                EffectiveFrom = form.EffectiveFrom ?? default,
                PeriodicReviewDue = form.PeriodicReviewDue,
                Keywords = string.IsNullOrWhiteSpace(form.Keywords)
                    ? null
                    : form.Keywords.Trim(),
                LegacyReference = string.IsNullOrWhiteSpace(form.LegacyReference)
                    ? null
                    : form.LegacyReference.Trim(),
                TrainingRequired = form.TrainingRequired,
                File = Path.GetFileName(fileName)
            };

            // Validate metadata using the existing bulk-import validation.
            var validationProblem =
                await _unitOfWork.Compliance.ValidateLegacyRowAsync(row);

            if (!string.IsNullOrWhiteSpace(validationProblem))
            {
                row.Problems.AddRange(
                    validationProblem
                        .Split("; ", StringSplitOptions.RemoveEmptyEntries)
                        .Distinct());
            }

            // Validate document format.
            try
            {
                await _fileStore.CheckFormatAsync(Path.GetFileName(fileName));
            }
            catch (FileStoreException ex)
            {
                row.Problems.Add(ex.Message);
            }

            // Validate file size.
            if (fileSize <= 0)
                row.Problems.Add($"File '{fileName}' is empty.");

            row.Problems = row.Problems.Distinct().ToList();

            // Save temporarily so we can calculate the hash and import the file
            // without loading the entire document into memory.
            var tempFile = Path.Combine(
                Path.GetTempPath(),
                "dms-legacy-single-" + Guid.NewGuid().ToString("N"));

            try
            {
                await using (var tempStream = new FileStream(
                    tempFile,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await document.CopyToAsync(tempStream, ct);
                }

                // Calculate SHA-256 for the batch record.
                string sha;

                await using (var hashStream = new FileStream(
                    tempFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    sha = Convert.ToHexString(
                            await SHA256.HashDataAsync(hashStream, ct))
                        .ToLowerInvariant();
                }

                // Get the signature meaning from configuration.
                var meaning = await _config.GetStringAsync(
                    "legacy.import.signatureMeaning",
                    "Approved by",
                    ct);

                // Create the legacy batch.
                var start = await _unitOfWork.Compliance.StartLegacyBatchAsync(
                    form.BatchReference,
                    form.DryRun,
                    form.Justification,
                    meaning,
                    Path.GetFileName(fileName),
                    sha,
                    1,
                    actor,
                    ip);

                if (!start.Success)
                    throw new LegacyImportException(start.Message);

                var batchId = start.Id;

                // If validation failed, record the failure and finish the batch.
                if (row.Problems.Count > 0)
                {
                    await _unitOfWork.Compliance.LogLegacyRowAsync(
                        batchId,
                        row.RowNo,
                        row.DocumentNumber,
                        false,
                        null,
                        string.Join("; ", row.Problems));

                    await _unitOfWork.Compliance.FinishLegacyBatchAsync(
                        batchId, actor, ip);

                    return batchId;
                }

                // Dry run: validate everything but do not store or import the file.
                if (form.DryRun)
                {
                    await _unitOfWork.Compliance.LogLegacyRowAsync(
                        batchId,
                        row.RowNo,
                        row.DocumentNumber,
                        true,
                        null,
                        $"Valid - would be imported as {row.DocumentNumber} " +
                        $"rev {row.Revision}, effective {row.EffectiveFrom:dd-MMM-yyyy}");

                    await _unitOfWork.Compliance.FinishLegacyBatchAsync(
                        batchId, actor, ip);

                    return batchId;
                }

                // Actual import.
                StoredFile? stored = null;

                try
                {
                    await using var content = new FileStream(
                        tempFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        81920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    // Store the document.
                    stored = await _fileStore.SaveCheckedAsync(
                        content,
                        fileSize,
                        Path.GetFileName(fileName),
                        "documents",
                        ct);

                    // Import with the selected scope and authenticated user's plant ID.
                    var result = await _unitOfWork.Compliance.ImportLegacyDocumentAsync(
                        batchId,
                        row,
                        stored,
                        signed,
                        actor,
                        ip,
                        form.AccessScope,
                        plantId);

                    if (!result.Success)
                    {
                        if (stored != null)
                        {
                            try
                            {
                                _fileStore.Archive(stored.RelativePath);
                            }
                            catch
                            {
                                // Preserve the original import failure.
                            }
                        }

                        await _unitOfWork.Compliance.LogLegacyRowAsync(
                            batchId,
                            row.RowNo,
                            row.DocumentNumber,
                            false,
                            null,
                            result.Message);

                        await _unitOfWork.Compliance.FinishLegacyBatchAsync(
                            batchId, actor, ip);

                        return batchId;
                    }

                    // Record successful import.
                    await _unitOfWork.Compliance.LogLegacyRowAsync(
                        batchId,
                        row.RowNo,
                        row.DocumentNumber,
                        true,
                        null,
                        "Legacy document imported successfully.");

                    await _unitOfWork.Compliance.FinishLegacyBatchAsync(
                        batchId, actor, ip);

                    // Trigger PDF rendition processing.
                    RenditionWorker.Nudge();

                    _logger.LogInformation(
                        "Single legacy document {DocumentNumber} imported successfully in batch {BatchId}.",
                        row.DocumentNumber,
                        batchId);

                    return batchId;
                }
                catch (Exception ex) when (
                    ex is FileStoreException or IOException or InvalidDataException)
                {
                    if (stored != null)
                    {
                        try
                        {
                            _fileStore.Archive(stored.RelativePath);
                        }
                        catch
                        {
                            // Preserve the original exception.
                        }
                    }

                    await _unitOfWork.Compliance.LogLegacyRowAsync(
                        batchId,
                        row.RowNo,
                        row.DocumentNumber,
                        false,
                        null,
                        ex.Message);

                    await _unitOfWork.Compliance.FinishLegacyBatchAsync(
                        batchId, actor, ip);

                    return batchId;
                }
            }
            finally
            {
                // Clean up the temporary upload.
                try
                {
                    if (File.Exists(tempFile))
                        File.Delete(tempFile);
                }
                catch (IOException)
                {
                    // Cleanup failure should not change the import result.
                }
            }
        }








        private async Task CheckRowsAsync(List<LegacyManifestRow> rows, Dictionary<string, ZipArchiveEntry> entries)
        {
            var numbers = rows.GroupBy(r => r.DocumentNumber.Trim(), StringComparer.OrdinalIgnoreCase)
                              .Where(g => g.Key.Length > 0 && g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var problem = await _unitOfWork.Compliance.ValidateLegacyRowAsync(row);
                if (!string.IsNullOrEmpty(problem)) row.Problems.AddRange(problem.Split("; "));
                if (numbers.Contains(row.DocumentNumber.Trim())) row.Problems.Add("the document number appears more than once in the manifest");
                if (string.IsNullOrWhiteSpace(row.File)) row.Problems.Add("no file named");
                else if (!entries.TryGetValue(Normalise(row.File), out var entry)) row.Problems.Add($"file '{row.File}' is not in the package");
                else
                {
                    try { await _fileStore.CheckFormatAsync(entry.Name); }
                    catch (FileStoreException ex) { row.Problems.Add(ex.Message); }
                    if (entry.Length == 0) row.Problems.Add($"file '{row.File}' is empty");
                }
                row.Problems = row.Problems.Distinct().ToList();
            }
        }

        private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private static bool TryDate(string s, out DateTime value)
            => DateTime.TryParseExact(s.Trim(), new[] { "yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "dd-MMM-yyyy", "d-MMM-yyyy", "dd.MM.yyyy", "yyyy/MM/dd" },
                                      CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

        /// <summary>RFC 4180: quoted fields, doubled quotes, line breaks inside quotes; comma or semicolon separated.</summary>
        internal static List<List<string>> ParseCsv(string text)
        {
            var firstLine = text.Split('\n')[0];
            var sep = firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';
            var records = new List<List<string>>();
            var record = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else field.Append(c);
                }
                else if (c == '"' && field.Length == 0) quoted = true;
                else if (c == sep) { record.Add(field.ToString()); field.Clear(); }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    record.Add(field.ToString()); field.Clear();
                    records.Add(record); record = new List<string>();
                }
                else field.Append(c);
            }
            if (field.Length > 0 || record.Count > 0) { record.Add(field.ToString()); records.Add(record); }
            return records;
        }
    }
}
