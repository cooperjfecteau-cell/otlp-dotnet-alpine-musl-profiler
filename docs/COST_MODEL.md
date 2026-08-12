# Cost model

A parameterized model for what this pipeline costs to run, so you can scope it against your own
fleet before deploying anything.

There is an [interactive version of this
model](https://cooperjfecteau-cell.github.io/otlp-dotnet-alpine-musl-profiler/cost-model.html) if
you would rather change inputs than do arithmetic.

**To fill in the inputs for a real environment, see [DISCOVERY.md](DISCOVERY.md)** — most of them
are countable facts rather than estimates, and it says which ones are worth the effort.

> **Superseding an earlier estimate.** Earlier drafts of `GETTING_STARTED.md` quoted **$0.0006**
> per 90-second single-pod profile. That was a pre-deployment estimate. Measuring the real records
> after the pipeline was running gives **$0.0025** — about **4× higher**. The measured number is
> the one used throughout this document. If you have an older copy of the docs, use these figures.

---

## Measured baseline

Everything below scales from four real captures taken on 2026-08-11/12, on .NET 9 / Alpine 3.23 /
arm64, one pod, eBPF at 19 Hz, EventPipe at its default rate:

| Session | Half | Records | Avg bytes/record | Total |
|---|---|---:|---:|---:|
| `01KZSX1S…` | eBPF | 5,540 | 1,522 | 8.0 MiB |
| `01KZSX1S…` | EventPipe | 2,194 | 2,773 | 4.2 MiB |
| `01KZSFV1…` | eBPF | 4,427 | 1,511 | 6.4 MiB |
| `01KZSFV1…` | EventPipe | 2,014 | 2,857 | 3.5 MiB |
| `01KZCONT…` | eBPF | 3,584 | 1,558 | 5.3 MiB |
| `01KZCONT…` | EventPipe | 1,684 | 2,794 | 2.9 MiB |
| `01JEVENT…` | eBPF | 4,910 | 1,485 | 7.0 MiB |
| `01JEVENT…` | EventPipe | 1,489 | 2,736 | 2.7 MiB |

**Rounded baseline: 8.04 MiB eBPF + 4.23 MiB EventPipe = 12.3 MiB per 90-second session per pod.**

Reproduce it against your own data with `docs/queries/` or directly:

```dql
fetch logs, from:-6h
| filter isNotNull(profile.session_id)
| fieldsAdd half = if(isNull(profile.source), "ebpf", else:profile.source)
| fieldsAdd bytes = stringLength(profile.stack.folded) + 400
| summarize records = count(),
            avg_bytes = round(avg(bytes), decimals: 0),
            total_kb = round(sum(bytes) / 1024.0, decimals: 0),
            by:{profile.session_id, half}
```

Note the `+ 400`: `profile.stack.folded` is the bulk of each record, and 400 bytes is the
approximate weight of the surrounding attributes. Billed bytes are what Grail actually ingests, so
treat this as ±20%, not as an invoice.

---

## The formula

### Inputs

| Symbol | Meaning | Default |
|---|---|---|
| `N` | Pods carrying the sidecar | — |
| `S` | Sessions per pod per day | — |
| `D` | Session length, seconds | 90 |
| `H` | eBPF sample rate, Hz | 19 |
| `F` | Scope factor: `1` if the collector filters to your services, `10` if not | 1 |
| `R` | Bucket retention, days | 7 |
| `V` | Flame graph views per day | — |
| `P_i` | Log ingest, $/GiB | 0.20 |
| `P_r` | Retention, $/GiB-day | 0.0007 |
| `P_q` | Query, $/GiB scanned | 0.0035 |

### Data volume

```
session_MiB = 8.04 × (D/90) × (H/19) × F      ← eBPF half
            + 4.23 × (D/90)                   ← EventPipe half

ingest_GiB_day = N × S × session_MiB / 1024
```

The eBPF half scales with sample rate; the EventPipe half does not, because it is driven by
EventPipe's own event rate rather than a timer you control.

### Dynatrace cost, per month (30.4 days)

```
ingest_$  = ingest_GiB_day × 30.4 × P_i
retain_$  = ingest_GiB_day × 1.5 × R × P_r × 30.4
query_$   = V × scan_GiB × P_q × 30.4
```

`1.5` is the index/storage expansion factor over raw ingest — see
`research/06-grail-bucket-and-cost.md`.

`scan_GiB` depends entirely on whether your queries are bounded:

```
scan_GiB = session_MiB / 1024                   ← bounded by profile.session_id
         = ingest_GiB_day × 1.5 × R             ← unbounded, scans the whole bucket
```

### Compute cost, per month

```
collector_frac = max(0.10 vCPU / node_vCPU, 0.25 GiB / node_GiB)
sidecar_frac   = max(0.10 vCPU / node_vCPU, 0.25 GiB / node_GiB)

compute_$ = (nodes × collector_frac + N × sidecar_frac) × node_$_month
edge_$    = node_$_month   if you need the x86 EdgeConnect node, else 0
```

The collector DaemonSet requests 100m CPU / 256Mi **per node**. Each profiled pod adds two
sidecars — `dotnet-monitor` and the agent — at 50m / 128Mi each, so 100m / 256Mi **per pod**.
Taking the max of the CPU and memory fractions prices whichever dimension you run out of first.

### Total

```
total_$ = ingest_$ + retain_$ + query_$ + compute_$ + edge_$
```

---

## Worked scenarios

All at list rates, `F = 1` (scoped), `H = 19`, `D = 90`, `R = 7`, and 2 vCPU / 8 GiB nodes at
$49/month.

### One 90-second profile, one pod

12.3 MiB → **$0.0025** in Dynatrace data cost. Ingest is $0.0024 of it.

Profiling is cheap enough per-invocation that it should never be the thing you ration.

### Fleet snapshot — 100 pods, one profile each

1.2 GiB → **$0.25**. Still negligible.

### Steady state — 10 pods, 4 sessions/day, 20 flame graph views/day

| | $/month |
|---|---:|
| Log ingest | 2.91 |
| Retention | 0.11 |
| Queries (bounded) | 0.03 |
| **Dynatrace subtotal** | **3.05** |
| Compute — collector on 3 nodes + 10 pods of sidecars | 31.85 |
| EdgeConnect x86 node | 49.00 |
| **Total** | **83.90** |

**The Dynatrace bill is 3.6% of the total.** At any realistic on-demand scope, this pipeline's
cost is compute, not data — which is worth knowing before you scope it against a data budget.

### The one to avoid — always-on, 10 pods

Running the sidecars continuously instead of on demand takes 12.3 MiB/90s/pod to 118 GiB/day:

| | $/month |
|---|---:|
| Log ingest | 716 |
| Retention | 26 |
| **Dynatrace subtotal** | **742** |

**240× the on-demand bill.** This is the entire reason the pipeline is session-gated rather than
always-on, and the reason the gate fails *closed* when the session ConfigMap is malformed.

---

## Which line item wins as you scale

Compute and ingest both scale linearly with pod count, so **pod count never decides which one
dominates** — it cancels out. Per profiled pod, per month:

```
ingest  = S × (session_MiB/1024) × 30.4 × P_i
compute = sidecar_frac × node_$        ≈ 0.1 vCPU × ($/vCPU-month)
```

That compute figure is ~**$2.45/pod/month** and barely moves with instance choice: a node with 4×
the vCPU shrinks `sidecar_frac` by 4× and costs about 4× more, so the two cancel. Treat it as
"0.1 vCPU at your cluster's rate."

Setting the two equal gives the crossover, which depends only on **duty cycle, sample rate and
scope**:

| Configuration | Ingest overtakes compute at |
|---|---:|
| 19 Hz, scoped *(default)* | ~34 sessions/pod/day |
| 99 Hz, scoped | ~9 |
| 19 Hz, unscoped | ~5 |
| 99 Hz, unscoped | ~1 |

34 sessions of 90 seconds is ~50 minutes of profiling per pod per day — a **3.5% duty cycle**. At
the intended use, a handful of problem-triggered captures a day, **compute runs about 8× the data
cost**, and stays there however many pods you add.

Two things this changes about how you scope:

- **The EdgeConnect node is a small-deployment tax, not a scaling cost.** Flat $49/month means
  58% of the bill at 10 pods and rounding error at 500. Don't extrapolate from it.
- **Sample rate and scope move the crossover far harder than fleet size does**, and they compound:
  unscoped at 99 Hz, ingest wins at *one* session per pod per day.

Query cost sits outside this entirely — it scales with views and retention, not pods, and
unbounded it can exceed both other lines at any scale. See lever 3 below.

## The four levers, in order of leverage

1. **Session gating.** On-demand vs always-on is 240×. It is already built; do not disable it.
2. **Scope the collector to your services.** The eBPF DaemonSet profiles every process on the
   node. Unfiltered, expect ~10× the records — you are paying to profile `kubelet`.
3. **Bound every query by `profile.session_id`.** Unbounded, a single view scans the whole
   retained bucket. At 100 pods on a 7-day bucket that is ~$1.48 per run; on a one-minute
   dashboard refresh, ~$2,100/day. The viewer bounds its queries. Anything you build must too.
   This is the only line item in the model that can run away from you.
4. **Sample rate.** 19 Hz vs 99 Hz is ~5× the eBPF half. 19 Hz was enough to resolve 12-frame
   call chains end to end in testing; there was no accuracy reason to go higher.

Note the ordering: three of the four are configuration you set once, and none of them trade
against fidelity except the last.

---

## What this model deliberately leaves out

| Excluded | Why |
|---|---|
| **Your existing cluster** | Control plane, nodes, EBS, data transfer. You were paying these anyway; only the EdgeConnect node is incremental. |
| **EventPipe's memory against your app** | `BufferSizeInMB: 128` is charged to the *application* container, not the sidecar, so it does not appear as profiler cost — but it can OOMKill the workload it observes. Budget for it in the app's limit. This is a capacity risk the model cannot price for you. |
| **CI** | Multi-arch container builds. Free on public GitHub repos, billed against your plan on private ones. |
| **Dynatrace licensing beyond data** | Host units, OneAgent, whatever else your contract covers. |

---

## Where the model is soft

Read these before quoting a number to anyone.

- **Duration scaling over-estimates.** Records are unique `(stack, thread)` groups, and unique
  stacks *saturate* — a 180-second session does not produce twice the records of a 90-second one,
  because most of the extra samples land on stacks already seen. The model scales linearly anyway,
  which makes long sessions an upper bound rather than an estimate.
- **`F = 10` for unscoped collection is an order-of-magnitude judgement**, not a measurement. It
  depends entirely on what else runs on your nodes.
- **Byte counts are approximated** as folded-stack length + 400, per the caveat above.
- **The baseline is one workload.** A service with deeper stacks, more threads, or heavier
  contention will produce more per session. Re-measure with the DQL above once you have real
  traffic — it takes one session to replace every number here with your own.

---

## Interactive calculator

An interactive version of this model, with these defaults pre-loaded:

**https://cooperjfecteau-cell.github.io/otlp-dotnet-alpine-musl-profiler/cost-model.html**

Same formula, same measured baselines, with the query and scope warnings wired to fire on the
conditions that actually cost money.

It ships in this repo as [`docs/cost-model.html`](cost-model.html) — a single self-contained file
with no external assets, so you can also open it straight from disk, or hand it to someone who has
neither the repo nor a Dynatrace login.

**This document is the source of truth.** The page carries its own copy of the constants; if you
re-measure your baseline, change both.
