namespace System.Runtime.CompilerServices;

// Compiler-support shim, not part of the vendored library.
//
// MicroMoth.cs, alongside this file, uses a C# 9 `record struct` (for its immutable Gate
// type), which the
// compiler desugars using `init`-only setters. The marker type that makes `init` legal,
// System.Runtime.CompilerServices.IsExternalInit, ships in the BCL from .NET 5 onwards,
// but this project targets net48 (required by the game/OWML), whose BCL predates it. This
// empty type is the standard, compiler-recognized polyfill: the C# compiler only checks
// that a type with this exact name and namespace exists, never how it's implemented.
internal static class IsExternalInit
{
}
