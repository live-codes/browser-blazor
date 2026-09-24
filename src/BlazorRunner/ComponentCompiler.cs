using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.CodeAnalysis;

/// <summary>
/// Compiles a project — any mix of <c>.razor</c> markup and C# — into one assembly, then picks the
/// component to render.
///
/// All files go into a single compilation, which is what lets components in different files
/// reference each other and <c>@page</c> components register their routes. A component's
/// <c>.razor.css</c> is turned into a scoped stylesheet, the way CSS isolation works locally.
/// </summary>
public static class ComponentCompiler
{
    /// <summary>Fetches whatever this project needs before it can be compiled: the reference
    /// assemblies always, the Razor compiler only when there is markup. Awaited by the bridge so a
    /// failed download surfaces as a diagnostic rather than an interop rejection.</summary>
    public static async Task EnsureLoadedAsync(SourceFile[] files)
    {
        await ReferenceAssemblies.EnsureLoadedAsync();

        var hasMarkup = (files ?? Array.Empty<SourceFile>()).Any(file =>
            file?.Filename is not null &&
            file.Filename.EndsWith(".razor", StringComparison.OrdinalIgnoreCase));

        if (hasMarkup)
        {
            await RazorCompiler.EnsureLoadedAsync();
        }
    }

    /// <summary>Compiles the project and resolves the root component. <paramref name="rootTypeName"/>
    /// is optional: by convention a component named "App" is the root; with no App but with
    /// <c>@page</c> components, a router over the compiled assembly is used instead; otherwise the
    /// first component. <paramref name="rootNamespace"/> is the project's namespace, and what
    /// folder namespaces are built from.</summary>
    public static CompileResult Compile(SourceFile[] files, string rootTypeName, string rootNamespace)
    {
        if (ReferenceAssemblies.Count == 0)
        {
            throw new InvalidOperationException("No reference assemblies loaded.");
        }

        var project = files ?? Array.Empty<SourceFile>();
        var markup = new List<SourceFile>();
        var sources = new List<SourceFile>();
        var scopedCss = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in project)
        {
            var name = file?.Filename;
            if (string.IsNullOrEmpty(name)) continue;

            if (name.EndsWith(".razor.css", StringComparison.OrdinalIgnoreCase))
            {
                scopedCss[name] = file.Content ?? "";
            }
            else if (name.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            {
                markup.Add(file);
            }
            else if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                sources.Add(file);
            }

            // Anything else — wwwroot assets, say — is not compiled.
        }

        var styles = new StringBuilder();
        var cssScopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (markup.Count > 0)
        {
            // CSS isolation: <Name>.razor.css scopes <Name>.razor. The component gets the scope as an
            // attribute on the elements it renders, and the stylesheet's selectors are rewritten to
            // match, exactly as the Razor SDK does it.
            foreach (var file in markup)
            {
                if (!scopedCss.TryGetValue(file.Filename + ".css", out var css)) continue;

                var id = CssScopeId(file.Filename);
                cssScopes[file.Filename] = id;
                styles.Append(CssScoper.Scope(css, "[" + id + "]")).Append('\n');
            }

            if (!RazorCompiler.TryGenerate(markup.ToArray(), rootNamespace, cssScopes, out var generated, out var razorErrors))
            {
                return new CompileResult { Type = null, Errors = razorErrors };
            }

            for (var i = 0; i < generated.Count; i++)
            {
                sources.Add(new SourceFile { Filename = "Razor" + i + ".g.cs", Content = generated[i] });
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

        var rendered = styles.ToString();

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

            return Ok(named, imageLength, routes, rendered);
        }

        var root = components.FirstOrDefault(t => t.Name == "App");

        if (root is null && routes.Length > 0)
        {
            // Routed components but no App to host them: route over the assembly we just compiled.
            HostRouter.RoutesAssembly = assembly;
            return Ok(typeof(HostRouter), imageLength, routes, rendered);
        }

        root ??= components.FirstOrDefault();

        return root is null
            ? Fail("BLAZOR0001", "No Blazor component found. Declare a class that derives from ComponentBase.")
            : Ok(root, imageLength, routes, rendered);
    }

    /// <summary>A stable <c>b-xxxxxxxxxx</c> scope for a component's stylesheet, so the attribute the
    /// component emits and the rewritten CSS agree across compiles. The SDK generates one per file
    /// too.</summary>
    static string CssScopeId(string fileName)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var builder = new StringBuilder("b-");
        var first = Fnv(fileName);
        var second = Fnv(fileName + "#scope");

        for (var i = 0; i < 5; i++)
        {
            builder.Append(alphabet[(int)(first % 36)]);
            first /= 36;
        }

        for (var i = 0; i < 5; i++)
        {
            builder.Append(alphabet[(int)(second % 36)]);
            second /= 36;
        }

        return builder.ToString();
    }

    static uint Fnv(string value)
    {
        var hash = 2166136261u;

        foreach (var c in value ?? "")
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }

    static CompileResult Ok(Type type, int bytes, string[] routes, string styles) =>
        new CompileResult
        {
            Type = type,
            Bytes = bytes,
            Routes = routes,
            Styles = styles,
            Errors = Array.Empty<DiagnosticInfo>(),
        };

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
