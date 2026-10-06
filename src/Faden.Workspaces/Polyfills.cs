#if NETSTANDARD2_0
// Language features netstandard2.0 lacks the runtime types for.
namespace System.Runtime.CompilerServices
{
    /// <summary>Lets records and <c>init</c> accessors compile for netstandard2.0.</summary>
    internal static class IsExternalInit
    {
    }
}
#endif
