using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

/// <summary>
/// The app's fixed root component. It renders whatever component the user last compiled, so the
/// user's component participates in Blazor's real, interactive render tree.
/// </summary>
public class DynamicHost : ComponentBase
{
    /// <summary>Set on initialisation so the (static) JS interop entry point can reach the live instance.</summary>
    public static DynamicHost Current { get; private set; }

    [Inject]
    public NavigationManager Navigation { get; set; }

    HostErrorBoundary _boundary;
    Type _componentType;
    string _styles;
    int _renderId;
    TaskCompletionSource<bool> _rendered;

    protected override void OnInitialized() => Current = this;

    /// <summary>The exception the last render threw, if any.</summary>
    public Exception LastRenderError => _boundary?.LastError;

    /// <summary>Navigates the host, so <c>@page</c> routing can be driven from the page.</summary>
    public void NavigateTo(string url) =>
        _ = InvokeAsync(() => Navigation?.NavigateTo(url));

    /// <summary>Renders <paramref name="type"/> on the renderer's dispatcher and waits for the
    /// render pass to finish. <paramref name="styles"/> is the project's scoped CSS, if any.</summary>
    public async Task ShowAsync(Type type, string styles)
    {
        var rendered = new TaskCompletionSource<bool>();
        _rendered = rendered;

        await InvokeAsync(() =>
        {
            _componentType = type;
            _styles = styles;
            // Re-key the boundary so a component that already faulted starts clean.
            _renderId++;
            StateHasChanged();
        });

        // Waiting for the pass is what makes a component that throws while rendering reportable
        // rather than silently swallowed. Capped so a stuck render cannot hang the caller.
        await Task.WhenAny(rendered.Task, Task.Delay(5000));
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        _rendered?.TrySetResult(true);
        return Task.CompletedTask;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var seq = 0;

        // The project's scoped CSS rides along with the component it belongs to.
        if (!string.IsNullOrEmpty(_styles))
        {
            builder.OpenElement(seq++, "style");
            builder.AddContent(seq++, _styles);
            builder.CloseElement();
        }

        // Without a boundary, a component that throws while rendering takes down the whole
        // renderer — and with it the page. The boundary contains the damage to this subtree.
        builder.OpenComponent<HostErrorBoundary>(seq++);
        builder.SetKey(_renderId);
        builder.AddAttribute(seq++, "ChildContent", (RenderFragment)(child =>
        {
            if (_componentType is null)
            {
                child.OpenElement(0, "p");
                child.AddContent(1, "Nothing rendered yet.");
                child.CloseElement();
                return;
            }

            child.OpenComponent(0, _componentType);
            child.CloseComponent();
        }));
        // Must come after the attributes: a capture adds a frame, and an attribute may only be
        // added immediately after the element/component frame.
        builder.AddComponentReferenceCapture(seq++, captured => _boundary = (HostErrorBoundary)captured);

        builder.CloseComponent();
    }
}
