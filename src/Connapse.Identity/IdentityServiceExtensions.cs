using System.Security.Cryptography;
using System.Text;
using Connapse.Core;
using Connapse.Core.Interfaces;
using Connapse.Identity.Authentication;
using Connapse.Identity.Authorization;
using Connapse.Identity.Data;
using Connapse.Identity.Data.Entities;
using Connapse.Identity.Services;
using Connapse.Identity.Stores;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Connapse.Identity;

public static class IdentityServiceExtensions
{
    public static IServiceCollection AddConnapseIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Database connection string not found.");

        // Register Identity DbContext with separate migration history table
        services.AddDbContext<ConnapseIdentityDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Identity")));

        // Register ASP.NET Core Identity with API endpoint support
        services.AddIdentity<ConnapseUser, ConnapseRole>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredLength = 8;
                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = false;
            })
            .AddEntityFrameworkStores<ConnapseIdentityDbContext>()
            .AddClaimsPrincipalFactory<ConnapseUserClaimsPrincipalFactory>()
            .AddDefaultTokenProviders()
            .AddApiEndpoints();

        // Register services
        services.AddScoped<PatService>();
        services.AddScoped<ITokenService, JwtTokenService>();
        services.AddScoped<AdminSeedService>();
        services.AddScoped<InviteService>();
        services.TryAddScoped<IInviteAuthorizationPolicy, DefaultInviteAuthorizationPolicy>();
        services.AddScoped<IAgentService, AgentService>();
        services.AddScoped<IAuditLogger, AuditLogger>();
        services.AddScoped<OAuthAuthCodeService>();
        services.AddHttpClient<OAuthClientService>();
        services.AddScoped<ICloudIdentityStore, Stores.PostgresCloudIdentityStore>();
        services.AddScoped<ICloudIdentityService, CloudIdentityService>();
        services.AddHttpContextAccessor();

        // Configure JWT settings
        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));
        services.Configure<AzureAdSettings>(configuration.GetSection(AzureAdSettings.SectionName));
        services.Configure<AwsSsoSettings>(configuration.GetSection(AwsSsoSettings.SectionName));

        // Ensure JWT secret is available
        EnsureJwtSecret(configuration);

        return services;
    }

    public static IServiceCollection AddConnapseAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var jwtSettings = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
            ?? new JwtSettings();

        var jwtSecret = jwtSettings.Secret
            ?? throw new InvalidOperationException(
                "JWT secret not configured. Set Identity__Jwt__Secret environment variable.");

        services.AddAuthentication(options =>
            {
                // Explicitly set DefaultAuthenticateScheme so it is not overridden by the
                // "Identity.Application" value that AddIdentity<> registers internally.
                options.DefaultScheme = "MultiScheme";
                options.DefaultAuthenticateScheme = "MultiScheme";
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                // Keep Identity.Application for sign-in/sign-out so SignInManager works normally.
                options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignOutScheme = IdentityConstants.ApplicationScheme;
            })
            .AddPolicyScheme("MultiScheme", "Route to correct auth handler", options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    // Check for API key header — if the header is present (even empty),
                    // route to ApiKey scheme so it rejects invalid keys with 401 instead
                    // of falling through to cookie auth.
                    if (context.Request.Headers.ContainsKey(ApiKeyAuthenticationOptions.HeaderName))
                        return ApiKeyAuthenticationOptions.SchemeName;

                    // Check for JWT bearer token
                    var authHeader = context.Request.Headers.Authorization.ToString();
                    if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        return JwtBearerDefaults.AuthenticationScheme;

                    // Default to the Identity.Application cookie scheme so that Blazor UI
                    // sessions (issued by SignInManager) are validated correctly.
                    return IdentityConstants.ApplicationScheme;
                };
            })
            .AddCookie(options =>
            {
                options.LoginPath = "/login";
                options.LogoutPath = "/logout";
                options.AccessDeniedPath = "/access-denied";
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

                // For API/MCP endpoints, return 401 instead of redirecting to login.
                // Also reject requests that explicitly sent an X-Api-Key header
                // (even empty) — they intended API key auth, not cookie fallback.
                options.Events.OnRedirectToLogin = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/mcp"))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
                        context.Response.Headers.WWWAuthenticate =
                            $"Bearer resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource\"";
                        return Task.CompletedTask;
                    }

                    if (context.Request.Path.StartsWithSegments("/api") ||
                        context.Request.Headers.ContainsKey(ApiKeyAuthenticationOptions.HeaderName))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };

                options.Events.OnRedirectToAccessDenied = context =>
                {
                    if (context.Request.Path.StartsWithSegments("/api") ||
                        context.Request.Path.StartsWithSegments("/mcp"))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            })
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationOptions.SchemeName, _ => { })
            .AddJwtBearer(options =>
            {
                var validationKeys = new List<SecurityKey>
                {
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret))
                };

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = validationKeys,
                    ValidateIssuer = true,
                    ValidIssuer = jwtSettings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtSettings.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                };

                // Support JWT in query string for SignalR
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    },
                    OnChallenge = context =>
                    {
                        if (context.Request.Path.StartsWithSegments("/mcp"))
                        {
                            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
                            context.Response.Headers.WWWAuthenticate =
                                $"Bearer resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource\"";
                            context.HandleResponse();
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        return services;
    }

    public static IServiceCollection AddConnapseAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, ScopeAuthorizationHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy("RequireOwner", policy =>
                policy.RequireRole("Owner"))
            .AddPolicy("RequireAdmin", policy =>
                policy.RequireRole("Owner", "Admin"))
            .AddPolicy("RequireEditor", policy =>
                policy.RequireRole("Owner", "Admin", "Editor"))
            .AddPolicy("RequireViewer", policy =>
                policy.RequireRole("Owner", "Admin", "Editor", "Viewer"))
            .AddPolicy("RequireAgent", policy =>
                policy.RequireRole("Owner", "Admin", "Agent"))
            .AddPolicy("Scope:KnowledgeRead", policy =>
                policy.Requirements.Add(new ScopeRequirement("knowledge:read")))
            .AddPolicy("Scope:KnowledgeWrite", policy =>
                policy.Requirements.Add(new ScopeRequirement("knowledge:write")))
            .AddPolicy("Scope:AdminUsers", policy =>
                policy.Requirements.Add(new ScopeRequirement("admin:users")))
            .AddPolicy("Scope:AdminManageAdmins", policy =>
                policy.Requirements.Add(new ScopeRequirement("admin:manage-admins")))
            .AddPolicy("Scope:AgentIngest", policy =>
                policy.Requirements.Add(new ScopeRequirement("agent:ingest")));

        return services;
    }

    private static void EnsureJwtSecret(IConfiguration configuration)
    {
        var secret = configuration["Identity:Jwt:Secret"];

        if (!string.IsNullOrWhiteSpace(secret))
            return;

        // Auto-generate secret and persist to data directory
        var dataDir = configuration["DataDirectory"] ?? "appdata";
        var secretPath = Path.Combine(dataDir, "jwt-secret.key");

        if (File.Exists(secretPath))
        {
            var existingSecret = File.ReadAllText(secretPath).Trim();
            if (existingSecret.Length >= 64)
            {
                configuration["Identity:Jwt:Secret"] = existingSecret;
                return;
            }
        }

        // Generate a new 64-byte random secret
        var newSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        Directory.CreateDirectory(dataDir);
        File.WriteAllText(secretPath, newSecret);

        configuration["Identity:Jwt:Secret"] = newSecret;

        // We can't use ILogger here since we're in a static method during startup
        Console.WriteLine($"WARNING: Auto-generated JWT secret. Set Identity__Jwt__Secret for production use. Secret stored at: {secretPath}");
    }
}
