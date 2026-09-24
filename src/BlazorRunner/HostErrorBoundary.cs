using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

/// <summary>
/// An <see cref="ErrorBoundary"/> that keeps the exception it caught, so the host can report it
/// to the page instead of leaving the user with a blank area and a successful-looking result.
/// </summary>
public class HostErrorBoundary : ErrorBoundary
{
    public Exception LastError { get; private set; }

    protected override Task OnErrorAsync(Exception exception)
    {
        LastError = exception;
        return Task.CompletedTask;
    }
}
