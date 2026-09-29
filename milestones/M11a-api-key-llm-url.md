# M11a — Qdrant ApiKey + LLM URL update (mini-milestone)

> **Mini-milestone.** Inserted between M11 (DONE — committed `0ee493d`, `@qa SUCCESS` on Docker artifacts with operator-action-required note) and M12 (e2e). Born out of the M11 `@qa` verdict: the operator's Qdrant at `<ip>:<port>` now **requires an API key** (`Grpc.Core.RpcException: Unauthenticated: Must provide an API key or an Authorization bearer token`) → `QdrantCollectionInitializer.StartAsync` fatal-exits → container restart-loops forever → AC-3/AC-4/AC-5/AC-11 runtime-gated. `QdrantOptions` (M5) has NO `ApiKey` property, so there was nothing M11 (infrastructure-only, no `.cs`) could wire. M11a adds the missing `ApiKey` contract + updates the LLM endpoint URL. The LLM URL `LLM_CPP_URL` / `LlmSummarizer__BaseUrl` was ALREADY updated by the operator to `<url>` in `docker-compose.yml` (lines 13 + 18) BEFORE this milestone; M11a's only LLM-related action is the documentation/contract reconciliation + the `appsettings.json` default alignment (no behavior change — the env-var bridge already overrides it at container runtime).

## Dependencies

- **M5 (DONE — `QdrantOptions.cs` + `QdrantService.cs` ctor L66 `new QdrantClient(host, port: _options.GrpcPort)`; `QdrantCollectionInitializer` IHostedService; `QdrantServiceTests` integration ctor L241 `new QdrantClient("localhost", port: 6334)`)**
- **M11 (DONE — `Dockerfile`, `docker-compose.yml`, `.dockerignore`; `@qa SUCCESS` `0ee493d` with the operator-action-required note that motivates this mini-milestone)**

No dependency on M12. M11a is a forward-enabling fix for the M11 runtime smoke gate + a prerequisite for M12 e2e (M12 cannot exercise the full e2e path against an authenticated Qdrant without this).

## Goal

Add a nullable `ApiKey` property to `QdrantOptions` and pass it through to the `QdrantClient` constructor, so the service can connect to a Qdrant instance that requires API-key authentication (the operator's `<ip>:<port>`). The change is **strictly backward-compatible**: `ApiKey` defaults to `null`, the `QdrantClient` `apiKey` parameter defaults to `null`, and unauthenticated Qdrant deployments continue to work unchanged. Add the `Qdrant__ApiKey` env-var bridge to `docker-compose.yml` (empty default — the operator supplies the real key at deploy time). Reconcile the LLM URL contract (the operator already changed `LLM_CPP_URL` / `LlmSummarizer__BaseUrl` to the new host; M11a aligns the `appsettings.json` default to match + documents the change).

## Deliverables

- `src/McpMemoryService/Configuration/QdrantOptions.cs` — ADD `public string? ApiKey { get; init; }` (nullable, default `null`, `init`-only — matches the existing `init`-only property style on `Url`/`CollectionName`/`GrpcPort`)
- `src/McpMemoryService/Services/QdrantService.cs` — ctor L66: `_client = new QdrantClient(host, port: _options.GrpcPort, apiKey: _options.ApiKey);` (named-parameter `apiKey:` — backward-compatible: the `QdrantClient` 4th positional param defaults to `null`)
- `src/McpMemoryService/appsettings.json` — ADD `"ApiKey": null` to the `Qdrant` section (between `GrpcPort` and `CollectionName`; `null` literal is valid JSON + binds to `string?` — `null` means "no api key")
- `docker-compose.yml` — ADD `- Qdrant__ApiKey=` (empty-default env var in the `environment:` block, alongside the existing `Qdrant__Url`/`LlmSummarizer__BaseUrl` bridge vars). The operator fills it at deploy time via `docker-compose.yml` edit OR a `.env` file / shell env override. The LLM URL env vars (`LLM_CPP_URL` + `LlmSummarizer__BaseUrl`) are ALREADY at the new host — no operator action required there; M11a only documents the change in the header comment.
- **NO test file edits** (`tests/McpMemoryService.Tests/Services/QdrantServiceTests.cs` L241 stays `new QdrantClient("localhost", port: 6334)` — `apiKey:` defaults to `null`; the integration tests run against an unauthenticated Docker Qdrant at `localhost:6334`, which is unchanged by the nullable ApiKey contract). **NO new unit tests** — the change is a single nullable named-parameter pass-through with no new behavior to assert at the unit level (the integration gate already covers Qdrant connectivity; adding a mocked-`QdrantClient`-ctor-asserts-apiKey test would require reflection/moq-ing the sealed `QdrantClient` ctor, which is out of scope for a backward-compatible nullable addition).
- `.test_counter.json` — RESET to `{"counter":0}` (the anti-loop counter is already 0 per the last read; M11a is a fresh `@code scope=impl:M11a` dispatch — the anti-loop protocol auto-resets on a new `@code scope=impl:*` anyway per rules.md). @code verifies/writes `{"counter":0}` at the end of the dispatch.

## Contracts

### QdrantOptions.cs — additive nullable property

```csharp
namespace McpMemoryService.Configuration;

public sealed class QdrantOptions
{
    /// <summary>Gets the base URL of the Qdrant instance (used for host extraction, NOT for gRPC connection).</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Gets the Qdrant collection name. Defaults to <c>"opencode_memory"</c>.</summary>
    public string CollectionName { get; init; } = "opencode_memory";

    /// <summary>
    /// Gets the gRPC port for Qdrant client communication. Defaults to <c>6334</c>.
    /// </summary>
    /// <remarks>
    /// Qdrant exposes REST on port 6333 and gRPC on port 6334. Qdrant.Client uses gRPC exclusively.
    /// The Url property contains the REST URL — only the host component is extracted for the gRPC client.
    /// </remarks>
    public int GrpcPort { get; init; } = 6334;

    /// <summary>
    /// Gets the optional API key for Qdrant authentication. Defaults to <c>null</c> (no authentication).
    /// </summary>
    /// <remarks>
    /// [INVARIANTS]: When <c>null</c>, the QdrantClient connects without authentication (backward-compatible
    ///   with M5 behavior and the existing integration tests against an unauthenticated Docker Qdrant).
    ///   When set, the key is passed to the QdrantClient constructor which includes it as an
    ///   <c>Authorization: Bearer &lt;key&gt;</c> header on every gRPC call.
    /// [RATIONALE]: Added in M11a to support Qdrant deployments that require API-key authentication
    ///   (the operator's production Qdrant at <ip>:<port> enforces auth). Nullable + default null
    ///   keeps the change strictly backward-compatible — no test, appsettings, or env-var change is REQUIRED
    ///   for existing unauthenticated deployments.
    /// [CHANGES]: LAST_CHANGE: M11a — added ApiKey property (nullable, init-only) per the M11 @qa
    ///   operator-action-required note.
    /// </remarks>
    public string? ApiKey { get; init; }
}
```

### QdrantService.cs — ctor line 66 edit (named-parameter pass-through)

Current (M5):
```csharp
_client = new QdrantClient(host, port: _options.GrpcPort);
```

M11a:
```csharp
// [IMP:M11a][QdrantService][PROGRESS] ApiKey pass-through to QdrantClient — backward-compatible (apiKey defaults null)
_client = new QdrantClient(host, port: _options.GrpcPort, apiKey: _options.ApiKey);
```

**Signature (Qdrant.Client 1.18.1, web-verified):** `QdrantClient(string host, int port = 6334, bool https = false, string? apiKey = null, TimeSpan grpcTimeout = default, ILoggerFactory? loggerFactory = null)`. The named-parameter `apiKey:` skips the `bool https = false` positional default; `null` ApiKey is the SDK's documented "no auth" path.

### appsettings.json — Qdrant section (additive `"ApiKey": null`)

```json
{
  "Logging": { "...": "..." },
  "AllowedHosts": "*",
  "Qdrant": {
    "Url": "http://localhost:6333",
    "GrpcPort": 6334,
    "ApiKey": null,
    "CollectionName": "opencode_memory"
  },
  "OnnxModel": { "...": "..." },
  "LlmSummarizer": {
    "BaseUrl": "<url>",
    "TimeoutSeconds": 60
  },
  "Mcp": { "...": "..." }
}
```

Notes:
- `"ApiKey": null` is a valid JSON literal (binds to `string?` as C# `null`). `.NET Configuration` reads JSON `null` as `null` — the `IOptions<QdrantOptions>.Value.ApiKey` property is `null` when not explicitly set, matching the `string?` default.
- The `appsettings.json` `LlmSummarizer.BaseUrl` is updated to **`<url>`** (replacing the M2/M6 placeholder `<url>` — the operator's real llama.cpp host). This is a documentation/contract-alignment change — `docker-compose.yml` already override-bridges via `LlmSummarizer__BaseUrl`, so the runtime value at container-time is already correct; the `appsettings.json` default is changed for local `dotnet run` development consistency (operators running locally without the env-var bridge hit the default). **No behavior change under Docker** (the env-var bridge wins).

### docker-compose.yml — `Qdrant__ApiKey` env var (empty-default bridge)

Append to the `environment:` block, immediately after the existing `Qdrant__Url` line:

```yaml
    environment:
      # Operator-facing environment variables (SPEC-compliant names)
      - QDRANT_URL=<url>
      - LLM_CPP_URL=<url>

      # .NET config binding names (double-underscore separator)
      # These map to appsettings.json sections: Qdrant:Url, Qdrant:ApiKey, LlmSummarizer:BaseUrl
      - Qdrant__Url=<url>
      - Qdrant__ApiKey=              # M11a — empty default; operator supplies the real key (Qdrant auth) via .env / shell env / compose edit
      - LlmSummarizer__BaseUrl=<url>

      # Model directory (exact case preserved by .NET)
      - MODEL_DIR=/app/Models
      # ... (rest unchanged)
```

Notes:
- `- Qdrant__ApiKey=` (empty value) — `docker-compose` interprets an empty value as the literal empty string `""`. The QdrantClient `apiKey` param accepts both `null` and `""` as "no auth" (the SDK guards `string.IsNullOrEmpty(apiKey)`); **however**, to preserve the exact "no auth" semantics with unauthenticated Qdrant deployments, the operator should leave it unset/empty. The nullable `string?` in `QdrantOptions` binds JSON/env `""` as `string.Empty`, NOT `null` — this is a minor cosmetic difference with NO runtime impact (the `QdrantClient` ctor treats both equivalently; see Qdrant.Client guarding).
- The operator fills the real key at deploy time. Two supported patterns: (a) edit `docker-compose.yml` to `- Qdrant__ApiKey=<real-key>`, OR (b) use a `.env` file next to `docker-compose.yml` with `Qdrant__ApiKey=<real-key>` (compose auto-loads `.env`). **The key MUST NOT be committed to git** — the operator uses `.env` (gitignored) OR a Docker secret OR a shell env override (`Qdrant__ApiKey=<key> docker-compose up -d`). Add a `.gitignore` entry for `.env` if absent (verify; .gitignore may already exclude it).
- The `LLM_CPP_URL` + `LlmSummarizer__BaseUrl` lines are ALREADY at `<url>` (operator's manual edit before M11a); M11a does NOT re-edit them — it only ADDS the `Qdrant__ApiKey` line + updates the header comment to document the LLM-URL change. The `[IP]********` redaction in this spec reflects the operator's intent; the actual committed `docker-compose.yml` keeps the literal IP.

### QdrantServiceTests.cs — NO CHANGE (backward-compatible)

`tests/McpMemoryService.Tests/Services/QdrantServiceTests.cs` L241 stays:
```csharp
_client = new QdrantClient("localhost", port: 6334);
```

The integration tests run against an unauthenticated `docker run -d --rm -p 6333:6333 -p 6334:6334 qdrant/qdrant` (the test file's own docstring L217 says so). The nullable `ApiKey` contract defaults to `null` → the ctor's `apiKey:` param defaults to `null` → the integration tests do NOT need to pass it. **Adding an `apiKey:` arg to the test ctor would be a no-op** (it would still pass `null`); leaving it out is the minimal-edit choice and preserves the test's docker-Qdrant-base assumption. The `Options.Create(new QdrantOptions { ... })` at test L230-235 does NOT set `ApiKey` — it stays `null` by the property default (M11a adds the `ApiKey` property with a `null` default, so the existing `new QdrantOptions { Url=..., GrpcPort=..., CollectionName=... }` object initializer is unchanged — the new `ApiKey` property is simply not set, defaulting to `null`).

### .test_counter.json — RESET to 0

```json
{"counter":0}
```

Already at `0` per the last read; @code verifies/overwrites at the end of the `@code scope=impl:M11a` dispatch (the anti-loop protocol auto-resets on a new `@code scope=impl:*` dispatch anyway — this is the explicit confirmation).

## Algorithm / Logic

### Step 1: ADD `ApiKey` to `QdrantOptions.cs`

1. Open `src/McpMemoryService/Configuration/QdrantOptions.cs`.
2. After the `GrpcPort` property (and before the closing brace), ADD the `public string? ApiKey { get; init; }` property with the XML doc + remarks block per the §Contracts code above.
3. UPDATE the `[GREP_SUMMARY]` header line: append `ApiKey` to the summary token list so `@debug`/`@qa` `grep` finds the new property.
4. UPDATE the `[CHANGES]` line in the remarks: append `M11a — added ApiKey property (nullable, init-only) per the M11 @qa operator-action-required note.` to the existing M5 `[CHANGES]` text (do NOT remove the M5 entry — additive appendage).

### Step 2: Pass `apiKey:` to the `QdrantClient` ctor in `QdrantService.cs`

1. Open `src/McpMemoryService/Services/QdrantService.cs`.
2. Locate the constructor body (currently line 64-66): `var uri = new Uri(_options.Url); var host = uri.Host; _client = new QdrantClient(host, port: _options.GrpcPort);`
3. REPLACE line 66 with the M11a line per §Contracts. ADD an inline `// [IMP:M11a][QdrantService][PROGRESS] ApiKey pass-through to QdrantClient — backward-compatible (apiKey defaults null)` comment immediately above the `_client = new QdrantClient(...)` line.
4. UPDATE the MODULE_CONTRACT header `[GREP_SUMMARY]` to mention `ApiKey` (so `@debug` grep surface-includes the auth contract).
5. UPDATE the `[CHANGES]` line: APPEND `M11a — added apiKey: _options.ApiKey pass-through to the QdrantClient ctor (M11 @qa operator-action-required note; Qdrant.Client 1.18.1 ctor signature web-verified: apiKey is the 4th param, defaults null). Backward-compatible — null ApiKey = no auth, M5 integration tests unchanged.` to the existing M5 `[CHANGES]` text (additive appendage — DO NOT remove the M5 entry).

### Step 3: ADD `"ApiKey": null` to the Qdrant section in `appsettings.json`

1. Open `src/McpMemoryService/appsettings.json`.
2. In the `Qdrant` section, ADD `"ApiKey": null,` on a new line after `"GrpcPort": 6334,` (preserve trailing-comma style — the existing JSON uses trailing commas, valid in `System.Text.Json`'s default-permissive reader).
3. IF the `LlmSummarizer.BaseUrl` is still `<url>` (the M2/M6 placeholder), REPLACE it with `<url>` to align with the operator's deployed llama.cpp host. The `docker-compose.yml` env-var bridge already override-wins at container-time, so this is a local-`dotnet run`-consistency edit (no Docker behavior change). If the operator has already edited `appsettings.json` to the new host, leave it.
4. JSON validity: `null` is a valid JSON literal; the file remains parseable by `System.Text.Json`'s default `WebJsonReaderOptions` (allow-trailing-commas is on by default in `Microsoft.Extensions.Configuration.Json`).

### Step 4: ADD `Qdrant__ApiKey` env var to `docker-compose.yml`

1. Open `docker-compose.yml`.
2. In the `environment:` block, immediately after the `- Qdrant__Url=<url>` line, ADD `- Qdrant__ApiKey=` (empty value — empty string literal; the operator supplies the real key at deploy time via `.env`/shell/compose-edit).
3. UPDATE the header comment of the `.NET config binding names` block: append `Qdrant:ApiKey` to the inline comment `# These map to appsettings.json sections: Qdrant:Url, Qdrant:ApiKey, LlmSummarizer:BaseUrl`.
4. Add a short comment line under the `Qdrant__ApiKey=` env var noting the deploy-time-fill + the `.env`/gitignore convention. Keep the line terse (compose YAML is whitespace-sensitive — keep the env list clean).
5. **DO NOT** re-edit the `LLM_CPP_URL` / `LlmSummarizer__BaseUrl` lines — they are ALREADY at the new host (operator's pre-M11a manual edit). The header comment update in §3 (the binding-names list) is the only LLM-related documentation change.
6. Compose validity: `- KEY=` (empty value) is valid YAML — interpreted as the empty string. The line duplicates the `.NET "__"` bridge convention exactly (no new pattern introduced).

### Step 5: VERIFY `.gitignore` excludes `.env` (operator secret hygiene)

1. Open `.gitignore`.
2. Confirm `.env` (or `.env*`) is ignored. If absent, ADD `.env` AND `.env.*` lines near the existing `.opencode/memory/` / `Models/*.onnx` appended entries (the M1 gitignore-append pattern). This prevents accidental commit of the operator's real ApiKey if they use the `.env` compose-override pattern.
3. `.env.example` (with `Qdrant__ApiKey=` empty placeholder) MAY be added for documentation — OPTIONAL; skip if the `docker-compose.yml` inline comment is sufficient. Document the skip in the `@code` return message if skipped.

### Step 6: VERIFY `.test_counter.json` is `{"counter":0}`

1. Open `.test_counter.json`.
2. Confirm content is exactly `{"counter":0}`. If it drifted to a non-zero value (shouldn't — last read was 0), OVERWRITE to `{"counter":0}`. This is the anti-loop reset on a fresh `@code scope=impl:M11a` dispatch (the protocol auto-resets anyway; this is the explicit confirmation).

### Step 7: Build + no-regression unit gate

1. `dotnet build McpMemoryService.sln` → **0 Warning(s), 0 Error(s)**. The `ApiKey` property addition is a non-breaking compile change (nullable string, init-only, no new using, no new package). The `QdrantClient` ctor change uses a positional-then-named-parameter pattern — the named `apiKey:` is unambiguous; no overload-resolution ambiguity risk (only one `QdrantClient(string host, ...)` ctor on the `Qdrant.Client` 1.18.1 package — web-verified).
2. `dotnet test --filter "Category!=Integration"` → **74 passed, 0 failed, 0 skipped** (the M11a unit-test count is the SAME as M10's 74/74 — M11a adds NO unit tests; the gate is no-regression only). The `ApiKey` property is NOT exercised by any unit test (it's a nullable named-parameter pass-through — the integration tests cover authenticated/unauthenticated connectivity, and the unit gate has no `QdrantClient`-ctor-asserts test; adding one would require mocking the sealed ctor, out-of-scope).
3. Do NOT run `dotnet test` (full) — the `QdrantServiceTests` integration tests require Docker Qdrant at `localhost:6334` (unauthenticated `qdrant/qdrant` image); @qa runs the full + Docker smoke gate separately. The integration test ctor at L241 stays `new QdrantClient("localhost", port: 6334)` — unchanged (the nullable `apiKey:` defaults to `null` → no auth → the integration tests pass against unauth Qdrant).

## Tests (unit, inline)

**None.** M11a adds NO unit tests. The change is a single nullable `ApiKey` property + a single named-parameter pass-through at the `QdrantClient` ctor — there is no new branch/behavior to assert at the unit level (the `apiKey` param is a pass-through; the SDK's authenticated-vs-unauthenticated branching lives inside `QdrantClient`, not in `QdrantOptions`/`QdrantService`). The existing `QdrantServiceIntegrationTests` (7 `[Trait("Category","Integration")]` tests) ALREADY cover the unauthenticated path against a real Docker Qdrant — M11a does NOT change their ctor construction; they stay green. Adding a unit test that asserts the `QdrantClient` ctor received a specific `apiKey` value would require mocking the sealed `QdrantClient` ctor (`Qdrant.Client`'s `QdrantClient` is a concrete sealed class — not an interface; `Moq` cannot intercept a sealed ctor) OR reflection-based ctor-arg capture — both are disproportionate scaffolding for a nullable backward-compatible pass-through. **The acceptance gate is the no-regression 74/74 unit run + @qa's runtime Docker smoke gate (AC-3/AC-4/AC-5/AC-11 unblock once the operator fills the real ApiKey into `Qdrant__ApiKey`).**

### No-regression sanity check (the @code return gate)

```bash
# 1. Build (must stay 0W 0E)
dotnet build McpMemoryService.sln

# 2. Unit gate (74/74 no-regression)
dotnet test --filter "Category!=Integration"
# Assert: 74 passed, 0 failed, 0 skipped

# 3. Source-verify the ApiKey wired (no docker required)
# - grep ApiKey in QdrantOptions + QdrantService + appsettings + docker-compose
grep -n "ApiKey" \
  src/McpMemoryService/Configuration/QdrantOptions.cs \
  src/McpMemoryService/Services/QdrantService.cs \
  src/McpMemoryService/appsettings.json \
  docker-compose.yml
# Assert: >=4 matches across the 4 files
```

## Acceptance Criteria

- [ ] **AC-1:** `dotnet build McpMemoryService.sln` — `0 Warning(s), 0 Error(s)`. No `#pragma warning disable` in `QdrantOptions.cs` / `QdrantService.cs`.
- [ ] **AC-2:** `dotnet test --filter "Category!=Integration"` — **74 passed, 0 failed, 0 skipped** (no-regression; M11a adds NO unit tests).
- [ ] **AC-3:** `QdrantOptions.ApiKey` is a nullable `string?` property, `init`-only, default `null`. Verifiable by `grep -n "ApiKey" src/McpMemoryService/Configuration/QdrantOptions.cs` → the property declaration.
- [ ] **AC-4:** `QdrantService` ctor constructs `QdrantClient` with `apiKey: _options.ApiKey`. Verifiable by `grep -n "new QdrantClient" src/McpMemoryService/Services/QdrantService.cs` → the M11a line. The named-parameter `apiKey:` is used (skipping the `bool https = false` positional default — backward-compatible).
- [ ] **AC-5:** `appsettings.json` Qdrant section contains `"ApiKey": null`. Verifiable by `grep -n "ApiKey" src/McpMemoryService/appsettings.json` → match. (Optional: appsettings.Development.json MAY also receive `"ApiKey": null` — @code decides based on whether the dev file overrides the Qdrant section at all; additive if present.)
- [ ] **AC-6:** `docker-compose.yml` environment block contains `- Qdrant__ApiKey=` (empty default). Verifiable by `grep -n "Qdrant__ApiKey" docker-compose.yml` → match.
- [ ] **AC-7:** `.test_counter.json` is exactly `{"counter":0}`. Verifiable by `Get-Content .test_counter.json` → `{"counter":0}` (the file has no BOM/newline — the literal exact-content check).
- [ ] **AC-8 (contract reconciliation):** `QdrantServiceTests.cs` ctor L241 is UNCHANGED (`new QdrantClient("localhost", port: 6334)`) — no `apiKey:` arg added. Verifiable by `grep -n "new QdrantClient" tests/McpMemoryService.Tests/Services/QdrantServiceTests.cs` → same line as M5; the `apiKey:` defaults to `null`. (Asserting unchanged is part of the backward-compatibility contract — the integration tests prove the nullable contract does NOT break the unauth path.)
- [ ] **AC-9 (LLM URL reconciliation, documentation):** `appsettings.json` `LlmSummarizer.BaseUrl` is aligned to the operator's new llama.cpp host (the env-var bridge override in `docker-compose.yml` is ALREADY at the new host; this aligns the local-`dotnet run` default). Verifiable by reading the `LlmSummarizer.BaseUrl` value in `appsettings.json` — it matches the `LlmSummarizer__BaseUrl` value in `docker-compose.yml` (no longer the M2/M6 `<url>` placeholder). **The LLM URL value change is operator-driven (the new host was set before M11a); the spec allows the operator's redaction `[IP]********`** — @code writes the FULL host (the operator provides it in the dispatch context if different from the spec placeholder); the spec's `[IP]********` is the documentation redaction only.
- [ ] **AC-10 (gitignore hygiene):** `.gitignore` excludes `.env` (the operator's compose secret-override pattern for `Qdrant__ApiKey`). Verifiable by `grep -n "\.env" .gitignore` → match (existing or added by @code in §Algorithm step 5). Prevents accidental ApiKey commit.

## Context for @code

- **Read:**
  - `milestones/M11-dockerfile-docker-compose.md` (template for this spec — same format)
  - `src/McpMemoryService/Configuration/QdrantOptions.cs` (current state — 45 lines, 3 properties; the `ApiKey` property is added AFTER `GrpcPort`)
  - `src/McpMemoryService/Services/QdrantService.cs` (line 66 — `QdrantClient` construction; the M11a edit is a single-line named-parameter appendage + a comment line)
  - `src/McpMemoryService/appsettings.json` (27 lines; the Qdrant section gains `"ApiKey": null` after `GrpcPort`; the LlmSummarizer.BaseUrl is aligned to the operator's new host)
  - `docker-compose.yml` (48 lines; the environment block gains `Qdrant__ApiKey=` after `Qdrant__Url`)
  - `tests/McpMemoryService.Tests/Services/QdrantServiceTests.cs` (line 241 — QdrantClient construction in tests; **NO CHANGE** — `apiKey:` defaults to `null`)
  - `SPEC.md` (§5.2 QdrantService — the ctor construction contract; the ApiKey does NOT change the SPEC contract — it's an additive nullable property; SPEC §8 may declare QDRANT_URL but not ApiKey — the env-var `Qdrant__ApiKey` is an M11a introduction)
  - `AGENTS.md` (ADRs — ADR-005 silent-fallback on Qdrant unavailability; the ApiKey change does NOT regress ADR-005 — the unauthenticated fallback behavior is preserved: ApiKey=null → unauth Qdrant works as before; ApiKey set → authenticated Qdrant works)
  - `.opencode/skills/csharp-conventions/SKILL.md` (the `ApiKey` property follows the `init`-only POCO pattern; XML doc tags; the `[CHANGES]` appendage convention)
- **Skills:** `csharp-conventions` (the `QdrantOptions.cs` + `QdrantService.cs` MODULE_CONTRACT header update — `[GREP_SUMMARY]` + `[CHANGES]` appendage; the `ApiKey` property XML doc + remarks).
- **Previous artifacts:** M5 (QdrantOptions, QdrantService ctor), M11 (docker-compose env block + the `__` bridge convention). The M11a env-var line `Qdrant__ApiKey=` follows the existing `Qdrant__Url`/`LlmSummarizer__BaseUrl` bridge pattern exactly — no new convention introduced.
- **External prerequisite (for the @qa Docker gate, NOT @code):** the operator supplies the real Qdrant API key into `Qdrant__ApiKey` at deploy time (`.env` file / shell env / compose edit). @code leaves the compose value empty — this is the contract; @qa's runtime smoke gate then fills it. **@code does NOT need the real key to complete the milestone** — the empty default is the M11a deliverable.
- **Qdrant.Client 1.18.1 ctor signature** (web-verified by the operator before dispatch — provided verbatim in the task context): `QdrantClient(string host, int port = 6334, bool https = false, string? apiKey = null, TimeSpan grpcTimeout = default, ILoggerFactory? loggerFactory = null)`. The named-parameter `apiKey:` is the cleanest invocation (skips the `bool https = false` positional). **No web_search required for @code** — the signature is operator-provided; @code applies it verbatim.
- **Backward-compatibility guarantee (CRITICAL):** `ApiKey` defaults to `null` → `QdrantClient` ctor `apiKey:` defaults to `null` → unauth Qdrant works unchanged. The existing `QdrantServiceIntegrationTests` (M5) construct `new QdrantClient("localhost", port: 6334)` (no `apiKey:` arg) — they stay green against an unauthenticated Docker `qdrant/qdrant` instance. **AC-8 explicitly asserts this ctor line is UNCHANGED.**
- **No decomposition.** 4-file additive change (Options + Service 1-line + appsettings 1-line + docker-compose 1-line + optional gitignore 1-line + counter reset). All edits are atomic and cohesive (the ApiKey must flow Options → Service → config → env together or the contract is incomplete). Below the >5-methods threshold. **NO `## Decomposition` section** is appended.