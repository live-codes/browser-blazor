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

        if (!CSharpInProcess.TryCompile(source, OutputKind.DynamicallyLinkedLibrary, "Component.cs", out var assembly, out var errors))
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

            return new CompileResult { Type = named, Errors = Array.Empty<DiagnosticInfo>() };
        }

        // Convention: a component named "App" is the root (as in a Blazor project), otherwise
        // the first one declared.
        var root = components.FirstOrDefault(t => t.Name == "App") ?? components.FirstOrDefault();
        if (root is null)
        {
            return Fail("BLAZOR0001", "No Blazor component found. Declare a class that derives from ComponentBase.");
        }

        return new CompileResult { Type = root, Errors = Array.Empty<DiagnosticInfo>() };
    }

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
