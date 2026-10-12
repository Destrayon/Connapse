using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Connapse.Web.Tests.Components;

/// <summary>Which element a component last moved focus to, through ElementReference.FocusAsync.</summary>
internal static class FocusAssertions
{
    private const string FocusIdentifier = "Blazor._internal.domWrapper.focus";

    /// <summary>The element reference of the last focus call, or null when nothing was focused.</summary>
    public static string? LastFocusedRef(this BunitContext ctx)
    {
        var call = ctx.JSInterop.Invocations.LastOrDefault(i => i.Identifier == FocusIdentifier);
        return call.Identifier is not null && call.Arguments.Count > 0 && call.Arguments[0] is ElementReference target
            ? target.Id
            : null;
    }

    /// <summary>
    /// The element of the last focus call, found by the element reference bUnit renders on it;
    /// null when nothing was focused or the element is no longer in the markup. bUnit renders an
    /// empty reference on elements of CSS-isolated components, so for those compare
    /// <see cref="LastFocusedRef"/> with <see cref="RefField"/> instead.
    /// </summary>
    public static IElement? LastFocused<T>(this BunitContext ctx, IRenderedComponent<T> cut) where T : IComponent
    {
        string? id = ctx.LastFocusedRef();
        return id is null ? null : cut.FindAll("*").FirstOrDefault(e => e.GetAttribute("blazor:elementReference") == id);
    }

    /// <summary>The id of a component's private element reference field.</summary>
    public static string? RefField(object component, string field) =>
        (component.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(component) as ElementReference?)?.Id;
}
