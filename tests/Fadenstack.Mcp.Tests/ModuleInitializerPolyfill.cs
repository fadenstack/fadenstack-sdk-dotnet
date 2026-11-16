#if !NET
namespace System.Runtime.CompilerServices
{
    /// <summary>For the tests' module initializer on .NET Framework.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute
    {
    }
}
#endif
