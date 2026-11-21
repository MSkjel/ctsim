// Lets net48 use records and init-only setters (C# 9+): the compiler needs this type to exist.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
