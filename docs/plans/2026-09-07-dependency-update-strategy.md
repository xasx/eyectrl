# Dependency Update Strategy — 2026-09-07

Status: **EXECUTED** on branch `deps/dependency-updates-2026-09` (worktree
`.worktrees/dependency-updates-2026-09`), same day. See "Execution outcome" at the end for
what landed, what was reverted, and the two open owner decisions.

Scope: every outdated top-level NuGet package reported by
`dotnet list EyeCtrl.slnx package --outdated` (2026-09-07, nuget.org + CommunityToolkit feed),
plus the two standalone benchmark test projects. **No known vulnerabilities** exist in the
current graph (direct or transitive) — this is a currency/maintenance effort, not a security
fire drill. That licenses a patient, wave-by-wave approach.

## Principles

1. **One wave = one PR = one revert.** Never mix risk classes; rollback must always be a
   single `git revert`.
2. **Serialized landing, parallelizable prep.** Waves 1–3 are independent and can be
   prepped in parallel worktrees, but land in order so CI attribution stays clean.
3. **Every wave passes the full gate** (see below) before the next starts.
4. **Docs ride with the bump.** AGENTS.md pins versions in prose (Mvvm "v8.4.0", "xUnit v3
   (3.2.2)"); README/ARCHITECTURE were recently doc-synced and may mention versions too.
   Update in the same PR as the version change.
5. **Work happens in `.worktrees/`** (kebab dir + prefixed branch), per repo convention —
   never on `main`.

**Gate for every wave** (box must be quiet — no second test suite running):

```bash
dotnet build EyeCtrl.slnx
dotnet test                                   # all projects; includes benchmark-generator tests
dotnet test tests/EyeCtrl.App.Tests --filter "Category=UI"   # waves 2 and 4 only (see below)
```

CI additionally validates palettes and gates coverage (≥50 % line / ≥45 % branch) — the
coverage gate is the thing most likely to move on test-stack bumps, so watch it on wave 4.

App-launch smoke checks (needed for the Serilog wave) are **owner-gated**: launching EyeCtrl
seizes the user's only input channel. Ask first; UI-test runs also spawn app windows and
need a coordinated window.

## Current state (2026-09-07)

| Group | Package | Now | Latest | Where |
|---|---|---|---|---|
| Patch | Microsoft.Extensions.DependencyInjection | 10.0.3 | 10.0.11 | App |
| Patch | Microsoft.Extensions.Logging.Abstractions | 10.0.0 | 10.0.11 | Core, Platform, Platform.Tests |
| Patch | CommunityToolkit.Mvvm | 8.4.0 | 8.4.2 | Core, ViewModels |
| Serilog | Serilog.Sinks.File | 6.0.0 | 7.0.0 | App, Supervisor |
| Serilog | Serilog.Extensions.Logging | 9.0.1 | 10.0.0 | App |
| Native | Microsoft.ML.OnnxRuntime.DirectML | 1.21.0 | 1.24.4 | LM |
| Test stack | xunit.v3 | 3.2.2 | 4.0.0 | all 13 test projects |
| Test stack | xunit.runner.visualstudio | 3.1.5 | 4.0.0 | all 13 test projects |
| Test stack | xunit.analyzers | 1.27.0 | 2.0.0 | all 13 test projects |
| Test stack | Xunit.StaFact | 3.0.13 | 4.0.23 | App.Tests, Controls.Tests, Sdk.Wpf.Tests |
| Test stack | Microsoft.NET.Test.Sdk | 18.3.0 | 18.9.0 | all 13 test projects |
| Test stack | coverlet.collector | 8.0.0 | 10.0.1 | all 13 test projects |
| WinUI | Microsoft.WindowsAppSDK | 1.6.250602001 | 1.8.260804001 / 2.4.0 | Sdk.WinUI |

(EyeCtrl.Benchmarks itself has no updates; its test project inherits the test-stack wave.)

## Wave 1 — Patch batch (lowest risk, do first)

**Contents**
- Microsoft.Extensions.DependencyInjection 10.0.3 → 10.0.11 (App)
- Microsoft.Extensions.Logging.Abstractions 10.0.0 → 10.0.11 (Core, Platform, Platform.Tests)
- CommunityToolkit.Mvvm 8.0.0 → 8.4.2 (Core, ViewModels)
- AGENTS.md: the two "v8.4.0" mentions (project table + Dependencies bullet)

**Why first**: patch-level within pinned major lines, aligned with the pinned .NET 10.0.102
SDK. No API surface changes expected. Wave 2's Serilog.Extensions.Logging 10 may require a
newer M.E.Logging floor, so landing 10.0.11 first removes a possible restore conflict.

**Verification**: standard gate only.

## Wave 2 — Serilog majors (low real risk)

**Contents**
- Serilog.Sinks.File 6.0.0 → 7.0.0 (App, Supervisor)
- Serilog.Extensions.Logging 9.0.1 → 10.0.0 (App)

**Research finding**: Sinks.File 7's only breaking change is dropping pre-`netstandard2.0`
TFMs; this repo is net10.0/net8.0+, so it is a version-number major, not a behavior major.
Serilog.Extensions.Logging 10 aligns with the M.E.Logging 10 line — check its dependency
floor at restore time (wave 1 already handled the likely floor).

**Verification**
- Standard gate.
- Logging behavior: `LoggingInitializer` writes to `%APPDATA%/EyeCtrl/logs/eyectrl-.log`
  with daily rolling + 7-day retention. Confirm a fresh dated file appears and old ones age
  out (Supervisor tests cover part of this; an owner-approved app launch is the full check).

## Wave 3 — ONNX Runtime (native bump, seam-isolated)

**Contents**
- Microsoft.ML.OnnxRuntime.DirectML 1.21.0 → 1.24.4 (EyeCtrl.LM)

**Why isolated**: LM is a leaf project behind the `ILanguageModelPredictor` seam; Core has
no native ML deps. It can be prepped in parallel with waves 1–2.

**Verification**
- `dotnet test tests/EyeCtrl.LM.Tests`
- SmallLM benchmarks before/after (needs model in `%APPDATA%/EyeCtrl/models`):
  `dotnet run --project benchmarks/EyeCtrl.Benchmarks -c Release -- --filter *SmallLM*`
  — sanity-check latency is not regressed vs. the recorded baseline; DirectML runtime
  changes occasionally shift native call paths.
- Publish smoke (`dotnet publish ... -p:PublishSingleFile=true`) — native package size and
  loose-file behavior should not change, but the ONNX natives move with this bump.

## Wave 4 — Test stack, coordinated xUnit 4.0 migration

**Contents** (all six move together — they are a version-matched set)
- xunit.v3 3.2.2 → 4.0.0, xunit.runner.visualstudio 3.1.5 → 4.0.0,
  xunit.analyzers 1.27.0 → 2.0.0, Xunit.StaFact 3.0.13 → 4.0.23,
  Microsoft.NET.Test.Sdk 18.3.0 → 18.9.0, coverlet.collector 8.0.0 → 10.0.1
- Across all 13 test projects (12 solution + `tests/EyeCtrl.Benchmarks.Tests`).
- AGENTS.md: "xUnit v3 (3.2.2)" mention and any NFluent-pairing prose that names versions.

**Research findings that shape this wave**
- xunit.v3 4.0.0 (2026-08-14 — only weeks old) breaks "mostly extensibility APIs", drops
  official Microsoft Testing Platform v1 and Mono support, adds Native AOT.
- **Repo exposure is narrow**: no `Microsoft.Testing.Platform` references and no
  `xunit.runner.json` files exist (checked 2026-09-07), so the MTP drop and the config
  schema change don't apply. CI's `dotnet test --filter` path goes through
  xunit.runner.visualstudio, which moves to 4.0.0 in the same wave. We write no runners or
  extensibility points — plain `[Fact]`/`[StaFact]`/NFluent usage.
- Xunit.StaFact's 4.x line is the xunit.v3-compatible line (used in 50 test files — the
  largest repo-specific surface in this wave).
- coverlet.collector 10 requires .NET 8+ (fine) and fixed Cobertura path inconsistencies in
  10.0.1; the CI coverage gate is the watch-item.
- NFluent 3.1.0 is runner-agnostic (assertion library) and stays put.

**Verification** (the heaviest of any wave)
- Standard gate, then **UI tests** locally (`--filter "Category=UI"` — StaFact 4 touches
  every STA test; coordinate the window with the owner).
- Full CI run on the PR, explicitly comparing coverage numbers against the previous main
  run — the gate must not drift.
- Remember the known-local-only quirks (German-locale EyeCareHooks test, SettingsRenderMapping
  flake under full-solution runs, `ExternalDwellServiceTests` must not be run alone).

**Fallback / decision point**: if 4.0 breaks `dotnet test` integration or StaFact in a way
that isn't a quick fix, fall back to staying on xunit.v3 3.2.2 and taking only Test.Sdk
18.9 + coverlet 10.0.1 as a sub-wave (those two are xunit-version-independent). Record the
decision either way. Precedent doc: `docs/plans/2026-06-18-xunit-v3-migration.md`.

**Why after waves 1–3**: it touches every test project's csproj, so anything landing
concurrently would rebase over it; doing it last minimizes friction.

## Wave 5 — WindowsAppSDK (split into 5a conservative, 5b owner decision)

**5a — stay on the serviced 1.x line: 1.6.250602001 → 1.8.260804001 (Sdk.WinUI)**
- 1.8 is actively serviced (feature promotions still shipping in 1.8.x servicing releases);
  staying within 1.x avoids all SemVer-major breaking changes.
- `EyeCtrl.Sdk.WinUI` targets `net8.0-windows10.0.19041.0`; verify 1.8 supports that TFM
  floor (it does support net8 — confirm at restore).
- Verification: standard gate (Sdk.WinUI.Tests runs on net10.0 and is included in `dotnet test`).

**5b — 2.x evaluation (deferred, owner decision)**
- 2.0 is the SemVer reset: NuGet reference refactoring, package family name now follows the
  major version (2.x deploys **side-by-side** with 1.x), `FileSavePicker` no longer creates
  the file, App Content Search removed, ML split into `Microsoft.Windows.AI.MachineLearning`.
- Because `EyeCtrl.Sdk.WinUI` is consumed by external integrators, the side-by-side package
  family change is a *consumer-facing* decision, not just our build. Recommend: park 2.x
  until there is a feature need, revisit as a standalone spike (TFM floor + deployment
  implications for SDK consumers documented first).

## Sequencing

```
Wave 1 (patches) ──► Wave 2 (Serilog) ──► Wave 4 (test stack) ──► Wave 5a (WinAppSDK 1.8)
                                    ▲
Wave 3 (ONNX, parallel prep) ───────┘ (land anywhere before wave 4; no coupling)
```

Total: 4–5 PRs. Estimated effort: waves 1–2 are hours each; wave 3 is an hour plus a
benchmark run; wave 4 is a focused half-day including UI tests; wave 5a is an hour.

## Explicitly out of scope

- .NET SDK pin in `global.json` (10.0.102) — servicing updates evaluated separately.
- NFluent (3.1.0 is current), BenchmarkDotNet (current), Tobii SDK (on-disk conditional).
- Introducing Central Package Management (`Directory.Packages.props`). Worth considering as
  a follow-up — it would turn future cross-project bumps like wave 4 into one-file edits —
  but it's a structural change and shouldn't ride along with version churn.

---

## Execution outcome (2026-09-07, branch `deps/dependency-updates-2026-09`)

Gate used for every wave: `dotnet build EyeCtrl.slnx` + `dotnet test EyeCtrl.slnx --filter
"Category!=UI"` (12 projects, 2,885 tests, all green after each wave) + the benchmark-generator
tests (27). UI tests and the app-launch logging smoke were **not** run — both are owner-gated
(windows spawn; see principles). Commits, in order:

| Commit | Wave | Outcome |
|---|---|---|
| `1bb4a47e` | 1 — patches | **LANDED.** M.E.DI + M.E.Logging.Abstractions 10.0.11, Mvvm 8.4.2, AGENTS.md synced. |
| `87c2a2d2` | 2 — Serilog | **LANDED.** Sinks.File 7.0.0 (App, Supervisor), Extensions.Logging 10.0.0 (App). Restore + suite green. |
| — | 3 — ONNX | first attempt (1.24.4) **REVERTED** — see blocker below. Owner chose the version probe. |
| `64a4700f` | 3 — ONNX | **LANDED at 1.22.1.** Probe: 1.23.0 and 1.24.4 fail identically; 1.22.1 loads the pinned model at ENABLE_ALL and is ~2–3× faster on `PredictAndWait`. |
| `76b53360` | 4 — test stack | **FALLBACK LANDED.** Test.Sdk 18.9.0 + coverlet 10.0.1 only; xUnit family stays 3.2.2/3.1.5/1.27.0, StaFact stays 3.0.13. See below. |
| `65180808` | 5a — WinAppSDK | **LANDED.** 1.8.260804001; Sdk.WinUI Release build + its tests green, full gate green. |

### Wave 3 blocker — ONNX Runtime 1.24.4 cannot load the shipped model

`SmallLMPredictorModelTests.Predict_WithEveryRealDownloadedModel_ProducesASuggestion` fails
at session creation with:

```
OnnxRuntimeException: [ErrorCode:Fail] Exception during initialization:
onnxruntime::graph_utils::GetIndexFromName itr != node_args.end() was false.
Attempting to get index by a name which does not exist:
InsertedPrecisionFreeCast_/model/layers.1/input_layernorm/Constant_output_0
for node: /model/layers.0/input_layernorm/Mul_1/SimplifiedLayerNormFusion/
```

i.e. the 1.24.4 graph optimizer's SimplifiedLayerNormFusion pass cannot handle the exported
`qwen2.5-1.5b.onnx`. With `GraphOptimizationLevel.ORT_ENABLE_BASIC` the model loads and
infers correctly (that test passes), and the 3-iteration ShortRun timing was *not* slower
(1.87 s vs 2.01 s baseline; the rigorous Default-job comparison wasn't captured — noisy
evidence either way). Baseline on 1.21.0 + ENABLE_ALL: `PredictAndWait` ≈ 2.29 s ± 0.09 s.

Reverted because shipping 1.24.4 would force `ORT_ENABLE_BASIC` (or similar) for **every**
user and model — a lasting product-behavior change riding inside a dependency bump, on weak
perf evidence. Owner options:
1. Stay on 1.21.0 (status quo of this branch) until the model is re-exported for newer ORT.
2. Probe 1.22.x/1.23.x — the regression was introduced somewhere in that range; one of them
   may load at ENABLE_ALL.
3. Accept ENABLE_BASIC + 1.24.4 after a proper Default-job benchmark comparison.
4. Re-export the ONNX model with a current exporter (fixes the root cause).

**Resolution (2026-09-07, owner chose the probe — option 2):** probed newest-first.

- `1.23.0` — **fails**, identical `SimplifiedLayerNormFusion` / `GetIndexFromName` error.
  The regression window is exactly the 1.23.0 release.
- `1.22.1` — **loads at ENABLE_ALL.** All 38 LM tests pass.
- Upstream check: `onnx-community/Qwen2.5-1.5B-Instruct` on HF has no newer export — the
  pinned commit `6287331f` is the repo's newest; the model files date to 2024-09-23. So
  option "repoint at a newer commit" was not available, and a self-export remains the only
  path to a 1.23+ runtime.

Benchmark on 1.22.1 (same box, same command as the 1.21.0 baseline):
`PredictAndWait` Default mean 1.073 s (median 0.644 s) vs baseline 2.292 s mean — a ~2–3×
**improvement**, tokenizers unchanged (encode 6.49 µs / decode 0.77 µs), identical managed
allocations. Wave 3 landed at **1.22.1** (`64a4700f`) after the full solution gate (12/12
projects, 2,885 tests green). ONNX 1.23+/1.24.x stays blocked until the model is re-exported.

**Re-export executed (2026-09-07, option 4) — artifact produced; CPU-1.23+ still blocked
(upstream ORT bug, not the artifact).**

Pipeline (all in `~/qwen-reexport/`, scripts kept): venv (torch 2.7.1 CPU + optimum 1.27 —
the 1.x line still owns the ONNX exporter; optimum 2.x is a stub, and torch 2.14 breaks the
1.x exporter's symbolic hooks) → optimum export of `Qwen/Qwen2.5-1.5B-Instruct`
(text-generation-with-past, validated against PyTorch by optimum at ~1e-4) → **custom
deterministic fp16 pass** (`fp16_pass.py`) because BOTH stock fp16 converters
(onnxconverter-common and onnxruntime.transformers) produce mixed-dtype graphs for this
decoder (three distinct failure modes hit; the custom pass converts every float
initializer/constant/Cast target, inserts `InsertedPrecisionFreeCast_` boundary casts —
the original artifact's own design — and topologically re-sorts) → q4 quantization
(`quantize.py`, MatMulNBits bits=4 **block_size=32** symmetric, lm_head excluded → exactly
196 nodes like the original; tied-embedding dedupe impossible here: optimum exports lm_head
weight pre-transposed as a separate initializer → file is **1.67 GB vs 1.22 GB original**).
ONNX checker clean; single-file.

Result matrix (clean-process probe, CPU-only vs DirectML):

| Runtime | old artifact | new artifact |
|---|---|---|
| 1.21.0 CPU / DML | OK / OK | OK / OK |
| 1.22.1 CPU / DML | OK / OK | OK / OK |
| 1.24.4 CPU | **fails** (fusion bug) | **fails** (same bug) |
| 1.24.4 DML | untested | **OK** |

Conclusion: the 1.23+ blocker is an **ORT CPU-optimizer bug** (SimplifiedLayerNormFusion
chokes on the q4f16 boundary-cast pattern regardless of artifact vintage — worth an
upstream issue), NOT a property of the old export. The new artifact is a valid, current-
tooling drop-in replacement (kept installed on this machine; original backed up at
`%APPDATA%/EyeCtrl/qwen2.5-1.5b.onnx.orig-backup`), but it does not unlock 1.24.4 for the
CPU path the model test exercises (`preferGpu: false`), so the branch stays on 1.22.1.
Hosting the 1.67 GB artifact + `models.json` repoint remains an owner decision (needs a
URL + sha256 + sizeBytes; also decide whether the +450 MB is acceptable or whether lm_head
should be int4-quantized instead, trading a little logit precision for ~1.32 GB).
Intermediates (~15 GB) live in `~/qwen-reexport/` for cleanup.

### Wave 4 — why xUnit 4.0 was not taken (and what taking it would involve)

The pre-flight validated the strategy's optimism: StaFact 4.0.23's stable line is built for
xunit.v3 4.0.0 (`xunit.v3.extensibility.core >= 4.0.0`), the one source break
(`CollectionBehaviorAttribute.DisableTestParallelization` → `[assembly:
Parallelization(Mode = ParallelMode.None)]` in `Xunit.v3`/`Xunit.Sdk`) was found and fixed,
and the solution compiled. But `dotnet test` then **failed on every test project**:

```
Microsoft.Testing.Platform.msbuild 2.3.3: error : Testing with VSTest target is no longer
supported by Microsoft.Testing.Platform on .NET 10 SDK and later. If you use dotnet test,
you should opt-in to the new dotnet test experience.
```

The `xunit.v3` 4.0.0 metapackage is hard-pinned (`[4.0.0]`) to the `xunit.v3.mtp-v2` flavor:
v4 test projects *are* Microsoft Testing Platform v2 projects, and MTP v2 refuses VSTest-mode
`dotnet test` on the .NET 10 SDK. Migrating means all of:

1. `global.json` gains `{"test":{"runner":"Microsoft.Testing.Platform"}}` (repo-wide).
2. Command syntax changes repo-wide: `dotnet test --solution EyeCtrl.slnx`,
   `--project X.csproj`; VSTest's `--filter "Category!=UI"` becomes xUnit's MTP
   `--filter-trait` syntax — **negation** (`Category!=UI`, which CI depends on) needs
   verifying; per-framework arg routing via `TestingPlatformCommandLineArguments` may be
   needed.
3. **CI coverage collection must be rebuilt**: coverlet.collector is VSTest-only; MTP mode
   needs `Microsoft.Testing.Extensions.CodeCoverage` (cobertura output feeding the ≥50 %/45 %
   gates). The GitHub workflow needs a real rewrite, not a version bump.
4. AGENTS.md's entire Test section (commands, filter examples) changes.
5. Risk: MTP may run test modules concurrently — this repo's named-pipe suites
   (`HeartbeatPublisher`, `ExternalDwellService`, machine-global pipe names) have a
   documented history of exactly that class of cross-suite theft; parallel-by-default would
   need auditing or opting out.

Fell back per the strategy's own decision point: Test.Sdk 18.9.0 + coverlet.collector 10.0.1
landed (xunit-version-independent, full gate green); the 4.0 jump is deferred as its own
planned migration (est. half-day plus CI work) whenever the owner wants it.

### Remaining outdated (all deliberate)

**Shipping in the LM release PR (2026-10-17):** `Microsoft.ML.OnnxRuntime.DirectML`
**1.24.4** moves together with the fused Qwen2.5-1.5B artifact (PR link TBD — the release
ticket's PR), so the ORT entry is no longer outdated; the re-export pipeline is committed
under `tools/model-export/qwen2.5-1.5b/`. Still deliberate: `Microsoft.WindowsAppSDK` 2.4.0
(wave-5b owner decision), `xunit.v3`/`xunit.runner.visualstudio`/`xunit.analyzers` 4.0.0 +
`Xunit.StaFact` 4.0.23 (wave-4 MTP migration). Everything else is current as of 2026-09-07.

## Follow-up (same day, post-landing): the fused artifact unlocks 1.24.4

Seconded plan executed 2026-09-07: branch landed on main (rebased over the parallel
caregiver work, pushed as `819f4fc9..a5afe78f`, full non-UI gate green on the merged
combination), the upstream ORT issue drafted at `~/qwen-reexport/onnxruntime-issue.md`
(awaiting owner review before filing), and the offline-fusion experiment **succeeded**:

1. `onnxruntime.transformers.optimizer` (python, model_type="gpt2", heads=12, hidden=1536)
   fused the fp32 export's 57 decomposed RMSNorm patterns into
   `SimplifiedLayerNormalization`/`SkipSimplifiedLayerNormalization` contrib ops
   (6,964 → 3,225 nodes). The buggy runtime fusion only fires on the *decomposed* pattern.
2. The deterministic fp16 pass + q4 quantization then applied unchanged
   (`fuse.py`/`fp16_pass.py`/`quantize.py`, all in `~/qwen-reexport/`).
3. Result `model_q4f16_fused.onnx`, 1,671,599,992 bytes,
   sha256 `61e17fef2f6df1fd112dc42c69777298271c8b5992de21d35a43b4bfef99d688`,
   196 MatMulNBits (block 32, sym, lm_head excluded). ONNX checker can't validate
   com.microsoft contrib ops at this opset — ORT itself is the gate, and it passes:
   **loads on 1.21.0 / 1.22.1 / 1.24.4 × CPU / DirectML — universal**, including the
   EyeCtrl model test (CPU-only, `preferGpu: false`) on both 1.22.1 and 1.24.4 (38/38),
   and the full solution gate on 1.24.4. Benchmark (fused, 1.24.4): `PredictAndWait`
   1.27 s mean ±0.06 (tight) vs 1.22.1-unfused 1.07 s mean/0.64 s median (bimodal) vs
   1.21 baseline 2.29 s — same ballpark, all far ahead of baseline. The fused model is
   installed in `%APPDATA%/EyeCtrl/models` (validated against the live app's 1.21.0);
   the prior non-fused export and the original download are backed up alongside.

**Shipping 1.24.4 is now purely a coordinated-release task**: host the fused artifact
(owner decision: HF repo vs GitHub release; note the int4-lm_head option for
~1.32 GB), update `models.json` (url/sha256/sizeBytes — existing clients re-download on
hash change), bump `EyeCtrl.LM` to 1.24.4, run the full gate. All three must land
together — a runtime-only bump would break the CPU path for every user still holding a
previously downloaded model. The fp16-IO dodge was also tested and falsified (same fusion
failure without any boundary casts), confirming the trigger is the decomposed-pattern +
MatMulNBits shape itself.

## Sources

- Serilog.Sinks.File releases: https://github.com/serilog/serilog-sinks-file/releases
- xUnit v3 4.0.0 release notes: https://xunit.net/releases/v3/4.0.0 (adapter:
  https://xunit.net/releases/visualstudio/4.0.0)
- Xunit.StaFact (4.x = xunit.v3 line): https://github.com/AArnott/Xunit.StaFact/releases,
  https://github.com/AArnott/Xunit.StaFact/issues/147
- coverlet changelog: https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/Changelog.md
- Windows App SDK 2.0 release notes:
  https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-2-0
- dotnet test VSTest/MTP modes + migration:
  https://learn.microsoft.com/dotnet/core/testing/unit-testing-with-dotnet-test
- xunit ParallelizationAttribute source: `src/xunit.v3.core/Attributes/ParallelizationAttribute.cs`
  and `src/xunit.v3.common/Options/ParallelMode.cs` in github.com/xunit/xunit
