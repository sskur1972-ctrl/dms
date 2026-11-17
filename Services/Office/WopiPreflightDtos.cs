//Copyright(c) 2023 - 2026 Propix Technologies Pvt. Ltd. All Rights Reserved.
// Ported from Bcl.Dms (namespace changed; logic unchanged unless marked "DMS_V2").
using DMS.Web.Services;

namespace DMS.Web.Services.Office;

/// <summary>
/// One thing that was checked before anybody opens a document in the editor,
/// and what it said.
/// </summary>
public sealed class WopiPreflightCheckDto
{
    public string Name { get; set; } = string.Empty;

    public WopiPreflightOutcome Outcome { get; set; }

    /// <summary>What was found. Written for the person configuring it, not for a log.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>What to do about it, when there is something to do.</summary>
    public string? Remedy { get; set; }
}

public enum WopiPreflightOutcome
{
    /// <summary>Checked and correct.</summary>
    Pass,

    /// <summary>Works, but will bite somebody later.</summary>
    Warn,

    /// <summary>The editor will not work until this is fixed.</summary>
    Fail,

    /// <summary>Not checked, because something it depends on failed first.</summary>
    Skipped
}

/// <summary>
/// The result of the whole preflight.
///
/// <para><b>Why this exists</b></para>
/// The two ends of a WOPI integration have to be able to reach EACH OTHER, and
/// nothing on either screen tells you when they cannot. The browser loads the
/// editor from the editor server; the editor server then calls back into this
/// application for the file. So the failure everybody hits first is a
/// <c>publicBaseUrl</c> the editor cannot resolve - most often <c>localhost</c>,
/// which inside a container means the container - or an editor whose own
/// allow-list does not include this host. Both produce the same symptom: a grey
/// frame, and no message anywhere.
///
/// <para>
/// So this checks what can be checked from the server's own network position,
/// and says plainly which of the remaining failures it cannot see.
/// </para>
/// </summary>
public sealed class WopiPreflightReportDto
{
    public IReadOnlyList<WopiPreflightCheckDto> Checks { get; set; } = Array.Empty<WopiPreflightCheckDto>();

    public bool Enabled { get; set; }
    public string? EditorName { get; set; }
    public string? DiscoveryUrl { get; set; }
    public string? PublicBaseUrl { get; set; }

    /// <summary>The editor URL for a .docx edit action, as discovery gave it.</summary>
    public string? DocxEditUrl { get; set; }

    /// <summary>The editor URL for a .xlsx edit action, when the engine offers one.</summary>
    public string? XlsxEditUrl { get; set; }

    public bool CanOpenADocument => Checks.All(c => c.Outcome != WopiPreflightOutcome.Fail);

    public int Failures => Checks.Count(c => c.Outcome == WopiPreflightOutcome.Fail);
    public int Warnings => Checks.Count(c => c.Outcome == WopiPreflightOutcome.Warn);
}
