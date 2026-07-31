using System;
using System.IO;
using BizHawkNetplay.Core.Diag;

namespace BizHawkNetplay.Tool;

/// <summary>
/// Where this tool keeps its logs, and how many. The writing itself is
/// <see cref="RotatingLogFile"/>; all that is decided here is the folder, the name and the depth —
/// the parts that are this application's business rather than a log file's.
/// </summary>
internal static class SessionLog
{
    /// <summary>Ten launches is a few days of ordinary use and a handful of megabytes at most —
    /// enough that "send me the one from last night" still works.</summary>
    private const int KeepFiles = 10;

    /// <summary>Beside settings.txt, which is already where this tool's per-user state lives.</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BizHawkNetplay", "logs");

    /// <summary>Prepare a log. No file exists until the tool activates it — see
    /// <see cref="RotatingLogFile.Activate"/> for why an idle launch must not write one.</summary>
    public static RotatingLogFile Prepare(string header) =>
        RotatingLogFile.Deferred(Folder, "netplay", KeepFiles, header);
}
