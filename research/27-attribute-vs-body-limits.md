# Where the folded stack goes: attribute or body?

**Resolves [#27](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/27).**
Research #3 and #6 disagreed. This settles it by measurement against the live tenant rather
than by re-reading docs.

## Method

A Kubernetes Job (`probe-limits` namespace, `python:3.12-alpine`) using the Python
OpenTelemetry SDK — chosen because Dynatrace's OTLP endpoint is protobuf-only, and the SDK
speaks protobuf natively. For each size *S* it emitted two records:

- one carrying an *S*-char **body**
- one carrying an *S*-char **attribute** (`probe.payload`)

Each payload ended in a distinctive marker (`ENDOFBODYMARKER` / `ENDOFATTRMARKER`) so that
truncation could be detected by **tail survival**, not merely by measuring length — a value
truncated to the limit still *looks* plausible if you only check its size.

Sizes: 1024 → 524288, doubling, plus 2560 to bracket research #6's claimed ~2,500 B cap.

Every record was accepted by the exporter. Nothing was rejected. What differed was what
survived, read back with DQL.

## Result

| Sent | Attribute length stored | Attr tail intact | Body length stored | Body tail intact |
|---:|---:|:--|---:|:--|
| 1,024 | 1,023 | yes | 1,023 | yes |
| 2,048 | 2,047 | yes | 2,047 | yes |
| 2,560 | 2,559 | yes | 2,559 | yes |
| 4,096 | 4,095 | yes | 4,095 | yes |
| 8,192 | 8,191 | yes | 8,191 | yes |
| 16,384 | 16,383 | yes | 16,383 | yes |
| 32,768 | 32,767 | yes | 32,767 | yes |
| 65,536 | **32,768** | **NO** | 65,535 | yes |
| 131,072 | **32,768** | **NO** | 131,071 | yes |
| 262,144 | **32,768** | **NO** | 262,143 | yes |
| 524,288 | **32,768** | **NO** | 524,287 | yes |

## Findings

**Attribute values cap at exactly 32,768 characters.** Research #3 was right; research #6's
~2,500 B figure is wrong for this tenant.

**Body holds at least 524,287 characters** — every size tested survived intact. Consistent
with the 524,288 B figure in #6.

**Truncation is silent.** This is the finding that matters. Oversized attributes are not
rejected, do not produce a warning, and do not set any flag. The record ingests successfully
and the value is simply cut at 32,768. Read back, a truncated stack is indistinguishable
from a genuine one unless you already know what you sent.

For profiling that is the worst available failure mode: the stacks that overflow are the
**deepest** ones, which are exactly the interesting ones. A flame graph built from silently
truncated data looks healthy while systematically under-representing deep call paths.

## Decision: attribute, with an explicit overflow guard

Put the folded stack in **`profile.stack.folded`, an attribute**, because:

- 32 KB is ample for realistic .NET stacks. The 29-frame ASP.NET stack observed in #7 folds
  to roughly 1.7 KB; even a 100-frame stack at 100 chars per frame is ~10 KB. Headroom is
  roughly 3–20x.
- Attributes are directly queryable in DQL. The flame-graph query in #13 needs to filter and
  group on the stack; doing that against the body means string-parsing prose on every query,
  on the signal that is 94% of the bill.
- It keeps the record shaped like an OTLP `Sample` (#3), which is the entire reason for
  mirroring the profiles model.

**The guard is not optional.** Because truncation is silent, the exporter must own it:

1. Measure the folded string before emitting.
2. If it exceeds a threshold below the ceiling — **30,000 chars**, leaving margin — truncate
   *deliberately*, from the **root end**, preserving the leaf frames that carry the hotspot.
3. Set `profile.stack.truncated = true` and `profile.stack.original_depth = <n>`.
4. Never let the platform do the cutting.

That converts an invisible data-quality failure into a visible, queryable one. Any flame
graph rendered from truncated records can then say so.

**Fallback if a workload genuinely exceeds 32 KB folded** — deep recursion is the realistic
case — is to move that record's stack to the body and set `profile.stack.in_body = true`.
The body has 16x the headroom and the viewer can handle the rare case. Not needed for v1, but
worth designing the flag in now rather than retrofitting.

## Reproducing

`probe.py` and the job manifest are in the scratchpad, not committed — the probe is a
one-shot measurement, not part of the build. The DQL that read the results:

```
fetch logs, from:-40m
| filter probe.run == "<run id>"
| fields kind = probe.kind,
         sent = toLong(probe.bytes),
         body_len = stringLength(content),
         attr_len = stringLength(probe.payload),
         body_tail_ok = contains(content, "ENDOFBODYMARKER"),
         attr_tail_ok = contains(probe.payload, "ENDOFATTRMARKER")
| sort kind asc, sent asc
```

Note the probe records landed in `default_logs`, not `profiling_dotnet_7d` — no OpenPipeline
routing exists yet (#21), which is itself a small confirmation of #6's finding that a record
cannot select its own bucket.
