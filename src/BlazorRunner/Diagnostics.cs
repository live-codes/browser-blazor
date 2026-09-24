using Microsoft.CodeAnalysis;

/// <summary>A compiler or runtime error, in the shape the page consumes.</summary>
public sealed class DiagnosticInfo
{
    public string Id { get; set; }
    public string Message { get; set; }
    public string Severity { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }

    public static DiagnosticInfo From(Diagnostic diagnostic)
    {
        // Mapped so diagnostics from generated Razor code point back at the .razor source,
        // which the generator marks up with #line directives.
        var span = diagnostic.Location.GetMappedLineSpan();
        return new DiagnosticInfo
        {
            Id = diagnostic.Id,
            Message = diagnostic.GetMessage(),
            Severity = diagnostic.Severity.ToString(),
            Line = span.StartLinePosition.Line + 1,
            Column = span.StartLinePosition.Character + 1,
        };
    }

    public static DiagnosticInfo Error(string id, string message) =>
        new DiagnosticInfo { Id = id, Message = message, Severity = "Error" };
}

/// <summary>Result of compiling a component: the resolved root type, or the errors.</summary>
public sealed class CompileResult
{
    public System.Type Type { get; set; }

    /// <summary>Size of the emitted assembly, for reporting.</summary>
    public int Bytes { get; set; }

    public DiagnosticInfo[] Errors { get; set; }
}

/// <summary>Result of running a console program: captured stdout, or the errors.</summary>
public sealed class RunResult
{
    public bool Success { get; set; }
    public string Output { get; set; }
    public DiagnosticInfo[] Errors { get; set; }
}
