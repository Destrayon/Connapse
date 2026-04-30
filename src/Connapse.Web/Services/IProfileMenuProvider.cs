namespace Connapse.Web.Services;

public record ProfileMenuItem(string Key, string Label, string Href, string Icon);

public interface IProfileMenuProvider
{
    Task<IReadOnlyList<ProfileMenuItem>> GetItemsAsync();
    string BackUrl { get; }

    /// <summary>
    /// Brand image rendered at the top of the profile sidebar.
    /// Default: <c>connapse-logo.svg</c>. Downstream apps with their own
    /// branding (e.g. multi-tenant Cloud) override this.
    /// </summary>
    string LogoUrl => "connapse-logo.svg";
}
