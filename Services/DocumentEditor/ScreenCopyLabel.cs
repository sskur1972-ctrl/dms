namespace DMS.Web.Services.DocumentEditor;

public static class ScreenCopyLabel
{
    public static string Resolve(string? configured, bool effective, bool historicalOrObsolete)
    {
        // Use the same state watermark as the controlled rendition, without personal metadata.
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var label = configured.Trim();
            return label.ToUpperInvariant() switch
            {
                "DRAFT COPY" => "Draft Copy", "MASTER COPY" => "Master Copy",
                "DUPLICATE COPY" => "Duplicate Copy", _ => label
            };
        }
        return historicalOrObsolete ? "Duplicate Copy" : effective ? "Master Copy" : "Draft Copy";
    }
}
