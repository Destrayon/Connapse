namespace Connapse.Application.Links;

/// <summary>Every account-link operation in one place, for the profile integrations page.</summary>
public sealed class AccountLinkOperations(ListAccountLinks list)
{
    public ListAccountLinks List { get; } = list;
}
