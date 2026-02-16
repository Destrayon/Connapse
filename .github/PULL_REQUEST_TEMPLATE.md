## Description

<!-- Provide a clear and concise description of your changes -->

## Type of Change

<!-- Please check the relevant option(s) -->

- [ ] 🐛 Bug fix (non-breaking change that fixes an issue)
- [ ] ✨ New feature (non-breaking change that adds functionality)
- [ ] 💥 Breaking change (fix or feature that would cause existing functionality to change)
- [ ] 📚 Documentation update
- [ ] 🔧 Configuration change
- [ ] ♻️ Refactoring (no functional changes)
- [ ] ⚡ Performance improvement
- [ ] ✅ Test update or addition

## Related Issues

<!-- Link to related issues using GitHub keywords (e.g., "Fixes #123", "Closes #456", "Relates to #789") -->

Fixes #

## Changes Made

<!-- List the specific changes you made in this PR -->

-
-
-

## Testing

<!-- Describe the tests you ran and how to reproduce them -->

### Test Environment
- OS: <!-- e.g., Windows 11, macOS 14, Ubuntu 22.04 -->
- .NET Version: <!-- e.g., .NET 10.0 -->
- Deployment: <!-- e.g., Docker Compose, Local development -->

### Test Cases
<!-- Describe what you tested -->

- [ ] Unit tests pass (`dotnet test --filter "Category=Unit"`)
- [ ] Integration tests pass (`dotnet test --filter "Category=Integration"`)
- [ ] Manual testing performed
- [ ] Tested with Docker Compose
- [ ] Tested edge cases and error scenarios

### Test Results
<!-- Paste relevant test output or describe manual test results -->

```
# Paste test output here
```

## Screenshots

<!-- If applicable, add screenshots to help explain your changes -->

## Checklist

<!-- Please check all applicable items -->

### Code Quality
- [ ] My code follows the project's [code style guidelines](../CONTRIBUTING.md#code-style)
- [ ] I have performed a self-review of my code
- [ ] I have commented my code, particularly in hard-to-understand areas
- [ ] I have removed any debug code or console.log statements
- [ ] My changes generate no new warnings or errors

### Testing
- [ ] I have added tests that prove my fix is effective or that my feature works
- [ ] New and existing unit tests pass locally with my changes
- [ ] New and existing integration tests pass locally with my changes
- [ ] I have tested this change in a Docker Compose environment

### Documentation
- [ ] I have updated the documentation accordingly (README, docs/, CLAUDE.md)
- [ ] I have updated `.claude/state/` files if applicable (decisions.md, conventions.md, progress.md)
- [ ] I have added or updated XML comments for public APIs
- [ ] I have updated API documentation if endpoints changed

### Git
- [ ] My commits are atomic and have descriptive messages
- [ ] I have rebased my branch on the latest main
- [ ] I have resolved any merge conflicts
- [ ] My branch has a clear, descriptive name

### Database Changes (if applicable)
- [ ] I have created an EF Core migration
- [ ] The migration has both Up() and Down() methods
- [ ] I have tested the migration on a clean database
- [ ] I have updated the schema documentation

### Breaking Changes (if applicable)
- [ ] I have clearly documented the breaking changes in this PR description
- [ ] I have updated `.claude/state/api-surface.md` with breaking changes
- [ ] I have provided migration guidance for users

## Additional Notes

<!-- Add any additional context about the PR here -->

## Deployment Notes

<!-- Any special considerations for deployment? Required configuration changes? -->

---

**Reminder**: This is pre-alpha software. Security features (authentication, authorization, rate limiting) are not yet implemented. See [SECURITY.md](../SECURITY.md) for current limitations.
