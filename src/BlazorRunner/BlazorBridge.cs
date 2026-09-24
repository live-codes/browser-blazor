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
    };

    /// <summary>Compiles and renders a Blazor component. <paramref name="rootType"/> is an
    /// optional component name; when empty the component named "App" is used, else the first.
    /// Returns <c>{ success, type, errors[] }</c>.</summary>
    [JSInvokable]
    public static async Task<string> RenderComponent(string source, string rootType)
    {
        try
        {
            var result = ComponentCompiler.Compile(source, rootType);

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

            return Serialize(new RenderResult
            {
                Success = true,
                Type = result.Type.FullName,
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

    static string Serialize(RenderResult result) => JsonSerializer.Serialize(result, JsonOptions);
}

public sealed class RenderResult
{
    public bool Success { get; set; }
    public string Type { get; set; }
    public DiagnosticInfo[] Errors { get; set; }
}
