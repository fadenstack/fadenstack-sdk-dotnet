#if !NET
namespace System.Runtime.CompilerServices
{
    /// <summary>For init accessors on .NET Framework; this project's own, since the SDK's assemblies each hold an internal one.</summary>
    internal static class IsExternalInit
    {
    }
}
#endif
