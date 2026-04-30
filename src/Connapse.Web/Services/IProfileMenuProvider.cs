namespace Connapse.Web.Services;

public record ProfileMenuItem(string Key, string Label, string Href, string Icon);

public interface IProfileMenuProvider
{
    Task<IReadOnlyList<ProfileMenuItem>> GetItemsAsync();
    string BackUrl { get; }
}
