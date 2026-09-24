using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

/// <summary>
/// The layout used for routed pages when neither the page's own <c>@layout</c> nor the Router names
/// one, so every page renders inside a stable wrapper. A project overrides it per page with
/// <c>@layout SomeLayout</c>, or globally via <c>RouteView.DefaultLayout</c>.
/// </summary>
public class HostLayout : LayoutComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        var seq = 0;

        builder.OpenElement(seq++, "div");
        builder.AddAttribute(seq++, "class", "host-layout");
        builder.AddContent(seq++, Body);
        builder.CloseElement();
    }
}
