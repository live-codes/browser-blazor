using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Compiles a user-authored Blazor component (plain C#) entirely in-process with the real
/// Roslyn C# compiler, then loads it. Reference assemblies are embedded in the app assembly,
/// so no file system is involved.
/// </summary>
public static class ComponentCompiler
{
    static readonly List<MetadataReference> References = new List<MetadataReference>();

    public static int ReferenceCount => References.Count;

    /// <summary>Loads reference assemblies embedded as resources named
    /// "&lt;prefix&gt;&lt;file&gt;.dll" (WebAssembly has no file system).</summary>
    public static void LoadRefsFromAssemblyResources(Assembly assembly, string prefix = "lib.")
    {
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal);

        foreach (var name in names)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            References.Add(MetadataReference.CreateFromImage(buffer.ToArray(), filePath: name.Substring(prefix.Length)));
        }
    }

    public static CompileResult Compile(string source)
    {
        if (References.Count == 0)
        {
            throw new InvalidOperationException("No reference assemblies loaded. Call LoadRefsFromAssemblyResources first.");
        }

        var tree = CSharpSyntaxTree.ParseText(
            source ?? "",
            new CSharpParseOptions(LanguageVersion.Latest),
            path: "Component.cs");

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            // The Mono interpreter is used in the browser, so don't over-optimize.
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithPlatform(Platform.AnyCpu)
            // Roslyn's parallel binding schedules work on the thread pool; the WebAssembly
            // runtime is single-threaded, so keep it sequential.
            .WithConcurrentBuild(false);

        // A fresh identity per compile: loading two assemblies with the same name into the
        // default load context would clash.
        var assemblyName = "UserComponent_" + Guid.NewGuid().ToString("N");
        var compilation = CSharpCompilation.Create(assemblyName, new[] { tree }, References, options);

        using var peStream = new MemoryStream();
        var emit = compilation.Emit(peStream);

        var errors = emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(ToDiagnosticInfo)
            .ToArray();

        if (!emit.Success)
        {
            return new CompileResult { Type = null, Errors = errors };
        }

        var assembly = Assembly.Load(peStream.ToArray());
        var type = GetLoadableTypes(assembly).FirstOrDefault(t =>
            typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

        if (type is null)
        {
            return new CompileResult
            {
                Type = null,
                Errors = new[]
                {
                    new DiagnosticInfo
                    {
                        Id = "BLAZOR0001",
                        Message = "No Blazor component found. Declare a class that derives from ComponentBase.",
                        Severity = "Error",
                        Line = 0,
                        Column = 0,
                    },
                },
            };
        }

        return new CompileResult { Type = type, Errors = Array.Empty<DiagnosticInfo>() };
    }

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

    static DiagnosticInfo ToDiagnosticInfo(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new DiagnosticInfo
        {
            Id = diagnostic.Id,
            Message = diagnostic.GetMessage(),
            Severity = diagnostic.Severity.ToString(),
            Line = span.StartLinePosition.Line + 1,
            Column = span.StartLinePosition.Character + 1,
        };
    }
}

public sealed class CompileResult
{
    public Type Type { get; set; }
    public DiagnosticInfo[] Errors { get; set; }
}

public sealed class DiagnosticInfo
{
    public string Id { get; set; }
    public string Message { get; set; }
    public string Severity { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
}
