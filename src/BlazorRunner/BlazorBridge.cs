using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.JSInterop;

/// <summary>
/// The JS interop surface. Called from the page with
/// <c>DotNet.invokeMethodAsync('BlazorRunner', 'RenderComponent', source)</c>.
/// </summary>
public static class BlazorBridge
{
    static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [JSInvokable]
    public static async Task<string> RenderComponent(string source)
    {
        try
        {
            var result = ComponentCompiler.Compile(source);

            if (result.Type is null)
            {
                return Serialize(new RenderResult { Success = false, Errors = result.Errors });
            }

            if (DynamicHost.Current is null)
            {
                return Serialize(new RenderResult
                {
                    Success = false,
                    Errors = new[] { Error("Host", "The Blazor host is not ready yet.") },
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
            return Serialize(new RenderResult { Success = false, Errors = new[] { Error(ex.GetType().Name, ex.Message) } });
        }
    }

    [JSInvokable]
    public static int ReferenceCount() => ComponentCompiler.ReferenceCount;

    static string Serialize(RenderResult result) => JsonSerializer.Serialize(result, JsonOptions);

    static DiagnosticInfo Error(string id, string message) =>
        new DiagnosticInfo { Id = id, Message = message, Severity = "Error" };
}

public sealed class RenderResult
{
    public bool Success { get; set; }
    public string Type { get; set; }
    public DiagnosticInfo[] Errors { get; set; }
}
