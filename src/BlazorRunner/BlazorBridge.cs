using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.JSInterop;

/// <summary>
/// The JS interop surface, called from the page with
/// <c>DotNet.invokeMethodAsync('BlazorRunner', …)</c>.
/// </summary>
public static class BlazorBridge
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    static readonly Dictionary<string, string> NoAssets = new Dictionary<string, string>();

    /// <summary>Compiles and renders a project — any mix of <c>.razor</c> markup and C# — with all
    /// files compiled together, so components can reference each other and <c>@page</c> components
    /// register routes. <paramref name="filesJson"/> is a JSON array of <c>{ filename, content }</c>.
    /// <paramref name="rootType"/> optionally names the component to render, and
    /// <paramref name="rootNamespace"/> the project's namespace (folder namespaces hang off it).</summary>
    [JSInvokable]
    public static async Task<string> RenderProject(string filesJson, string rootType, string rootNamespace)
    {
        SourceFile[] files;
        try
        {
            files = JsonSerializer.Deserialize<SourceFile[]>(filesJson ?? "[]", JsonOptions)
                ?? Array.Empty<SourceFile>();
        }
        catch (JsonException ex)
        {
            return Serialize(new RenderResult
            {
                Success = false,
                Errors = new[] { DiagnosticInfo.Error("JSON", ex.Message) },
            });
        }

        var name = string.IsNullOrEmpty(rootNamespace) ? RazorCompiler.DefaultRootNamespace : rootNamespace;
        var assets = ProjectAssets.Collect(files);

        return await Render(
            () => ComponentCompiler.EnsureLoadedAsync(files),
            () => ComponentCompiler.Compile(files, rootType, name),
            assets);
    }

    /// <summary>Compiles and renders a single component written as C#.</summary>
    [JSInvokable]
    public static Task<string> RenderComponent(string source, string rootType)
    {
        var files = new[] { new SourceFile { Filename = "Components.cs", Content = source ?? "" } };

        return Render(
            () => ComponentCompiler.EnsureLoadedAsync(files),
            () => ComponentCompiler.Compile(files, rootType, RazorCompiler.DefaultRootNamespace),
            NoAssets);
    }

    /// <summary>Compiles and renders a single component written as <c>.razor</c> markup.
    /// <paramref name="componentName"/> names the generated class, and defaults to "App".</summary>
    [JSInvokable]
    public static Task<string> RenderRazor(string source, string componentName)
    {
        var files = new[]
        {
            new SourceFile
            {
                Filename = (string.IsNullOrEmpty(componentName) ? "App" : componentName) + ".razor",
                Content = source ?? "",
            },
        };

        return Render(
            () => ComponentCompiler.EnsureLoadedAsync(files),
            () => ComponentCompiler.Compile(files, componentName, RazorCompiler.DefaultRootNamespace),
            NoAssets);
    }

    /// <summary>Drives the host's NavigationManager, so <c>@page</c> routing can be exercised from
    /// the page.</summary>
    [JSInvokable]
    public static Task NavigateTo(string url)
    {
        DynamicHost.Current?.NavigateTo(url ?? "/");
        return Task.CompletedTask;
    }

    /// <summary>Compiles and runs a C# console program, capturing stdout. Returns
    /// <c>{ success, output, errors[] }</c> — the same field names the LiveCodes
    /// 'csharp-wasm' language consumes, so one bundle can back both.</summary>
    [JSInvokable]
    public static async Task<string> RunCode(string source, string stdin)
    {
        try
        {
            await ReferenceAssemblies.EnsureLoadedAsync();

            var result = await ConsoleRunner.Run(source, stdin);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(
                new RunResult
                {
                    Success = false,
                    Output = "",
                    Errors = new[] { DiagnosticInfo.Error(ex.GetType().Name, ex.Message) },
                },
                JsonOptions);
        }
    }

    /// <summary>The bundle's base URL, pushed in by blazor-wasm.js once the runtime is up. The
    /// compiler payloads are fetched relative to it, not to the page.</summary>
    [JSInvokable]
    public static void SetBaseUrl(string baseUrl) => Payload.BaseUrl = baseUrl ?? "";

    /// <summary>Which compiler payloads have been fetched. A console program needs only the
    /// reference assemblies, so this reports "refs.zip" and nothing else.</summary>
    [JSInvokable]
    public static string[] LoadedPayloads() => Payload.Loaded;

    /// <summary>How many reference assemblies are loaded, fetching them if they are not yet — the
    /// page uses this as a readiness check.</summary>
    [JSInvokable]
    public static async Task<int> ReferenceCount()
    {
        await ReferenceAssemblies.EnsureLoadedAsync();
        return ReferenceAssemblies.Count;
    }

    /// <summary>The host instance exists only once the app's first render has run, which can be
    /// after the first interop call arrives. Wait briefly for it rather than failing a render that
    /// would otherwise succeed.</summary>
    static async Task<DynamicHost> WaitForHostAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (DynamicHost.Current is not null)
            {
                return DynamicHost.Current;
            }

            await Task.Delay(50);
        }

        return null;
    }

    static async Task<string> Render(Func<Task> ensure, Func<CompileResult> compile, Dictionary<string, string> assets)
    {
        try
        {
            await ensure();

            var result = compile();

            if (result.Type is null)
            {
                return Serialize(new RenderResult { Success = false, Errors = result.Errors });
            }

            var host = await WaitForHostAsync();
            if (host is null)
            {
                return Serialize(new RenderResult
                {
                    Success = false,
                    Errors = new[] { DiagnosticInfo.Error("Host", "The Blazor host is not ready yet.") },
                });
            }

            await host.ShowAsync(result.Type, result.Styles);

            // A component can compile cleanly and still throw while rendering; the boundary
            // catches that, so report it as a failure rather than a success that rendered nothing.
            var renderError = host.LastRenderError;
            if (renderError is not null)
            {
                return Serialize(new RenderResult
                {
                    Success = false,
                    Type = result.Type.FullName,
                    Bytes = result.Bytes,
                    Routes = result.Routes,
                    Styles = result.Styles,
                    Assets = assets,
                    Errors = new[] { DiagnosticInfo.Error(renderError.GetType().Name, renderError.Message) },
                });
            }

            return Serialize(new RenderResult
            {
                Success = true,
                Type = result.Type.FullName,
                Bytes = result.Bytes,
                Routes = result.Routes,
                Styles = result.Styles,
                Assets = assets,
                Errors = Array.Empty<DiagnosticInfo>(),
            });
        }
        catch (Exception ex)
        {
            return Serialize(new RenderResult
            {
                Success = false,
                Errors = new[] { DiagnosticInfo.Error(ex.GetType().Name, ex.Message) },
            });
        }
    }

    static string Serialize(RenderResult result) => JsonSerializer.Serialize(result, JsonOptions);
}

public sealed class RenderResult
{
    public bool Success { get; set; }
    public string Type { get; set; }
    public int Bytes { get; set; }

    /// <summary>Route templates declared by the project, for the page to link to.</summary>
    public string[] Routes { get; set; }

    /// <summary>The project's scoped CSS (already rendered alongside the component; reported so a
    /// consumer can place it itself if it prefers).</summary>
    public string Styles { get; set; }

    /// <summary>The project's <c>wwwroot/</c> files as data URLs, keyed by their path below it.</summary>
    public Dictionary<string, string> Assets { get; set; }

    public DiagnosticInfo[] Errors { get; set; }
}
