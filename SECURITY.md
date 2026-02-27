# Security Policy

## Development Status

**Connapse v0.2.0 is in beta.** Authentication and authorization are fully implemented. The project is suitable for self-hosted deployments in trusted environments, but is **not yet recommended for production exposure to the public internet** without review of the remaining limitations below.

### What Changed in v0.2.0

v0.2.0 delivered the full authentication and authorization foundation:

- **Authentication**: ASP.NET Core Identity with password-based login (cookie auth for the Blazor UI, bearer tokens for API clients)
- **Invite-only registration**: The first user becomes the system admin. All subsequent users require a time-limited invite link issued by an admin. No public self-registration.
- **Role-based access control**: Four built-in roles — Admin, Editor, Viewer, Agent
- **Personal Access Tokens (PATs)**: `cnp_`-prefixed tokens for programmatic access (CLI, scripts, integrations). Scoped, revocable, and auto-revoked on re-login
- **CLI PKCE auth flow**: Secure browser-based login for the CLI — no password ever touches the terminal
- **JWT tokens**: Standard JWTs for third-party SDK and API client access
- **Scope-based authorization**: Fine-grained per-token permission scopes enforced server-side
- **Agent API keys**: Dedicated API keys for agent entities with their own scope model
- **Audit logging**: All authentication events and data mutations are logged
- **CORS**: Configurable allowed origins via `Cors:AllowedOrigins`; blocks cross-origin requests by default
- **Anti-forgery protection**: CSRF tokens enforced for all Blazor form submissions
- **Input sanitization**: All user-supplied values sanitized before processing
- **CodeQL fixes**: Log injection and PII exposure vulnerabilities resolved

### Remaining Limitations

These are known gaps to address before v1.0.0:

- **No rate limiting**: APIs have no request throttling. Do not expose publicly without an upstream reverse proxy (nginx, Caddy) that enforces rate limits
- **No encryption at rest**: Database and object storage data is stored unencrypted. Rely on OS/disk-level encryption (e.g., LUKS, BitLocker, encrypted EBS volumes) for sensitive deployments
- **No MFA**: Multi-factor authentication is planned for v0.3.0
- **No OIDC / SSO**: OAuth/OIDC integration (GitHub, Google, Microsoft) is planned for v0.3.0

## Reporting Security Vulnerabilities

We take security seriously.

### For Beta Issues (v0.2.x)

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
- [ ] Place a reverse proxy (nginx, Caddy, Traefik) in front that enforces rate limiting and TLS
- [ ] Keep `.env` and secret files out of version control
- [ ] Enable OS-level encryption at rest for database and storage volumes
- [ ] Restrict network access — expose only ports 80/443 to the internet
- [ ] Regularly update dependencies (`dotnet outdated`)
- [ ] Review the CHANGELOG for security fixes before upgrading
- [ ] Monitor audit logs for suspicious authentication activity
- [ ] Rotate Personal Access Tokens periodically; revoke any tokens that may have been exposed

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

## Resources

- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
- [ASP.NET Core Security](https://learn.microsoft.com/en-us/aspnet/core/security/)
- [ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity)
- [.NET Security Best Practices](https://learn.microsoft.com/en-us/dotnet/standard/security/)

## Version History

| Version | Status | Authentication | Production Ready |
|---------|--------|----------------|------------------|
| v0.1.0-alpha | Released | None | No |
| v0.2.0 | Current (Beta) | Password + PATs + JWT + CLI PKCE | Self-hosted (trusted networks) |
| v0.3.0 | Planned | + MFA + OIDC/SSO | Self-hosted (public-facing) |
| v1.0.0 | Future | Full auth + RBAC + rate limiting + encryption at rest | Yes |

---

**Last Updated**: 2026-02-27
**Status**: Beta (v0.2.0)
