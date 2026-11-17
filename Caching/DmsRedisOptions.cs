namespace DMS.Web.Caching
{
    public sealed class DmsRedisOptions
    {
        public bool Enabled { get; set; } = true;

        public string InstanceName { get; set; } = "dms:";

        public int PdfExpirationMinutes { get; set; } = 10;

        public long MaxPdfCacheBytes { get; set; } = 32 * 1024 * 1024;
    }
}

