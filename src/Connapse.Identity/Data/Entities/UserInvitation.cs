namespace Connapse.Identity.Data.Entities;

public class UserInvitation
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string Role { get; set; } = "Viewer";
    public string TokenHash { get; set; } = "";
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public Guid? AcceptedByUserId { get; set; }

    // Navigation properties
    public ConnapseUser? CreatedByUser { get; set; }
    public ConnapseUser? AcceptedByUser { get; set; }
}
