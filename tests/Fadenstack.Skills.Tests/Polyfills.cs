#if !NET
namespace System.Runtime.CompilerServices
{
    /// <summary>For init accessors on .NET Framework; this project's own, since the SDK's assemblies each hold an internal one.</summary>
    internal static class IsExternalInit
    {
    }

    /// <summary>For the tests' module initializer on .NET Framework (TestFolders).</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute
    {
    }
}
#endif
