# Building a library on the SDK

This page covers libraries that build on the SDK, such as a shared set of host tools or a module, and why they should target the same frameworks as the SDK.

## Target netstandard2.0 and net8.0

Build your library for both of the SDK's targets:

```xml
<PropertyGroup>
  <TargetFrameworks>netstandard2.0;net8.0</TargetFrameworks>
</PropertyGroup>
```

A library on `Faden.UI.Wpf` targets `net472` and `net8.0-windows` for the same reason.

## Why both

The SDK's types use `init` setters: its records, `MessageAttachment`, the options classes. In the `netstandard2.0` build, these setters are compiled against a different marker type than in the `net8.0` build, so their signatures differ between the two builds.

A library built for `netstandard2.0` only is compiled against the SDK's `netstandard2.0` build. A .NET 8 or later application that loads it gets the SDK's `net8.0` build, and the library's calls to those setters fail at run time with `MissingMethodException`.

Built for both targets, each application loads matching builds: a .NET 8 or later application gets the `net8.0` builds of your library and the SDK, and a .NET Framework host gets the `netstandard2.0` builds of both.

The same applies to streaming. A `netstandard2.0` build that uses `IAsyncEnumerable<T>`, as anything that reads `StreamAsync` does, depends on the `Microsoft.Bcl.AsyncInterfaces` package, which the .NET 10 SDK may prune from a .NET application's package graph. The library then fails to load in that application. Its `net8.0` build has no such dependency.

## Records and init setters in your own code

If your library declares records or `init` setters itself, its `netstandard2.0` build needs the `IsExternalInit` marker type, which `netstandard2.0` does not have. Declare it once, for that target only:

```csharp
#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
```

This is what the SDK does itself.
