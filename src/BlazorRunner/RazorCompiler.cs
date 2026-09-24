using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

/// <summary>
/// Compiles <c>.razor</c> markup to C# by driving the SDK's Razor source generator by hand.
///
/// The Razor compiler is not a callable library any more: it ships as the incremental generator
/// <c>Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator</c>, normally run by csc. This
/// runs it through a <see cref="CSharpGeneratorDriver"/>, reproducing the inputs the Razor SDK
/// supplies (see Microsoft.NET.Sdk.Razor.SourceGenerators.targets):
///
/// * the <c>.razor</c> files as additional files, plus an <c>_Imports.razor</c>;
/// * the compile-visible MSBuild properties (RootNamespace, RazorLangVersion, ...);
/// * and, on each additional file, <c>build_metadata.AdditionalFiles.TargetPath</c> — which the
///   SDK base64-encodes. Omitting it makes the generator silently skip component directives, so
///   <c>@onclick</c> comes out as literal markup instead of an event handler.
/// </summary>
public static class RazorCompiler
{
    const string ImportsFileName = "_Imports.razor";

    const string GeneratorTypeName = "Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator";

    /// <summary>The root namespace used when a project does not name one — the stand-in for a project
    /// name, and what folder namespaces are built from (<c>UserRazor.Layout</c>).</summary>
    public const string DefaultRootNamespace = "UserRazor";

    /// <summary>
    /// What a Blazor project gets from its template's root <c>_Imports.razor</c>, used only when the
    /// project does not bring one of its own.
    ///
    /// Folder usings such as <c>@using UserRazor.Layout</c> are deliberately not included: the
    /// template puts those in the project's own file, and mirroring that means a project behaves here
    /// exactly as it does under <c>dotnet build</c>.
    /// </summary>
    static string DefaultImports(string rootNamespace) =>
        "@using System.Net.Http\n" +
        "@using System.Net.Http.Json\n" +
        "@using Microsoft.AspNetCore.Components.Forms\n" +
        "@using Microsoft.AspNetCore.Components.Routing\n" +
        "@using Microsoft.AspNetCore.Components.Web\n" +
        "@using Microsoft.AspNetCore.Components.Web.Virtualization\n" +
        "@using Microsoft.AspNetCore.Components.WebAssembly.Http\n" +
        "@using Microsoft.JSInterop\n" +
        "@using " + rootNamespace + "\n";

    static readonly string[] CompilerResources =
    {
        "razor.Microsoft.AspNetCore.Razor.Utilities.Shared.dll",
        "razor.Microsoft.CodeAnalysis.Razor.Compiler.dll",
    };

    const string UtilitiesAssemblyName = "Microsoft.AspNetCore.Razor.Utilities.Shared";

    static IIncrementalGenerator _generator;

    static Assembly _utilities;

    /// <summary>
    /// Loads the Razor compiler from the resources embedded in this assembly and creates its source
    /// generator.
    ///
    /// It is loaded from bytes rather than referenced: an assembly reference makes MSBuild resolve
    /// the Razor compiler's own Roslyn dependency (5.9) out of the SDK and publish it, and Roslyn
    /// 5.9 aborts the WebAssembly runtime. Loaded this way it binds by simple name against the
    /// Roslyn already in the app (4.14), which runs.
    /// </summary>
    static IIncrementalGenerator GetGenerator()
    {
        if (_generator is not null)
        {
            return _generator;
        }

        var host = typeof(RazorCompiler).Assembly;
        Assembly razorAssembly = null;

        foreach (var resource in CompilerResources)
        {
            using var stream = host.GetManifestResourceStream(resource);
            if (stream is null)
            {
                throw new InvalidOperationException($"The bundle is missing '{resource}'.");
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            var loaded = Assembly.Load(buffer.ToArray());
            if (resource.IndexOf("Razor.Compiler", StringComparison.Ordinal) >= 0)
            {
                razorAssembly = loaded;
            }
            else
            {
                _utilities = loaded;
            }
        }

        // An assembly loaded from bytes is not discoverable by name, so the compiler's own
        // dependency on the utilities assembly has to be resolved explicitly.
        AssemblyLoadContext.Default.Resolving += (_, name) =>
            name.Name == UtilitiesAssemblyName ? _utilities : null;

        var type = razorAssembly.GetType(GeneratorTypeName, throwOnError: false)
            ?? throw new InvalidOperationException("The Razor compiler has no " + GeneratorTypeName + ".");

        _generator = (IIncrementalGenerator)Activator.CreateInstance(type);
        return _generator;
    }

    /// <summary>Generates the C# for every <c>.razor</c> file of a project. They are handed to the
    /// generator together, which is what lets components in different files reference each other
    /// and <c>@page</c> components register their routes. <paramref name="cssScopes"/> maps a
    /// <c>.razor</c> file to the CSS scope its rendered elements should carry.</summary>
    public static bool TryGenerate(
        SourceFile[] files,
        string rootNamespace,
        IReadOnlyDictionary<string, string> cssScopes,
        out List<string> generatedSources,
        out DiagnosticInfo[] errors)
    {
        generatedSources = new List<string>();
        errors = Array.Empty<DiagnosticInfo>();

        var razorFiles = (files ?? Array.Empty<SourceFile>())
            .Where(file => file is not null && !string.IsNullOrEmpty(file.Filename))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "UserRazor",
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            references: ReferenceAssemblies.All);

        var additionalTexts = new List<AdditionalText>();

        // A project's own _Imports.razor is used as-is, exactly as it would be locally; the template's
        // equivalent is only supplied when the project has none.
        var hasImports = razorFiles.Any(file =>
            file.Filename.Equals(ImportsFileName, StringComparison.OrdinalIgnoreCase) ||
            file.Filename.EndsWith("/" + ImportsFileName, StringComparison.OrdinalIgnoreCase));

        if (!hasImports)
        {
            additionalTexts.Add(new RazorAdditionalText(ImportsFileName, DefaultImports(rootNamespace)));
        }

        additionalTexts.AddRange(razorFiles.Select(file => (AdditionalText)new RazorAdditionalText(file.Filename, file.Content ?? "")));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { GetGenerator().AsSourceGenerator() },
            additionalTexts,
            new CSharpParseOptions(LanguageVersion.Latest),
            new RazorOptionsProvider(rootNamespace, cssScopes));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

        // Running the generator can fail without failing the compilation — a crashed generator is
        // reported as a warning — so keep every diagnostic to explain an empty result.
        var generatorDiagnostics = diagnostics.Select(DiagnosticInfo.From).ToArray();

        foreach (var result in driver.GetRunResult().Results)
        {
            foreach (var source in result.GeneratedSources)
            {
                // _Imports.razor generates a class of its own; the components' generated code
                // already carries its usings.
                if (source.HintName is null ||
                    source.HintName.StartsWith("_Imports", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                generatedSources.Add(source.SourceText.ToString());
            }
        }

        if (generatedSources.Count == 0)
        {
            errors = generatorDiagnostics.Length > 0
                ? generatorDiagnostics
                : new[] { DiagnosticInfo.Error("RAZOR0001", "The Razor compiler produced no output for this project.") };
            return false;
        }

        return true;
    }
}

/// <summary>A <c>.razor</c> file handed to the generator as an additional file.</summary>
internal sealed class RazorAdditionalText : AdditionalText
{
    readonly SourceText _text;

    public RazorAdditionalText(string fileName, string content)
    {
        Path = fileName;
        _text = SourceText.From(content, Encoding.UTF8);
    }

    public override string Path { get; }

    public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
}

/// <summary>The compile-visible properties and item metadata the Razor generator expects.</summary>
internal sealed class RazorOptionsProvider : AnalyzerConfigOptionsProvider
{
    static readonly IReadOnlyDictionary<string, string> NoScopes = new Dictionary<string, string>();

    readonly AnalyzerConfigOptions _global;
    readonly IReadOnlyDictionary<string, string> _cssScopes;

    public RazorOptionsProvider(string rootNamespace, IReadOnlyDictionary<string, string> cssScopes)
    {
        _global = new ProjectOptions(rootNamespace);
        _cssScopes = cssScopes ?? NoScopes;
    }

    public override AnalyzerConfigOptions GlobalOptions => _global;

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _global;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
        textFile is RazorAdditionalText file ? new FileOptions(file, _cssScopes) : _global;

    sealed class ProjectOptions : AnalyzerConfigOptions
    {
        readonly string _rootNamespace;

        public ProjectOptions(string rootNamespace) => _rootNamespace = rootNamespace;

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
                    value = "/";
                    return true;
                default:
                    value = null;
                    return false;
            }
        }
    }

    sealed class FileOptions : AnalyzerConfigOptions
    {
        readonly RazorAdditionalText _file;
        readonly IReadOnlyDictionary<string, string> _cssScopes;

        public FileOptions(RazorAdditionalText file, IReadOnlyDictionary<string, string> cssScopes)
        {
            _file = file;
            _cssScopes = cssScopes;
        }

        public override bool TryGetValue(string key, out string value)
        {
            switch (key)
            {
                // The SDK base64-encodes the logical path here; the generator decodes it.
                case "build_metadata.AdditionalFiles.TargetPath":
                    value = Convert.ToBase64String(Encoding.UTF8.GetBytes(_file.Path));
                    return true;
                // Set for a component that has a matching .razor.css, so its elements carry the scope.
                case "build_metadata.AdditionalFiles.CssScope":
                    return _cssScopes.TryGetValue(_file.Path, out value);
                default:
                    value = null;
                    return false;
            }
        }
    }
}
