namespace BizHawkNetplay.Core.Session;

/// <summary>
/// What a joiner is trusting when it loads its host's savestate, said once, in the session log.
///
/// <b>Why this is a message and not a fix.</b> A savestate is a trusted-input format all the way
/// down. On a waterbox core — Snes9x, bsnes, melonDS, Ares64, the Nyma cores, most of the modern
/// set — <c>IStatable.LoadStateBinary</c> reaches <c>wbx_load_state</c>, which restores guest memory
/// contents, guest page protection bits and the guest stack pointer from the stream. Those are the
/// inputs to native execution and the sender chooses all of them. Upstream is not wrong to work
/// that way; every decision there is correct for a file off your own disk. This tool is what turns
/// it into a remote input, and no wire change here can make the parser safe.
///
/// <b>What the wire change DID fix.</b> Before v0.31.0 anyone on the path could inject a Resync and
/// reach that parser without knowing the password. Every control frame after AUTH now carries a
/// MAC, so with a password the sender is provably the host. That narrows the exposure from "anyone
/// on your network" to "the person you joined" — which is a real narrowing, and is exactly why the
/// message below distinguishes the two cases instead of issuing one blanket warning.
///
/// <b>What is left, stated plainly.</b> With a password: you are trusting the host, and nobody else
/// can reach you. With none: the key derives from public nonces, so integrity holds only against
/// blind off-path injection, and joining a stranger is as trusting as it sounds.
///
/// Deliberately NOT a refusal or a prompt. There is no safe alternative to offer — a joiner that
/// declines the state cannot join — so a dialog would be a choice between joining and not joining,
/// dressed up as a security control. Saying it once, accurately, is the honest thing available.
/// </summary>
public static class StateImportTrust
{
    /// <summary>
    /// The line to log the first time this joiner loads a host's state, or null when there is
    /// nothing to say — which is the host's own case, since a host never imports a peer's state.
    /// </summary>
    public static string? Advisory(bool isHost, bool hasPassword)
    {
        if (isHost) return null;   // every state-bearing path is joiner-side; see the class remarks
        return hasPassword
            ? "note: joining loads the host's savestate into your emulator, and a savestate is a " +
              "format the core trusts completely — it can set memory, page permissions and the " +
              "stack pointer of the emulated machine. Your session has a password, so only the " +
              "host can send you one. You are trusting them, and nobody else can reach you."
            : "note: joining loads the host's savestate into your emulator, and a savestate is a " +
              "format the core trusts completely — it can set memory, page permissions and the " +
              "stack pointer of the emulated machine. This session has NO password, so control " +
              "frames are protected only against blind off-path injection. Set a password on both " +
              "sides if you are not on a network you trust, and only join hosts you know.";
    }
}
