using System;

namespace BizHawkNetplay.Core.Emu;

/// <summary>
/// A savestate could not be loaded back into the core.
///
/// Distinct from an ordinary session error because of where it leaves the emulator: the rollback
/// paths save the live position, jump somewhere else, and put it back. If the putting-back fails,
/// the core is standing on a frame the session does not think it is on, and every subsequent frame
/// diverges from every peer. Nothing downstream can recover from that, so it is named rather than
/// folded into the generic error text — the player needs to be told the core failed, not the network.
/// </summary>
public sealed class StateRestoreFailedException : Exception
{
    public StateRestoreFailedException(string message)
        : base("core state restore failed — session cannot continue: " + message) { }

    public StateRestoreFailedException(string message, Exception inner)
        : base("core state restore failed — session cannot continue: " + message, inner) { }
}
