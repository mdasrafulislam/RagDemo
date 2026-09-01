# RAG API — ASP.NET Core 10 · Three-Tier · Semantic Kernel · OpenAI · pgvector

Retrieval-augmented question answering over plain-text/Markdown documents.

**Ingest:** you `POST` a file name; the API reads that file from a fixed folder, splits it into
1,000-character chunks, embeds each chunk with the OpenAI API, and stores them in the existing
`"RK"."PlanInfo"` table.

**Search:** you `POST` a question; the API embeds it, retrieves the nearest chunks, and asks an
OpenAI chat model to answer **using only those chunks** — returning `answered: false` with an
explanation when the indexed text cannot support an answer, rather than inventing one. See
[How hallucination is prevented](#how-hallucination-is-prevented).

Minimal APIs, OpenAPI, Scalar. No test projects.

---

## Architecture — three tiers, linear stack

```
┌──────────────────────────────────────────────────────────────────┐
│  Rag.WebApi      Minimal API endpoints · OpenAPI · Scalar        │
│                  ProblemDetails mapping · composition            │
└────────────────────────────┬─────────────────────────────────────┘
                             │ references
┌────────────────────────────▼─────────────────────────────────────┐
│  Rag.Business    Services: ingestion, search, chunking,          │
│                  embeddings, answer generation, health           │
│                  Validation, orchestration, OpenAI + SK          │
└────────────────────────────┬─────────────────────────────────────┘
                             │ references
┌────────────────────────────▼─────────────────────────────────────┐
│  Rag.Repository  SQL · Npgsql · pgvector. The only tier that     │
│                  knows the database exists.                      │
└──────────────────────────────────────────────────────────────────┘
```

The stack is strictly linear, and two properties are worth keeping:

- **`Rag.WebApi` does not reference `Rag.Repository` at all.** It cannot query the database even
  by accident. `AddBusiness()` registers the repository tier beneath itself, so the web tier
  never names a data-access type.
- **`Rag.Business` contains no `Npgsql` reference.** The repository translates driver failures
  into its own `RepositoryException`, which the business tier re-labels as `UpstreamException`.
  Swapping the database would not touch a single service.

### Where the code lives

| Project | Contents |
|---|---|
| `Rag.Repository` | `IPlanInfoRepository` / `PlanInfoRepository` (all SQL), row models, `RepositoryException`, `RepositoryOptions` |
| `Rag.Business` | `IngestionService`, `SearchService`, `DocumentFileService`, `ChunkingService`, `EmbeddingService`, `AnswerService`, `HealthService`, options, DTOs, exceptions |
| `Rag.WebApi` | `Program.cs`, three endpoint classes, request records, `BusinessExceptionHandler`, `Documents/` folder, settings |

Errors travel as exceptions, not result objects. `ValidationException` → 400,
`NotFoundException` → 404, `UpstreamException` → 503 (with `Retry-After`); anything else → 500.
`BusinessExceptionHandler` does that mapping in one place, which is why no endpoint contains a
try/catch.

> ⚠️ **One guard now rests on convention rather than structure.** `DocumentFileService` is the
> sole gatekeeper for caller-supplied file names, and nothing in the type system forces callers
> through it — a future service that opens a file directly would bypass the traversal check. If
> you add file access, call `DocumentFileService`; do not touch `File.*` elsewhere.

---

## Prerequisites

| Requirement | Notes |
|---|---|
| **.NET 10 SDK** | **Not currently installed on this machine** (highest is 9.0.102). Nothing builds until it is: https://aka.ms/dotnet/download |
| PostgreSQL with `pgvector` | The `"RK"."PlanInfo"` table and its HNSW index already exist. |
| OpenAI API key | Needs access to **both** an embedding model and a chat model — see below. |

### Embedding model must yield 1536 dimensions

`PlanInfo.chunk_embedding` is `VECTOR(1536)`, so the model's output width is not negotiable:

| Model | Native dims | Configuration |
|---|---|---|
| `text-embedding-3-small` | 1536 | Default. Cheapest, and the recommended starting point. |
| `text-embedding-ada-002` | 1536 | Works; legacy, no reason to prefer it. |
| `text-embedding-3-large` | 3072 | Set `OpenAI:Dimensions` to `1536`. OpenAI truncates and renormalises, so a shortened 3-large vector still scores better than 3-small at the same width — at higher cost. |

`EmbeddingService` re-checks every returned vector, so a wrong model fails on the first call
with a clear message instead of an opaque Postgres type error mid-insert.

### Working before the SDK is installed

The Business and Repository tiers build on the 9.0 SDK via an override — this is how the code was
verified to compile before the SDK landed:

```powershell
dotnet build src\Rag.Repository -p:RagTargetFramework=net9.0
dotnet build src\Rag.Business   -p:RagTargetFramework=net9.0
```

`Rag.WebApi` is .NET 10-only — `Microsoft.AspNetCore.OpenApi` is pinned to `10.0.11` and has no
.NET 9 asset. Drop the override once the SDK is in place.

---

## Setup

```powershell
# 1. Credentials — never in appsettings.json
cd src\Rag.WebApi
dotnet user-secrets set "ConnectionStrings:Postgres" "Host=...;Database=...;Username=...;Password=..."
dotnet user-secrets set "OpenAI:ApiKey"              "sk-..."

# 2. Run
cd ..\..
dotnet run --project src\Rag.WebApi
```

Then open <http://localhost:5080/scalar> for the interactive API, or `/openapi/v1.json` for the
raw document. A ~26,000-character sample document (`meridian-sample-plan.md`) is already in
`src/Rag.WebApi/Documents/`, so ingest works immediately once credentials are set.

### NuGet feed

`NuGet.config` scopes restore to nuget.org with `<clear/>`. The machine-level config also lists a
private Azure DevOps feed (`Unified-Feed`) that returns **401 Unauthorized** and aborts restore.
If your org requires that feed, authenticate to it and add it back.

---

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/api/documents` | Lists ingestible file names — use it to get the exact name for ingest. |
| `POST` | `/api/ingest` | `{ "fileName": "plan.md", "category": null }` → chunk, embed, store. |
| `POST` | `/api/search` | Retrieves the nearest chunks **and answers the question from them**. |
| `GET` | `/health` | PostgreSQL round trip. Add `?deep=true` to also call OpenAI. |

`similarity` is 0–1 where **1 is identical** — pgvector's `<=>` returns *distance*, and the
repository inverts it so every tier above deals in "higher is better".

`/health` only calls OpenAI when `deep=true`: every embedding call costs quota, and a
frequently-polled probe would spend real money to report nothing new.

### `POST /api/ingest`

```jsonc
{ "fileName": "meridian-sample-plan.md" }        // category defaults to "meridian-sample-plan"
{ "fileName": "meridian-sample-plan.md", "category": "retirement-plans" }
```

```jsonc
{
  "fileName": "meridian-sample-plan.md",
  "category": "meridian-sample-plan",
  "characterCount": 26131,
  "chunksInserted": 31,
  "chunksReplaced": 0,
  "elapsedMs": 1840
}
```

Post the same body again and `chunksReplaced` should equal `chunksInserted`, with the table's row
count unchanged — that is the idempotency check.

### `POST /api/search`

```jsonc
// request — only `query` is required
{
  "query": "how long before a new employee can join",
  "topK": 5,               // chunks retrieved as grounding, 1–50, default 5
  "category": null,        // optional exact-match filter
  "minSimilarity": null,   // optional floor, 0–1
  "generateAnswer": true,  // false → retrieval only, no chat call
  "includeSources": true   // return the chunks used as grounding
}
```

```jsonc
// response
{
  "query": "how long before a new employee can join",
  "answer": "An employee becomes eligible on the first day of the calendar quarter following ninety consecutive days of service [S1]...",
  "answered": true,
  "citedRecordIds": [ 412 ],
  "sources": [
    { "recordId": 412, "category": "meridian-sample-plan", "chunkText": "An employee becomes eligible…", "similarity": 0.61 }
  ],
  "usage": {
    "retrievedChunks": 5, "chunksUsedAsContext": 5, "topSimilarity": 0.61,
    "inputTokens": 1240, "outputTokens": 78, "elapsedMs": 2310, "finishReason": "Stop"
  }
}
```

**Branch on `answered`, not on whether `answer` is non-empty.** `answer` always contains prose;
`answered: false` means that prose is an explanation of a gap, not an answer.

### Don't set `minSimilarity` until you've seen real numbers

Cosine similarity from `text-embedding-3-small` does **not** put good matches near 0.9 — a
genuinely correct paraphrase match typically lands around **0.3–0.6**, with unrelated text near
0.0–0.2. Set `minSimilarity: 0.8` and you will get zero results from a perfectly working system.
Leave it out at first, look at the values real hits produce, then set the floor just below.

It also **filters rather than backfills**: `topK: 5` with a floor can return 2 results — you do
not get the next-best three that would have passed.

---

## How hallucination is prevented

Four independent guards, because a single prompt instruction is not a guarantee:

**1. No context → no model call.** If retrieval returns nothing (or everything falls below
`minSimilarity`), `SearchService` returns `answered: false` **without calling the chat model at
all**. This is the strongest guard because it removes the opportunity rather than asking the
model to decline: a model handed an empty context either refuses — a wasted call — or answers
from its training data, which is the exact failure being avoided.

**2. The system prompt confines the model to the excerpts.** Verbatim, in `AnswerService`:

> You answer questions using ONLY the numbered source excerpts provided in the user message. The
> excerpts are the entire body of knowledge available to you for this task.
>
> 1. Base every statement on the excerpts. Do not use your own knowledge, do not infer beyond
>    what the text supports, and do not fill gaps with what is typically true.
> 2. Cite the source of each claim inline using its label, like `[S1]` or `[S2, S3]`.
> 3. If the excerpts do not contain the answer, reply with exactly `INSUFFICIENT_CONTEXT` on the
>    first line, then one sentence naming what is missing. Do this even when you are confident
>    you know the answer from elsewhere — reporting the gap is the correct outcome, not a failure.
> 4. If the excerpts only partially answer the question, give the part they support, then state
>    plainly what they do not cover.
> 5. If two excerpts conflict, say so and cite both rather than silently choosing one.
> 6. Answer in prose at the length the question warrants…

Rule 3 is phrased to counter the model's pull toward being helpful — the usual reason a RAG
system answers a question its corpus cannot support. Rule 4 exists because partial answers are
where hallucination actually creeps in: the model has *most* of what it needs and quietly invents
the remainder.

**3. A sentinel, not prose, signals the gap.** The model emits `INSUFFICIENT_CONTEXT`, which
`AnswerService` strips and converts to `answered: false`. Callers get a boolean to branch on
instead of pattern-matching apologetic phrasing — and a UI that checks a boolean cannot
accidentally render a refusal as an answer.

**4. Citations are extracted and returned.** Chunks are labelled `[S1]`, `[S2]` — short labels
rather than raw record ids, which a model can confuse with numbers appearing in the document text
— then mapped back to `recordid` values in `citedRecordIds`. An answer with `answered: true` and
an **empty** `citedRecordIds` is worth alerting on: the model produced prose it did not attribute
to anything.

Supporting details: `temperature` defaults to **0**, and `sources` is returned by default so a
human can check the answer against the text it claims to come from.

### What this still does not prevent

- **A subtly wrong paraphrase of text that *is* present.** The model can cite `[S1]` correctly
  and still mis-state what S1 says. Citations make this checkable, not impossible.
- **Retrieval returning plausible-but-wrong chunks.** If the top 5 are topically adjacent but
  don't hold the answer, the model may stitch them into something that reads correctly. Watch
  `usage.topSimilarity`.
- **Prompt injection from ingested documents.** A document containing "ignore previous
  instructions" reaches the model as context. Low-risk for trusted internal documents; not
  low-risk for user uploads.

The mitigation for all three is the same, and it is not code: keep `sources` visible in whatever
UI consumes this.

---

## ⚠️ Frozen-schema limitations

`"RK"."PlanInfo"` has no `source_path`, `chunk_index`, or `content_hash` column, so **nothing
identifies which rows came from which document**.

Ingest approximates idempotency by deleting rows whose text is byte-identical to the incoming
chunks within the same category, then inserting — all in one transaction. Re-ingesting an
unchanged document is stable, and `chunksReplaced` tells you it was a re-ingest.

Three cases this **cannot** cover:

1. **Editing a document leaves orphans.** Chunks whose text changed no longer match the delete
   predicate, so old versions stay in the table permanently and compete in search against the new
   ones. Nothing can find them to remove them. **Updates are not supported — only initial load.**
2. Two documents sharing a category and an identical passage will delete each other's copy of it.
3. `category` is load-bearing for the delete, so ingesting the same file under a different
   category stores it twice.

All three disappear with:

```sql
ALTER TABLE "RK"."PlanInfo"
  ADD COLUMN source_path  VARCHAR,
  ADD COLUMN chunk_index  INTEGER,
  ADD COLUMN content_hash VARCHAR;

CREATE UNIQUE INDEX planinfo_source_chunk ON "RK"."PlanInfo" (source_path, chunk_index);
```

That turns ingest into a true upsert. Cheap now, a data migration later.

---

## ⚠️ Verify the index operator class

A pgvector index only accelerates the distance operator matching its operator class. This code
uses `<=>` (cosine), which requires **`vector_cosine_ops`**. If your index is `vector_l2_ops` or
`vector_ip_ops`, the planner **silently ignores it** and every search becomes a sequential scan —
no error, just slow.

```sql
SELECT indexname, indexdef FROM pg_indexes
WHERE schemaname = 'RK' AND tablename = 'PlanInfo';
```

| Operator class | Operator | Similarity |
|---|---|---|
| `vector_cosine_ops` | `<=>` | `1 - distance` ← assumed here |
| `vector_l2_ops` | `<->` | not a 0–1 similarity |
| `vector_ip_ops` | `<#>` | negated inner product |

If it differs, change the operator **and** the similarity conversion together — both are in
`PlanInfoRepository.SearchAsync`. Confirm with `EXPLAIN ANALYZE` that the plan shows an **Index
Scan**, not a Seq Scan.

`Repository:HnswEfSearch` (recall vs latency, default 40) is applied per search transaction via
`SET LOCAL`, since it is a session GUC rather than a property of the index. The repository floors
the effective value at `topK`: pgvector's HNSW scan visits at most `ef_search` candidates, so a
value below the `LIMIT` silently returns fewer rows than requested.

---

## Verification sequence

There is no test suite, so this manual sequence is the only verification. Run all of it after any
change to ingestion or search.

```powershell
dotnet build
dotnet run --project src\Rag.WebApi

# 1. GET  /health?deep=true    → healthy, embeddings "ok"
#    Proves PostgreSQL reachable, the key valid, and the model 1536-dimension.

# 2. GET  /api/documents       → meridian-sample-plan.md is listed

# 3. POST /api/ingest          { "fileName": "meridian-sample-plan.md" }
#         → chunksInserted ≈ 31, chunksReplaced = 0
#    SELECT count(*), count(chunk_embedding) FROM "RK"."PlanInfo";
#         -- equal counts; no NULL embeddings

# 4. POST the SAME request again — the idempotency check
#         → chunksReplaced ≈ 31, and the row count is UNCHANGED.
#    If it doubled, idempotency is broken. Stop and investigate.

# 5. Path traversal — all must be 400, and must not read a file:
#    POST /api/ingest { "fileName": "../appsettings.json" }
#    POST /api/ingest { "fileName": "..\\..\\..\\Windows\\win.ini" }
#    POST /api/ingest { "fileName": "nested/inner.txt" }
#    POST /api/ingest { "fileName": "plan.exe" }

# 6. Index actually used:
#    EXPLAIN ANALYZE SELECT recordid FROM "RK"."PlanInfo"
#      ORDER BY chunk_embedding <=> '[...]' LIMIT 5;
#    -- expect Index Scan, NOT Seq Scan.

# 7. Retrieval quality — inspect chunks WITHOUT paying for generation:
#    POST /api/search { "query": "when does an employee become eligible to join",
#                       "topK": 5, "generateAnswer": false }

# 8. Grounded answer:
#    POST /api/search { "query": "when does an employee become eligible to join" }
#         → answered: true, citedRecordIds non-empty, answer traceable to sources[]

# 9. The refusal path — ask something the corpus cannot answer:
#    POST /api/search { "query": "what is the company holiday schedule" }
#         → answered: false, and NO invented schedule in `answer`.
#    If it answers this confidently, grounding is broken. Most important check here.
```

Steps 7–9 are what separate a working API from a working RAG system. Step 9 matters most: a
system that confidently answers a question its corpus cannot support is worse than one that
returns nothing, because the caller cannot tell the difference.

---

## Configuration

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:Postgres` | — | Required. user-secrets or environment. |
| `OpenAI:ApiKey` | — | Required. **Never** in `appsettings.json`. |
| `OpenAI:EmbeddingModel` | `text-embedding-3-small` | Must yield 1536 dims. |
| `OpenAI:Dimensions` | `1536` | Sent as the API's `dimensions` parameter. |
| `OpenAI:BatchSize` | `64` | Inputs per embedding request. |
| `OpenAI:ChatModel` | `gpt-4o-mini` | Grounded synthesis is an easy task — retrieval quality matters more than model size. |
| `OpenAI:MaxOutputTokens` | `800` | Hitting it truncates; the answer says so. |
| `OpenAI:Temperature` | `0` | Set to `null` to omit — some models reject non-default values. |
| `OpenAI:MaxContextChars` | `12000` | Retrieved text sent to the chat model per question. |
| `OpenAI:MaxRetries` | `5` | Client pipeline honours `Retry-After` on 429. |
| `OpenAI:TimeoutSeconds` | `100` | |
| `OpenAI:Endpoint` | *(empty)* | Optional. Only for an OpenAI-compatible gateway/proxy. |
| `OpenAI:Organization` | *(empty)* | Optional. Only for multi-org keys. |
| `Chunking:MaxChars` | `1000` | |
| `Chunking:OverlapChars` | `150` | `0` for strictly non-overlapping chunks. |
| `Documents:RootPath` | `Documents` | Relative to the content root. |
| `Documents:AllowedExtensions` | `.txt`, `.md` | |
| `Documents:MaxFileSizeBytes` | `5242880` | |
| `Search:DefaultTopK` / `MaxTopK` | `5` / `50` | |
| `Search:MaxQueryLength` | `2000` | |
| `Repository:HnswEfSearch` | `40` | pgvector's default; floored at `topK` at query time. |

All options are validated with `ValidateOnStart()`, so a missing or malformed setting fails at
boot rather than on the first request that needs it.

---

## Pinned dependencies

Versions are centralised in `Directory.Packages.props` and were each verified against nuget.org
by compiling a probe, not assumed.

| Package | Version | Tier |
|---|---|---|
| `Microsoft.SemanticKernel` (+ `.Core`, `.Connectors.OpenAI`) | 1.80.0 | Business |
| `Microsoft.Extensions.AI.Abstractions` | 10.9.0 | Business |
| `Npgsql` | 10.0.3 | Repository |
| `Pgvector` | 0.3.2 | Repository |
| `Microsoft.AspNetCore.OpenApi` | 10.0.11 | WebApi |
| `Scalar.AspNetCore` | 2.17.2 | WebApi |

> **There is no `Pgvector.Npgsql` package.** `UseVector()` ships inside the `Pgvector` package, in
> the `Pgvector.Npgsql` *namespace*. A package reference to `Pgvector.Npgsql` fails with "no
> versions available".

Semantic Kernel 1.80.0 exposes both the legacy `Add…TextEmbeddingGeneration` /
`Add…ChatCompletion` methods and the `Microsoft.Extensions.AI` ones
(`AddOpenAIEmbeddingGenerator`, `AddOpenAIChatClient`). This project uses the latter.

Chunking note: the requirement is 1,000-**character** chunks, but SK's `TextChunker` counts
**tokens**. They reconcile through its optional token-counter delegate — supplying one that
returns `s.Length` turns the token budget into a character budget. `ChunkingService` re-checks
the output, so a future SK change that breaks the limit fails loudly.

---

## Not included

- **Automated tests.** Removed on request; the manual sequence above is the only check.
- **Streaming answers.** `/api/search` returns the complete answer in one response.
- **Conversational follow-ups.** Each question is independent; there is no chat history.
- **Bulk ingest** (`POST /api/ingest/all`) — a small addition if wanted.
- **Document update/delete** — blocked by the frozen schema, not by the architecture.
- **Hybrid/keyword search** (needs a `tsvector` column), re-ranking, authentication,
  multi-tenancy, containerisation.
