using System;
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

    /// <summary>Compiles and renders a project — any mix of <c>.razor</c> markup and C# — with all
    /// files compiled together, so components can reference each other and <c>@page</c> components
    /// register routes. <paramref name="filesJson"/> is a JSON array of <c>{ name, content }</c>.
    /// <paramref name="rootType"/> optionally names the component to render.</summary>
    [JSInvokable]
    public static Task<string> RenderProject(string filesJson, string rootType)
    {
        SourceFile[] files;
        try
        {
            files = JsonSerializer.Deserialize<SourceFile[]>(filesJson ?? "[]", JsonOptions)
                ?? Array.Empty<SourceFile>();
        }
        catch (JsonException ex)
        {
            return Task.FromResult(Serialize(new RenderResult
            {
                Success = false,
                Errors = new[] { DiagnosticInfo.Error("JSON", ex.Message) },
            }));
        }

        return Render(() => ComponentCompiler.Compile(files, rootType));
    }

    /// <summary>Compiles and renders a single component written as C#.</summary>
    [JSInvokable]
    public static Task<string> RenderComponent(string source, string rootType) =>
        Render(() => ComponentCompiler.Compile(
            new[] { new SourceFile { Filename = "Components.cs", Content = source ?? "" } },
            rootType));

    /// <summary>Compiles and renders a single component written as <c>.razor</c> markup.
    /// <paramref name="componentName"/> names the generated class, and defaults to "App".</summary>
    [JSInvokable]
    public static Task<string> RenderRazor(string source, string componentName) =>
        Render(() => ComponentCompiler.Compile(
            new[]
            {
                new SourceFile
                {
                    Filename = (string.IsNullOrEmpty(componentName) ? "App" : componentName) + ".razor",
                    Content = source ?? "",
                },
            },
            componentName));

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

    [JSInvokable]
    public static int ReferenceCount() => ReferenceAssemblies.Count;

    static async Task<string> Render(Func<CompileResult> compile)
    {
        try
        {
            var result = compile();

            if (result.Type is null)
            {
                return Serialize(new RenderResult { Success = false, Errors = result.Errors });
            }

            if (DynamicHost.Current is null)
            {
                return Serialize(new RenderResult
                {
                    Success = false,
                    Errors = new[] { DiagnosticInfo.Error("Host", "The Blazor host is not ready yet.") },
                });
            }

            await DynamicHost.Current.ShowAsync(result.Type);

            // A component can compile cleanly and still throw while rendering; the boundary
            // catches that, so report it as a failure rather than a success that rendered nothing.
            var renderError = DynamicHost.Current.LastRenderError;
            if (renderError is not null)
            {
                return Serialize(new RenderResult
                {
                    Success = false,
                    Type = result.Type.FullName,
                    Bytes = result.Bytes,
                    Routes = result.Routes,
                    Errors = new[] { DiagnosticInfo.Error(renderError.GetType().Name, renderError.Message) },
                });
            }

            return Serialize(new RenderResult
            {
                Success = true,
                Type = result.Type.FullName,
                Bytes = result.Bytes,
                Routes = result.Routes,
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

    public DiagnosticInfo[] Errors { get; set; }
}
