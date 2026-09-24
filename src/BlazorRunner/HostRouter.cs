using System;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;

/// <summary>
/// A fallback root for projects that declare <c>@page</c> components but no component to host them:
/// it routes over the assembly the project compiled into.
///
/// A project can also bring its own <c>App.razor</c> with a <c>&lt;Router&gt;</c> (as a Blazor app
/// normally does), in which case this is not used.
/// </summary>
public class HostRouter : ComponentBase
{
    /// <summary>The assembly whose <see cref="RouteAttribute"/>s should be routed to.</summary>
    public static Assembly RoutesAssembly { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var seq = 0;

        builder.OpenComponent<Router>(seq++);
        builder.AddAttribute(seq++, "AppAssembly", RoutesAssembly);
        builder.AddAttribute(seq++, "Found", (RenderFragment<RouteData>)(routeData => view =>
        {
            view.OpenComponent<RouteView>(0);
            view.AddAttribute(1, "RouteData", routeData);
            // Pages that declare @layout still win; this is only the default.
            view.AddAttribute(2, "DefaultLayout", typeof(HostLayout));
            view.CloseComponent();
        }));
        builder.AddAttribute(seq++, "NotFound", (RenderFragment)(notFound =>
        {
            notFound.OpenElement(0, "p");
            notFound.AddContent(1, "No component matches this route.");
            notFound.CloseElement();
        }));

        builder.CloseComponent();
    }
}
