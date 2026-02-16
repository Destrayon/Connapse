# Contributing to Connapse

Thank you for considering contributing to Connapse! This document provides guidelines and instructions for contributing to this project.

## 🌟 Ways to Contribute

- 🐛 **Report bugs** - Found a bug? Open an issue!
- 💡 **Suggest features** - Have an idea? Start a discussion!
- 📖 **Improve documentation** - Fix typos, clarify explanations, add examples
- 🧪 **Write tests** - Increase test coverage, add edge cases
- 💻 **Submit code** - Fix bugs, implement features, refactor code
- 🎨 **Improve UI/UX** - Design improvements, accessibility fixes
- 🔍 **Review pull requests** - Help review code from other contributors

## 📋 Before You Start

1. **Check existing issues and PRs** to avoid duplicate work
2. **Read the [Code of Conduct](CODE_OF_CONDUCT.md)** - we expect all contributors to be respectful
3. **Review [CLAUDE.md](CLAUDE.md)** - our development guide with architecture and conventions
4. **Read [SECURITY.md](SECURITY.md)** - understand current security limitations

## 🐛 Reporting Bugs

**Before submitting a bug report:**
- Search [existing issues](https://github.com/yourusername/Connapse/issues) to see if it's already reported
- Test with the latest code on the `master` branch
- Check [SECURITY.md](SECURITY.md) - some limitations are known and documented

**When submitting a bug report, include:**
- **Description**: Clear, concise description of the bug
- **Steps to reproduce**: Numbered list to reproduce the issue
- **Expected behavior**: What you expected to happen
- **Actual behavior**: What actually happened
- **Environment**:
  - OS (Windows, macOS, Linux + version)
  - .NET version (`dotnet --version`)
  - Docker version (`docker --version`)
  - Browser (if UI-related)
- **Logs**: Relevant error messages or stack traces
- **Screenshots**: If applicable

**Use this template:**

```markdown
### Description
[Brief description of the bug]

### Steps to Reproduce
1. [First step]
2. [Second step]
3. [...]

### Expected Behavior
[What should happen]

### Actual Behavior
[What actually happens]

### Environment
- OS: [e.g., Windows 11, Ubuntu 22.04]
- .NET: [e.g., 10.0.1]
- Docker: [e.g., 24.0.7]
- Browser: [e.g., Chrome 120, Firefox 121]

### Logs
```
[Paste relevant logs here]
```

### Additional Context
[Any other information, screenshots, etc.]
```

## 💡 Suggesting Features

**Before suggesting a feature:**
- Check [existing discussions](https://github.com/yourusername/Connapse/discussions) and issues
- Review the [roadmap](README.md#-roadmap) to see if it's already planned
- Consider whether it fits the project's scope and vision

**When suggesting a feature:**
- **Use Case**: Describe the problem this feature solves
- **Proposed Solution**: How you envision it working
- **Alternatives**: Other solutions you've considered
- **Implementation Ideas**: Technical approach (if you have thoughts)
- **Willing to Implement**: Let us know if you'd like to work on it!

**Submit as a [GitHub Discussion](https://github.com/yourusername/Connapse/discussions) first, not an issue.** This allows for community feedback before committing to implementation.

## 💻 Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://docs.docker.com/get-docker/) & [Docker Compose](https://docs.docker.com/compose/install/)
- [Git](https://git-scm.com/)
- IDE: [Visual Studio 2024](https://visualstudio.microsoft.com/), [VS Code](https://code.visualstudio.com/), or [JetBrains Rider](https://www.jetbrains.com/rider/)

### Initial Setup

```bash
# 1. Fork the repository on GitHub
# 2. Clone your fork
git clone https://github.com/YOUR-USERNAME/Connapse.git
cd Connapse

# 3. Add upstream remote
git remote add upstream https://github.com/yourusername/Connapse.git

# 4. Start infrastructure (database + storage)
docker-compose up -d postgres minio

# 5. Build the solution
dotnet build

# 6. Run tests to ensure everything works
dotnet test

# 7. Run the web app
dotnet run --project src/Connapse.Web
```

### Keeping Your Fork Updated

```bash
# Fetch upstream changes
git fetch upstream

# Merge upstream/master into your local master
git checkout master
git merge upstream/master

# Push to your fork
git push origin master
```

## 🔀 Pull Request Process

### 1. Create a Feature Branch

```bash
# Always branch from master
git checkout master
git pull upstream master

# Create a descriptive branch name
git checkout -b feature/your-feature-name
# OR
git checkout -b fix/bug-description
```

**Branch naming conventions:**
- `feature/description` - New features
- `fix/description` - Bug fixes
- `docs/description` - Documentation changes
- `refactor/description` - Code refactoring
- `test/description` - Test additions/changes

### 2. Make Your Changes

Follow the [code conventions](#-code-conventions) below.

### 3. Write Tests

- **All new features MUST have tests**
- **Bug fixes SHOULD include tests** that prevent regression
- We use:
  - **xUnit** for test framework
  - **FluentAssertions** for assertions
  - **NSubstitute** for mocking
  - **Testcontainers** for integration tests

**Test naming convention:**
```csharp
public void MethodName_Scenario_ExpectedResult()
{
    // Arrange
    var sut = new SystemUnderTest();

    // Act
    var result = sut.MethodName();

    // Assert
    result.Should().Be(expectedValue);
}
```

### 4. Run Tests Locally

```bash
# Run all tests
dotnet test

# Run just unit tests (fast)
dotnet test --filter "Category=Unit"

# Run just integration tests
dotnet test --filter "Category=Integration"

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"
```

**All tests must pass before submitting a PR.**

### 5. Commit Your Changes

**Commit message format:**
```
<type>: <short summary> (max 72 chars)

<optional detailed explanation>

Co-Authored-By: Your Name <your.email@example.com>
```

**Types:**
- `feat:` - New feature
- `fix:` - Bug fix
- `docs:` - Documentation changes
- `test:` - Test additions/changes
- `refactor:` - Code refactoring
- `perf:` - Performance improvements
- `chore:` - Maintenance tasks (dependencies, build config)

**Examples:**
```bash
git commit -m "feat: Add folder creation API endpoint"
git commit -m "fix: Handle null values in search results"
git commit -m "docs: Update API examples in README"
git commit -m "test: Add integration tests for container deletion"
```

### 6. Push to Your Fork

```bash
git push origin feature/your-feature-name
```

### 7. Open a Pull Request

1. Go to [github.com/yourusername/Connapse](https://github.com/yourusername/Connapse)
2. Click "New Pull Request"
3. Select your fork and branch
4. Fill out the PR template:

```markdown
## Description
[Brief description of changes]

## Related Issue
Fixes #[issue-number]

## Type of Change
- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Breaking change (fix or feature that would cause existing functionality to change)
- [ ] Documentation update

## Testing
- [ ] Unit tests added/updated
- [ ] Integration tests added/updated
- [ ] All tests pass locally
- [ ] Manual testing performed

## Checklist
- [ ] Code follows project conventions (see CLAUDE.md)
- [ ] Self-review completed
- [ ] Comments added for complex logic
- [ ] Documentation updated (if needed)
- [ ] No new warnings introduced
- [ ] Tested on local environment
```

### 8. Code Review Process

- A maintainer will review your PR
- Feedback may be provided via comments
- Make requested changes and push to your branch
- The PR will update automatically
- Once approved, a maintainer will merge your PR

**Be patient and respectful during the review process.**

## 🎨 Code Conventions

### C# / .NET 10

- **File-scoped namespaces**: `namespace Connapse.Core;` (not `namespace Connapse.Core { ... }`)
- **Nullable enabled**: All projects have `<Nullable>enable</Nullable>`
- **Records for DTOs**: Use `record` for immutable data transfer objects
- **Primary constructors**: Use where appropriate (classes with dependency injection)
- **Async all the way**: Never block with `.Result` or `.Wait()`
- **IOptions pattern**: Use `IOptions<T>` or `IOptionsMonitor<T>` for configuration

**Example:**
```csharp
namespace Connapse.Core;

public class DocumentStore(IDbContextFactory<AppDbContext> contextFactory) : IDocumentStore
{
    public async Task<Document?> FindAsync(Guid id, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        return await context.Documents.FindAsync([id], ct);
    }
}
```

### Blazor Components

- **Interactive Server mode**: For real-time features
- **Inject services**: Use `@inject` directive, not constructor injection
- **Keep components thin**: Extract logic to services
- **Component naming**: PascalCase, descriptive (e.g., `FileUploadDialog.razor`)

### Testing

- **Framework**: xUnit
- **Assertions**: FluentAssertions (`.Should()` syntax)
- **Mocking**: NSubstitute
- **Naming**: `MethodName_Scenario_ExpectedResult`
- **Categorize tests**:
  - `[Trait("Category", "Unit")]` for unit tests
  - `[Trait("Category", "Integration")]` for integration tests

**Example:**
```csharp
[Fact]
[Trait("Category", "Unit")]
public void ParseDocument_WithPdfFile_ReturnsTextContent()
{
    // Arrange
    var parser = new PdfParser();
    var pdfBytes = TestHelpers.GetSamplePdf();

    // Act
    var result = parser.Parse(pdfBytes);

    // Assert
    result.Should().NotBeNull();
    result.Text.Should().Contain("expected content");
}
```

### Database Conventions

- **EF Core**: Use `DbContext` with dependency injection
- **Migrations**: Create migrations for schema changes: `dotnet ef migrations add MigrationName`
- **Parameterized queries**: Always use parameters, never string interpolation
- **Async queries**: Use `ToListAsync()`, `FirstOrDefaultAsync()`, etc.

### API Conventions

- **Minimal APIs**: Use `MapGet`, `MapPost`, etc. in `Program.cs` or endpoint extensions
- **Validation**: Validate inputs and return `Results.ValidationProblem()` on failure
- **Error handling**: Use `Results.Problem()` for 500 errors
- **DTOs**: Use records for request/response objects
- **Naming**: RESTful conventions (`GET /api/containers`, `POST /api/containers/{id}/files`)

## 📝 Documentation

- **Code comments**: Add XML documentation comments (`///`) for public APIs
- **Complex logic**: Explain "why", not "what" in code comments
- **README updates**: Update README if you change functionality or add features
- **API docs**: Update `docs/api.md` if you change API endpoints
- **Architecture docs**: Update `docs/architecture.md` if you change system design

## 🚫 What NOT to Do

- ❌ Don't commit secrets (API keys, passwords, etc.)
- ❌ Don't commit large files (>5 MB)
- ❌ Don't use `dynamic` keyword
- ❌ Don't use `var` for primitive types (use `int`, `string`, etc.)
- ❌ Don't block async code with `.Result` or `.Wait()`
- ❌ Don't share `DbContext` across threads/requests
- ❌ Don't submit PRs with failing tests
- ❌ Don't include unrelated changes in a single PR

## 🏷️ Issue Labels

We use these labels to categorize issues:

- `bug` - Something isn't working
- `feature` - New feature request
- `documentation` - Documentation improvements
- `good-first-issue` - Good for newcomers
- `help-wanted` - Extra attention needed
- `security` - Security-related issue
- `performance` - Performance improvement
- `question` - Question or discussion
- `wontfix` - This will not be worked on
- `duplicate` - This issue already exists

## 🎯 Good First Issues

New to the project? Look for issues labeled [`good-first-issue`](https://github.com/yourusername/Connapse/labels/good-first-issue). These are small, well-defined tasks perfect for getting started.

## 💬 Getting Help

- 📖 Read [CLAUDE.md](CLAUDE.md) for detailed architecture and conventions
- 💡 Start a [GitHub Discussion](https://github.com/yourusername/Connapse/discussions)
- 🐛 Open an [issue](https://github.com/yourusername/Connapse/issues) if you're stuck

## 📜 License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).

---

**Thank you for contributing to Connapse! 🎉**
