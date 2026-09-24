using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

// Desktop spike: can we drive the SDK's Razor source generator by hand?
// Usage: dotnet run --project prototype/RazorProto [path/to/Component.razor]
internal static class Program
{
    static int Main(string[] args)
    {
        var razorPath = Path.GetFullPath(args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Component.razor"));
        if (!File.Exists(razorPath))
        {
            Console.Error.WriteLine("No such file: " + razorPath);
            return 2;
        }

        var projectDir = Path.GetDirectoryName(razorPath);
        var razorSource = File.ReadAllText(razorPath);
        var references = LoadReferences();
        Console.WriteLine($"references: {references.Count}");

        var compilation = CSharpCompilation.Create(
            "UserRazor",
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            references: references);

        var generator = new Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator();
        Console.WriteLine("generator: " + generator.GetType().FullName);
        Console.WriteLine("roslyn in use: " + typeof(CSharpCompilation).Assembly.FullName);
        foreach (var reference in generator.GetType().Assembly.GetReferencedAssemblies()
                     .Where(a => a.Name.StartsWith("Microsoft.CodeAnalysis"))
                     .OrderBy(a => a.Name))
        {
            Console.WriteLine("  razor compiler references: " + reference.Name + " " + reference.Version);
        }

        // The compiler is handed the .razor files as AdditionalFiles with a TargetPath, plus
        // _Imports.razor, exactly as the Razor SDK does.
        var additionalTexts = new AdditionalText[]
        {
            new RazorFile(Path.Combine(projectDir, "_Imports.razor"), "@using Microsoft.AspNetCore.Components.Web\n", "_Imports.razor"),
            new RazorFile(razorPath, razorSource, Path.GetFileName(razorPath)),
        };

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { generator.AsSourceGenerator() },
            additionalTexts,
            new CSharpParseOptions(LanguageVersion.Latest),
            new RazorOptionsProvider(projectDir, "UserRazor"));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        Console.WriteLine("=== driver diagnostics ===");
        foreach (var d in diagnostics) Console.WriteLine("  " + d);

        var run = driver.GetRunResult();
        foreach (var result in run.Results)
        {
            foreach (var d in result.Diagnostics) Console.WriteLine("  run: " + d);
            foreach (var source in result.GeneratedSources)
            {
                Console.WriteLine("=== generated " + source.HintName + " ===");
                Console.WriteLine(source.SourceText.ToString());
            }
        }

        Console.WriteLine("=== generated file count: " + run.Results.Sum(r => r.GeneratedSources.Length) + " ===");
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Console.WriteLine("=== compilation errors: " + errors.Length + " ===");
        foreach (var d in errors.Take(10)) Console.WriteLine("  " + d);

        return 0;
    }

    static List<MetadataReference> LoadReferences()
    {
        var sdkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "packs");
        var refs = new List<MetadataReference>();

        foreach (var pack in new[] { "Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref" })
        {
            var root = Path.Combine(sdkRoot, pack);
            if (!Directory.Exists(root)) continue;

            var version = Directory.GetDirectories(root).OrderBy(d => d).Last();
            var target = Directory.GetDirectories(Path.Combine(version, "ref")).OrderBy(d => d).Last();

            foreach (var file in Directory.GetFiles(target, "*.dll"))
            {
                refs.Add(MetadataReference.CreateFromFile(file));
            }
        }

        return refs;
    }
}

internal sealed class RazorFile : AdditionalText
{
    readonly SourceText _text;

    public RazorFile(string path, string content, string targetPath)
    {
        Path = path;
        TargetPath = targetPath;
        _text = SourceText.From(content, Encoding.UTF8);
    }

    public override string Path { get; }

    public string TargetPath { get; }

    public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
}

internal sealed class RazorOptionsProvider : AnalyzerConfigOptionsProvider
{
    readonly AnalyzerConfigOptions _global;

    public RazorOptionsProvider(string projectDirectory, string rootNamespace)
    {
        _global = new ProjectOptions(projectDirectory, rootNamespace);
    }

    public override AnalyzerConfigOptions GlobalOptions => _global;

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _global;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
        textFile is RazorFile file ? new FileOptions(file) : _global;

    sealed class ProjectOptions : AnalyzerConfigOptions
    {
        readonly string _projectDirectory;
        readonly string _rootNamespace;

        public ProjectOptions(string projectDirectory, string rootNamespace)
        {
            _projectDirectory = projectDirectory;
            _rootNamespace = rootNamespace;
        }

        public override bool TryGetValue(string key, out string value)
        {
            switch (key)
            {
                case "build_property.RootNamespace":
                    value = _rootNamespace;
                    return true;
                case "build_property.RazorLangVersion":
                    value = "10.0";
                    return true;
                case "build_property.SupportLocalizedComponentNames":
                    value = "true";
                    return true;
                case "build_property.GenerateRazorMetadataSourceChecksumAttributes":
                    value = "false";
                    return true;
                case "build_property.MSBuildProjectDirectory":
                    value = _projectDirectory;
                    return true;
                default:
                    value = null;
                    return false;
            }
        }
    }

    sealed class FileOptions : AnalyzerConfigOptions
    {
        readonly RazorFile _file;

        public FileOptions(RazorFile file) => _file = file;

        public override bool TryGetValue(string key, out string value)
        {
            switch (key)
            {
                // The SDK base64-encodes the logical path into this metadata.
                case "build_metadata.AdditionalFiles.TargetPath":
                    value = Convert.ToBase64String(Encoding.UTF8.GetBytes(_file.TargetPath));
                    return true;
                default:
                    value = null;
                    return false;
            }
        }
    }
}
