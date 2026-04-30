namespace Connapse.Web.Services;

public class DefaultProfileMenuProvider : IProfileMenuProvider
{
    private static readonly IReadOnlyList<ProfileMenuItem> Items =
    [
        new ProfileMenuItem("profile", "Profile", "/profile", "bi-person-fill"),
        new ProfileMenuItem("integrations", "Integrations", "/profile/integrations", "bi-plug-fill"),
    ];

    public Task<IReadOnlyList<ProfileMenuItem>> GetItemsAsync() => Task.FromResult(Items);

    public string BackUrl => "/";
}
