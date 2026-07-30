// Enables C# 9 `init` accessors and records when targeting netstandard2.0 / net48,
// whose BCLs predate this compiler-required type. Internal so it never clashes with
// the real one on modern targets that reference this library.
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit { }
