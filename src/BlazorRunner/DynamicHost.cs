using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

/// <summary>
/// The app's fixed root component. It renders whatever component the user last compiled,
/// so the user's component participates in Blazor's real, interactive render tree.
/// </summary>
public class DynamicHost : ComponentBase
{
    /// <summary>Set on initialisation so the (static) JS interop entry point can reach the live instance.</summary>
    public static DynamicHost Current { get; private set; }

    Type _componentType;

    protected override void OnInitialized() => Current = this;

    /// <summary>Renders <paramref name="type"/> on the renderer's dispatcher.</summary>
    public Task ShowAsync(Type type) =>
        InvokeAsync(() =>
        {
            _componentType = type;
            StateHasChanged();
        });

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var seq = 0;

        if (_componentType is null)
        {
            builder.OpenElement(seq++, "p");
            builder.AddContent(seq++, "Nothing rendered yet.");
            builder.CloseElement();
            return;
        }

        builder.OpenComponent(seq++, _componentType);
        builder.CloseComponent();
    }
}
