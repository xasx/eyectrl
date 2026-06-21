# NFluent Test Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add NFluent as an assertion library and migrate one focused prediction test class from xUnit `Assert.*` to NFluent `Check.That(...)` to judge readability and migration impact.

**Architecture:** Keep xUnit as the test runner and test discovery framework. NFluent only replaces assertion syntax; it does not replace `[Fact]`, project structure, or test execution. NFluent is assertion-only and runner-agnostic, so it works unchanged under this repo's xUnit v3 setup (`tests/Directory.Build.props` pins `xunit.v3` 3.2.2). The exemplar migration targets `tests/EyeCtrl.Core.Tests/Prediction/BigramModelTests.cs` because it is small, self-contained, and exercises common assertion patterns without UI dependencies.

**Tech Stack:** xUnit v3 3.2.2, NFluent 3.1.0, .NET 10 test project.

---

## Impact Snapshot

- Candidate exemplar: `tests/EyeCtrl.Core.Tests/Prediction/BigramModelTests.cs`
- Alternative larger candidate: `tests/EyeCtrl.Core.Tests/Prediction/WordPredictorTests.cs`
- GitNexus impact for `BigramModelTests`: `LOW`, 0 direct upstream dependents, 0 affected processes, 0 affected modules.
- GitNexus impact for `WordPredictorTests`: `LOW`, 0 direct upstream dependents, 0 affected processes, 0 affected modules.
- Production code touched: none in the exemplar migration.
- Test project touched: `tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj`.
- NFluent status: NuGet package `NFluent` version `3.1.0`, stable, compatible with `net10.0` according to NuGet compatibility data, Apache-2.0 license. It is an assertion library designed to work with xUnit/NUnit and other test frameworks.

## Migration Policy

- Do not migrate all tests in one change.
- Migrate only one class first.
- Keep test method names unchanged.
- Keep Arrange/Act/Assert structure unchanged unless the current comments are redundant.
- Do not add comments unless they clarify a non-obvious business rule.
- Do not change production code.
- Do not change test runner, package versions, or test project target framework.

---

### Task 1: Add NFluent to the Core test project

**Files:**
- Modify: `tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj`
- Test: `tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj`

- [ ] **Step 1: Add the package reference**

Add a new `<ItemGroup>` dedicated to test-framework packages (keep it separate from the ML tokenizer references):

```xml
  <ItemGroup>
    <PackageReference Include="NFluent" Version="3.1.0" />
  </ItemGroup>
```

> **Convention note:** Shared test-framework packages (xUnit v3, test SDK, coverlet) live centrally in `tests/Directory.Build.props`, not in individual `.csproj` files. This trial deliberately scopes NFluent to only `EyeCtrl.Core.Tests` — adding it centrally would force it onto all six test projects before the readability gate (Task 3) has decided anything. If the migration is adopted repo-wide, move the reference to `tests/Directory.Build.props` then. The repo does **not** use central package version management (there is no `Directory.Packages.props`), so the inline `Version` attribute is correct here.

- [ ] **Step 2: Restore/build to verify package compatibility**

Run:

```bash
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter FullyQualifiedName~BigramModelTests
```

Expected: package restore succeeds, tests run, and the existing xUnit assertions still pass.

- [ ] **Step 3: Commit the dependency-only change**

Run:

```bash
git add tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj
git commit -m "test: add NFluent assertion package"
```

---

### Task 2: Migrate `BigramModelTests` to NFluent

**Files:**
- Modify: `tests/EyeCtrl.Core.Tests/Prediction/BigramModelTests.cs`
- Test: `tests/EyeCtrl.Core.Tests/Prediction/BigramModelTests.cs`

- [ ] **Step 1: Add the NFluent using directive**

Replace the current using section:

```csharp
using EyeCtrl.Core.Prediction;
```

with:

```csharp
using EyeCtrl.Core.Prediction;
using NFluent;
```

- [ ] **Step 2: Replace simple equality/count assertions**

Use these replacements throughout the file:

```csharp
Assert.Single(collection);
```

to:

```csharp
Check.That(collection).HasSize(1);
```

```csharp
Assert.Equal(expected, actual);
```

to:

```csharp
Check.That(actual).IsEqualTo(expected);
```

```csharp
Assert.Empty(collection);
```

to:

```csharp
Check.That(collection).IsEmpty();
```

```csharp
Assert.Equal(3, collection.Count);
```

to:

```csharp
Check.That(collection).HasSize(3);
```

Prefer `HasSize(n)` over `Check.That(collection.Count).IsEqualTo(n)` — it reads better and gives a clearer failure message. (Applies to the `nextWords.Count` assertions in `Learn_StoresMultipleFollowersForSameWord`, `GetNextWords_RespectsMaxCount`, and `LoadFromDictionary_RestoresState`.)

- [ ] **Step 3: Replace boolean (dictionary-key) assertions**

`bigrams` is a `Dictionary<string, Dictionary<string,int>>`, so use the dictionary-specific `ContainsKey` check:

```csharp
Assert.True(bigrams.ContainsKey("word1"));
```

to:

```csharp
Check.That(bigrams).ContainsKey("word1");
```

Do **not** use `Check.That(bigrams).Contains("word1")` — NFluent's enumerable `Contains` matches `KeyValuePair` elements, not keys, so a bare key string fails to compile (or fails at runtime).

- [ ] **Step 4: Keep collection indexing and local variable extraction where useful**

Keep this shape for readable failed messages:

```csharp
var nextWords = model.GetNextWords("hello", 10);

Check.That(nextWords).HasSize(1);
Check.That(nextWords[0].word).IsEqualTo("world");
Check.That(nextWords[0].frequency).IsEqualTo(2);
```

Do not inline all assertions into one chain if it makes the test harder to read.

- [ ] **Step 5: Run the migrated exemplar tests**

Run:

```bash
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj --filter FullyQualifiedName~BigramModelTests
```

Expected: all `BigramModelTests` pass.

- [ ] **Step 6: Verify change scope via GitNexus before committing**

Call the GitNexus MCP tool (per CLAUDE.md, mandatory before commit) — this is a tool invocation, not a shell command:

```text
detect_changes({ scope: "unstaged" })
```

Expected: only symbols in `BigramModelTests.cs` are flagged; no production symbols or unrelated test classes appear.

- [ ] **Step 7: Run the full Core test project**

Run:

```bash
dotnet test tests/EyeCtrl.Core.Tests/EyeCtrl.Core.Tests.csproj
```

Expected: all Core tests pass.

- [ ] **Step 8: Run the full solution build**

Run:

```bash
dotnet build EyeCtrl.slnx
```

Expected: build succeeds.

- [ ] **Step 9: Commit the exemplar migration**

Run:

```bash
git add tests/EyeCtrl.Core.Tests/Prediction/BigramModelTests.cs
git commit -m "test: migrate BigramModelTests to NFluent assertions"
```

---

### Task 3: Review readability before expanding migration

**Files:**
- Review: `tests/EyeCtrl.Core.Tests/Prediction/BigramModelTests.cs`

- [ ] **Step 1: Compare readability against the original style**

Check these criteria:

- The migrated test reads like a sentence: `Check.That(actual).IsEqualTo(expected)`.
- The actual value is checked before the expected value, avoiding expected/actual order confusion.
- The test does not become more verbose than the original.
- The test does not use awkward NFluent APIs just for the sake of migration.
- Failure messages are likely to be more helpful than xUnit `Assert.*` messages. (Note: xUnit v3 may surface NFluent failures as *errors* rather than *assertion failures* — cosmetic; judge the message text itself.)

- [ ] **Step 2: Decide whether to continue**

Proceed only if at least 3 of these are true:

- NFluent reads clearer in the migrated class.
- NFluent failure messages are materially better.
- The migration was mechanically straightforward.
- The package adds no meaningful build/test friction.
- You (the sole maintainer) find this assertion style easier to read and write.

---

### Task 4: Optional follow-up migration batch

**Files:**
- Candidate: `tests/EyeCtrl.Core.Tests/Prediction/WordPredictorTests.cs`
- Candidate: `tests/EyeCtrl.Core.Tests/Prediction/PhraseModelTests.cs`
- Candidate: `tests/EyeCtrl.Core.Tests/Prediction/FuzzyEditDistanceTests.cs`

- [ ] **Step 1: Pick the next class only if Task 3 is positive**

Prefer the next class with:

- No UI dependencies.
- No fragile timing.
- Repeated assertion patterns that benefit from fluent syntax.
- Existing tests that are already well named and well structured.

- [ ] **Step 2: Run impact analysis before editing**

Call the GitNexus MCP tool (not a shell command):

```text
impact({ target: "<NextTestClassName>", direction: "upstream" })
```

Expected: risk is `LOW` or `MEDIUM`. If risk is `HIGH` or `CRITICAL`, stop and review before editing.

- [ ] **Step 3: Migrate one class per commit**

Do not migrate multiple large files in one commit.

---

## Self-Review

- Scope is focused: one package reference plus one test class.
- No production code is changed.
- No test runner is changed.
- No placeholders remain.
- Verification commands are explicit.
- `detect_changes` (GitNexus MCP tool) is run before the exemplar commit, per CLAUDE.md.
- Expansion is gated by readability review, not automatic migration.
