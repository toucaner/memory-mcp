# M14 — Model footprint reduction: quantized ONNX variant + factual memory docs

## Dependencies
- M4 (OnnxEmbeddingService + `Scripts/Download-Model.ps1` + `Scripts/download-model.sh` + IEmbeddingService)
- M11 (Dockerfile model mount decision: model lives on the TrueNAS dataset at `/app/Models`, not baked in)
- M12 (integration harness — reused for the retrieval-quality gate)
- M13 (recommended first: disposal/knob fixes; M14 is independent but the RSS target assumes M13 shipped)

## Goal
Replace the FP32 ONNX model currently downloaded by the scripts (448.5 MiB, ~2/3 of the observed
RSS) with the **dynamically-quantized int8** Xenova variant (~112.8 MiB) of the *same* model
family, keeping the 384-dim / tokenizer / graph-IO contract intact. Align scripts, config, and the
memory claims in `docs/docker-deploy.md` + `SPEC.md` with measured reality.

## Problem Statement (evidence)
- The download scripts fetch the FP32 file:
  `.../Xenova/paraphrase-multilingual-MiniLM-L12-v2/resolve/main/onnx/model.onnx`
  (`Scripts/Download-Model.ps1:10`, `Scripts/download-model.sh:13`).
- HuggingFace metadata: `onnx/model.onnx` = **470,268,510 B (448.5 MiB)**. This single file
  accounts for roughly 2/3 of the observed `RSS 674 MB`.
- The docs claim the model is `~90-120MB` (`docs/docker-deploy.md:176`, `SPEC.md:206`,
  `docker-compose.yml:26`) — that is the size of the **quantized** file, not the FP32 file that is
  actually downloaded. The budget arithmetic in SPEC §6.2 is therefore wrong.
- Available variants (HF metadata, all same 384-dim architecture):
  | file | bytes | MiB | note |
  |---|---|---|---|
  | `model.onnx` (current) | 470,268,510 | 448.5 | FP32 |
  | `model_fp16.onnx` | 235,336,673 | 224.4 | FP16 fallback |
  | `model_quantized.onnx` | 118,308,126 | 112.8 | **chosen** (canonical Xenova quantized) |
  | `model_int8.onnx` | 118,054,609 | 112.6 | alternative int8 |
  | `model_uint8.onnx` | 118,054,642 | 112.6 | alternative uint8 |

## Decision
Primary variant = **`onnx/model_quantized.onnx`** (118,308,126 B). Same `tokenizer.json`, same
graph input names (`input_ids`, `attention_mask`, `token_type_ids`) and output
(`last_hidden_state`), same 384-dim output → no change to `OnnxEmbeddingService` logic.
Fallback if the retrieval-quality gate regresses: `model_fp16.onnx` (224.4 MiB). Record the
variant as **ADR-011a** (amendment to ADR-011).

## Deliverables
- `src/McpMemoryService/Scripts/Download-Model.ps1` (source URL → quantized; size verification)
- `src/McpMemoryService/Scripts/download-model.sh` (same)
- `src/McpMemoryService/appsettings.json` — only if the destination filename changes (see contract)
- `docs/docker-deploy.md` — corrected memory budget + variant note + update procedure
- `SPEC.md` §6.2 — corrected model-size / memory-limit arithmetic
- `docker-compose.yml` — corrected limit comment (no behavioral change here; enforcement is M15)
- `tests/McpMemoryService.Tests/Services/OnnxEmbeddingServiceTests.cs` — retrieval-quality gate
- `SPEC.md` / ADR registry note for **ADR-011a**

## Contracts

### 1. Download source + destination
- **Primary approach (stable path):** keep the destination filename `Models/model.onnx`
  (unchanged `OnnxModel:ModelPath` default, unchanged Dockerfile mount `/app/Models`, unchanged
  compose volume) but change the **source URL** to `.../resolve/main/onnx/model_quantized.onnx`.
  Transparent provenance is preserved via a printed/logged variant line + a size assertion.
- **Alternative (explicit filename, if the operator prefers):** download to
  `Models/model_quantized.onnx` and set `OnnxModel:ModelPath` (and, in Docker, the
  `OnnxModel__ModelPath` env var) accordingly. Do NOT do both.
- `tokenizer.json` URL is unchanged.

### 2. Expected-size assertions (both scripts)
After download, assert the file size is within tolerance of the expected variant size
(`118308126 ± 1 MiB`). On mismatch → fail loudly (`curl --fail` / `Invoke-WebRequest`
`$ErrorActionPreference='Stop'` + explicit throw). Emit `model: <MiB> MB (variant=quantized)`.

### 3. Retrieval-quality gate
Re-run the M4 semantic-discrimination test (and add at least one multilingual RU/EN pair, since
ADR-011 specifies multilingual support):
- `EmbedAsync_SimilarTexts_ProduceSimilarVectors` — `sim(similar) > sim(dissimilar)`.
- `EmbedAsync_MultilingualSimilarity_IsPreserved` — a RU pair vs a RU/EN unrelated string.
Quantized dynamic int8 typically preserves embedding geometry; if the gate fails, switch the URL
to `model_fp16.onnx` and re-run before escalating to `@debug`.

### 4. Preserved invariants
- 384 dims, L2-normalized output; same tokenizer; ADR-005 fatal-on-load unchanged.
- `OnnxEmbeddingService` source is NOT modified by this milestone.

## Algorithm / Logic

### Step 1: Update `Download-Model.ps1`
Change `$onnxUrl` to the `model_quantized.onnx` URL. Keep `$onnxPath = ...\model.onnx`
(primary approach). Add an expected-size constant and a post-download assertion + variant print.

### Step 2: Update `download-model.sh`
Change `ONNX_URL` to the quantized URL. Add a `stat -c%s` / `wc -c` size check against
`118308126` (± tolerance) and exit non-zero on mismatch. Keep the `du -h` summary line and add
`variant=quantized`.

### Step 3: appsettings / compose
Primary approach → no `ModelPath` change. Update only the explanatory comment in
`docker-compose.yml` (model size) — the memory **limit** change happens in M15, not here.

### Step 4: Correct documentation
- `docs/docker-deploy.md:176` and the "High memory usage" section: replace the `~90-120MB` claim
  with a variant table (FP32 448.5 MiB / FP16 224.4 MiB / int8 112.8 MiB) and a corrected budget
  (`quantized model ~113MB + runtime/native ~150-200MB ≈ ~300MB RSS`).
- `SPEC.md` §6.2 comment "модель ~200МБ + runtime ~150МБ + буферы" → corrected arithmetic with the
  chosen variant.
- Update the "Update ONNX Model" procedure to note the variant and the size check.

### Step 5: Integration quality gate
`dotnet test --filter "Category=Integration"` (Docker + model required) — all green, including the
new multilingual similarity test. If regression → switch to `model_fp16.onnx`, re-run; document.

### Step 6: Verify on-disk size
`Get-Item Models/model.onnx` / `du -h` → ≤ 130 MiB. Record the measured value in `test_guide.md`.

## Tests
| Test | Category | Asserts |
|---|---|---|
| `EmbedAsync_SimilarTexts_ProduceSimilarVectors` | Integration | unchanged; passes with quantized model |
| `EmbedAsync_MultilingualSimilarity_IsPreserved` | Integration | RU similar > RU/EN unrelated (new) |
| existing M4 ONNX tests | Integration | dim/norm/nulls unchanged |

## Acceptance Criteria
- [ ] Download scripts fetch `onnx/model_quantized.onnx` and verify the expected size
- [ ] On-disk model file ≤ 130 MiB (target ~112.8 MiB)
- [ ] `dotnet build McpMemoryService.sln` — OK
- [ ] Unit gate (`Category!=Integration`) — all PASS
- [ ] Integration gate — all PASS, including the multilingual similarity test
- [ ] `OnnxEmbeddingService` behavior unchanged: 384-dim, L2, fatal-on-load
- [ ] `docs/docker-deploy.md` and `SPEC.md` §6.2 contain accurate model sizes / budget
- [ ] `docker-compose.yml` model-size comment corrected (limit change deferred to M15)
- [ ] ADR-011a recorded (quantized variant, same dims)
- [ ] Grader note: if int8 quality fails, `model_fp16.onnx` fallback executed and documented

## Context for @code
- Read: `src/McpMemoryService/Scripts/Download-Model.ps1`, `Scripts/download-model.sh`
- Read: `docs/docker-deploy.md` (High memory usage section), `SPEC.md` §6, `docker-compose.yml`
- Read: `milestones/M4-onnx-embedding-service.md` (model contract, ADR-011), `AGENTS.md` §5 ADR-011
- Read: `milestones/M13-onnx-memory-hygiene.md` (disposal/knobs — do not duplicate here)
- HF metadata (source of sizes): `https://huggingface.co/api/models/Xenova/paraphrase-multilingual-MiniLM-L12-v2/tree/main/onnx`
- Do NOT modify `OnnxEmbeddingService.cs` in this milestone
- The model is NOT baked into the Docker image; the operator re-downloads to the TrueNAS dataset
  mounted at `/app/Models` (see `Dockerfile:29-34`)
