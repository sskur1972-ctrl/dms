namespace DMS.Web.Caching
{
    public interface IDmsDocumentCache
    {
        bool Enabled { get; }

        bool CanCachePdf(long sizeBytes);

        Task<byte[]?> GetPdfAsync(
            long documentVersionId,
            string sha256,
            CancellationToken ct = default);

        Task SetPdfAsync(
            long documentVersionId,
            string sha256,
            byte[] content,
            CancellationToken ct = default);

        Task RemovePdfAsync(
            long documentVersionId,
            string sha256,
            CancellationToken ct = default);
    }
}
