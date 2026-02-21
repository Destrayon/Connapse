using Microsoft.AspNetCore.Identity;

namespace Connapse.Identity.Data.Entities;

public class ConnapseRole : IdentityRole<Guid>
{
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}
