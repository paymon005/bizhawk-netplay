using System;

namespace BizHawkNetplay.Core.Tests;

/// <summary>
/// Stand-ins for BCL surface that .NET Framework 4.8 does not have.
///
/// The tests run on <c>net10.0</c> AND <c>net48</c>, because BizHawkNetplay.Core is netstandard2.0
/// and BizHawk executes it inside a .NET Framework 4.8 host — so net48 is the runtime it actually
/// ships on, and testing only on net10.0 leaves the production runtime unexercised. That is not
/// hypothetical: the savestate transfer path now runs DeflateStream, whose implementation differs
/// between .NET Framework and .NET Core.
///
/// Rather than fence off the affected assertions with #if, the handful of calls that reached for
/// .NET Core-only APIs use these. They are test-only: nothing in Core needs them, which the first
/// net48 build confirmed by compiling Core clean while only test code failed.
/// </summary>
internal static class Net48Compat
{
    /// <summary>Replaces the range operator (<c>buffer[0..3]</c>), which needs System.Index and
    /// System.Range — neither of which exists on net48 whatever the language version says.</summary>
    public static T[] Slice<T>(T[] source, int start, int length)
    {
        var result = new T[length];
        Array.Copy(source, start, result, 0, length);
        return result;
    }

    /// <summary>Replaces <c>double.IsFinite</c>, which is .NET Core only.</summary>
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
