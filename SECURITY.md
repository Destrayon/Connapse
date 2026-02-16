# Security Policy

## ⚠️ Development Status

**Connapse is currently in pre-alpha development (v0.1.0-alpha) and is NOT production-ready.**

### Critical Security Limitations

This project currently has **NO AUTHENTICATION OR AUTHORIZATION** implemented. This means:

- ❌ **No user login** - Anyone with network access can use the system
- ❌ **No access control** - All users can read, modify, and delete all data
- ❌ **No rate limiting** - APIs can be abused without restriction
- ❌ **No audit logging** - No tracking of who did what
- ❌ **Default credentials** - Database and MinIO use development passwords
- ❌ **No encryption at rest** - Data stored in plaintext
- ❌ **No CORS restrictions** - Any website can make requests

### Safe Usage

**✅ Local Development Only**
- Run on localhost or isolated development networks
- Do not expose ports to the internet
- Use for testing and development purposes only

**❌ DO NOT**
- Deploy to public networks or cloud servers
- Store sensitive or confidential data
- Use in production environments
- Expose API endpoints publicly
- Share network access with untrusted users

## 🔐 Planned Security Features (v0.2.0)

Authentication and authorization are the **#1 priority** for the next release:

### Planned for v0.2.0
- ✅ Password-based authentication (ASP.NET Core Identity)
- ✅ API key support for programmatic access (CLI, MCP)
- ✅ Role-based access control (Admin, User, Read-Only)
- ✅ Rate limiting on all endpoints
- ✅ CORS configuration
- ✅ Audit logging
- ✅ Secure credential management

### Future Enhancements
- Multi-factor authentication (MFA)
- OAuth/OIDC integration (Google, GitHub, Microsoft)
- Container-level permissions (share access to specific projects)
- SSO for enterprise deployments
- Encryption at rest (database, object storage)
- Security scanning in CI/CD pipeline

## 🐛 Reporting Security Vulnerabilities

We take security seriously. If you discover a security vulnerability:

### For Pre-Release Issues
Since this is pre-alpha software with known security limitations, please:
1. Check if it's a [known limitation](#critical-security-limitations) above
2. If it's a new issue, open a [GitHub Issue](https://github.com/yourusername/Connapse/issues) with details
3. Use the `security` label

### For Production Issues (v0.2.0+)
Once authentication is implemented:
1. **DO NOT** open a public issue
2. Email security@your-domain.com (or use GitHub Security Advisories)
3. Include:
   - Description of the vulnerability
   - Steps to reproduce
   - Potential impact
   - Any suggested fixes
4. We will respond within 48 hours
5. We will coordinate disclosure timeline with you

## 📋 Security Checklist for Self-Hosting

If you choose to self-host (even in development):

- [ ] Change all default credentials in docker-compose.yml
- [ ] Use strong passwords (20+ characters, random)
- [ ] Enable firewall rules (allow only necessary ports)
- [ ] Run on isolated network (no public internet exposure)
- [ ] Keep .env files out of version control
- [ ] Regularly update dependencies (`dotnet outdated`, `npm audit`)
- [ ] Review CHANGELOG for security fixes before upgrading
- [ ] Back up data regularly (database + MinIO storage)
- [ ] Monitor logs for suspicious activity

## 🔒 Secure Development Practices

We follow these practices:

- **Dependency Scanning**: Automated checks for vulnerable packages
- **Code Review**: All PRs reviewed before merge
- **Least Privilege**: Services run with minimal permissions
- **Input Validation**: All user inputs sanitized
- **SQL Injection Prevention**: Parameterized queries only (EF Core)
- **XSS Prevention**: Blazor auto-escapes output
- **CSRF Protection**: ASP.NET Core built-in protection
- **Secrets Management**: Never commit secrets to git

## 📚 Resources

- [OWASP Top 10](https://owasp.org/www-project-top-ten/)
- [ASP.NET Core Security](https://learn.microsoft.com/en-us/aspnet/core/security/)
- [.NET Security Best Practices](https://learn.microsoft.com/en-us/dotnet/standard/security/)

## 📝 Version History

| Version | Status | Authentication | Production Ready |
|---------|--------|----------------|------------------|
| v0.1.0-alpha | Current | ❌ None | ❌ No |
| v0.2.0 | Planned | ✅ Password + API Keys | ⚠️ Beta |
| v1.0.0 | Future | ✅ Full Auth + RBAC | ✅ Yes |

---

**Last Updated**: 2026-02-08
**Status**: Pre-Alpha Development
