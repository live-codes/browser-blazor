using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.CodeAnalysis;

/// <summary>
/// Compiles a project — any mix of <c>.razor</c> markup and C# — into one assembly, then picks the
/// component to render.
///
/// All files go into a single compilation, which is what lets components in different files
/// reference each other and <c>@page</c> components register their routes.
/// </summary>
public static class ComponentCompiler
{
    /// <summary>Compiles the project and resolves the root component. <paramref name="rootTypeName"/>
    /// is optional: by convention a component named "App" is the root; with no App but with
    /// <c>@page</c> components, a router over the compiled assembly is used instead; otherwise the
    /// first component.</summary>
    public static CompileResult Compile(SourceFile[] files, string rootTypeName)
    {
        if (ReferenceAssemblies.Count == 0)
        {
            throw new InvalidOperationException("No reference assemblies loaded.");
        }

        var project = files ?? Array.Empty<SourceFile>();
        var markup = project
            .Where(file => file?.Name is not null && file.Name.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var sources = new List<SourceFile>(project.Where(file =>
            file?.Name is not null && !file.Name.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)));

        if (markup.Length > 0)
        {
            if (!RazorCompiler.TryGenerate(markup, out var generated, out var razorErrors))
            {
                return new CompileResult { Type = null, Errors = razorErrors };
            }

            for (var i = 0; i < generated.Count; i++)
            {
                sources.Add(new SourceFile { Name = "Razor" + i + ".g.cs", Content = generated[i] });
            }
        }

        if (sources.Count == 0)
        {
            return Fail("BLAZOR0001", "There is nothing to compile.");
        }

        if (!CSharpInProcess.TryCompile(
                sources.ToArray(),
                OutputKind.DynamicallyLinkedLibrary,
                out var assembly,
                out var imageLength,
                out var errors))
        {
            return new CompileResult { Type = null, Errors = errors };
        }

        var components = GetLoadableTypes(assembly)
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .ToList();

        var routes = components
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(a => a.Template))
            .Where(template => !string.IsNullOrEmpty(template))
            .Distinct()
            .OrderBy(template => template, StringComparer.Ordinal)
            .ToArray();

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

            return Ok(named, imageLength, routes);
        }

        var root = components.FirstOrDefault(t => t.Name == "App");

        if (root is null && routes.Length > 0)
        {
            // Routed components but no App to host them: route over the assembly we just compiled.
            HostRouter.RoutesAssembly = assembly;
            return Ok(typeof(HostRouter), imageLength, routes);
        }

        root ??= components.FirstOrDefault();

        return root is null
            ? Fail("BLAZOR0001", "No Blazor component found. Declare a class that derives from ComponentBase.")
            : Ok(root, imageLength, routes);
    }

    static CompileResult Ok(Type type, int bytes, string[] routes) =>
        new CompileResult { Type = type, Bytes = bytes, Routes = routes, Errors = Array.Empty<DiagnosticInfo>() };

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
