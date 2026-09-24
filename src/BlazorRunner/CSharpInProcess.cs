using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>
/// Shared in-process compilation: parse → emit → <c>Assembly.Load</c>. Used by the component
/// compiler and the console runner so they share one code path (and one copy of the reference
/// assemblies). A project compiles several sources into a single assembly, which is what lets
/// components in different files reference each other.
/// </summary>
public static class CSharpInProcess
{
    public static bool TryCompile(
        string source,
        OutputKind outputKind,
        string fileName,
        out Assembly assembly,
        out int imageLength,
        out DiagnosticInfo[] errors) =>
        TryCompile(
            new[] { new SourceFile { Filename = fileName, Content = source } },
            outputKind,
            out assembly,
            out imageLength,
            out errors);

    public static bool TryCompile(
        SourceFile[] sources,
        OutputKind outputKind,
        out Assembly assembly,
        out int imageLength,
        out DiagnosticInfo[] errors)
    {
        assembly = null;
        imageLength = 0;

        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = (sources ?? Array.Empty<SourceFile>())
            .Select(file => CSharpSyntaxTree.ParseText(file.Content ?? "", parseOptions, path: file.Filename))
            .ToArray();

        var options = new CSharpCompilationOptions(outputKind)
            // The Mono interpreter is used in the browser, so don't over-optimize.
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithPlatform(Platform.AnyCpu)
            // Roslyn's parallel binding schedules work on the thread pool; the WebAssembly
            // runtime is single-threaded, so keep it sequential.
            .WithConcurrentBuild(false);

        // A fresh identity per compile: loading two assemblies with the same name into the
        // default load context would clash.
        var assemblyName = "User_" + Guid.NewGuid().ToString("N");
        var compilation = CSharpCompilation.Create(assemblyName, trees, ReferenceAssemblies.All, options);

        using var peStream = new MemoryStream();
        var emit = compilation.Emit(peStream);

        errors = emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(DiagnosticInfo.From)
            .ToArray();

        if (!emit.Success)
        {
            return false;
        }

        var image = peStream.ToArray();
        imageLength = image.Length;
        assembly = Assembly.Load(image);
        return true;
    }
}
