using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

/// <summary>
/// Compiles and runs a C# console program in-process (the capability the existing
/// 'csharp-wasm' bundle provides). Console output is captured and stdin is fed from the page,
/// so the same bundle can serve both the C# language and Blazor components.
/// </summary>
public static class ConsoleRunner
{
    public static async Task<RunResult> Run(string source, string stdin)
    {
        if (ReferenceAssemblies.Count == 0)
        {
            throw new InvalidOperationException("No reference assemblies loaded.");
        }

        if (!CSharpInProcess.TryCompile(source, OutputKind.ConsoleApplication, "Program.cs", out var assembly, out _, out var errors))
        {
            return new RunResult { Success = false, Output = "", Errors = errors };
        }

        var entryPoint = assembly.EntryPoint;
        if (entryPoint is null)
        {
            return new RunResult
            {
                Success = false,
                Output = "",
                Errors = new[] { DiagnosticInfo.Error("CS5001", "No entry point found. A C# program needs a static Main method.") },
            };
        }

        var writer = new StringWriter();
        var oldOut = Console.Out;
        var oldError = Console.Error;

        Console.SetOut(writer);
        Console.SetError(writer);
        // Only SetIn: on the WebAssembly runtime the Console.In *getter* throws
        // PlatformNotSupportedException, so it is never read or restored.
#pragma warning disable CA1416 // Console.SetIn does work on browser-wasm; only the getter throws.
        Console.SetIn(new StringReader(stdin ?? ""));
#pragma warning restore CA1416

        try
        {
            var parameters = entryPoint.GetParameters();
            var arguments = parameters.Length == 0
                ? Array.Empty<object>()
                : new object[] { Array.Empty<string>() };

            var result = entryPoint.Invoke(null, arguments);
            if (result is Task task)
            {
                await task;
            }
            else if (result is int exitCode && exitCode != 0)
            {
                writer.WriteLine($"(exit code: {exitCode})");
            }

            return new RunResult { Success = true, Output = writer.ToString(), Errors = Array.Empty<DiagnosticInfo>() };
        }
        catch (Exception ex)
        {
            // Reflection wraps the program's own exception; report the real one.
            var inner = ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : ex;
            return new RunResult
            {
                Success = false,
                Output = writer.ToString(),
                Errors = new[] { DiagnosticInfo.Error(inner.GetType().Name, inner.Message) },
            };
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }
}
