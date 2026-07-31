using System;
using System.IO;
using System.Linq;
using BizHawkNetplay.Core.Diag;
using Xunit;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// The log file exists so someone can send their session afterwards, which puts three properties
/// under test: what it writes has to survive being read while still open, what it DELETES has to be
/// the right files, and a launch that did nothing has to leave the folder exactly as it found it.
/// </summary>
public class RotatingLogFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bhnp-logtest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string[] LogNames() =>
        Directory.GetFiles(_dir, "*.log").Select(Path.GetFileName)
                 .OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    private void SeedLogs(params string[] stamps)
    {
        Directory.CreateDirectory(_dir);
        foreach (var stamp in stamps)
            File.WriteAllText(Path.Combine(_dir, $"netplay-{stamp}.log"), stamp);
    }

    private static string ReadWhileOpen(string path)
    {
        using var reader = new StreamReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return reader.ReadToEnd();
    }

    // ---- the idle-launch guarantee ----------------------------------------------------

    /// <summary>
    /// Opening the tool and closing it again is not a session. If every launch wrote a file, a
    /// handful of idle open/close cycles would rotate away every log anyone actually wanted —
    /// quietly, and exactly when someone had been asked to go and find one.
    /// </summary>
    [Fact]
    public void ALaunchThatNeverActivatesWritesNothingAndDeletesNothing()
    {
        SeedLogs("20260101-000000", "20260102-000000", "20260103-000000");
        var before = LogNames();

        var log = RotatingLogFile.Deferred(_dir, "netplay", keepFiles: 2, header: "HEADER");
        log.Write("chatter nobody asked for");
        log.Flush();
        log.Dispose();

        Assert.False(log.IsOpen);
        Assert.Null(log.Path);
        Assert.Equal(before, LogNames()); // not one file added, not one pruned
    }

    [Fact]
    public void WhatWasLoggedBeforeActivationStillEndsUpInTheFile()
    {
        // The run-up — which core loaded, what the probe measured — is context for the failure that
        // follows it, so deferring the file must not cost it.
        using var log = RotatingLogFile.Deferred(_dir, "netplay", 10, "HEADER");
        log.Write("core loaded: GPGX");
        log.Write("probe says depth 20");
        Assert.True(log.Activate());
        log.Write("connecting…");
        log.Flush();

        var text = ReadWhileOpen(log.Path!);
        Assert.Contains("HEADER", text);
        Assert.Contains("core loaded: GPGX", text);
        Assert.Contains("probe says depth 20", text);
        Assert.Contains("connecting…", text);
        // ...and in the order they happened.
        Assert.True(text.IndexOf("core loaded", StringComparison.Ordinal)
                    < text.IndexOf("connecting", StringComparison.Ordinal));
    }

    [Fact]
    public void ActivatingTwiceKeepsTheSameFile()
    {
        using var log = RotatingLogFile.Deferred(_dir, "netplay", 10);
        Assert.True(log.Activate());
        var first = log.Path;
        Assert.True(log.Activate());   // e.g. host, disconnect, host again
        Assert.Equal(first, log.Path);
        Assert.Single(LogNames());
    }

    // ---- writing -----------------------------------------------------------------------

    [Fact]
    public void WhatIsWrittenIsReadableWhileTheFileIsStillOpen()
    {
        using var log = RotatingLogFile.Deferred(_dir, "netplay", 10, "HEADER");
        log.Activate();
        log.Write("first");
        log.Write("second");
        log.Flush();

        // Opened for sharing on purpose: someone asked for their log should not have to close the
        // emulator to send it, and that is exactly when they are asked.
        var text = ReadWhileOpen(log.Path!);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }

    [Fact]
    public void TheHeaderReachesDiskBeforeAnythingElseIsWritten()
    {
        // A crash during the very first session still has to leave behind which build produced it.
        using var log = RotatingLogFile.Deferred(_dir, "netplay", 10, "v0.24.0");
        log.Activate();
        Assert.Contains("v0.24.0", ReadWhileOpen(log.Path!));
    }

    // ---- rotation ----------------------------------------------------------------------

    [Fact]
    public void OldLogsArePrunedToTheKeepCountWithTheNewestSurviving()
    {
        SeedLogs("20260101-000000", "20260102-000000", "20260103-000000",
                 "20260104-000000", "20260105-000000", "20260106-000000");

        using var log = RotatingLogFile.Deferred(_dir, "netplay", keepFiles: 3);
        log.Activate();

        // Three total, counting the one just opened — so two survivors, and they are the newest two.
        var names = LogNames();
        Assert.Equal(3, names.Length);
        Assert.Contains("netplay-20260105-000000.log", names);
        Assert.Contains("netplay-20260106-000000.log", names);
        Assert.DoesNotContain("netplay-20260101-000000.log", names);
        Assert.Contains(Path.GetFileName(log.Path)!, names);
    }

    /// <summary>
    /// Age is read off the NAME, not the filesystem timestamp. Copying a folder, restoring a backup
    /// or syncing a roaming profile rewrites every mtime — and pruning by mtime would then delete
    /// whichever logs happened to be touched least recently, which is not the same question.
    /// </summary>
    [Fact]
    public void PruningIgnoresFileTimestampsAndGoesByTheNameTheLaunchWroteIn()
    {
        SeedLogs("20260101-000000", "20260109-000000");
        var oldest = Path.Combine(_dir, "netplay-20260101-000000.log");
        var newest = Path.Combine(_dir, "netplay-20260109-000000.log");
        // Invert the mtimes: the oldest launch now looks freshly written.
        File.SetLastWriteTimeUtc(oldest, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newest, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        using var log = RotatingLogFile.Deferred(_dir, "netplay", keepFiles: 2);
        log.Activate();

        var names = LogNames();
        Assert.Equal(2, names.Length);
        Assert.Contains("netplay-20260109-000000.log", names); // kept: newest by name
        Assert.DoesNotContain("netplay-20260101-000000.log", names);
    }

    [Fact]
    public void OtherFilesInTheFolderAreLeftAlone()
    {
        SeedLogs("20260101-000000", "20260102-000000", "20260103-000000",
                 "20260104-000000", "20260105-000000");
        File.WriteAllText(Path.Combine(_dir, "settings.txt"), "not ours");
        File.WriteAllText(Path.Combine(_dir, "other-20260101-000000.log"), "another tool's");

        using var log = RotatingLogFile.Deferred(_dir, "netplay", keepFiles: 2);
        log.Activate();

        Assert.True(File.Exists(Path.Combine(_dir, "settings.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "other-20260101-000000.log")));
        Assert.Equal(2, Directory.GetFiles(_dir, "netplay-*.log").Length);
    }

    // ---- failing safely -----------------------------------------------------------------

    /// <summary>
    /// A log that cannot be created must be a no-op, not an exception. This is wired into the frame
    /// path and a session must never fail because its diagnostics could not.
    /// </summary>
    [Fact]
    public void AnUnopenableLogSwallowsEverythingInsteadOfThrowing()
    {
        // A path that cannot be a directory, because a file of that name is in the way.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocker, "x");

        using var log = RotatingLogFile.Deferred(blocker, "netplay", 10, "header");

        Assert.False(log.Activate());
        Assert.False(log.IsOpen);
        Assert.Null(log.Path);
        log.Write("this goes nowhere");   // must not throw
        log.Flush();
    }

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var log = RotatingLogFile.Deferred(_dir, "netplay", 10);
        log.Activate();
        log.Write("line");
        log.Dispose();
        log.Dispose();
        log.Write("after disposal"); // still a no-op rather than an ObjectDisposedException
        Assert.False(log.Activate()); // and it cannot rise from the dead and prune something
    }

    /// <summary>
    /// A window left open all day with verbose logging on and no session must not grow a buffer for
    /// ever. What survives is the RECENT context, and the file says how much was dropped rather than
    /// presenting a gap as continuity.
    /// </summary>
    [Fact]
    public void TheUnactivatedBufferIsBoundedAndSaysWhatItDropped()
    {
        using var log = RotatingLogFile.Deferred(_dir, "netplay", 10);
        for (int i = 0; i < 1500; i++) log.Write($"line {i}");
        log.Activate();
        log.Flush();

        var text = ReadWhileOpen(log.Path!);
        Assert.Contains("line 1499", text);          // the newest survived
        Assert.DoesNotContain("line 0\n", text);     // the oldest did not
        Assert.Contains("dropped before this file was created", text);
    }
}
