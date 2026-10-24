namespace Connapse.Application.Links;

/// <summary>Every account-link operation in one place, for the profile integrations page and its cards.</summary>
public sealed class AccountLinkOperations(ListAccountLinks list, UnlinkAccount unlink)
{
    public ListAccountLinks List { get; } = list;

    public UnlinkAccount Unlink { get; } = unlink;
}
