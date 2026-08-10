using System;

namespace BizHawkNetplay.Core.Emu;

/// <summary>
/// What a core's <c>DeterministicEmulation</c> flag actually means for netplay.
///
/// BizHawk documents the flag as a contract: "If a core wants to implement non-deterministic
/// features (like speed hacks, frame-skipping), it must be done only when this flag is false." So a
/// false flag is permission, not a diagnosis — which is why this tool treated it as advisory and
/// then, for a long time, told the handshake a flat <c>true</c> regardless of what the core said.
///
/// Reading the 2.11.1 cores rather than reasoning about the doc comment turns that into a much
/// sharper picture. Nearly every core that computes the flag computes it the same way:
///
///     DeterministicEmulation = lp.DeterministicEmulationRequested || !_syncSettings.UseRealTime;
///
/// and then seeds its real-time clock from <c>DateTime.Now</c> when the result is false. Ares64,
/// BSNES, MelonDS, MAME, AppleII, TIC80, SameBoy, UAE and the Nyma cores all do some version of it.
/// Two peers starting a session seconds apart therefore begin with different clocks, and any game
/// that reads the clock diverges — not eventually, but from the first frame that looks. MAME goes
/// further and registers a different set of memory domains when the flag is false, which changes
/// what the desync checksum is even looking at.
///
/// Nothing loads a ROM here — the player does, in EmuHawk, without a movie — so
/// <c>DeterministicEmulationRequested</c> is false and the flag reduces to the core's own sync
/// settings. Those ARE compared by the handshake, but agreement does not help: two peers who agree
/// on <c>UseRealTime = true</c> agree on seeding from two different wall clocks.
///
/// Hence: the flag decides, and the exceptions are named.
///
/// The exception list is not just N64. A second group reaches false by accident — cores that
/// declare the flag as an auto-property and never assign it, so it defaults to false and stays
/// there while nothing reads it. A7800Hawk is the one this was found on, when a player was told to
/// turn off a setting the 7800 core does not have and had no way forward at all. See
/// <see cref="InertCores"/> for the group and how each was verified.
/// </summary>
public static class DeterminismPolicy
{
    /// <summary>
    /// Mupen64Plus reports <c>DeterministicEmulation => false</c> as a hardcoded expression, and
    /// nothing anywhere in the N64 core reads it back — verified against 2.11.1, where the only
    /// occurrence in the whole <c>Consoles/Nintendo/N64</c> tree is the declaration itself. There is
    /// no clock to seed and no hack to enable, so the flag is inert there.
    ///
    /// This is why the exception is by name rather than by "false is probably fine". It was fine for
    /// N64, which is what this tool is mostly used for, and that experience is what made the blanket
    /// <c>true</c> look harmless for every other core too.
    /// </summary>
    public const string MupenCoreName = "Mupen64Plus";

    /// <summary>
    /// Cores whose false flag drives nothing, each verified by reading the 2.11.1 core rather than
    /// by assuming false is harmless.
    ///
    /// Mupen64Plus declares its false deliberately. The rest reach it by accident, and share one
    /// signature: they declare <c>public bool DeterministicEmulation { get; set; }</c> and then
    /// never assign it — not in the constructor, not from sync settings, and not from EmuHawk, whose
    /// only assignments anywhere are to the CPCHawk and ZXHawk *sync settings* objects. So the
    /// property sits at C#'s default of false for the life of the core while nothing reads it back.
    /// The tell is GBHawk, which sets <c>DeterministicEmulation = true</c> in its constructor and is
    /// therefore fine; its Link variants are the same code with that line missing.
    ///
    /// None of these cores contains a <c>DateTime</c>, a real-time-clock option, or a "Use Real
    /// Time" setting — the 7800 and the Odyssey 2 have no RTC hardware to seed in the first place.
    ///
    /// Keeping them listed by name rather than pattern-matching "Hawk" is deliberate: the next core
    /// to report false should have to be read before it is trusted, which is the whole point.
    /// </summary>
    private static readonly string[] InertCores =
    {
        MupenCoreName,
        "A7800Hawk",    // Atari 7800 — two-player, and the console this was first hit on
        "O2Hawk",       // Odyssey 2 — also two-player
        "VectrexHawk",
        "GBHawkLink",   // the Link cores are GBHawk minus its constructor's assignment
        "GBHawkLink3x",
        "GBHawkLink4x",
        "GGHawkLink",
    };

    /// <summary>
    /// The two cores that genuinely derive the flag from a sync setting of their own — and call it
    /// "Deterministic Emulation", defaulting to on. Naming "Use Real Time" at these players sends
    /// them looking for a setting their core does not have, so they get the remedy that exists.
    /// </summary>
    private static readonly string[] DeterminismIsItsOwnSetting = { "CPCHawk", "ZXHawk" };

    /// <summary>
    /// Whether the session may proceed given what the core reports.
    ///
    /// A core that reports true qualifies. A core that reports false qualifies only if it is a named
    /// exception — and "opaque" is not an exception: Libretro also hardcodes false, but a Libretro
    /// core is arbitrary third-party code whose clock handling nobody here can inspect, so it is
    /// refused rather than assumed.
    /// </summary>
    public static bool Qualifies(bool coreReportsDeterministic, string? coreName) =>
        coreReportsDeterministic || IsInertWhenFalse(coreName);

    /// <summary>True for cores whose false flag is known to drive nothing.</summary>
    public static bool IsInertWhenFalse(string? coreName) => Names(InertCores, coreName);

    private static bool Names(string[] cores, string? coreName)
    {
        if (string.IsNullOrEmpty(coreName)) return false;
        foreach (string name in cores)
            if (string.Equals(coreName, name, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Why the session is being refused, and what to change to make it work — in the player's terms,
    /// because "the core is not deterministic" is a true sentence that tells nobody what to do.
    ///
    /// The remedy is nearly always the same setting under a slightly different name, so it is named
    /// generically and the core is named specifically. Returns null when nothing is wrong.
    /// </summary>
    public static string? Refusal(bool coreReportsDeterministic, string? coreName)
    {
        if (Qualifies(coreReportsDeterministic, coreName)) return null;
        string core = string.IsNullOrEmpty(coreName) ? "this core" : coreName!;
        string remedy = Names(DeterminismIsItsOwnSetting, coreName)
            ? "Open the core's sync settings and turn ON \"Deterministic Emulation\""
            : "Open the core's sync settings and turn OFF \"Use Real Time\" (some cores call it a " +
              "real-time clock option)";
        return $"{core} is not running deterministically, which for most cores means its clock is " +
               "seeded from this machine's wall clock — two players would start with different " +
               $"times and any game that reads the clock would drift apart. {remedy}, then reload " +
               "the ROM and try again. Both players must do it.";
    }
}
