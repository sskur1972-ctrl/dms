using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace DMS.Web.Caching
{
    public sealed class RedisDocumentCache : IDmsDocumentCache
    {
        private readonly IDistributedCache _cache;
        private readonly DmsRedisOptions _options;
        private readonly ILogger<RedisDocumentCache> _logger;

        public RedisDocumentCache(
            IDistributedCache cache,
            IOptions<DmsRedisOptions> options,
            ILogger<RedisDocumentCache> logger)
        {
            _cache = cache;
            _options = options.Value;
            _logger = logger;
        }

        public bool Enabled => _options.Enabled;

        public bool CanCachePdf(long sizeBytes)
        {
            return Enabled
                   && sizeBytes > 0
                   && sizeBytes <= _options.MaxPdfCacheBytes;
        }

        public async Task<byte[]?> GetPdfAsync(
            long documentVersionId,
            string sha256,
            CancellationToken ct = default)
        {
            if (!Enabled)
                return null;

            var key = BuildKey(documentVersionId, sha256);

            var bytes = await _cache.GetAsync(key, ct);

            if (bytes is not null)
            {
                _logger.LogDebug(
                    "Redis cache HIT for document version {DocumentVersionId}",
                    documentVersionId);
            }
            else
            {
                _logger.LogDebug(
                    "Redis cache MISS for document version {DocumentVersionId}",
                    documentVersionId);
            }

            return bytes;
        }

        public async Task SetPdfAsync(
            long documentVersionId,
            string sha256,
            byte[] content,
            CancellationToken ct = default)
        {
            if (!CanCachePdf(content.LongLength))
                return;

            var key = BuildKey(documentVersionId, sha256);

            await _cache.SetAsync(
                key,
                content,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow =
                        TimeSpan.FromMinutes(_options.PdfExpirationMinutes)
                },
                ct);

            _logger.LogDebug(
                "Redis cache SET for document version {DocumentVersionId}, size {SizeBytes}",
                documentVersionId,
                content.LongLength);
        }

        public async Task RemovePdfAsync(
            long documentVersionId,
            string sha256,
            CancellationToken ct = default)
        {
            if (!Enabled)
                return;

            var key = BuildKey(documentVersionId, sha256);

            await _cache.RemoveAsync(key, ct);

            _logger.LogDebug(
                "Redis cache REMOVE for document version {DocumentVersionId}",
                documentVersionId);
        }

        private static string BuildKey(
            long documentVersionId,
            string sha256)
        {
            return $"dms:pdf:{documentVersionId}:{sha256}";
        }
    }
}
