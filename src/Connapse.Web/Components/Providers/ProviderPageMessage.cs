using Microsoft.AspNetCore.Components;

namespace Connapse.Web.Components.Providers;

/// <summary>
/// An outcome shown in <see cref="ProviderPageAlert"/> at the top of the Providers page.
/// </summary>
/// <param name="Text">What happened, in a sentence or two.</param>
/// <param name="Success">Whether it succeeded; a failure is announced at once.</param>
/// <param name="Extra">Links or a disclosure shown after the text, if any.</param>
public sealed record ProviderPageMessage(string Text, bool Success, RenderFragment? Extra = null);
