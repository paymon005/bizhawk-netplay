using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace BizHawkNetplay.Core.Session;

/// <summary>
/// Which discs are in the drive — all of them, in order.
///
/// <b>What was wrong.</b> A disc-based session identified itself by <c>GameInfo.Hash</c>, and for a
/// multi-disc set BizHawk derives that from disc one. Two players could therefore hold the same
/// disc 1 and different disc 2s, pass the handshake, play, and diverge the moment the game asked
/// for the second disc — with every identity check having said they matched.
///
/// <b>Order is part of the identity, deliberately.</b> Unlike the sibling machine domains, where a
/// differing enumeration order would be a false alarm, the disc order here IS meaningful: it is the
/// order the core was handed them, so it is what a disc-swap request indexes into. Two players whose
/// discs are the same set in a different order really would load different content on a swap, and
/// refusing them is right.
///
/// <b>Per-disc hashes travel, not just the digest.</b> The digest alone would say "your discs differ"
/// and leave two people comparing three files by hand. The point of the whole finding is that the
/// difference is invisible, so the refusal names the disc.
/// </summary>
public static class DiscIdentity
{
    /// <summary>Most discs a set may carry on the wire. Beyond this the digest still covers every
    /// disc — only the per-disc list that explains a mismatch is truncated, because the list is
    /// untrusted input and a peer should not be able to make the handshake body arbitrarily large.</summary>
    public const int MaxNamedDiscs = 16;

    /// <summary>
    /// Fold an ordered list of per-disc hashes into one comparable digest. Empty for a session with
    /// no discs at all, which is most of them, and which compares equal between two such peers.
    ///
    /// The count is hashed as well as the contents. Without it a set of three discs whose hashes
    /// happened to concatenate the same as a set of two could collide — cheap to prevent, and the
    /// kind of thing that is obvious only after it happens.
    /// </summary>
    public static string Digest(IReadOnlyList<string>? discHashes)
    {
        if (discHashes == null || discHashes.Count == 0) return "";
        var sb = new StringBuilder();
        sb.Append(discHashes.Count).Append('\n');
        for (int i = 0; i < discHashes.Count; i++) sb.Append(i).Append(':').Append(discHashes[i] ?? "").Append('\n');
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return BitConverter.ToString(bytes, 0, 8).Replace("-", string.Empty);
    }

    /// <summary>
    /// Why two disc sets differ, naming the disc — or null when they match.
    ///
    /// Takes the per-disc lists rather than the digests because a digest cannot explain itself, and
    /// "your discs differ" for a three-disc game is the same unhelpful answer the missing check gave.
    /// </summary>
    public static string? Mismatch(IReadOnlyList<string>? local, IReadOnlyList<string>? remote)
    {
        int mine = local?.Count ?? 0;
        int theirs = remote?.Count ?? 0;

        if (mine != theirs)
            return $"disc count differs — you have {Describe(mine)} loaded, the other player has " +
                   $"{Describe(theirs)}. For a multi-disc game both players must load the whole set, " +
                   "in the same order (an .m3u playlist does this).";

        for (int i = 0; i < mine; i++)
        {
            string a = local![i] ?? "", b = remote![i] ?? "";
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) continue;
            // Named by position, because that is what the game asks for on a swap and what the
            // player can act on. Disc 1 matching while disc 2 does not is the exact case that used
            // to pass every check and then desync on the swap.
            return $"disc {i + 1} of {mine} differs between you and the other player" +
                   (i > 0 ? " — the earlier discs match, which is why nothing else caught this" : "") +
                   ". Both players need identical rips of every disc in the set.";
        }
        return null;
    }

    private static string Describe(int count) => count == 1 ? "1 disc" : $"{count} discs";
}
