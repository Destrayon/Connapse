# Per-user AWS search permissions — 5b-i, connecting an AWS identity

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A signed-in Connapse user can connect their AWS identity from the integrations page, and Connapse ends up holding an encrypted Cognito refresh token against that user — the durable thing every later permission resolution is built on.

**Architecture:** Connapse sign-in is untouched. This is an integration, not an authentication method: a plain OAuth 2.0 authorization-code flow with PKCE that Connapse drives against the customer's Cognito user pool, mirroring the `/azure/connect` + `/azure/callback` pair that already exists in `CloudIdentityEndpoints`. The refresh token is encrypted with the Data Protection key ring already persisted to the `appdata` volume.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, Blazor Server, EF Core with Npgsql, ASP.NET Core Data Protection, xUnit, FluentAssertions, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-08-27-per-user-aws-search-permissions-design.md` — components 2 and 3.

## Global Constraints

- .NET 10, file-scoped namespaces, nullable enabled, implicit usings.
- Records for DTOs; primary constructors for DI.
- Async all the way — never `.Result` or `.Wait()`.
- Always use `IDbContextFactory<T>` and short-lived contexts. Never share a scoped `DbContext` across threads.
- Do not use `var` for primitive types. Do not use `dynamic`.
- Parameterized SQL only.
- Tag every test `[Trait("Category", "Unit")]` or `[Trait("Category", "Integration")]`.
- Test naming: `MethodName_Scenario_ExpectedResult`.
- Wrap user-controlled values in logs with `LogSanitizer.Sanitize(...)` from `Connapse.Core.Utilities`.
- **Never log a token, an authorization code, a code verifier, or a client secret** — not at Debug, not in an exception message.
- Commit messages: `<type>: <summary>` using feat/fix/docs/test/refactor/perf/chore.
- Migrations: `dotnet ef migrations add <Name> --project src/Connapse.Identity` for the identity context.

## What this plan deliberately does not do

Refreshing the stored token, the weekly touch job that stops idle expiry, and surfacing a broken link are **5b-ii**. This plan ends when a token is stored and can be read back decrypted. Resolving scopes from it is 5d.

## File Structure

| File | Responsibility |
|---|---|
| `src/Connapse.Core/Models/CognitoSettings.cs` *(new)* | The pool's coordinates: issuer URL, hosted domain, client id, client secret, region. |
| `src/Connapse.Identity/Data/Entities/UserAwsIdentityLinkEntity.cs` *(new)* | One row per user: the encrypted refresh token, the email it was issued for, and when. |
| `src/Connapse.Identity/Data/ConnapseIdentityDbContext.cs` *(modify)* | Expose the new set and configure the unique index. |
| `src/Connapse.Identity/Services/AwsIdentityLinkStore.cs` *(new)* | Encrypt on write, decrypt on read, delete. The only code that touches the plaintext token. |
| `src/Connapse.Web/Endpoints/CloudIdentityEndpoints.cs` *(modify)* | `/cognito/connect` and `/cognito/callback`, mirroring the Azure pair. |
| `src/Connapse.Web/Components/Pages/ProfileIntegrations.razor` *(modify)* | A card to connect, show status, and disconnect. |

---

### Task 1: The pool's coordinates

Connapse needs to know which Cognito pool to talk to before anything else can happen. This mirrors `AwsSsoSettings`, which is the closest existing shape.

**Files:**
- Create: `src/Connapse.Core/Models/CognitoSettings.cs`
- Test: `tests/Connapse.Core.Tests/Models/CognitoSettingsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Connapse.Core.CognitoSettings` with `SectionName`, `IssuerUrl`, `Domain`, `ClientId`, `ClientSecret`, `Region`, and `bool IsConfigured`.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Core.Tests/Models/CognitoSettingsTests.cs`:

```csharp
using Connapse.Core;
using FluentAssertions;
using Xunit;

namespace Connapse.Core.Tests.Models;

/// <summary>
/// Whether a deployment has enough to talk to a Cognito user pool.
/// </summary>
[Trait("Category", "Unit")]
public class CognitoSettingsTests
{
    private static CognitoSettings Complete() => new()
    {
        IssuerUrl = "https://cognito-idp.us-west-1.amazonaws.com/us-west-1_abc123",
        Domain = "https://connapse.auth.us-west-1.amazoncognito.com",
        ClientId = "3ia37m5mg4rtioih2slv8etmed",
        ClientSecret = "shh",
        Region = "us-west-1",
    };

    [Fact]
    public void IsConfigured_WithEveryField_IsTrue()
    {
        Complete().IsConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData("http://cognito-idp.us-west-1.amazonaws.com/us-west-1_abc123")]
    [InlineData("http://connapse.auth.us-west-1.amazoncognito.com")]
    public void IsConfigured_WithAPlainHttpUrl_IsFalse(string insecure)
    {
        // Both URLs carry a credential — an authorization code on one, a token on the other — so
        // a plain-HTTP hop puts it on the wire in cleartext. Cognito refuses these too, so the
        // only thing accepting them buys is a rejection from AWS instead of an explanation here.
        var settings = Complete();
        settings.Domain = insecure;

        settings.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_WithLoopbackHttp_IsTrue()
    {
        // The one exception, and Cognito makes it too: a single-machine deployment has no TLS to
        // terminate and nothing on the wire to intercept.
        var settings = Complete();
        settings.Domain = "http://localhost:5001";

        settings.IsConfigured.Should().BeTrue();
    }

    [Theory]
    [InlineData("IssuerUrl")]
    [InlineData("Domain")]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("Region")]
    public void IsConfigured_WithAnyFieldMissing_IsFalse(string missing)
    {
        // Every field is load-bearing, and a half-configured pool fails at a different step
        // depending on which half is missing — the redirect 404s, or the token exchange 401s,
        // or the issuer does not match what Identity Center trusts. One check up front is
        // cheaper to explain than five failures spread across the flow.
        var settings = Complete();
        typeof(CognitoSettings).GetProperty(missing)!.SetValue(settings, "");

        settings.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_WithWhitespaceOnly_IsFalse()
    {
        // A settings row saved from a form with a stray space is not configuration.
        var settings = Complete();
        settings.ClientId = "   ";

        settings.IsConfigured.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~CognitoSettingsTests"`
Expected: FAIL — `CognitoSettings` does not exist.

- [ ] **Step 3: Implement**

Create `src/Connapse.Core/Models/CognitoSettings.cs`:

```csharp
namespace Connapse.Core;

/// <summary>
/// The customer's Amazon Cognito user pool — the identity provider they host inside their own AWS
/// account so that Connapse can prove which AWS identity a user is.
/// </summary>
/// <remarks>
/// A mutable class with a <c>SectionName</c> rather than a record, matching <see cref="AwsSsoSettings"/>
/// and every other settings category: they are bound by the options system and edited by an admin
/// form, both of which want settable properties.
/// <para>
/// <see cref="ClientSecret"/> is a secret at rest in the settings table like every other provider
/// credential here. It is not a per-user secret — one pool, one client, shared by the deployment.
/// </para>
/// </remarks>
public class CognitoSettings
{
    public const string SectionName = "Identity:Cognito";

    /// <summary>
    /// The pool's OIDC issuer, <c>https://cognito-idp.{region}.amazonaws.com/{poolId}</c>.
    /// </summary>
    /// <remarks>
    /// This exact string is what an issued token carries as its <c>iss</c> claim and what the
    /// Identity Center trusted token issuer is registered against. A mismatch between the two is
    /// rejected at exchange time with an error that names neither side.
    /// </remarks>
    public string IssuerUrl { get; set; } = string.Empty;

    /// <summary>
    /// The pool's hosted UI domain, which is where <c>/oauth2/authorize</c> lives.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="IssuerUrl"/> and genuinely needed: the token exchange against
    /// Identity Center works on a pool with no domain, but the browser redirect that starts this
    /// flow does not exist without one.
    /// </remarks>
    public string Domain { get; set; } = string.Empty;

    /// <summary>The app client id. Also the audience the Identity Center grant authorises.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    /// <summary>True when every field needed to complete a connection is present and usable.</summary>
    /// <remarks>
    /// The URLs must be HTTPS, with <c>http://localhost</c> the only exception. Both carry an
    /// authorization code or a token, so a plain-HTTP hop puts a credential on the wire in
    /// cleartext — and a non-empty check alone would happily accept one. Cognito enforces the same
    /// rule on its side for callback URLs, so anything else was never going to work anyway; failing
    /// here says why, rather than leaving the operator with a rejection from AWS.
    /// </remarks>
    public bool IsConfigured =>
        IsSecureUrl(IssuerUrl)
        && IsSecureUrl(Domain)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(Region);

    /// <summary>HTTPS, or loopback HTTP for a single-machine deployment.</summary>
    internal static bool IsSecureUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps
            || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Connapse.Core.Tests --filter "FullyQualifiedName~CognitoSettingsTests"`
Expected: PASS — 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Connapse.Core/Models/CognitoSettings.cs tests/Connapse.Core.Tests/Models/CognitoSettingsTests.cs
git commit -m "feat(identity): settings for the customer's Cognito user pool"
```

---

### Task 2: Somewhere to keep the token

One row per user holding an encrypted refresh token. A new entity rather than a third `CloudProvider` on `UserCloudIdentityEntity`, because that one stores plaintext metadata in an `IdentityDataJson` column and a refresh token must never live there.

**Files:**
- Create: `src/Connapse.Identity/Data/Entities/UserAwsIdentityLinkEntity.cs`
- Modify: `src/Connapse.Identity/Data/ConnapseIdentityDbContext.cs`
- Test: `tests/Connapse.Identity.Tests/UserAwsIdentityLinkEntityTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Connapse.Identity.Data.Entities.UserAwsIdentityLinkEntity` with `Guid Id`, `Guid UserId`, `string Email`, `string ProtectedRefreshToken`, `DateTime ConnectedAt`, `DateTime? LastUsedAt`; and `ConnapseIdentityDbContext.UserAwsIdentityLinks`.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Identity.Tests/UserAwsIdentityLinkEntityTests.cs`:

```csharp
using Connapse.Identity.Data.Entities;
using FluentAssertions;
using Xunit;

namespace Connapse.Identity.Tests;

/// <summary>
/// The shape of a stored AWS identity link.
/// </summary>
[Trait("Category", "Unit")]
public class UserAwsIdentityLinkEntityTests
{
    [Fact]
    public void ProtectedRefreshToken_IsNamedForWhatItHolds()
    {
        // The name is the guard rail. A property called RefreshToken invites someone to assign a
        // plaintext one; "Protected" says the value has already been through Data Protection and
        // that assigning a raw token here is the bug.
        typeof(UserAwsIdentityLinkEntity).GetProperty("ProtectedRefreshToken")
            .Should().NotBeNull();
        typeof(UserAwsIdentityLinkEntity).GetProperty("RefreshToken")
            .Should().BeNull("a plaintext token must have nowhere to go");
    }

    [Fact]
    public void NewLink_DefaultsToEmptyRatherThanNull()
    {
        var link = new UserAwsIdentityLinkEntity();

        link.Email.Should().BeEmpty();
        link.ProtectedRefreshToken.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Identity.Tests --filter "FullyQualifiedName~UserAwsIdentityLinkEntityTests"`
Expected: FAIL — the type does not exist.

- [ ] **Step 3: Create the entity**

Create `src/Connapse.Identity/Data/Entities/UserAwsIdentityLinkEntity.cs`:

```csharp
namespace Connapse.Identity.Data.Entities;

/// <summary>
/// A Connapse user's connected AWS identity, and the token that lets Connapse prove it again later
/// without them present.
/// </summary>
/// <remarks>
/// Separate from <see cref="UserCloudIdentityEntity"/> on purpose. That entity records which cloud
/// account a user signed into, as plaintext metadata in a JSON column, and it predates this feature.
/// A refresh token is a per-user secret; putting one in a column built for display metadata would
/// be a mistake nothing in the type system would catch.
/// <para>
/// One row per user, enforced by a unique index. Connecting again replaces the row rather than
/// adding a second, so there is never a question of which token is current.
/// </para>
/// </remarks>
public class UserAwsIdentityLinkEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>
    /// The verified email the token was issued for, and the join key into IAM Identity Center.
    /// </summary>
    /// <remarks>
    /// Stored rather than re-read from the token because it is what a later exchange is matched on,
    /// and because it lets the integrations page say which identity is connected without decrypting
    /// anything. AWS accepts only user name, email or external ID as the claim mapped to a directory
    /// user, so the opaque OIDC subject cannot serve here.
    /// </remarks>
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The Cognito refresh token, already through ASP.NET Core Data Protection.
    /// </summary>
    /// <remarks>
    /// Named for its state so that assigning a plaintext token reads as wrong at the call site.
    /// Only <c>AwsIdentityLinkStore</c> protects and unprotects it; nothing else should hold the
    /// plaintext for longer than one exchange.
    /// </remarks>
    public string ProtectedRefreshToken { get; set; } = string.Empty;

    public DateTime ConnectedAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    public ConnapseUser User { get; set; } = null!;
}
```

- [ ] **Step 4: Expose it on the context**

In `src/Connapse.Identity/Data/ConnapseIdentityDbContext.cs`, add alongside the existing sets (near line 22):

```csharp
    public DbSet<UserAwsIdentityLinkEntity> UserAwsIdentityLinks => Set<UserAwsIdentityLinkEntity>();
```

And in `OnModelCreating`, following the pattern the neighbouring entities use:

```csharp
        builder.Entity<UserAwsIdentityLinkEntity>(e =>
        {
            e.ToTable("user_aws_identity_links");
            e.HasKey(x => x.Id);

            // One link per user: connecting again replaces it, so nothing has to decide which of
            // two stored tokens is the live one.
            e.HasIndex(x => x.UserId).IsUnique();

            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.ProtectedRefreshToken).IsRequired();

            e.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
```

Match the surrounding style — if neighbouring entities configure their table names or columns differently (snake_case conventions applied globally, for instance), follow what is already there rather than this literal block.

- [ ] **Step 5: Create the migration**

```bash
dotnet ef migrations add AddUserAwsIdentityLinks --project src/Connapse.Identity
```

Read the generated migration before continuing. It must create exactly one table with a unique index on `user_id` and a cascading foreign key, and touch nothing else. If it contains unrelated changes, the model has drifted from the last migration — stop and report that rather than committing it.

- [ ] **Step 6: Run to verify it passes and the solution builds**

Run: `dotnet build && dotnet test tests/Connapse.Identity.Tests --filter "FullyQualifiedName~UserAwsIdentityLinkEntityTests"`
Expected: build clean, 2 tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Connapse.Identity/Data/Entities/UserAwsIdentityLinkEntity.cs src/Connapse.Identity/Data/ConnapseIdentityDbContext.cs src/Connapse.Identity/Migrations tests/Connapse.Identity.Tests/UserAwsIdentityLinkEntityTests.cs
git commit -m "feat(identity): a table for a user's connected AWS identity"
```

---

### Task 3: Encrypt on the way in, decrypt on the way out

The one place that handles a plaintext refresh token. Everything else deals in the entity.

**Files:**
- Create: `src/Connapse.Identity/Services/AwsIdentityLinkStore.cs`
- Modify: `src/Connapse.Identity/IdentityServiceExtensions.cs` (registration)
- Test: `tests/Connapse.Integration.Tests/AwsIdentityLinkStoreTests.cs`

**Interfaces:**
- Consumes: `UserAwsIdentityLinkEntity`, `ConnapseIdentityDbContext` from Task 2; `IDataProtectionProvider`.
- Produces: `Connapse.Identity.Services.AwsIdentityLinkStore` with
  `Task SaveAsync(Guid userId, string email, string refreshToken, CancellationToken ct = default)`,
  `Task<string?> GetRefreshTokenAsync(Guid userId, CancellationToken ct = default)`,
  `Task<UserAwsIdentityLinkEntity?> GetAsync(Guid userId, CancellationToken ct = default)`,
  `Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Integration.Tests/AwsIdentityLinkStoreTests.cs`. Follow the collection and fixture shape used by the neighbouring integration tests in this directory — the collection name is the literal `"Integration Tests"`:

```csharp
using Connapse.Identity.Data;
using Connapse.Identity.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Storing a per-user refresh token so that only this deployment can read it back.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class AwsIdentityLinkStoreTests(SharedWebAppFixture fixture)
{
    private static AwsIdentityLinkStore Build(IServiceProvider sp) =>
        sp.GetRequiredService<AwsIdentityLinkStore>();

    private async Task<Guid> SeedUserAsync(IServiceProvider sp)
    {
        // A real user row, because the link table has a cascading foreign key to it. Create the
        // user the way the neighbouring identity integration tests do.
        var factory = sp.GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var user = new Connapse.Identity.Data.Entities.ConnapseUser
        {
            Id = Guid.NewGuid(),
            UserName = $"u-{Guid.NewGuid():N}@example.com",
            Email = $"u-{Guid.NewGuid():N}@example.com",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task SaveAsync_ThenGetRefreshTokenAsync_RoundTripsThePlaintext()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = Build(scope.ServiceProvider);

        await store.SaveAsync(userId, "person@example.com", "the-refresh-token");

        (await store.GetRefreshTokenAsync(userId)).Should().Be("the-refresh-token");
    }

    [Fact]
    public async Task SaveAsync_DoesNotStoreThePlaintext()
    {
        // The point of the whole class. Asserting the round trip alone would pass just as happily
        // against an implementation that wrote the token straight to the column.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = Build(scope.ServiceProvider);

        await store.SaveAsync(userId, "person@example.com", "the-refresh-token");

        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var row = await db.UserAwsIdentityLinks.SingleAsync(x => x.UserId == userId);

        row.ProtectedRefreshToken.Should().NotBe("the-refresh-token");
        row.ProtectedRefreshToken.Should().NotContain("the-refresh-token");
        row.Email.Should().Be("person@example.com");
    }

    [Fact]
    public async Task SaveAsync_CalledTwice_ReplacesRatherThanAdds()
    {
        // Connecting again must not leave two rows, or something later has to decide which token
        // is live and there is no correct way to choose.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = Build(scope.ServiceProvider);

        await store.SaveAsync(userId, "first@example.com", "first-token");
        await store.SaveAsync(userId, "second@example.com", "second-token");

        var factory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<ConnapseIdentityDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        (await db.UserAwsIdentityLinks.CountAsync(x => x.UserId == userId)).Should().Be(1);
        (await store.GetRefreshTokenAsync(userId)).Should().Be("second-token");
        (await store.GetAsync(userId))!.Email.Should().Be("second@example.com");
    }

    [Fact]
    public async Task GetRefreshTokenAsync_ForAnUnconnectedUser_IsNull()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);

        (await Build(scope.ServiceProvider).GetRefreshTokenAsync(userId)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheRow_AndIsSafeToRepeat()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = await SeedUserAsync(scope.ServiceProvider);
        var store = Build(scope.ServiceProvider);
        await store.SaveAsync(userId, "person@example.com", "the-refresh-token");

        (await store.DeleteAsync(userId)).Should().BeTrue();
        (await store.DeleteAsync(userId)).Should().BeFalse("nothing was left to delete");
        (await store.GetRefreshTokenAsync(userId)).Should().BeNull();
    }
}
```

If `ConnapseUser` requires more properties than `Id`, `UserName` and `Email` to save, populate them the way the neighbouring identity integration tests do rather than inventing values.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~AwsIdentityLinkStoreTests"`
Expected: FAIL — `AwsIdentityLinkStore` does not exist. Docker must be running.

- [ ] **Step 3: Implement**

Create `src/Connapse.Identity/Services/AwsIdentityLinkStore.cs`:

```csharp
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Connapse.Identity.Services;

/// <summary>
/// Reads and writes a user's connected AWS identity, and is the only code that sees the refresh
/// token in plaintext.
/// </summary>
/// <remarks>
/// Encryption uses the Data Protection key ring already configured in <c>Program.cs</c>, which is
/// persisted to the <c>appdata</c> volume — so a stored token survives a container restart. It is
/// worth knowing what that does and does not buy: the ring is not itself encrypted at rest, so this
/// protects a token against someone reading the database, not against someone with the volume.
/// <para>
/// The purpose string is deliberately specific. Data Protection derives a distinct key from it, so
/// a payload protected here cannot be unprotected by any other part of the application even though
/// they share a key ring.
/// </para>
/// </remarks>
public sealed class AwsIdentityLinkStore(
    IDbContextFactory<ConnapseIdentityDbContext> factory,
    IDataProtectionProvider protectionProvider,
    TimeProvider timeProvider)
{
    private const string Purpose = "Connapse.AwsIdentityLink.RefreshToken.v1";

    private IDataProtector Protector => protectionProvider.CreateProtector(Purpose);

    /// <summary>Stores a user's link, replacing any existing one.</summary>
    public async Task SaveAsync(
        Guid userId, string email, string refreshToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.UserAwsIdentityLinks
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);

        // Replace rather than add: the unique index would reject a second row anyway, and an
        // upsert keeps ConnectedAt meaning "when this link was established".
        if (existing is null)
        {
            db.UserAwsIdentityLinks.Add(new UserAwsIdentityLinkEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Email = email,
                ProtectedRefreshToken = Protector.Protect(refreshToken),
                ConnectedAt = timeProvider.GetUtcNow().UtcDateTime,
            });
        }
        else
        {
            existing.Email = email;
            existing.ProtectedRefreshToken = Protector.Protect(refreshToken);
            existing.ConnectedAt = timeProvider.GetUtcNow().UtcDateTime;
            existing.LastUsedAt = null;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The link's metadata, or null when the user has not connected one.</summary>
    public async Task<UserAwsIdentityLinkEntity?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.UserAwsIdentityLinks
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);
    }

    /// <summary>The plaintext refresh token, or null when there is no usable link.</summary>
    /// <remarks>
    /// Returns null rather than throwing when the stored payload cannot be unprotected. That
    /// happens for one real reason — the key ring was lost or rotated beyond its retention — and a
    /// caller cannot do anything about it except treat the link as absent and ask the user to
    /// reconnect, which is exactly what null already means here.
    /// </remarks>
    public async Task<string?> GetRefreshTokenAsync(Guid userId, CancellationToken ct = default)
    {
        var link = await GetAsync(userId, ct);
        if (link is null)
            return null;

        try
        {
            return Protector.Unprotect(link.ProtectedRefreshToken);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Removes a user's link. False when there was nothing to remove.</summary>
    public async Task<bool> DeleteAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);

        var existing = await db.UserAwsIdentityLinks
            .SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (existing is null)
            return false;

        db.UserAwsIdentityLinks.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
```

- [ ] **Step 4: Register it**

In `src/Connapse.Identity/IdentityServiceExtensions.cs`, alongside the existing service registrations:

```csharp
        services.AddScoped<AwsIdentityLinkStore>();
```

`TimeProvider.System` is already registered as a singleton at `src/Connapse.Web/Program.cs:159`, so the constructor's `TimeProvider` resolves without anything further.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet build && dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~AwsIdentityLinkStoreTests"`
Expected: build clean, 5 tests PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Identity/Services/AwsIdentityLinkStore.cs src/Connapse.Identity/IdentityServiceExtensions.cs tests/Connapse.Integration.Tests/AwsIdentityLinkStoreTests.cs
git commit -m "feat(identity): encrypt a user's AWS refresh token at rest"
```

---

### Task 4: The connect flow

Two endpoints mirroring the `/azure/connect` and `/azure/callback` pair that already exists in the same file. Read those first — they establish how state is carried, how the user is resolved, and how errors come back to the page.

**Files:**
- Modify: `src/Connapse.Web/Endpoints/CloudIdentityEndpoints.cs`
- Test: `tests/Connapse.Integration.Tests/CognitoConnectEndpointTests.cs`

**Interfaces:**
- Consumes: `CognitoSettings` (Task 1), `AwsIdentityLinkStore` (Task 3).
- Produces: `GET /api/v1/auth/cloud/cognito/connect` → 302 to the pool's authorize endpoint; `GET /api/v1/auth/cloud/cognito/callback` → exchanges the code and stores the token.

- [ ] **Step 1: Read the Azure pair**

Read `src/Connapse.Web/Endpoints/CloudIdentityEndpoints.cs` lines 40–105 — `/azure/connect` and `/azure/callback`. Match their conventions exactly: how they generate and validate `state`, where they keep the code verifier between the two calls, how they get the current user, and what they redirect to on success and on failure. Do not invent a different approach; a second convention in the same file is worse than an imperfect one repeated.

- [ ] **Step 2: Write the failing test**

Create `tests/Connapse.Integration.Tests/CognitoConnectEndpointTests.cs`. These assert what can be checked without a live Cognito pool — the redirect's shape, and that the callback refuses what it should:

```csharp
using System.Net;
using FluentAssertions;
using Xunit;

namespace Connapse.Integration.Tests;

/// <summary>
/// Starting and finishing a Cognito connection.
/// </summary>
/// <remarks>
/// The happy-path callback cannot be tested here: completing it needs an authorization code that
/// only a real Cognito pool issues. What is testable, and what these cover, is everything the
/// endpoint decides on its own — whether it redirects at all, what it redirects to, and what it
/// refuses.
/// </remarks>
[Trait("Category", "Integration")]
[Collection("Integration Tests")]
public class CognitoConnectEndpointTests(SharedWebAppFixture fixture)
{
    [Fact]
    public async Task Connect_WhenCognitoIsNotConfigured_DoesNotRedirectToAPoolThatDoesNotExist()
    {
        // A deployment with no Cognito settings must fail in a way that says so. Redirecting to a
        // half-built URL sends the user to a 404 on a domain they have never heard of.
        var client = fixture.Factory.CreateClient(new()
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/v1/auth/cloud/cognito/connect");

        response.StatusCode.Should().NotBe(HttpStatusCode.Redirect,
            "there is nowhere valid to redirect to");
    }

    [Fact]
    public async Task Callback_WithNoState_IsRejected()
    {
        // An unsolicited callback is either a bug or an attempt to plant a token against someone
        // else's account. Either way it is not a connection.
        var client = fixture.Factory.CreateClient(new()
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/v1/auth/cloud/cognito/callback?code=abc");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Callback_WithMismatchedState_IsRejected()
    {
        var client = fixture.Factory.CreateClient(new()
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync(
            "/api/v1/auth/cloud/cognito/callback?code=abc&state=not-a-state-we-issued");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
```

**These tests as written are vacuous, and fixing that is part of this step.** The route uses `RequireAuthorization()`, so an unauthenticated client gets 401 — and `NotBe(Redirect)` and `NotBe(OK)` both accept 401 and 404, so all three would pass against a deployment where the endpoint does not exist at all. Before finishing:

- **Authenticate the client** the way the neighbouring endpoint tests do, so the request reaches the handler rather than stopping at the auth middleware.
- **Assert the specific contract**, not the absence of one code. Decide what each case returns — 409 for unconfigured, 400 for a bad or missing state — and assert that exact status. Match whatever the Azure pair does for the same conditions.
- **Assert `NotBe(HttpStatusCode.NotFound)` explicitly** in each test, so a route that was never registered fails loudly instead of passing.
- **The mismatched-state test must first start a real connection**, so a valid state exists to mismatch against. Comparing against a state nobody issued tests a different, easier branch.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~CognitoConnectEndpointTests"`
Expected: FAIL — the routes do not exist (404 where the tests expect a refusal that is not 200, some will pass vacuously; confirm each fails for the right reason before continuing).

- [ ] **Step 4: Implement the two endpoints**

In `src/Connapse.Web/Endpoints/CloudIdentityEndpoints.cs`, add beside the Azure pair. The shape:

```csharp
        // Mirrors /azure/connect deliberately. A second convention for the same job in the same
        // file costs more than reusing an imperfect one.
        group.MapGet("/cognito/connect", (
            HttpContext http,
            IOptionsMonitor<CognitoSettings> settings) =>
        {
            var cognito = settings.CurrentValue;
            if (!cognito.IsConfigured)
                return Results.Problem(
                    "Cognito is not configured. An administrator sets it up under Settings.",
                    statusCode: StatusCodes.Status409Conflict);

            // PKCE: the verifier never leaves this deployment, and the challenge is what Cognito
            // holds until the callback proves possession of the verifier that produced it.
            string verifier = GenerateCodeVerifier();
            string challenge = ToCodeChallenge(verifier);
            string state = GenerateState();

            StashVerifierAndState(http, verifier, state);

            string authorize =
                $"{cognito.Domain.TrimEnd('/')}/oauth2/authorize" +
                $"?response_type=code" +
                $"&client_id={Uri.EscapeDataString(cognito.ClientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(CallbackUri(http))}" +
                $"&scope={Uri.EscapeDataString("openid email offline_access")}" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                $"&code_challenge_method=S256";

            return Results.Redirect(authorize);
        }).RequireAuthorization();
```

`offline_access` is the scope that makes the connection outlive the visit — without it Cognito issues no refresh token and every later resolution would need the user present.

The callback exchanges the code at `{Domain}/oauth2/token` with `grant_type=authorization_code`, the client id and secret, the redirect URI and the stashed verifier; reads `id_token` and `refresh_token` from the response; validates that the ID token's `email_verified` claim is true and takes its `email`; then calls `AwsIdentityLinkStore.SaveAsync`. On success redirect to `/profile/integrations`; on failure redirect there with an error the page can render.

Five rules for the implementation:

1. **Validate `state` before anything else**, comparing against what was stashed, and clear it either way. A callback whose state does not match is not a connection.
2. **Validate the ID token before reading any claim from it.** Signature against the pool's JWKS at `{IssuerUrl}/.well-known/jwks.json`, `iss` against `CognitoSettings.IssuerUrl`, `aud` against `ClientId`, and lifetime. Use `Microsoft.IdentityModel.Tokens` with a `ConfigurationManager<OpenIdConnectConfiguration>` so the signing keys are fetched and cached rather than pinned.

   Worth knowing why this is belt-and-braces rather than the only line of defence: the token arrives on a direct server-to-server call to the token endpoint, over TLS, authenticated with the client secret, and OIDC Core §3.1.3.7 permits skipping signature validation in exactly that case. It is specified here anyway because the claim being read is the **join key into an authorization decision**, and a plan that says "read the email claim" without saying "validate first" will produce code that never validates at all.
3. **Bind a nonce.** Generate one in the authorization request, stash it beside the state and verifier, and check the ID token's `nonce` matches. Cheap, and it is what stops a token minted for a different request being replayed into this one.
4. **Refuse an unverified email.** `email_verified` false means the pool has not proven the address, and the address is the join key into Identity Center. Store nothing and tell the user.
5. **Never log the code, the verifier, the nonce, the client secret, or either token** — not on the error path, not in an exception message.

Add tests for a token that fails each of validation, nonce, and `email_verified`, asserting in each case that `AwsIdentityLinkStore.SaveAsync` was **not** called. A token can be forged locally with a throwaway signing key for these — the point is that a badly signed or wrongly addressed token stores nothing.

- [ ] **Step 5: Register the settings category — in three places, not one**

Settings reach `IOptionsMonitor<CognitoSettings>.CurrentValue` only if all three of these are done. Miss the second and the endpoint returns 409 forever no matter what an admin saves, with nothing to indicate why.

**a. Bind the options.** In `src/Connapse.Identity/IdentityServiceExtensions.cs`, beside lines 73-74:

```csharp
        services.Configure<CognitoSettings>(configuration.GetSection(CognitoSettings.SectionName));
```

**b. Map the category to the section.** In `src/Connapse.Storage/Settings/DatabaseSettingsProvider.cs`, add to `CategoryPrefixMap` (line 20):

```csharp
        ["cognito"] = "Identity:Cognito",
```

This is load-bearing and easy to miss. That dictionary's own comment says categories not listed default to `Knowledge:{category}`, so without this line a saved `"cognito"` category lands at `Knowledge:cognito` while `CognitoSettings.SectionName` reads `Identity:Cognito`. The two never meet, `CurrentValue` stays empty, and every symptom points at the endpoint rather than at a missing dictionary entry.

**c. Expose read and write.** In `src/Connapse.Web/Endpoints/SettingsEndpoints.cs`, follow exactly what `"awssso"` does at lines 53 and 101 — a read arm and a write arm — adding a `"cognito"` case for `CognitoSettings`.

- [ ] **Step 5b: Prove the settings path end to end**

Add to `tests/Connapse.Integration.Tests/CognitoConnectEndpointTests.cs` a test that saves settings through the same path an admin uses and then asserts they arrive:

```csharp
    [Fact]
    public async Task SavedSettings_ReachTheOptionsMonitor()
    {
        // The three-place registration is invisible until it is wrong, and when it is wrong the
        // failure looks like a broken endpoint rather than a missing dictionary entry. This is the
        // only test that would catch the category prefix and the section name disagreeing.
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ISettingsStore>();

        await store.SaveAsync("cognito", JsonSerializer.Serialize(new
        {
            IssuerUrl = "https://cognito-idp.us-west-1.amazonaws.com/us-west-1_test",
            Domain = "https://example.auth.us-west-1.amazoncognito.com",
            ClientId = "client-id",
            ClientSecret = "secret",
            Region = "us-west-1",
        }));

        // Reload so the configuration provider picks the row up, the way the app does.
        scope.ServiceProvider.GetRequiredService<IConfigurationRoot>().Reload();

        var monitor = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CognitoSettings>>();
        monitor.CurrentValue.IsConfigured.Should().BeTrue();
        monitor.CurrentValue.ClientId.Should().Be("client-id");
    }
```

Match how the neighbouring settings tests save and reload — if they use a different store interface or reload mechanism, use theirs.

- [ ] **Step 6: Run to verify it passes and the solution builds**

Run: `dotnet build && dotnet test tests/Connapse.Integration.Tests --filter "FullyQualifiedName~CognitoConnectEndpointTests"`
Expected: build clean, 3 tests PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Connapse.Web/Endpoints/CloudIdentityEndpoints.cs src/Connapse.Web/Endpoints/SettingsEndpoints.cs tests/Connapse.Integration.Tests/CognitoConnectEndpointTests.cs
git commit -m "feat(web): connect an AWS identity through Cognito"
```

---

### Task 5: The card on the integrations page

Where a user actually does this. `ProfileIntegrations.razor` already has a Cloud Identities section with AWS and Azure cards, and connect/disconnect handlers — this adds a third card in the same shape.

**Files:**
- Modify: `src/Connapse.Web/Components/Pages/ProfileIntegrations.razor`
- Test: `tests/Connapse.Web.Tests/Components/AwsIdentityLinkCopyTests.cs`

**Interfaces:**
- Consumes: `AwsIdentityLinkStore` (Task 3), the endpoints from Task 4.
- Produces: no new public API.

- [ ] **Step 1: Write the failing test**

Create `tests/Connapse.Web.Tests/Components/AwsIdentityLinkCopyTests.cs`, using the `PageTestPaths.RepositoryRoot()` helper — it is defined in `tests/Connapse.Web.Tests/Components/ProvidersPageTests.cs`, the same project and namespace, so it needs no new using:

```csharp
using FluentAssertions;
using Xunit;

namespace Connapse.Web.Tests.Components;

/// <summary>
/// What the integrations page promises about a connected AWS identity.
/// </summary>
/// <remarks>
/// Wording, not markup. This page already carries a test pinning it to *not* claiming that linking
/// an identity filters search results, because it once did and that was false. The same care
/// applies to the new card: connecting stores a token so Connapse can check permissions later, and
/// saying more than that would be the same defect again.
/// </remarks>
[Trait("Category", "Unit")]
public class AwsIdentityLinkCopyTests
{
    private static readonly string Markup =
        File.ReadAllText(Path.Combine(
            PageTestPaths.RepositoryRoot(),
            "src", "Connapse.Web", "Components", "Pages", "ProfileIntegrations.razor"));

    [Fact]
    public void Page_OffersToConnectAnAwsIdentity()
    {
        Markup.Should().Contain("cognito/connect",
            "the card has to actually start the flow");
    }

    [Fact]
    public void Page_SaysWhyConnectingMatters()
    {
        Markup.Should().Contain("permission",
            "a user asked to connect an account deserves to know what it buys");
    }

    [Fact]
    public void Page_DoesNotClaimSearchIsAlreadyFiltered()
    {
        // Nothing filters until 5d registers a resolver. Promising it here would repeat exactly
        // the defect #422 removed from this same page.
        Markup.Should().NotContain("results are narrowed");
        Markup.Should().NotContain("only the documents you can");
    }

    [Fact]
    public void Page_CanBeRead()
    {
        // A source-pinning test that passes when it cannot find its subject is worse than none.
        Markup.Should().NotBeNullOrWhiteSpace();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Connapse.Web.Tests --filter "FullyQualifiedName~AwsIdentityLinkCopyTests"`
Expected: FAIL — the markup contains none of it.

- [ ] **Step 3: Add the card**

In the Cloud Identities section of `src/Connapse.Web/Components/Pages/ProfileIntegrations.razor`, add a card beside the existing AWS and Azure ones. It shows one of three states:

- **Not configured** — Cognito settings are absent. Say an administrator sets this up, and do not offer a button that cannot work.
- **Not connected** — a Connect button that navigates to `/api/v1/auth/cloud/cognito/connect`.
- **Connected** — the email it is connected as, when, and a Disconnect button.

**Disconnect must revoke, not just forget.** Deleting the local row leaves the refresh token valid at Cognito, so anything that already copied it keeps working — the link is gone from Connapse's point of view and alive from AWS's. Call the pool's `POST {Domain}/oauth2/revoke` with the token and the client credentials **before** deleting the row, and delete the row whether or not revocation succeeded: a user who clicks Disconnect must end up disconnected locally regardless of what AWS says.

If revocation fails, say so plainly — "Disconnected here, but AWS could not be told; the token stays valid until it expires" — rather than reporting a clean success. And if you decide not to implement revocation at all, then the button and the spec must both say *local unlink only*. What is not acceptable is a UI that says "disconnected" while a live token sits in someone's logs.

Copy guidance, and the reason this task has a test at all: say that connecting lets Connapse check the user's AWS permissions **when per-user filtering is switched on**, and do not imply it is filtering now. This page previously claimed linking an identity narrowed search results when nothing of the sort happened; #422 removed that claim and left a test pinning its absence. Do not reintroduce a softer version of it.

Load the current state in the same lifecycle method that already loads `awsIdentity` and `azureIdentity`, and reload after connecting or disconnecting so the card settles without a page refresh. Follow how `LoadIdentitiesAsync` and `DisconnectAsync` already work rather than adding a parallel mechanism.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet build && dotnet test tests/Connapse.Web.Tests --filter "FullyQualifiedName~AwsIdentityLinkCopyTests"`
Expected: build clean, 4 tests PASS.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: PASS. `main` was last green at 1354 unit and 397 integration; this plan adds roughly 20. Integration tests need Docker.

- [ ] **Step 6: Commit**

```bash
git add src/Connapse.Web/Components/Pages/ProfileIntegrations.razor tests/Connapse.Web.Tests/Components/AwsIdentityLinkCopyTests.cs
git commit -m "feat(web): connect an AWS identity from the integrations page"
```

---

## Verification

Automated tests cannot complete a real connection — that needs a live Cognito pool issuing an authorization code. After Task 5, verify by hand against a real pool:

1. Configure Cognito settings with a real pool's issuer, domain, client id, secret and region.
2. Register the deployment's callback URL as an allowed callback on the pool's app client. Cognito accepts `http://localhost:<port>` and otherwise requires HTTPS.
3. Sign in to Connapse as an ordinary user, go to `/profile/integrations`, and connect.
4. Confirm the card shows the connected email.
5. Confirm the stored token is not plaintext:
   `docker exec connapse-postgres-1 psql -U connapse -d connapse -c "SELECT email, left(protected_refresh_token, 24) FROM user_aws_identity_links;"`
6. Restart the container and confirm the token still decrypts — this is what proves the key ring is genuinely persisted, and it is the failure that would otherwise appear weeks later as every link breaking at once.
7. Disconnect, and confirm the row is gone.

## Risks

**A stored token is a per-user secret at rest.** The Data Protection key ring lives unencrypted on the `appdata` volume, so this protects tokens against database access, not against volume access. `ProtectKeysWithCertificate` is the lever if that is not acceptable; it is a deployment decision and out of scope here.

**Email is the join key**, and AWS permits nothing better — only user name, email or external ID map to a directory user. The callback refusing an unverified email is what stands between that and someone connecting an identity they do not own.

**Nothing here filters anything.** No resolver is registered, so search behaves exactly as it does today. That stays true until 5d, and the page must not suggest otherwise.
