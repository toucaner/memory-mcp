# M13 — OnnxEmbeddingService: native memory hygiene + ORT tuning

## Dependencies
- M4 (OnnxEmbeddingService + IEmbeddingService + model download script)
- M10 (LddMarkers constants + EmbeddingResiliencePolicy — `[IMP:*]` markers, ADR-005)
- M12 (integration test harness — reused for the memory-stability gate)

## Goal
Eliminate native-memory retention in the ONNX embedding hot path and expose ONNX Runtime
memory knobs, **without** changing the 384-dim / L2-normalized contract or the ADR-005
fatal-on-load semantics. This is the **code** half of the memory-consumption remediation:
it does NOT change the model file (that is M14) and does NOT change deployment limits (M15).

## Problem Statement (evidence)
The deployed service showed `RSS 674 MB` against a host cgroup limit of `31.2 GB`.
This milestone addresses the *native retention* component, not the model-file component.

1. **Output collection never disposed.** `OnnxEmbeddingService.EmbedAsync` assigns
   `results = _session.Run(inputs)` (`src/McpMemoryService/Services/OnnxEmbeddingService.cs:213`)
   but never disposes it. `_session.Run` returns
   `IDisposableReadOnlyCollection<DisposableNamedOnnxValue>`, whose elements hold native
   `OrtValue` buffers. Without an explicit `Dispose`, those native buffers are only released
   when the GC finalizes the wrappers — RSS grows under sustained MCP traffic.
2. **Input `NamedOnnxValue` wrappers never disposed.** The `inputs` list
   (`OnnxEmbeddingService.cs:200-205`) is not disposed after `Run`.
3. **`SessionOptions` never disposed.** `var sessionOptions = new SessionOptions();`
   (`OnnxEmbeddingService.cs:111`) is a local that is never disposed after the
   `InferenceSession` is constructed (`:120`). `SessionOptions` wraps native state.
4. **No ORT memory knobs exposed.** `SessionOptions` sets only CPU provider + thread counts
   (`:111-114`). The CPU arena allocator and memory-pattern optimization retain activation
   memory that ONNX Runtime does not return to the OS. `IntraOpNumThreads` defaults to 4
   (`Configuration/OnnxModelOptions.cs:35`, `appsettings.json:18`), multiplying per-thread
   arena allocations.

`OnnxEmbeddingService.Dispose()` (`:510-513`) disposes only `_session`.

## Deliverables
- `src/McpMemoryService/Services/OnnxEmbeddingService.cs` (dispose fixes + session config entries)
- `src/McpMemoryService/Configuration/OnnxModelOptions.cs` (new memory knobs)
- `src/McpMemoryService/appsettings.json`, `src/McpMemoryService/appsettings.Development.json`
  (bind the new knobs; `IntraOpNumThreads` default 4 → 2)
- `tests/McpMemoryService.Tests/Services/OnnxEmbeddingServiceTests.cs` (add stability test)
- `tests/test_guide.md` (progress checklist + `[IMP:N]` markers)

## Contracts

### 1. Dispose the inference output collection (`EmbedAsync`)
```csharp
// was: results = _session.Run(inputs);
using var results = _session.Run(inputs);
```
`IDisposableReadOnlyCollection<DisposableNamedOnnxValue>` implements `IDisposable`; disposing it
releases the native output tensors deterministically. The extracted `lhs` tensor is read into
managed `float[] pooled` (via `MeanPooling`) **before** the `using` scope ends, so disposal does
not invalidate the returned vector. The return value is `pooled` (managed), not the native tensor.

### 2. Dispose input wrappers (`EmbedAsync`)
After `Run` completes, dispose every `NamedOnnxValue` in `inputs` (e.g. `try/finally` or
`foreach (var v in inputs) v.Dispose();`). Disposing a `CreateFromTensor` wrapper does not dispose
the backing `DenseTensor<long>` (managed), so the arrays remain valid.

### 3. Dispose `SessionOptions` (constructor)
```csharp
using var sessionOptions = new SessionOptions();
sessionOptions.AppendExecutionProvider_CPU(0);
sessionOptions.InterOpNumThreads = 1;
sessionOptions.IntraOpNumThreads = opts.IntraOpNumThreads;
sessionOptions.EnableCpuMemArena = opts.EnableCpuMemArena;
sessionOptions.EnableMemoryPattern = opts.EnableMemoryPattern;
sessionOptions.AddSessionConfigEntry("session.use_ort_model_bytes_for_initializers",
    opts.UseOrtModelBytesForInitializers ? "1" : "0");
_session = new InferenceSession(modelPath, sessionOptions);
```
The `InferenceSession` copies the options it needs during construction; disposing the
`SessionOptions` afterwards is safe and is the documented pattern.

### 4. New `OnnxModelOptions` knobs
```csharp
/// <summary>Enables the ONNX Runtime CPU arena allocator (retains activation memory; default false for bounded RSS).</summary>
public bool EnableCpuMemArena { get; init; } = false;

/// <summary>Enables ONNX Runtime memory-pattern optimization (faster, higher retained memory; default true).</summary>
public bool EnableMemoryPattern { get; init; } = true;

/// <summary>References initializers directly from the model file bytes to avoid duplicating weights (default true).</summary>
public bool UseOrtModelBytesForInitializers { get; init; } = true;
```
`IntraOpNumThreads` default changes from `4` to `2` (SPEC §5.1 headroom intent preserved;
halves per-thread arenas). The value stays overridable via `appsettings.json`.

> **[UNVERIFIED_VERSION]** The property names `EnableCpuMemArena` / `EnableMemoryPattern` and the
> session config key `session.use_ort_model_bytes_for_initializers` must be validated against
> `Microsoft.ML.OnnxRuntime` 1.27.0 at compile + runtime. If a knob is absent/unsupported,
> `@code`/`@debug` drops that single line (do NOT substitute a guessed key) and records the probe
> result in `BUG_FIX_CONTEXT`.

### 5. Preserved invariants (MUST NOT regress)
- `Dimension == 384` (ADR-011); output remains L2-normalized (`L2Normalize`).
- Model/tokenizer load remains in the constructor; failure → log `[FATAL]` + rethrow
  (ADR-005 startup Exit 1).
- `EmbedAsync` rethrows on inference failure (no swallow; M10 middleware catches).
- `Dispose()` still disposes `_session` (and the tokenizer if it is `IDisposable`).

## Algorithm / Logic

### Step 1: Extend `OnnxModelOptions`
Add the three new `init` properties above. Update the `[GREP_SUMMARY]`/`[CHANGES]` header
additively (M2/M4 entries preserved). Change `IntraOpNumThreads` default to `2`.

### Step 2: Wire the knobs in `OnnxEmbeddingService` ctor
Wrap `SessionOptions` in `using`; apply `EnableCpuMemArena`, `EnableMemoryPattern`, and the
`AddSessionConfigEntry` call before `new InferenceSession(...)`. Add `[IMP:M13]` LDD info log
after session creation listing the effective knobs (arena, memoryPattern, intraOpThreads,
useOrtModelBytes) — use a new `LddMarkers` constant if the single-source rule requires it.

### Step 3: Fix `EmbedAsync` disposal
Replace the bare `results = _session.Run(inputs)` with `using var results = ...`; dispose the
`inputs` wrappers after `Run` (before reading outputs is not required; dispose after extracting
`lhs`/pooling is acceptable — choose the earliest point where `results` is no longer needed).
Do not change the tokenization, mean-pooling, or L2 logic.

### Step 4: Update appsettings
`OnnxModel` section gains the three booleans; `IntraOpNumThreads` set to `2`. Mirror in
`appsettings.Development.json` only if it overrides `OnnxModel`.

### Step 5: Tests
Add `EmbedAsync_RepeatedCalls_DoNotDegrade` (Category=Integration): warm up once, then run
N=500 `EmbedAsync` calls, asserting every result is length 384 and norm ≈ 1.0. Add a
memory-stability companion that samples `GC.GetTotalMemory(false)` and
`Process.GetCurrentProcess().WorkingSet64` before/after the warm loop and asserts the growth is
below a calibrated threshold. Mark the threshold `[UNVERIFIED_VERSION]` and tune after the first
run (suggested initial bound: working-set growth < 100 MB over 500 calls). Do NOT assert an
absolute RSS value here — that is M15's deployment gate.

### Step 6: Gate
`dotnet build McpMemoryService.sln` → 0 warnings / 0 errors.
`dotnet test --filter "Category!=Integration"` → unit gate green (M4 ONNX tests are
`Category=Integration`, so they run in the integration gate, not here).
`dotnet test --filter "Category=Integration"` (Docker + model required) → green, including the
new stability test.

## Tests
| Test | Category | Asserts |
|---|---|---|
| `EmbedAsync_RepeatedCalls_DoNotDegrade` | Integration | 500 calls → dim 384, norm ≈ 1.0, no exception |
| `EmbedAsync_WarmupMemoryGrowth_IsBounded` | Integration | WorkingSet growth over N warm calls < calibrated threshold |
| existing M4 ONNX tests | Integration | unchanged (no regression) |

## Acceptance Criteria
- [ ] `dotnet build McpMemoryService.sln` — OK (0 warnings, 0 errors)
- [ ] Unit gate (`Category!=Integration`) — all PASS, no regressions
- [ ] `results` collection is disposed (`using`) in `EmbedAsync`
- [ ] `inputs` `NamedOnnxValue` wrappers are disposed after `Run`
- [ ] `SessionOptions` is disposed after `InferenceSession` construction
- [ ] `OnnxModelOptions` exposes `EnableCpuMemArena` / `EnableMemoryPattern` / `UseOrtModelBytesForInitializers`
- [ ] `IntraOpNumThreads` default = 2 in `OnnxModelOptions` + `appsettings.json`
- [ ] 500× `EmbedAsync` produce valid 384-dim L2-normalized vectors
- [ ] Integration memory-stability test PASS (growth below calibrated threshold)
- [ ] 384-dim / L2 / fatal-on-load / no-swallow invariants preserved
- [ ] Logs contain the new `[IMP:M13]` effective-knobs marker

## Context for @code
- Read: `src/McpMemoryService/Services/OnnxEmbeddingService.cs` (esp. `:111`, `:200-213`, `:510-513`)
- Read: `src/McpMemoryService/Configuration/OnnxModelOptions.cs`, `appsettings.json`
- Read: `milestones/M4-onnx-embedding-service.md` (ADR-011, thread tuning)
- Skill: `csharp-conventions` (`#region`, XML docs, LDD via `LddMarkers`)
- Preserve the existing `BUG_FIX_CONTEXT` scars in `OnnxEmbeddingService.cs` verbatim
- Do NOT touch `LoadUnigramTokenizerFromHfJson` (pinned to Microsoft.ML.Tokenizers 2.0.0 internals)
- This milestone does NOT change the model file, Dockerfile, or docker-compose
