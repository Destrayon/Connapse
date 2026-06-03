# container_describe — Echo Server Instructions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Echo the canonical MCP server routing rules in the `container_describe` tool's success output, so agents on clients that drop the connect-time `instructions` field still receive them.

**Architecture:** Append a delimited `Server instructions:` block — carrying the existing `McpServerConfig.McpServerInstructions` constant *by reference* (single source of truth) — to the end of `ContainerDescribe`'s success text. The not-found error path returns earlier and is left untouched, so errors stay terse. No model, schema, or other-tool changes.

**Tech Stack:** C# / .NET, ModelContextProtocol SDK; xUnit + FluentAssertions + NSubstitute for tests.

**Repo / branch:** Connapse repo (`d:\CodeProjects\Connapse`), branch `feat/container-describe-server-instructions`. Run all commands from the Connapse repo root.

**Spec:** `docs/superpowers/specs/2026-06-02-container-describe-server-instructions-design.md`

---

## File Structure

Two existing files are modified; no files are created.

- **Modify** `src/Connapse.Web/Mcp/McpTools.cs`
  - `ContainerDescribe` method (~lines 781–828): append the instructions block before `return text;`, and extend the `[Description(...)]` attribute.
  - `McpServerConfig` lives in the same `Connapse.Web.Mcp` namespace, so `McpServerConfig.McpServerInstructions` is reachable with **no new `using`**.
- **Modify** `tests/Connapse.Core.Tests/Mcp/McpToolsContainerDescribeTests.cs`
  - Add one positive test (instructions appended on success) and one negative guard test (error path omits the header). The file already has `using Connapse.Web.Mcp;`, so `McpServerConfig` is in scope.

> ⚠️ **Edit-anchor warning:** the two lines
> `text += $"Created: {container.CreatedAt:u}";` followed by `return text;`
> appear **twice** in `McpTools.cs` (a stats helper near line 776 and `ContainerDescribe` near line 825). To target `ContainerDescribe` uniquely, anchor the edit on the consecutive `Storage:` → `Created:` pair, which only `ContainerDescribe` has (the helper's `Storage:` line is followed by embedding-model lines, not `Created:`).

---

## Task 1: Echo server instructions on success (TDD)

**Files:**
- Test: `tests/Connapse.Core.Tests/Mcp/McpToolsContainerDescribeTests.cs`
- Modify: `src/Connapse.Web/Mcp/McpTools.cs` (`ContainerDescribe` method)

- [ ] **Step 1: Write the failing positive test**

Add this method inside the `McpToolsContainerDescribeTests` class (e.g., after the existing `ContainerDescribe_WithSummary_ReturnsFullDescription` test):

```csharp
[Fact]
public async Task ContainerDescribe_Success_AppendsServerInstructions()
{
    var result = await McpTools.ContainerDescribe(_services, ContainerId.ToString());

    result.Should().Contain("Server instructions:");
    result.Should().Contain(McpServerConfig.McpServerInstructions);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```bash
dotnet test --filter "FullyQualifiedName~McpToolsContainerDescribeTests.ContainerDescribe_Success_AppendsServerInstructions"
```
Expected: **FAIL** — `Failed! - Failed: 1`. The assertion `result.Should().Contain("Server instructions:")` fails because `ContainerDescribe` does not yet append the block. (It compiles — `McpServerConfig.McpServerInstructions` already exists.)

- [ ] **Step 3: Append the instructions block in `ContainerDescribe`**

In `src/Connapse.Web/Mcp/McpTools.cs`, find this block inside `ContainerDescribe` (anchored on the `Storage:` line so it matches only here):

```csharp
        text += $"Storage: {FormatBytes(stats.TotalSizeBytes)}\n";
        text += $"Created: {container.CreatedAt:u}";

        return text;
```

Replace it with:

```csharp
        text += $"Storage: {FormatBytes(stats.TotalSizeBytes)}\n";
        text += $"Created: {container.CreatedAt:u}";
        text += "\n\n---\nServer instructions:\n" + McpServerConfig.McpServerInstructions;

        return text;
```

- [ ] **Step 4: Update the tool `[Description]` to stay honest**

In the same file, find the `ContainerDescribe` attribute line:

```csharp
     Description("Returns an agent-optimized description of a container: its user-supplied description, auto-generated summary (if available), and document statistics. Use this to understand what a container covers before querying via search_knowledge, or when container_list output is insufficient to choose between containers.")]
```

Replace it with (one appended sentence):

```csharp
     Description("Returns an agent-optimized description of a container: its user-supplied description, auto-generated summary (if available), and document statistics. Use this to understand what a container covers before querying via search_knowledge, or when container_list output is insufficient to choose between containers. The response also echoes the server's tool-routing instructions for clients that don't surface them on connect.")]
```

- [ ] **Step 5: Run the new test to verify it passes**

Run:
```bash
dotnet test --filter "FullyQualifiedName~McpToolsContainerDescribeTests.ContainerDescribe_Success_AppendsServerInstructions"
```
Expected: **PASS** — `Passed! - Failed: 0, Passed: 1`.

- [ ] **Step 6: Run the full describe test class to confirm no regressions**

Run:
```bash
dotnet test --filter "FullyQualifiedName~Connapse.Core.Tests.Mcp.McpToolsContainerDescribeTests"
```
Expected: **PASS** — all tests in the class pass. The existing tests assert on the facts portion (name, ID, summary, documents, storage, created), which is unchanged.

- [ ] **Step 7: Commit**

```bash
git add src/Connapse.Web/Mcp/McpTools.cs tests/Connapse.Core.Tests/Mcp/McpToolsContainerDescribeTests.cs
git commit -m "feat(mcp): echo server routing instructions in container_describe

Append the canonical McpServerConfig.McpServerInstructions (by reference)
to container_describe's success output, for MCP clients that drop the
connect-time instructions field. Tool description updated to match.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Guard the error path stays terse (regression test)

**Files:**
- Test: `tests/Connapse.Core.Tests/Mcp/McpToolsContainerDescribeTests.cs`

This test documents and locks in the invariant that the **not-found error response does not carry the instructions block**. It is expected to pass immediately, because `ContainerDescribe` returns the error string before the facts (and the appended block) are built. Including it prevents a future refactor from accidentally appending instructions to errors.

- [ ] **Step 1: Write the guard test**

Add this method inside `McpToolsContainerDescribeTests` (e.g., next to the existing `ContainerDescribe_ContainerNotFound_ReturnsError` test):

```csharp
[Fact]
public async Task ContainerDescribe_NotFound_OmitsServerInstructionsHeader()
{
    var result = await McpTools.ContainerDescribe(_services, "nonexistent");

    result.Should().StartWith("Error:");
    result.Should().NotContain("Server instructions:");
}
```

- [ ] **Step 2: Run the guard test (expected to pass)**

Run:
```bash
dotnet test --filter "FullyQualifiedName~McpToolsContainerDescribeTests.ContainerDescribe_NotFound_OmitsServerInstructionsHeader"
```
Expected: **PASS** — `Passed! - Failed: 0, Passed: 1`. The unresolved container short-circuits to `Error: Container 'nonexistent' not found.` before any append.

- [ ] **Step 3: Commit**

```bash
git add tests/Connapse.Core.Tests/Mcp/McpToolsContainerDescribeTests.cs
git commit -m "test(mcp): guard container_describe error path omits server instructions

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Full verification

**Files:** none (verification only).

- [ ] **Step 1: Build the solution**

Run:
```bash
dotnet build
```
Expected: **Build succeeded** — 0 errors.

- [ ] **Step 2: Run the full unit-test suite**

Run:
```bash
dotnet test --filter "Category=Unit"
```
Expected: **PASS** — `Passed!`, 0 failed. Confirms the change did not break any other unit test (e.g., MCP server-config or container-list tests).

- [ ] **Step 3: Confirm the branch is ready**

Run:
```bash
git log --oneline -3
git status --short
```
Expected: the two new commits on top of the spec commit, and a clean working tree. The branch `feat/container-describe-server-instructions` is ready for a PR.

---

## Done criteria

- `container_describe` success output ends with a `---` + `Server instructions:` block containing the verbatim `McpServerConfig.McpServerInstructions` text.
- The not-found error response contains no `Server instructions:` header.
- The tool `[Description]` mentions the echoed routing instructions.
- `dotnet build` and `dotnet test --filter "Category=Unit"` both pass.
