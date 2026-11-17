//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
/* Office editor (Collabora Online / any WOPI client) - contracts, as in Bcl.Dms. */
namespace DMS.Web.Services.Office;

public interface IWopiTokenProtector
{
    string Protect(WopiTokenDto token);
    WopiTokenDto? Unprotect(string? protectedToken);
}

public interface IWopiDiscoveryClient
{
    Task<string?> GetActionUrlAsync(string extension, string action, bool refresh = false, CancellationToken ct = default);
    Task<(bool Reachable, string? Detail)> ProbeAsync(CancellationToken ct = default);
}

public interface IWopiPreflightService
{
    Task<WopiPreflightReportDto> RunAsync(CancellationToken ct = default);
}
