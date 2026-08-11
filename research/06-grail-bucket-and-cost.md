# 06 — Grail custom bucket, 7-day retention, and what profile data actually costs

**Ticket:** [#6](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/6)
**Status:** researched from documentation only. No live API calls were made against
`YOUR-TENANT`. Every item marked **VERIFY** must be confirmed on the tenant before the
build lands.
**Date of source review:** 2026-08-11.

---

## TL;DR

- **A single 90-second, 99 Hz profile of one pod costs about `$0.0006`** at Dynatrace
  public list rates — roughly **1,700 profiles per dollar**. A simultaneous 100-pod
  fleet snapshot costs about **`$0.059`**.
- **Continuous profiling** (one 90 s window every 10 minutes, per pod) costs about
  **`$2.60` / `$26` / `$256` per 30 days for 1 / 10 / 100 pods**, all-in
  (ingest + 7-day retention + 20 flame-graph queries per day).
- **Ingest is ~94% of the bill.** At 7-day retention, storage adds only ~3.7% on top of
  ingest, and disciplined queries add ~2%. Optimise the record, not the retention.
- **The record schema is the only lever that matters.** Cost scales linearly with
  (unique stacks per window) × (bytes per record). Skipping the per-window dedupe —
  one record per *sample* instead of per *unique stack* — multiplies the bill by
  **7.4×**.
- **Bucket routing is not a record attribute.** A log record cannot name its own Grail
  bucket. Bucket assignment happens **only** in the OpenPipeline *Storage* stage, via a
  `bucketAssignment` processor whose DQL `matcher` tests attributes the exporter set.
  The exporter's job is to stamp a stable, unambiguous marker attribute; the pipeline's
  job is to match it.
- **Rates below are Dynatrace's published USD list rate card.** Your DPS contract rate
  card will differ. Every table shows the arithmetic so you can substitute your own
  rate.

---

## Part 1 — Creating the custom bucket

### 1.1 The API

Grail buckets are managed by the **Storage Management API**, resource
`bucket-definitions`.

| Operation | Method + path |
|---|---|
| List | `GET /bucket-definitions` |
| Read one | `GET /bucket-definitions/{bucketName}` |
| **Create** | `POST /bucket-definitions` |
| Update | `PUT` or `PATCH /bucket-definitions/{bucketName}` |
| Truncate (delete contents, keep bucket) | `POST /bucket-definitions/{bucketName}:truncate` |
| Delete | `DELETE /bucket-definitions/{bucketName}` |

Base path is `/platform/storage/management/v1` on the **platform host**
(`https://<env-id>.apps.dynatrace.com`), *not* the classic `live.dynatrace.com` host.

> **VERIFY.** The docs pages describe the resource paths but do not print a full curl
> example with the host. Confirm the exact absolute URL on your tenant with
> `dtctl get buckets --debug` (with `--debug`, dtctl logs the full HTTP request URL it
> builds). Do that once and paste the real URL into the adoption docs rather than
> trusting this line.

### 1.2 Create payload

```http
POST https://<env-id>.apps.dynatrace.com/platform/storage/management/v1/bucket-definitions
Authorization: Bearer <platform token>
Content-Type: application/json
```

```json
{
  "bucketName": "profiling_dotnet_7d",
  "table": "logs",
  "displayName": "dotnet profiling — folded stacks (7d)",
  "retentionDays": 7
}
```

| Field | Required | Rules |
|---|---|---|
| `bucketName` | yes | 3–100 chars, starts with a letter, lowercase alphanumeric + `_` + `-` only. **Immutable after creation.** Cannot start with `default_` or `dt_` (reserved). |
| `table` | yes | One of `logs`, `events`, `bizevents`, `spans`, `security.events`, `user.sessions`, `user.events`. Use **`logs`** — profiles ship as logs. |
| `retentionDays` | yes | 1–3657 (1 day to 10 years + 1 week). |
| `displayName` | no | ≤ 200 chars. |
| `bucketClass` | no | `live` (default) or `historic`. Leave at `live`. |
| `includedQueryLimitDays` | no | Opts the bucket into the *Retain with Included Queries* pricing model. See §1.5 — **not usable at 7 days.** |

Additional behaviour worth knowing:

- **Creation is asynchronous** and can take up to ~1 minute before the bucket accepts
  writes. Do not create the bucket and immediately start the exporter in the same
  script step without a readiness poll.
- **Updates use mandatory optimistic locking.** `PUT`/`PATCH` require the current
  `optimisticLockingVersion`; a stale version is rejected. Read the bucket first,
  echo its version back.
- **Shortening retention deletes the data outside the new window**, and any
  data-deleting operation is a long-running job that can take up to a few days to
  complete.
- **Data cannot be moved between buckets after ingest.** If routing is wrong on day
  one, the misrouted records live out their retention in the wrong bucket. Get the
  matcher right before switching the exporter on.
- Default cap of **80 custom buckets per environment** (raisable by support).

### 1.3 Required scopes

For the platform token / OAuth client doing the work:

```
storage:bucket-definitions:read
storage:bucket-definitions:write
storage:bucket-definitions:delete       # only if you need teardown
storage:bucket-definitions:truncate     # only if you need to wipe without deleting
```

To **query** the bucket afterwards, the querying identity additionally needs Grail
read access covering it — bucket-level access is granted through IAM policies in
Account Management (`storage:logs:read` scoped to the bucket, plus
`storage:buckets:read`). A token that can create a bucket cannot necessarily read
from it; these are separate grants and people trip on this.

### 1.4 dtctl can do this — and it is the low-risk path

`dtctl` 0.37.0 (installed on this machine) has first-class bucket support:
`create` / `get` / `describe` / `delete` bucket, alias `bkt`.

```powershell
# create
dtctl create bucket `
  --name profiling_dotnet_7d `
  --table logs `
  --retention 7 `
  --display-name "dotnet profiling - folded stacks (7d)"

# preview first
dtctl create bucket --name profiling_dotnet_7d --table logs --retention 7 --dry-run

# confirm the token has what it needs, without doing anything
dtctl create bucket --name profiling_dotnet_7d --table logs --retention 7 --check-scopes

# read back
dtctl get buckets
dtctl describe bucket profiling_dotnet_7d -o yaml
```

`--retention` accepts 1–3657, and `--table` accepts `logs`, `events`, `bizevents`.
`dtctl create bucket -f bucket.yaml` takes a file, which is the form to commit to the
repo so bucket creation is reviewable.

Two dtctl flags are worth building into the runbook: `--dry-run` (prints the intended
call without issuing it) and `--check-scopes` (asserts the active token carries the
required scopes, then exits). Both let an adopter validate before touching a tenant.

### 1.5 The 7-day retention decision, and its one real consequence

Two documented facts are in tension and both matter:

1. The **bucket API** accepts `retentionDays` from **1** to 3657.
2. The **logs bucket UI tutorial** describes the log-bucket retention range as
   **10 days to 10 years**.

> **VERIFY.** If the Storage Management UI refuses 7 days, create the bucket via
> API/dtctl instead — the API range is the authoritative one — or accept 10 days.
> This is a one-line check on the tenant; do it before writing the adoption doc.

**The consequence that actually bites:** *Retain with Included Queries* (the pricing
model where queries within the retention window cost nothing extra, at a higher
per-GiB-day retention rate) has a **minimum included-query period of 10 days**. A
7-day bucket therefore **cannot** use it. A 7-day profiling bucket is billed under the
standard **Retain + Query** model: `$0.0007/GiB-day` storage plus `$0.0035/GiB` scanned.

For profile data that is *good* — see §4.4. Retain-with-Included-Queries costs
`$0.02/GiB-day`, ~28.6× the standard retention rate, and buys a query allowance of
15× retained GiB per 24 h. Profiling is write-heavy and read-light: you ingest
constantly and query only when someone opens a flame graph. Paying 28.6× on storage to
make rare queries free is the wrong trade here. Standard Retain + Query is correct for
this workload, and 7 days makes it correct by construction.

---

## Part 2 — Getting records *into* the bucket (the part people get wrong)

### 2.1 The rule

> **A log record cannot select its own Grail bucket.** There is no `dt.system.bucket`
> attribute you can set on the wire, no `bucket` field in OTLP that Dynatrace honours,
> and no header. `dt.system.bucket` is a **read-only system field** stamped by the
> platform — you can filter on it in DQL, you cannot write it at ingest.

Bucket assignment is decided by **OpenPipeline**, at the very end of processing, in the
**Storage** stage. Everything the record carries is only *input to a matcher*.

### 2.2 The actual path a profiling record takes

```
OTLP exporter
  │  POST /api/v2/otlp/v1/logs   (application/x-protobuf, token scope: logs.ingest)
  ▼
Ingest source  (OpenPipeline "Logs" configuration scope)
  ▼
Routing table  — ordered list of routes; each route has a DQL matcher + a pipelineId.
                 First match wins. No match → the built-in Default route.
  ▼
Pipeline  — stages run in order:
              Processing → Metric extraction → Smartscape node/edge extraction →
              Metric extraction (spans) → Data extraction → Davis →
              Cost allocation → Product allocation → Permissions → **Storage**
  ▼
Storage stage  — "first match only". Processors available here:
                   • bucketAssignment  → writes the record to a named bucket
                   • noStorage         → drops the record (not retained, not billed for
                                          retention; ingest has already been billed)
  ▼
Grail bucket
```

Two independent match points, and it is easy to configure one and forget the other:

- **The route matcher** decides *which pipeline* the record enters.
- **The `bucketAssignment` matcher inside that pipeline's Storage stage** decides
  *which bucket*.

If your route sends profiling records to a custom pipeline but that pipeline's Storage
stage has no matching `bucketAssignment` processor, the records fall through to the
default bucket (`default_logs`, 35-day retention) — silently, at 5× the retention you
budgeted for.

### 2.3 What the OTLP exporter must set

The exporter's only job is to make the record **unambiguously identifiable by a DQL
matcher**. Concretely:

1. **Stamp one dedicated, high-signal marker attribute.** Do not match on something
   incidental like `service.name` or a namespace — those change. Use a purpose-built
   key that nothing else in the tenant will ever carry, e.g.:

   ```
   dt.openpipeline.source = "dotnet-profiler"      # or
   profile.signal          = "folded-stacks"
   ```

   Set it as an **OTLP resource attribute** so it is emitted once per batch (cheaper
   at ingest — see §3.2) and copied onto every record by the platform.

2. **Do not prefix custom keys with `dt.system.`** — reserved.

3. **Put the folded stack in the log record `Body`, not in an attribute.** This is a
   hard constraint, not a preference:

   | Placement | Documented limit |
   |---|---|
   | `content` (from OTLP `Body`) | 65,536 bytes generic ingest; **524,288 bytes with OpenPipeline processing** |
   | attribute value | 32 kB per the LMA limits page; **2,500 bytes** cited for the OpenPipeline-increased default |

   A deep .NET stack with async state machines and generic type names routinely exceeds
   2,500 bytes (this doc models 2,200 bytes as *average*). Folded stacks in an
   attribute will be silently trimmed. Put them in the body.

4. **Respect the batch limits.** Per request: **10 MB payload**, **50,000 log records**.
   And critically — *"OTLP ingestion copies resource and scope attributes to each log
   record"*, and **if the payload exceeds 16 MB after processing it is rejected**. A
   1,200-record window with a fat resource block can inflate several-fold post-copy.
   Size batches against the *post-copy* number, not the wire number.

5. **Other per-record limits:** max 500 attributes per record (250 cited for the
   OpenPipeline-increased default), attribute keys ≤ 100 bytes.

### 2.4 The pipeline configuration

The **OpenPipeline Configurations API is deprecated and reached end of life on
2026-06-29.** OpenPipeline is now configured through the **Settings API** as ordinary
settings objects:

| Concern | Schema ID |
|---|---|
| Routing table for logs | `builtin:openpipeline.logs.routing` |
| Individual pipelines | `builtin:openpipeline.logs.pipelines` |
| Ingest sources | `builtin:openpipeline.logs.ingest-sources` |

Endpoint: `/api/v2/settings` (settings objects). Permissions:
`settings:objects:read` + `settings:objects:write`; **routing additionally requires
`settings:objects:admin`** because owner-based access control is not available for the
routing object.

Policy grant example:

```
ALLOW settings:objects:write WHERE settings:schemaId = "builtin:openpipeline.logs.pipelines";
```

`dtctl` handles settings objects (`dtctl get settings`, `dtctl apply -f`,
`dtctl get settings-schemas`), so the pipeline can be applied from a committed file the
same way the bucket is.

**The `bucketAssignment` processor**, as it appears in the pipeline's Storage stage:

```json
{
  "type": "bucketAssignment",
  "id": "bucket-assignment-dotnet-profiling",
  "description": "Route .NET folded-stack profile records to the 7-day profiling bucket",
  "enabled": true,
  "matcher": "matchesValue(dt.openpipeline.source, \"dotnet-profiler\")",
  "sampleData": "{\"dt.openpipeline.source\":\"dotnet-profiler\"}",
  "bucketAssignment": {
    "bucketName": "profiling_dotnet_7d"
  }
}
```

The `matcher` is DQL matcher syntax evaluated per record. `sampleData` is used by the
UI's preview and is worth filling in — it is how you prove the matcher fires before you
save.

UI equivalent, if you'd rather click it the first time:
**Settings → Process and contextualize → OpenPipeline → Logs → Pipelines →** *(your
pipeline)* **→ Storage → Processor → Bucket assignment**. For logs you must have
**dynamic routing enabled** for custom routes to be evaluated at all.

### 2.5 Verification, before you trust it

```dql
// Did anything land in the bucket at all?
fetch logs, bucket:{"profiling_dotnet_7d"}, from:-15m
| summarize records = count()
```

```dql
// The failure mode: profiling records leaking into the default bucket.
// Should return zero rows. If it doesn't, the matcher is wrong.
fetch logs, from:-15m
| filter dt.openpipeline.source == "dotnet-profiler"
| summarize records = count(), by: {dt.system.bucket}
```

That second query is the one to put in the runbook. It is the only way to catch a
matcher that *almost* works.

Also worth knowing: the pipeline has a **Cost allocation** stage. Tagging profiling
records with `dt.cost.costcenter` there makes the profiling spend separable in the
billing data later, at no extra cost. Cheap to add now, impossible to backfill.

---

## Part 3 — The DPS cost model

### 3.1 Published list rates

From the public Dynatrace rate card (USD):

| Capability | Unit | List rate |
|---|---|---|
| Logs — Ingest & Process | per GiB | **$0.20** |
| Logs — Query | per GiB scanned | **$0.0035** |
| Logs — Retain | per GiB-day | **$0.0007** |
| Logs — Retain with Included Queries | per GiB-day | **$0.02** |

> **These are list prices, and the docs are explicit that the number applied to you is
> "the GiB price **as per your rate card**".** DPS rate cards are negotiated per
> contract; discounts at volume are normal. The rate card page carries no regional
> price table, but do not assume rate parity across regions or contract tiers.
> **Substitute your own `P_ingest`, `P_query`, `P_retain` into the formulas in Part 4
> before quoting any figure internally.** Authoritative actuals live in
> **Account Management → Subscription → Overview → Cost and usage details**.

### 3.2 What is actually metered

**Ingest & Process** — *"the amount of raw data in bytes sent to Dynatrace **before**
enrichment and transformation."* Tracked by `builtin:billing.log.ingest.usage`, hourly.

Two consequences that shape the estimate:

- **Enrichment is free at ingest.** All the `dt.*` topology fields Dynatrace adds, and
  the resource attributes it copies onto every record, are *not* billed as ingest.
  (They *are* billed as retention and as scanned bytes — see below.)
- **Resource attributes amortise on the wire.** OTLP sends the resource block once per
  `ResourceLogs` batch. One profiling window = one batch = one resource block for all
  N stack records. This is a real saving and it argues for putting the join keys at
  resource level, not record level.

**Query** — *"the number of GiB of uncompressed data read during query execution."* You
pay for what the query **scans**, not what it returns. A query returning zero rows can
still be expensive. Grail applies automatic optimisations that identify data provably
irrelevant to the result and **discounts scanning that data by 98%** — this is why the
worked estimate below is a *ceiling*, not a prediction. Tracked by
`builtin:billing.log.query.usage`.

**Retain** — GiB-days: *(uncompressed GiB stored) × (days stored)*. Billed hourly at
1/24 of a GiB-day. This is measured on the **stored** record — post-enrichment — which
is larger than the ingested record.

**No free/included volume applies to logs.** DPS included volumes exist for Metrics
Ingest and Traces Ingest (deducted against a host-monitoring baseline). Log Management
& Analytics has no equivalent baseline deduction. Every ingested profile byte is
billable from the first byte.

---

## Part 4 — Worked cost estimate

**Read this as a spreadsheet you can edit.** Every input is named, every number is
derived from the ones above it.

### 4.1 Inputs and assumptions

| Symbol | Meaning | Value used | Basis |
|---|---|---|---|
| `S` | sample rate | 99 Hz | given |
| `D` | profile window | 90 s | given |
| `T` | avg on-CPU threads sampled per tick | 1 | **assumption** — a container with ~1 CPU. Multiply everything by `T` for wider pods. |
| `samples` | `S × D × T` | **8,910** | derived |
| `U` | **unique stacks per window per pod** | **1,200** | **assumption** — ~13% of samples unique. Sampled stacks are heavily repetitive; a steady-state ASP.NET Core service typically yields hundreds to low thousands of distinct stacks in 90 s. Range modelled: 300–3,000. |
| `f` | avg stack depth (frames) | 40 | **assumption** — managed .NET with async/await + ASP.NET pipeline is deep; 30–60 typical. |
| `c` | avg bytes per frame incl. `;` separator | 55 | **assumption** — `Namespace.Type.Method` style, UTF-8 ASCII. |
| `folded` | `f × c` | **2,200 B** | derived |
| `ovh` | stack hash + sample count + record-level attrs + OTLP envelope | 300 B | **assumption** |
| `b` | **billed bytes per record** = `folded + ovh` | **2,500 B** | derived |
| `E` | stored ÷ ingested size factor | **1.5** | **assumption**. Dynatrace warns enrichment "can increase your data volume significantly", by a factor of 2 or more. 1.5 is the base case; a 2.0 row is shown in §4.6. |
| `W` | profile windows per pod per day | **144** (one 90 s window every 10 min) | scenario choice |
| `R` | retention | **7 days** | design |
| `N` | pods | 1 / 10 / 100 | scenario |
| `P_i` | ingest price | $0.20 / GiB | list |
| `P_r` | retention price | $0.0007 / GiB-day | list |
| `P_q` | query price | $0.0035 / GiB scanned | list |

**What is deliberately excluded:** the resource-attribute block (~700 B once per
batch — <0.03% of a 3 MB window, rounding error); network compression (billing is on
raw bytes, so gzip on the wire does not reduce the bill); and Grail's 98% irrelevant-data
query discount (excluded to keep query numbers a ceiling).

### 4.2 The formulas

```
bytes_per_window_per_pod = U × b
ingest_GiB_per_day       = N × W × U × b / 2^30
ingest_cost_per_day      = ingest_GiB_per_day × P_i

stored_GiB_per_day       = ingest_GiB_per_day × E
resident_GiB             = stored_GiB_per_day × R          (steady state, day 8 onward)
retain_cost_per_day      = resident_GiB × P_r

query_cost_per_run       = scanned_GiB × P_q
  where scanned_GiB ≈ stored_GiB_per_day × (query_timeframe_hours / 24)
        for a query with an explicit bucket filter and time bound
```

### 4.3 One profile — the number to quote

Per pod, per 90-second window:

```
bytes  = U × b = 1,200 × 2,500          = 3,000,000 B
GiB    = 3,000,000 / 1,073,741,824      = 0.0027940 GiB   (2.86 MiB)
ingest = 0.0027940 × $0.20              = $0.00055880
stored = 0.0027940 × 1.5                = 0.0041910 GiB
retain = 0.0041910 × 7 days × $0.0007   = $0.00002054
query  = 0.0041910 × $0.0035            = $0.00001467     (one flame graph of that window)
                                          ─────────────
total per profile per pod               = $0.00059401
```

| Scenario | Ingest | 7-day retain | 1 flame-graph query | **Total** |
|---|---:|---:|---:|---:|
| **N = 1** — one pod, one profile | $0.000559 | $0.000021 | $0.000015 | **$0.00059** |
| **N = 10** — fleet snapshot, 10 pods | $0.005588 | $0.000205 | $0.000147 | **$0.00594** |
| **N = 100** — fleet snapshot, 100 pods | $0.055880 | $0.002054 | $0.001467 | **$0.05940** |

**A 100-pod fleet-wide 90-second profile costs six cents.** On-demand,
workflow-triggered profiling is essentially free. The cost question only becomes real
when profiling runs continuously.

### 4.4 Continuous profiling — 30-day steady state

`W = 144` windows/pod/day (one every 10 minutes). Query line assumes **20 flame-graph
queries per day**, each with a bucket filter and a **1-hour** time bound.

Per-pod derived values:

```
ingest_GiB_per_day_per_pod = 0.0027940 × 144         = 0.402336 GiB
stored_GiB_per_day_per_pod = 0.402336 × 1.5          = 0.603504 GiB
resident_GiB_per_pod       = 0.603504 × 7            = 4.224528 GiB
retain_$_per_day_per_pod   = 4.224528 × $0.0007      = $0.00295717
scan_per_1h_query_per_pod  = 0.603504 / 24           = 0.025146 GiB
query_$_per_run_per_pod    = 0.025146 × $0.0035      = $0.00008801
```

| | **N = 1** | **N = 10** | **N = 100** |
|---|---:|---:|---:|
| Ingest, GiB/day | 0.402 | 4.023 | 40.23 |
| Ingest, $/day | $0.0805 | $0.8047 | $8.047 |
| **Ingest, $/30 days** | **$2.41** | **$24.14** | **$241.40** |
| Resident volume (7 d, GiB) | 4.22 | 42.25 | 422.5 |
| Retain, $/day | $0.0030 | $0.0296 | $0.2957 |
| **Retain, $/30 days** | **$0.09** | **$0.89** | **$8.87** |
| Scanned per flame-graph query (1 h window, GiB) | 0.025 | 0.251 | 2.515 |
| Cost per flame-graph query | $0.000088 | $0.00088 | $0.0088 |
| **Queries, $/30 days** (20/day) | **$0.05** | **$0.53** | **$5.28** |
| **TOTAL, $/30 days** | **$2.56** | **$25.56** | **$255.55** |
| Ingest share of total | 94.3% | 94.4% | 94.5% |

**Retention at 7 days is a rounding error, and that is provable in closed form:**

```
retain_cost / ingest_cost = E × R × P_r / P_i
                          = 1.5 × 7 × 0.0007 / 0.20
                          = 3.675%
```

Substituting the default 35-day `default_logs` retention gives 18.4% — five times
more. That is the entire justification for the dedicated bucket, and it is smaller than
people expect. **The bucket is worth having, but it saves ~15% of a bill that ingest
already dominates.** Do not let it distract from the record schema.

### 4.5 The query number that should worry you

Everything above assumes a **bucket filter and a time bound**. Without them:

| Query shape (N = 100, steady state) | Scanned | Cost per run |
|---|---:|---:|
| Bucket filter + one 90 s window | 0.42 GiB | $0.0015 |
| Bucket filter + 1 hour | 2.51 GiB | $0.0088 |
| Bucket filter + 24 hours | 60.4 GiB | $0.211 |
| Bucket filter + **full 7-day bucket** | 422 GiB | **$1.48** |
| **No bucket filter**, 7 days — scans *every* log bucket | your entire log estate | **unbounded** |

A dashboard tile running the unbounded 7-day query on a 1-minute auto-refresh:
`1,440 × $1.48 = $2,129/day`. That single mistake costs **8× the entire profiling
pipeline's monthly bill, every day.** This is why every documented query in this repo
carries an explicit bucket filter — see Part 5.

(Grail's 98% irrelevant-data discount will usually make the real numbers much lower.
These are ceilings. Design against the ceiling.)

### 4.6 Sensitivity — where the cost actually lives

**30-day ingest cost, N = 100, continuous (W = 144).** Formula:
`cost = U × b × W × N × 30 × P_i / 2^30` = `U × b × 8.04663 × 10⁻⁵`

| unique stacks `U` ↓ / bytes per record `b` → | **1,200 B** (interned frame table, short names) | **2,500 B** (base case) | **5,000 B** (deep async, verbose generics) |
|---|---:|---:|---:|
| **300** (simple worker, idle-ish) | $28.97 | $60.35 | $120.70 |
| **1,200** (base case) | $115.87 | **$241.40** | $482.80 |
| **3,000** (complex, many endpoints) | $289.68 | $603.50 | $1,207.00 |
| **8,910** (*no dedupe* — one record per sample) | $860.35 | **$1,792.39** | $3,584.77 |

Read across one row and down one column and the design priorities fall out:

1. **Dedupe per window is worth 7.4×.** One record per unique stack, not one per
   sample. This is the single largest decision in the pipeline. It is already the
   design; this table is why it must stay the design.
2. **The folded stack is ~88% of every record** (2,200 of 2,500 bytes). Halving frame
   name length — dropping assembly prefixes, collapsing generic arity, emitting a
   per-batch frame dictionary and referencing frames by index — halves the bill. This
   is the second-largest lever and it is entirely in the exporter's control.
3. **Retention is a 3.7% line item.** Changing 7 days to 3 days saves ~$3.80/month at
   N=100. Not worth the loss of a weekend's worth of history.

Other sensitivities:

| Change | Effect on total |
|---|---|
| `T = 4` (4 on-CPU threads/pod) | `U` rises, but sub-linearly — threads share stacks. Expect ~1.5–2.5×, not 4×. **Measure it.** |
| `E = 2.0` instead of 1.5 | Retain +33% → ~$11.83/30 d at N=100. Total +1.2%. Ingest unaffected (billed pre-enrichment). |
| `W = 1440` (every minute instead of every 10) | Everything ×10 → **~$2,555/30 d at N=100**. Cadence is a first-class cost control. |
| `W = 24` (hourly) | ÷6 → **~$42.60/30 d at N=100**. |
| Your rate card at 50% of list | Everything ÷2, exactly — all three prices scale linearly. |

### 4.7 Check the model against reality on day one

Do not trust this document's `U` and `b` for your workload. Both are measurable within
an hour of switching the exporter on.

**Actual bytes billed at ingest** (needs `dt.system.events` read):

```dql
fetch dt.system.events, from:-24h
| filter event.kind == "BILLING_USAGE_EVENT"
| filter event.type == "Log Management & Analytics - Ingest & Process"
| dedup {event.id, event.type}
| summarize gib = sum(billed_bytes) / 1073741824
```

**Actual unique-stack cardinality and record size in your bucket:**

```dql
fetch logs, bucket:{"profiling_dotnet_7d"}, from:-1h
| summarize
    records          = count(),
    unique_stacks    = countDistinct(profile.stack.hash),
    avg_content_bytes = avg(stringLength(content))
```

**What a given query actually scanned** — run the flame-graph query, then:

```dql
fetch dt.system.events, from:-1h
| filter event.kind == "QUERY_EXECUTION_EVENT"
| filter bucket == "profiling_dotnet_7d"
| fields timestamp, scanned_bytes, scanned_records, delivered_records,
         execution_duration_ms, query_string
| sort timestamp desc
| limit 20
```

`scanned_bytes / 1073741824 × P_q` is the real cost of that query. Compare it against
§4.5. Any gap between `scanned_bytes` here and `billed_bytes` on the Query billing
event is Grail's optimisation discount (or zero-rating), not a bug.

`dtctl` also reports per-bucket contribution directly, which is the fastest way to
confirm a bucket filter is earning its keep:

```powershell
dtctl query --include-contributions --metadata=contributions -o json "<THE_DQL_QUERY>"
```

The `matchedRecordsRatio` per bucket in the metadata tells you which buckets a query is
actually touching.

---

## Part 5 — Keeping query cost down

Billing is on **bytes scanned**, so every rule below is the same rule: read less.

### 5.1 Always filter to the bucket — in the `fetch`, not in a `filter`

```dql
// ✅ prunes at the source — only this bucket is read
fetch logs, bucket:{"profiling_dotnet_7d"}, from:-1h

// ❌ reads every log bucket the user can see, then throws most of it away.
//    You are billed for all of it.
fetch logs, from:-1h
| filter dt.system.bucket == "profiling_dotnet_7d"
```

This is the highest-leverage line in any profiling query and it is one keyword. **Every
documented query in this repo carries it.**

### 5.2 Bound time as tightly as the question allows

A flame graph answers a question about *one window*. Query that window, not the day.

```dql
fetch logs, bucket:{"profiling_dotnet_7d"}, from:"2026-08-11T14:30:00Z", to:"2026-08-11T14:31:30Z"
```

Scanned volume is very nearly linear in timeframe: 1 h → 24 h is a 24× cost increase
for the same flame graph. Prefer absolute timestamps derived from the profile session
record over relative `-24h` defaults; a dashboard whose default timeframe is "last 7
days" is a standing invoice.

### 5.3 Filter to one session/pod immediately after `fetch`

```dql
fetch logs, bucket:{"profiling_dotnet_7d"}, from:"...", to:"..."
| filter profile.session.id == "sess-01J..."      // ← before anything else
| filter k8s.pod.name == "checkout-7d9f-x2kq"
```

Filters placed after `fieldsAdd` or `summarize` are 2–10× worse. Filter, then compute.

### 5.4 Select only the fields the flame graph needs

A flame graph needs three columns. Ask for three columns.

```dql
fetch logs, bucket:{"profiling_dotnet_7d"}, from:"...", to:"..."
| filter profile.session.id == "sess-01J..."
| fields content, profile.stack.hash, profile.sample.count
| summarize samples = sum(profile.sample.count), by: {content}
| sort samples desc
```

Note `fields` **before** `summarize` — trim the row early.

### 5.5 Aggregate in DQL, not in the app

Pull the folded-stack → count map out of Grail already summed. Do not fetch 1,200 rows
per pod into the UI and reduce them in JavaScript; that scans the same bytes and adds
egress.

### 5.6 Combine questions into one scan

```dql
// ✅ one scan
fetch logs, bucket:{"profiling_dotnet_7d"}, from:"...", to:"..."
| summarize
    total_samples = sum(profile.sample.count),
    unique_stacks = countDistinct(profile.stack.hash),
    pods          = countDistinct(k8s.pod.name)
```

Three separate queries scan the same bytes three times and bill three times.

### 5.7 Do not group by the folded stack across a wide timeframe

`content` is effectively unbounded cardinality. `summarize by: {content}` over one
90-second window is fine (~1,200 groups). Over 7 days across 100 pods it is millions of
groups — slow, and it forces a full scan. Bound the window first, always.

### 5.8 Watch what *automation* does

Interactive queries are self-limiting; a human gets bored. Dashboards, workflows,
anomaly detectors, and app functions run on a timer forever. Before shipping any
scheduled query against this bucket, compute
`cost_per_run × runs_per_day × 30` and put that number in the PR description. §4.5
shows what happens when nobody does.

Attribute after the fact with:

```dql
fetch dt.system.events, from:-7d
| filter event.kind == "BILLING_USAGE_EVENT"
| filter event.type == "Log Management & Analytics - Query"
| dedup {event.id, event.type}
| fieldsAdd attribution = coalesce(client.source, client.application_context,
    client.internal_service_context, client.workflow_context, client.function_context, "unknown")
| summarize gib_scanned = sum(billed_bytes) / 1073741824, by: {attribution}
| sort gib_scanned desc
```

### 5.9 The cheapest query is the one you don't run

If the flame-graph app re-queries on every UI interaction (zoom, filter, colour change),
it is billing every interaction. Fetch the aggregated stack→count map **once** per
session and do all subsequent manipulation client-side. The dataset for one window
(~1,200 rows) is small enough to hold in memory comfortably.

---

## Open items — confirm on the tenant

| # | Item | Why it matters | How to check |
|---|---|---|---|
| 1 | Does the Storage Management **UI** accept `retentionDays = 7` for a logs bucket, or is 10 the floor? | Determines whether adopters can click it or must use API/dtctl. | Try it; fall back to `dtctl create bucket --retention 7`. |
| 2 | Exact absolute URL of the bucket-definitions endpoint on `YOUR-TENANT`. | The adoption doc should contain a URL that works, not one inferred from docs. | `dtctl get buckets --debug` and read the logged request URL. |
| 3 | Confirm `dt.system.bucket` is genuinely unwritable at OTLP ingest. | If it *were* writable it would be a much simpler routing story. Docs say assignment is pipeline-only; prove it. | Send one record with `dt.system.bucket` set, then run the §2.5 leak query. |
| 4 | Real `U` (unique stacks/window) and `b` (bytes/record) for the actual .NET workload. | These two inputs drive 100% of the estimate. §4.6 spans a 124× range. | §4.7 queries, first hour after go-live. |
| 5 | Whether ingest billing counts the **uncompressed** decoded OTLP record or the compressed wire bytes. | Docs say "raw data in bytes sent … before enrichment"; if gzip counts, ingest drops sharply for repetitive folded strings. | Ingest a known-size batch, read `builtin:billing.log.ingest.usage` for that hour. |
| 6 | Attribute-value ceiling on this tenant: 2,500 B or 32 kB? | Only matters if someone ignores §2.3 and puts the stack in an attribute. Belt and braces. | Ingest a 5 kB attribute value, query it back, check for trimming. |
| 7 | Tenant's actual DPS rate card. | Every dollar figure here is list price. | Account Management → Subscription → Overview → Cost and usage details. |

---

## Sources

Grail buckets and retention:
- [Use Grail buckets to partition data](https://docs.dynatrace.com/docs/platform/grail/organize-data/partition-data)
- [How to organize your data stored in Grail](https://docs.dynatrace.com/docs/platform/grail/organize-data)
- [Configure data storage and retention for logs](https://docs.dynatrace.com/docs/analyze-explore-automate/logs/lma-bucket-assignment)
- [Grail storage management API (SDK reference)](https://developer.dynatrace.com/develop/sdks/client-bucket-management/v3/)
- [Optimize log retention and reduce scanned data volume](https://docs.dynatrace.com/docs/analyze-explore-automate/logs/lma-use-cases/optimize-log-retention)

OpenPipeline routing:
- [Processing in OpenPipeline](https://docs.dynatrace.com/docs/platform/openpipeline/concepts/processing)
- [Data flow in OpenPipeline](https://docs.dynatrace.com/docs/platform/openpipeline/concepts/data-flow)
- [Log processing with OpenPipeline](https://docs.dynatrace.com/docs/analyze-explore-automate/logs/lma-log-processing/lma-openpipeline)
- [Migrate OpenPipeline configurations to Settings API](https://docs.dynatrace.com/docs/platform/openpipeline/migration-settings)
- [Ingest sources in OpenPipeline](https://docs.dynatrace.com/docs/platform/openpipeline/reference/api-ingestion-reference)

OTLP logs ingest:
- [Ingest OTLP logs](https://docs.dynatrace.com/docs/ingest-from/opentelemetry/otlp-api/ingest-logs)
- [Log Management and Analytics default limits](https://docs.dynatrace.com/docs/analyze-explore-automate/logs/lma-limits)

Pricing:
- [Dynatrace pricing rate card](https://www.dynatrace.com/pricing/rate-card/) — list prices
- [Log Management and Analytics (DPS)](https://docs.dynatrace.com/docs/manage/dynatrace-platform-subscription/capabilities/dps-log-management)
- [Calculate your consumption — Log Query (DPS)](https://docs.dynatrace.com/docs/license/log-management/dps-log-query)
- [Calculate your consumption — Retain with Included Queries (DPS)](https://docs.dynatrace.com/docs/license/capabilities/log-analytics/dps-log-retain-included)

Local skills consulted: `.claude/skills/dt-platform-costs` (billing event types,
`billed_bytes` semantics, cost normalisation weights — which match the published list
rates exactly), `.claude/skills/dt-dql-essentials/references/optimization.md` (bucket
filters, filter-early, field selection, `dtctl query --include-contributions`).
`dt-app-mcp` DQL knowledge base consulted for `QUERY_EXECUTION_EVENT` field names
(`scanned_bytes`, `scanned_records`, `bucket`).
