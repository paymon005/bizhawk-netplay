using System;
using System.Text;

namespace BizHawkNetplay.Core.Session;

/// <summary>
/// Which BizHawk this actually is — precisely enough that two peers running different ones cannot
/// agree that they match.
///
/// The handshake compared <c>CoreVersion</c>, an assembly version read off the core's DLL. On a
/// release build that is "2.11.1.0" for every 2.11.1 there has ever been: a stock release, a
/// developer build off a branch, a fork with a patched core, and a custom build someone published
/// all produce the identical string. Two peers whose emulators differ in ways that change emulation
/// therefore shook hands, started, and desynced — and the desync named the game rather than the
/// builds, because nothing in the session knew they were different.
///
/// BizHawk has always known. <c>VersionInfo</c> carries the release version, the git branch and
/// commit hash it was built from, whether it is a developer build, and any custom-build string from
/// <c>dll/custombuild.txt</c>. This assembles those into one comparable line, plus the process
/// architecture, which is not in VersionInfo and matters because an x86 and an x64 build of the same
/// commit are different programs.
///
/// Formatting lives here rather than in the tool so the parts, the order and the separator are one
/// decision: two peers assembling the same facts differently would refuse each other forever.
/// </summary>
public static class BuildIdentity
{
    /// <summary>The separator between parts. Chosen because none of the parts can contain it — a
    /// version is digits and dots, a hash is hex, a branch name cannot hold whitespace, and the
    /// custom-build string is sanitised below.</summary>
    private const char Sep = '|';

    /// <summary>
    /// Assemble the fingerprint. Every argument may be null or empty: an older or unusual build that
    /// cannot report a git hash still produces a stable line, and two such peers still match each
    /// other — a missing fact must not become a mismatch, only a weaker guarantee.
    /// </summary>
    public static string Format(string? mainVersion, string? gitHash, string? branch,
        bool developerBuild, string? customBuild, bool is64Bit)
    {
        var sb = new StringBuilder();
        sb.Append(Clean(mainVersion, 32, "?"));
        // The short hash is enough to distinguish builds and keeps the handshake line small; the
        // full hash would be forty characters repeated in every log line that quotes this.
        sb.Append(Sep).Append(Clean(Shorten(gitHash), 12, "?"));
        sb.Append(Sep).Append(Clean(branch, 40, "?"));
        sb.Append(Sep).Append(developerBuild ? "dev" : "rel");
        sb.Append(Sep).Append(is64Bit ? "x64" : "x86");
        var custom = Clean(customBuild, 40, "");
        if (custom.Length > 0) sb.Append(Sep).Append(custom);
        return sb.ToString();
    }

    /// <summary>
    /// Why two builds differ, phrased so the reader knows what to do — or null when they match.
    ///
    /// The three cases want different advice. Same version and different commit is the one worth
    /// spelling out, because both players believe they are on "2.11.1" and the version number is
    /// what they will compare when they check by hand.
    /// </summary>
    public static string? Mismatch(string? local, string? remote)
    {
        local ??= "";
        remote ??= "";
        if (string.Equals(local, remote, StringComparison.Ordinal)) return null;

        var mine = local.Split(Sep);
        var theirs = remote.Split(Sep);
        string prefix = $"BizHawk build mismatch (yours {local}; theirs {remote}) — ";

        if (mine.Length > 4 && theirs.Length > 4
            && !string.Equals(mine[4], theirs[4], StringComparison.Ordinal))
            return prefix + "one of you is running the 32-bit build and the other the 64-bit one. " +
                   "They emulate differently. Both players need the same architecture.";

        if (mine.Length > 0 && theirs.Length > 0
            && !string.Equals(mine[0], theirs[0], StringComparison.Ordinal))
            return prefix + "you are on different BizHawk releases. Both players need the same one.";

        return prefix + "you are both on the same BizHawk release but built from different commits " +
               "(a developer build, a fork, or a custom build). The version number matching is not " +
               "enough — the emulator itself differs, which desyncs. Both players need the same " +
               "download.";
    }

    private static string? Shorten(string? hash) =>
        hash != null && hash.Length > 9 ? hash.Substring(0, 9) : hash;

    /// <summary>
    /// Reduce an untrusted string to something that cannot break the line format or the log.
    ///
    /// <c>CustomBuildString</c> is the first line of a file anyone can put in their BizHawk folder,
    /// so it reaches here as arbitrary text — the separator, a newline, or a few kilobytes of it.
    /// </summary>
    private static string Clean(string? value, int maxLength, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var sb = new StringBuilder(Math.Min(value!.Length, maxLength));
        foreach (var c in value)
        {
            if (sb.Length >= maxLength) break;
            sb.Append(c == Sep || char.IsControl(c) || char.IsWhiteSpace(c) ? '_' : c);
        }
        return sb.Length == 0 ? fallback : sb.ToString();
    }
}
