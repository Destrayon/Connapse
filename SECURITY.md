# Security Policy

## Development Status

**Connapse v0.3.x is in beta.** Authentication and authorization are fully implemented. Cloud identity linking is being rebuilt (see [Corrections since v0.3.0](#corrections-since-v030)). The project is suitable for self-hosted deployments in trusted environments, but is **not yet recommended for production exposure to the public internet** without review of the remaining limitations below.

### What Changed in v0.2.0

v0.2.0 delivered the full authentication and authorization foundation:

- **Authentication**: ASP.NET Core Identity with password-based login (cookie auth for the Blazor UI, bearer tokens for API clients)
- **Invite-only registration**: The first user becomes the system admin. All subsequent users require a time-limited invite link issued by an admin. No public self-registration.
- **Role-based access control**: Four built-in roles — Admin, Editor, Viewer, Agent
- **Personal Access Tokens (PATs)**: `cnp_`-prefixed tokens for programmatic access (scripts, integrations). Scoped, revocable, and auditable
- **CLI OAuth 2.1 auth**: Secure browser-based login via OAuth 2.1 authorization code + PKCE (RFC 7636) — no password ever touches the terminal. JWT + refresh token rotation with replay detection.
- **JWT tokens**: Standard JWTs for third-party SDK and API client access
- **Scope-based authorization**: Fine-grained per-token permission scopes enforced server-side
- **Agent API keys**: Dedicated API keys for agent entities with their own scope model
- **Audit logging**: All authentication events and data mutations are logged
- **CORS**: Configurable allowed origins via `Cors:AllowedOrigins`; blocks cross-origin requests by default
- **Anti-forgery protection**: CSRF tokens enforced for all Blazor form submissions
- **Input sanitization**: All user-supplied values sanitized before processing
- **CodeQL fixes**: Log injection and PII exposure vulnerabilities resolved

### What Changed in v0.3.0

v0.3.0 added cloud connector architecture with identity-based access control:

- **Encrypted cloud identities**: Cloud identity data encrypted at rest via ASP.NET Core DataProtection (`IDataProtector`)
- **Multi-provider AI support**: Embedding (Ollama, OpenAI, Azure OpenAI) and LLM (Ollama, OpenAI, Azure OpenAI, Anthropic) providers with API key management via runtime settings
- **Connection testing**: All cloud connectors and AI providers can be validated before saving credentials
- **S3 connector security**: Prefers a configured Connapse IAM credential, falling back to the ambient AWS chain (IAM roles, instance profiles) when none is set. A configured access key is encrypted at rest via DataProtection under its own purpose (`ProviderCredential.v1`)

### Corrections since v0.3.0

Two capabilities the v0.3.0 notes originally claimed do not hold in the current build. Both have been
removed from the list above and are recorded here instead, because each one changes what an operator
can rely on:

- **AWS IAM Identity Center linking was removed** ([#435](https://github.com/Destrayon/Connapse/issues/435)).
  v0.3.0 shipped a device authorization flow for AWS. It could not carry a per-user identity through
  to a usable token, so it stored data nothing could read. AWS sign-in was rebuilt on an IAM Identity
  Center SAML link instead, which now backs the per-user AWS search filtering described below.
- **Per-user AWS search filtering is live, and opt-in** ([#436](https://github.com/Destrayon/Connapse/issues/436)).
  `ISearchScopeResolver` is wired into the search pipeline and fails closed. Until a deployment links
  AWS identities (IAM Identity Center SAML sign-in, permissions resolved via AWS S3 Access Grants),
  search is unrestricted and any user who can reach a container can search every document in it —
  the same as before. Once AWS identity linking is configured, results are filtered to what the
  searching user's AWS identity can reach.
- **Azure Blob Storage connector and Azure AD cloud identity linking were removed**
  ([#476](https://github.com/Destrayon/Connapse/issues/476)). Both were unverified and torn down for
  a clean rebuild; the dead `CloudScopeService`/`ICloudIdentityService` scaffolding they shared was
  removed with them. Only the S3 and Filesystem connectors remain, and the only cloud identity link
  today is the AWS IAM Identity Center (SAML) link on the Integrations page.

### Remaining Limitations

These are known gaps to address before v1.0.0:

- **Rate limiting is basic**: Built-in ASP.NET Core rate limiting is configured with per-user and per-IP fixed-window policies. For high-traffic public deployments, consider an upstream reverse proxy (nginx, Caddy) or API gateway for more advanced throttling
- **No encryption at rest**: Database and object storage data is stored unencrypted. Rely on OS/disk-level encryption (e.g., LUKS, BitLocker, encrypted EBS volumes) for sensitive deployments
- **No MFA**: Multi-factor authentication is not yet implemented
- **No traditional OIDC / SSO login**: Social login via GitHub, Google, or Microsoft is not yet implemented. OAuth 2.1 with PKCE is used for CLI authentication, but web UI login is still password-based.
- **Per-user search filtering is opt-in**: Results are filtered by AWS permissions only once a deployment configures AWS identity linking; unconfigured deployments have no per-user filtering. See [Corrections since v0.3.0](#corrections-since-v030).
- **Cloud provider testing**: Cloud connector and AI provider integrations are unit-tested with mocks only — no end-to-end tests against real cloud services

## Reporting Security Vulnerabilities

We take security seriously.

### For Beta Issues

1. **DO NOT** open a public GitHub issue for security vulnerabilities
2. Email **psummers1050@gmail.com** or open a [GitHub Security Advisory](https://github.com/Destrayon/Connapse/security/advisories/new)
3. Include:
   - Description of the vulnerability
   - Steps to reproduce
   - Potential impact
   - Any suggested fixes
4. We will respond within 48 hours and coordinate a disclosure timeline with you

### For Known Limitations

If you are reporting something listed under [Remaining Limitations](#remaining-limitations), you can open a regular GitHub issue referencing the known gap.

## Self-Hosting Security Checklist

If you are self-hosting Connapse:

- [ ] Change all default credentials in `docker-compose.yml` (Postgres password, MinIO secret key)
- [ ] Use strong passwords (20+ characters, randomly generated)
- [ ] Set `Cors:AllowedOrigins` to your actual domain — do not leave it as wildcard
- [ ] Place a reverse proxy (nginx, Caddy, Traefik) in front for TLS termination (built-in rate limiting is active by default; tune limits via `RateLimiting` config section)
- [ ] Keep `.env` and secret files out of version control
- [ ] Enable OS-level encryption at rest for database and storage volumes
- [ ] Restrict network access — expose only ports 80/443 to the internet
- [ ] Regularly update dependencies (`dotnet outdated`)
- [ ] Review the [CHANGELOG](CHANGELOG.md) for security fixes before upgrading
- [ ] Monitor audit logs for suspicious authentication activity
- [ ] Rotate Personal Access Tokens periodically; revoke any tokens that may have been exposed
- [ ] Set a strong `Identity__Jwt__Secret` (min 32 characters) — see [deployment guide](docs/deployment.md)
- [ ] For cloud connectors: use IAM roles / managed identities — never store cloud access keys

## Secure Development Practices

- **Dependency scanning**: Automated checks for vulnerable packages via CodeQL and Dependabot
- **Code review**: All PRs reviewed before merge
- **Least privilege**: Services run with minimal permissions; PATs are scoped
- **Input validation**: All user inputs sanitized; path traversal protection on file system operations
- **SQL injection prevention**: Parameterized queries only (EF Core + explicit `NpgsqlParameter`)
- **XSS prevention**: Blazor auto-escapes output
- **CSRF protection**: ASP.NET Core anti-forgery middleware enforced
- **Secrets management**: Never commit secrets — use user-secrets in development, environment variables in production
- **Log safety**: Structured logging with sanitized values; PII excluded from log output
- **Auth scheme defense-in-depth**: Multi-scheme authentication pipeline (Cookie + Bearer + API Key) with explicit `DefaultAuthenticateScheme` to prevent scheme override bugs
- **Cloud identity encryption**: Cloud identity data encrypted at rest via DataProtection API

## Resources

- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
- [ASP.NET Core Security](https://learn.microsoft.com/en-us/aspnet/core/security/)
- [ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity)
- [.NET Security Best Practices](https://learn.microsoft.com/en-us/dotnet/standard/security/)

## Version History

| Version | Status | Authentication | Production Ready |
|---------|--------|----------------|------------------|
| v0.1.0-alpha | Released | None | No |
| v0.2.0 | Released (Beta) | Password + PATs + JWT | Self-hosted (trusted networks) |
| v0.3.0 | Released (Beta) | + Cloud identity (Azure AD) | Self-hosted (trusted networks) |
| v0.3.2 | Released (Beta) | + Rate limiting (per-user, per-IP, per-agent) | Self-hosted (trusted networks) |
| v0.3.x | Current (Beta) | + OAuth 2.1 (PKCE + refresh token rotation) for CLI/MCP auth | Self-hosted (trusted networks) |
| v1.0.0 | Future | + MFA + OIDC/SSO login + encryption at rest | Yes |

---

**Last Updated**: 2026-08-28
**Status**: Beta (v0.3.x)
