using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.CodeAnalysis;

/// <summary>
/// Compiles a user-authored Blazor component (plain C#) with the real Roslyn C# compiler and
/// loads it. A source file may declare several components — the root is the one named "App"
/// (or an explicit name), and other components can be used as children.
/// </summary>
public static class ComponentCompiler
{
    public static CompileResult Compile(string source, string rootTypeName = null)
    {
        if (ReferenceAssemblies.Count == 0)
        {
            throw new InvalidOperationException("No reference assemblies loaded.");
        }

        if (!CSharpInProcess.TryCompile(
                source,
                OutputKind.DynamicallyLinkedLibrary,
                "Component.cs",
                out var assembly,
                out var imageLength,
                out var errors))
        {
            return new CompileResult { Type = null, Errors = errors };
        }

        var components = GetLoadableTypes(assembly)
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .ToList();

        if (!string.IsNullOrEmpty(rootTypeName))
        {
            var named = components.FirstOrDefault(t => t.Name == rootTypeName || t.FullName == rootTypeName);
            if (named is null)
            {
                return Fail(
                    "BLAZOR0002",
                    $"Component '{rootTypeName}' was not found." +
                    (components.Count > 0
                        ? " Available: " + string.Join(", ", components.Select(c => c.Name)) + "."
                        : ""));
            }

            return Ok(named, imageLength);
        }

        // Convention: a component named "App" is the root (as in a Blazor project), otherwise
        // the first one declared.
        var root = components.FirstOrDefault(t => t.Name == "App") ?? components.FirstOrDefault();
        if (root is null)
        {
            return Fail("BLAZOR0001", "No Blazor component found. Declare a class that derives from ComponentBase.");
        }

        return Ok(root, imageLength);
    }

    /// <summary>Compiles a component written as <c>.razor</c> markup: markup to C# via the Razor
    /// generator, then that C# to an assembly. <paramref name="componentName"/> names the generated
    /// class (the Razor file name determines it), defaulting to "App".</summary>
    public static CompileResult CompileRazor(string razorSource, string componentName)
    {
        var name = string.IsNullOrEmpty(componentName) ? "App" : componentName;

        if (!RazorCompiler.TryGenerate(razorSource, name + ".razor", out var generated, out var razorErrors))
        {
            return new CompileResult { Type = null, Errors = razorErrors };
        }

        if (!CSharpInProcess.TryCompile(
                generated,
                OutputKind.DynamicallyLinkedLibrary,
                name + ".g.cs",
                out var assembly,
                out var imageLength,
                out var errors))
        {
            return new CompileResult { Type = null, Errors = errors };
        }

        var type = GetLoadableTypes(assembly)
            .FirstOrDefault(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

        return type is null
            ? Fail("BLAZOR0001", "No Blazor component found in the generated code.")
            : Ok(type, imageLength);
    }

    static CompileResult Ok(Type type, int bytes) =>
        new CompileResult { Type = type, Bytes = bytes, Errors = Array.Empty<DiagnosticInfo>() };

    static CompileResult Fail(string id, string message) =>
        new CompileResult { Type = null, Errors = new[] { DiagnosticInfo.Error(id, message) } };

    static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null);
        }
    }
}
