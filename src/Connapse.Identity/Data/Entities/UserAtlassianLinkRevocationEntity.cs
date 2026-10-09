namespace Connapse.Identity.Data.Entities;

/// <summary>How many times a user has unlinked Atlassian. A sign-in records the generation it started
/// under, and its link is saved only while that generation is still current, in the same transaction
/// that holds this row's lock — so an unlink that has returned can never be undone by a sign-in it outlived.</summary>
public class UserAtlassianLinkRevocationEntity
{
    public Guid UserId { get; set; }
    public long Generation { get; set; }
    public ConnapseUser User { get; set; } = null!;
}
