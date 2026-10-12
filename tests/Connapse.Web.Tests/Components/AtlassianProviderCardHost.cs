using Connapse.Web.Components.Providers;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// The Atlassian card as the Providers page hosts it: its outcomes go to the page-top alert.
/// </summary>
internal sealed class AtlassianProviderCardHost : ComponentBase
{
    private ProviderPageMessage? message;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<ProviderPageAlert>(0);
        builder.AddComponentParameter(1, nameof(ProviderPageAlert.Message), message);
        builder.AddComponentParameter(2, nameof(ProviderPageAlert.OnDismiss), EventCallback.Factory.Create(this, () => message = null));
        builder.CloseComponent();
        builder.OpenComponent<AtlassianProviderCard>(3);
        builder.AddComponentParameter(4, nameof(AtlassianProviderCard.OnMessage),
            EventCallback.Factory.Create<ProviderPageMessage?>(this, m => message = m));
        builder.CloseComponent();
    }
}
