using System.Reflection;
using ArchUnitNET.xUnit;
using ArchLoader = ArchUnitNET.Loader.ArchLoader;
using LoadedArchitecture = ArchUnitNET.Domain.Architecture;
using Fadenstack.UI;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Fadenstack.Architecture.Tests;

/// <summary>
/// A2 and A3 (fadenstack-dev sdk/52 §6, strict MVVM): <c>Fadenstack.UI</c> holds the view models and depends on no UI
/// framework, and no view model shows a UI type in its API. View models expose bools, enums, numbers, strings and icon keys;
/// the skins turn them into brushes, visibilities and images.
/// </summary>
public class ViewModelTests
{
    /// <summary>The UI frameworks' namespaces: WPF, Avalonia, MAUI, WinUI, UWP.</summary>
    private static readonly string[] Frameworks = { "System.Windows", "Avalonia", "Microsoft.Maui", "Microsoft.UI.Xaml", "Windows.UI" };

    /// <summary>UI types by name, whatever framework they come from.</summary>
    private static readonly string[] UiTypeNames = { "Brush", "Color", "Visibility", "Thickness", "ImageSource", "KeyGesture" };

    private static readonly Assembly UiLayer = typeof(UiStrings).Assembly;

    private static readonly LoadedArchitecture Loaded = new ArchLoader().LoadAssemblies(UiLayer).Build();

    private static bool InFramework(string? ns) =>
        ns is not null && Frameworks.Any(f => ns == f || ns.StartsWith(f + ".", StringComparison.Ordinal));

    /// <summary>A2: no type of the UI layer depends on a UI framework's type -- in its members, fields or method bodies.</summary>
    [Fact]
    public void The_UI_layer_depends_on_no_UI_framework()
    {
        Types().That().ResideInAssembly(UiLayer)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^(System\.Windows|Avalonia|Microsoft\.Maui|Microsoft\.UI\.Xaml|Windows\.UI)(\..*)?$")
            .Check(Loaded);
    }

    /// <summary>A3: the public API of the UI layer's types names no UI type.</summary>
    [Fact]
    public void No_view_model_API_shows_a_UI_type()
    {
        var found = new List<string>();
        foreach (var type in UiLayer.GetExportedTypes())
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var used in TypesOf(member).SelectMany(Expand).Where(t => InFramework(t.Namespace) || UiTypeNames.Contains(t.Name)))
                {
                    found.Add($"{type.FullName}.{member.Name}: {used.FullName}");
                }
            }
        }
        Assert.True(found.Count == 0, "UI types in view-model APIs:\n" + string.Join("\n", found.Distinct()));
    }

    private static IEnumerable<Type> TypesOf(MemberInfo member) => member switch
    {
        PropertyInfo p => new[] { p.PropertyType }.Concat(p.GetIndexParameters().Select(i => i.ParameterType)),
        FieldInfo f => new[] { f.FieldType },
        EventInfo e => e.EventHandlerType is { } handler ? new[] { handler } : Array.Empty<Type>(),
        MethodInfo m => new[] { m.ReturnType }.Concat(m.GetParameters().Select(p => p.ParameterType)),
        ConstructorInfo c => c.GetParameters().Select(p => p.ParameterType),
        Type nested => new[] { nested },
        _ => Array.Empty<Type>(),
    };

    /// <summary>A type with what it is made of: generic arguments, element types (arrays, by-ref, nullable).</summary>
    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } element)
        {
            foreach (var inner in Expand(element))
            {
                yield return inner;
            }
        }
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Expand))
            {
                yield return argument;
            }
        }
    }
}
