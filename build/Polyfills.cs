// Compiler support types missing from netstandard2.0 / net472, so records and init-only setters work there.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
