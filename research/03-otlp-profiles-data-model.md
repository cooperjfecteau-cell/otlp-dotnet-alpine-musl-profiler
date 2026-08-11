# OTLP Profiles Data Model (alpha) and its Mapping onto a Log Record

Resolves wayfinder research ticket #3. This file is the schema source of truth for the
exporter, the DQL, and the viewer. Build tickets should read the mapping table in
section 6 and the "no equivalent" rulings in section 7 and implement them literally.

**Researched**: 2026-08-11. **Pinned against**: `opentelemetry-proto` v1.11.0
(2026-07-21), which is byte-identical in field layout to v1.10.0 (2026-03-09, the public
alpha release). Sources in section 11.

---

## 1. Version status - read this before trusting any field name

| Fact | Value | Consequence for us |
|---|---|---|
| Proto release carrying the March 2026 alpha | `v1.10.0`, published 2026-03-09 | This is the "March 2026 alpha" the ticket asks about. |
| Latest proto release | `v1.11.0`, published 2026-07-21 | Adds `ProcessContext` (a *separate* proto, not part of OTLP). Profiles field layout unchanged. |
| Package path | `opentelemetry.proto.profiles.v1development` | Still **not** `v1`. The repo drops the `development` suffix only at release-candidate. |
| Message status markers | Every message carries `// Status: [Alpha]` | Field names and numbers may still change. |
| Spec doc status | `specification/profiles/data-format.md` = **Alpha**; semconv `docs/general/profiles.md` = **Development** | Semantic conventions for profiles are thinner than the proto. |
| OTLP HTTP path for profiles | `POST /v1development/profiles` | A future native swap changes the endpoint, not just the payload. |

**Diff between the alpha (v1.10.0) and current main**: verified by direct diff of
`profiles.proto` at both refs. The changes are **entirely documentation**: RFC-2119
keyword tightening, `Status: [Alpha]` markers added, cardinality labels in the ASCII
relationship diagram corrected (`1-n` -> `n-n` in three places), the `Sample` identity
comment promoted out of the message body, and a note that `link_table[0]` should use
16/8 zero-filled byte arrays rather than empty ones. **No field was added, removed,
renamed, or renumbered.** The layout below is therefore stable across the whole alpha
window to date.

**Because the model is Alpha, every record we emit MUST be self-describing about which
version it mirrors.** Emit `profile.schema_version` (see section 6) so that a future
migration can tell v1development-shaped records from whatever ships as v1.

---

## 2. The model in one page

```
ProfilesData
  |-- dictionary : ProfilesDictionary   <-- ONE per payload, all sharing happens here
  |     mapping_table[]   : Mapping
  |     location_table[]  : Location -> Line[] -> function_index
  |     function_table[]  : Function
  |     link_table[]      : Link (trace_id, span_id)
  |     string_table[]    : string
  |     attribute_table[] : KeyValueAndUnit (key, AnyValue, unit)
  |     stack_table[]     : Stack (location_indices[], LEAF FIRST)
  |
  '-- resource_profiles[] : ResourceProfiles
        resource (Resource.attributes -> service.name, host.name, container.id, ...)
        scope_profiles[]  : ScopeProfiles
          scope (InstrumentationScope)
          profiles[]      : Profile
            sample_type, period_type, period, time_unix_nano, duration_nano, profile_id
            samples[]     : Sample
              stack_index -> stack_table
              attribute_indices[] -> attribute_table   (thread.id, thread.name live HERE)
              link_index -> link_table                 (trace/span linkage lives HERE)
              values[] : int64        (per-observation)
              timestamps_unix_nano[]  (per-observation)
```

Three structural facts drive everything downstream:

1. **Everything except the outer hierarchy is an integer index into a payload-scoped
   dictionary.** `Sample` -> `Stack` -> `Location` -> `Line` -> `Function` is four
   levels of indirection. Those integers are meaningless outside the single
   `ProfilesData` message that carried them.
2. **Index 0 is reserved as the null/zero value in every table.** `string_table[0]` MUST
   be `""`, `link_table[0]` MUST be `Link{}`, etc. So `link_index: 0` means "no trace
   linkage" - it is a legitimate, spec-blessed absence, not missing data.
3. **A Sample's identity is the tuple `{stack_index, set_of(attribute_indices),
   link_index}`.** The proto says samples with the same identity SHOULD be merged by
   appending to `values` and `timestamps_unix_nano`. `values` and `timestamps` are
   explicitly *not* part of identity. **This is the definition our record grain must
   mirror** - see section 8.

---

## 3. Complete field inventory

Field numbers included so the exporter can be written against the wire format directly.

### `ProfilesDictionary`
| # | Field | Type | Notes |
|---|---|---|---|
| 1 | `mapping_table` | `repeated Mapping` | `[0]` MUST be `Mapping{}` |
| 2 | `location_table` | `repeated Location` | `[0]` MUST be `Location{}` |
| 3 | `function_table` | `repeated Function` | `[0]` MUST be `Function{}` |
| 4 | `link_table` | `repeated Link` | `[0]` MUST be `Link{}`, with 16/8 zero-filled byte arrays |
| 5 | `string_table` | `repeated string` | `[0]` MUST be `""` |
| 6 | `attribute_table` | `repeated KeyValueAndUnit` | `[0]` MUST be `KeyValueAndUnit{}` |
| 7 | `stack_table` | `repeated Stack` | `[0]` MUST be `Stack{}` |

Duplicates SHOULD NOT appear; orphans SHOULD NOT appear; a processor MAY garbage-collect
either without observable effect.

### `ProfilesData`
| # | Field | Type |
|---|---|---|
| 1 | `resource_profiles` | `repeated ResourceProfiles` |
| 2 | `dictionary` | `ProfilesDictionary` |

### `ResourceProfiles`
| # | Field | Type |
|---|---|---|
| 1 | `resource` | `opentelemetry.proto.resource.v1.Resource` |
| 2 | `scope_profiles` | `repeated ScopeProfiles` |
| 3 | `schema_url` | `string` |

(`reserved 1000;` - do not reuse.)

### `ScopeProfiles`
| # | Field | Type |
|---|---|---|
| 1 | `scope` | `InstrumentationScope` |
| 2 | `profiles` | `repeated Profile` |
| 3 | `schema_url` | `string` |

### `Profile`
| # | Field | Type | Notes |
|---|---|---|---|
| 1 | `sample_type` | `ValueType` | **Singular.** One value type per Profile in the alpha. e.g. `("samples","count")` |
| 2 | `samples` | `repeated Sample` | |
| 3 | `time_unix_nano` | `fixed64` | Collection start, UTC ns since epoch |
| 4 | `duration_nano` | `uint64` | Window length; may be 0 for instant profiles |
| 5 | `period_type` | `ValueType` | e.g. `("cpu","nanoseconds")` |
| 6 | `period` | `int64` | Distance between sampled occurrences, in `period_type` units |
| 7 | `profile_id` | `bytes` | 16 bytes; all-zero is invalid; optional at source, may be assigned later |
| 8 | `dropped_attributes_count` | `uint32` | |
| 9 | `original_payload_format` | `string` | e.g. `"jfr"`, `"pprof"`, `"linux_perf"`. MUST be set together with field 10 |
| 10 | `original_payload` | `bytes` | Raw source-format blob. MUST be set together with field 9 |
| 11 | `attribute_indices` | `repeated int32` | -> `attribute_table` |

Fields 3-12 are declared "informational, do not affect interpretation of results".

### `Sample`
| # | Field | Type | Notes |
|---|---|---|---|
| 1 | `stack_index` | `int32` | -> `stack_table` |
| 2 | `attribute_indices` | `repeated int32` | -> `attribute_table`; keys MUST be unique within one Sample |
| 3 | `link_index` | `int32` | -> `link_table`; **0 means no link** |
| 4 | `values` | `repeated int64` | per-observation, units from `Profile.sample_type` |
| 5 | `timestamps_unix_nano` | `repeated fixed64` | per-observation, SHOULD fall in `[time_unix_nano, time_unix_nano + duration_nano)` |

A Sample MUST have at least one entry in `values` or `timestamps_unix_nano`. If both are
populated they MUST be the same length and index-aligned. Three legal "shapes" are
spelled out in the proto:

- timestamps only, `values: []` -> consumer assumes value 1 per timestamp
- **single aggregated value, `values: [10]`, `timestamps: []`** <- the shape we mirror
- per-timestamp values, `values: [2,2,3,3]`, `timestamps: [1,2,3,4]`

All Samples in one Profile SHOULD use the same shape.

### `Stack`
| # | Field | Type | Notes |
|---|---|---|---|
| 1 | `location_indices` | `repeated int32` | **The first location is the LEAF frame.** `main -> foo -> bar` encodes as `[bar, foo, main]` |

### `Location`
| # | Field | Type | Notes |
|---|---|---|---|
| 1 | `mapping_index` | `int32` | 0 = unknown/not applicable |
| 2 | `address` | `uint64` | Instruction address, SHOULD be within the mapping's range |
| 3 | `lines` | `repeated Line` | Multiple lines = inlined functions; **last entry is the outermost caller** |
| 4 | `attribute_indices` | `repeated int32` | Where `profile.frame.type` lives |

### `Line`
| # | Field | Type |
|---|---|---|
| 1 | `function_index` | `int32` |
| 2 | `line` | `int64` (1-based, 0 = unset) |
| 3 | `column` | `int64` (1-based, 0 = unset) |

### `Function`
| # | Field | Type | Notes |
|---|---|---|---|
| 1 | `name_strindex` | `int32` | |
| 2 | `system_name_strindex` | `int32` | e.g. C++ mangled name |
| 3 | `filename_strindex` | `int32` | |
| 4 | `start_line` | `int64` | 1-based, 0 = unset |

At least one of `{name_strindex, system_name_strindex, filename_strindex}` MUST be
present.

### `Mapping`
| # | Field | Type |
|---|---|---|
| 1 | `memory_start` | `uint64` |
| 2 | `memory_limit` | `uint64` |
| 3 | `file_offset` | `uint64` |
| 4 | `filename_strindex` | `int32` |
| 5 | `attribute_indices` | `repeated int32` |

**A `Mapping` MUST carry at least one of** `process.executable.build_id.gnu`,
`process.executable.build_id.go`, `process.executable.build_id.htlhash`. The spec
recommends `htlhash` be present in *every* mapping, and it says why in terms that are
directly our problem: "In some environments GNU and/or Go build_id values are stripped
or not usable - for example Alpine Linux which is often used as a base for Docker
environments." The algorithm is
`SHA256(File[:4096] || File[-4096:] || BigEndianUInt64(len(File)))[:16]`, hex-encoded.

### `Link`
| # | Field | Type |
|---|---|---|
| 1 | `trace_id` | `bytes` (16) |
| 2 | `span_id` | `bytes` (8) |

### `ValueType`
| # | Field | Type |
|---|---|---|
| 1 | `type_strindex` | `int32` |
| 2 | `unit_strindex` | `int32` |

### `KeyValueAndUnit` - the attribute-units mechanism
| # | Field | Type | Notes |
|---|---|---|---|
| 1 | `key_strindex` | `int32` | |
| 2 | `value` | `AnyValue` | |
| 3 | `unit_strindex` | `int32` | **0 = unit implicit-by-semconv or undefined.** If present, SHOULD be UCUM |

This is the profiles-specific attribute encoding. It exists on `Profile`, `Sample`,
`Mapping` and `Location` only. `Resource` and `InstrumentationScope` use ordinary
`KeyValue` (with a profiles extension letting keys/string-values be string-table
references). **OTLP logs have no unit slot on an attribute at all** - see section 7.8.

---

## 4. Semantic conventions actually defined for profiles

Very little is standardized. The complete list from
`semantic-conventions/model/profile/registry.yaml` and `docs/general/profiles.md`:

- **`profile.frame.type`** (Recommended, string, Location-level). Well-known values:
  `beam`, `cpython`, **`dotnet`**, `go`, `jvm`, `kernel`, `luajit`, `native`, `perl`,
  `php`, `ruby`, `rust`, `v8js`. If one applies it MUST be used.
- **`pprof.*`** compatibility attributes: `pprof.location.is_folded`,
  `pprof.mapping.has_{filenames,functions,inline_frames,line_numbers}`,
  `pprof.profile.{comment,doc_url,drop_frames,keep_frames}`,
  `pprof.scope.{default_sample_type,sample_type_order}`. Not relevant to us.

Notably **there is no profiles semconv for thread or process identity.** The de-facto
convention comes from the reference implementation, the OTel eBPF Profiler
(`reporter/internal/pdata/generate.go`), which sets:

| Level | Keys it sets |
|---|---|
| Resource | `service.name`, `container.id`, `process.pid`, `process.executable.path`, `process.executable.name`, `process.environment_variable.<k>` |
| Sample | `thread.name`, `thread.id`, `cpu.logical_number`, `process.context.label.<k>` |
| Location | `profile.frame.type` |
| Mapping | `process.executable.build_id.{gnu,go,htlhash}` |

**We should copy this placement exactly.** `thread.id` being a *Sample* attribute rather
than a Resource attribute is the single most important thing to inherit, and it is what
forces the grain discussion in section 8.

---

## 5. What a log record can actually hold

`opentelemetry/proto/logs/v1/logs.proto`, `LogRecord`:

| # | Field | Type |
|---|---|---|
| 1 | `time_unix_nano` | `fixed64` |
| 11 | `observed_time_unix_nano` | `fixed64` |
| 2 | `severity_number` | enum |
| 3 | `severity_text` | `string` |
| 5 | `body` | `AnyValue` |
| 6 | `attributes` | `repeated KeyValue` |
| 7 | `dropped_attributes_count` | `uint32` |
| 8 | `flags` | `fixed32` |
| 9 | `trace_id` | `bytes` (16) |
| 10 | `span_id` | `bytes` (8) |
| 12 | `event_name` | `string` |

That is the entire target surface: one timestamp, one body, a flat string-keyed
attribute map with no units, and native 16/8-byte trace/span slots.

Dynatrace ingest constraints that bear on the schema (from Dynatrace LMA default limits
and the OTLP logs ingest doc):

- Endpoint `POST /api/v2/otlp/v1/logs`.
- Resource, scope and record attributes are all **flattened into one attribute map** on
  the resulting log record. Prefixes are our only namespacing.
- `trace_id`/`span_id` are mapped to Grail `trace_id`/`span_id` and hex-encoded.
- `dt.auth.origin` is appended automatically to every record.
- Attribute **key <= 100 bytes**; attribute **value <= 32 kB**; **<= 500 attributes**
  per record; **<= 32 values** in a multi-value attribute; nesting <= 5 levels.
- 50,000 records per request; request payload limit in the low tens of MB.
- Records older than 24h are dropped; timestamps more than 10 min in the future are
  reset to now.

The 32 kB attribute-value ceiling comfortably accommodates a folded stack (a 200-frame
.NET stack of 60-char names is ~12 kB), which validates putting the folded stack in an
attribute rather than the body. *Verify these numbers against the tenant during the
ingest ticket - the docs give differing body-size figures in different places.*

---

## 6. The mapping table

Naming rule: **use the semconv key where one exists; otherwise use `profile.<proto field
path>` with the proto's own field name.** Mechanical mirroring is the whole point - do
not invent friendlier names.

Record grain: **one log record per unique `(stack, thread)` per profiling window** (see
section 8). `event_name = "profile.sample"`.

### 6.1 Envelope

| OTLP profiles field | Log record slot | Type | Notes |
|---|---|---|---|
| - | `LogRecord.event_name` | string | `"profile.sample"`. Discriminator; also emit as attribute `event.name` if Dynatrace does not surface the native field. |
| - | `LogRecord.body` | string | Short human summary, e.g. `"cpu 42 samples in Leaf.Method"`. **Not** the folded stack. |
| - | `LogRecord.severity_number` / `severity_text` | enum/string | `9` / `"INFO"`. No profiles equivalent; constant. |
| `Profile.time_unix_nano` | `LogRecord.time_unix_nano` | fixed64 | Window start. All records in a window share it. |
| - | `LogRecord.observed_time_unix_nano` | fixed64 | Exporter emit time. |
| - | `profile.schema_version` | string | `"otlp-profiles-v1development/1.11.0"`. Mandatory; makes records self-describing when the model moves. |

### 6.2 Resource attributes (mandatory join keys)

All from `ResourceProfiles.resource.attributes`, all standard semconv, all present on
every record.

| OTLP profiles field | Log attribute | Type | Notes |
|---|---|---|---|
| `Resource.attributes["service.name"]` | `service.name` | string | |
| `Resource.attributes["host.name"]` | `host.name` | string | |
| `Resource.attributes["k8s.pod.name"]` | `k8s.pod.name` | string | |
| `Resource.attributes["k8s.namespace.name"]` | `k8s.namespace.name` | string | |
| `Resource.attributes["container.id"]` | `container.id` | string | Set by the eBPF profiler reference impl at resource level. |
| `Resource.attributes["process.pid"]` | `process.pid` | long | |
| `Resource.attributes["process.executable.name"]` | `process.executable.name` | string | Recommended, cheap. |
| `Resource.attributes["process.executable.path"]` | `process.executable.path` | string | Optional. |
| `ResourceProfiles.schema_url` | `otel.schema_url` | string | Optional; constant per exporter build. |

### 6.3 Scope

| OTLP profiles field | Log attribute | Type | Notes |
|---|---|---|---|
| `ScopeProfiles.scope.name` | `otel.scope.name` | string | Our exporter's name. |
| `ScopeProfiles.scope.version` | `otel.scope.version` | string | |

### 6.4 Profile header (denormalized onto every record)

| OTLP profiles field | Log attribute | Type | Notes |
|---|---|---|---|
| `Profile.profile_id` (bytes16) | `profile.id` | string | 32 lowercase hex chars. Generate one per window per process. |
| - | `profile.session_id` | string | **Ours, not OTLP.** The on-demand profiling session. No OTLP equivalent; see section 9. |
| `Profile.duration_nano` | `profile.duration_nano` | long | Window length in ns. With `LogRecord.time_unix_nano` this reconstructs the window. |
| `Profile.sample_type.type_strindex` -> string | `profile.sample_type` | string | e.g. `"samples"` |
| `Profile.sample_type.unit_strindex` -> string | `profile.sample_unit` | string | e.g. `"count"` |
| `Profile.period_type.type_strindex` -> string | `profile.period_type` | string | e.g. `"cpu"` |
| `Profile.period_type.unit_strindex` -> string | `profile.period_unit` | string | e.g. `"nanoseconds"` |
| `Profile.period` | `profile.period` | long | In `period_type` units. For a 100 Hz sampler: `10000000`. |
| `Profile.dropped_attributes_count` | `profile.dropped_attributes_count` | long | Omit when 0. |
| `Profile.original_payload_format` | `profile.original_payload_format` | string | Set to `"nettrace"`. Documents provenance for ~20 bytes. |
| `Profile.original_payload` | - | - | **No equivalent - do not send.** See 7.7. |
| `Profile.attribute_indices` | (resolved) | - | Resolve to the flat attributes above. Never emit indices. |

### 6.5 Sample

| OTLP profiles field | Log attribute | Type | Notes |
|---|---|---|---|
| `Sample.stack_index` | `profile.stack.hash` | string | Our surrogate for the dictionary index. Lowercase hex of a stable 64-bit hash (xxh64 or SHA-256 truncated) of the exact folded string. **Hash the stack only - not the thread** so DQL can group a stack across threads. |
| `Stack.location_indices` (resolved) | `profile.stack.folded` | string | `root;mid;leaf`. **Root-first - reverse the OTLP order.** Separator `;`. See 7.5 for what a frame token contains. |
| `Stack.location_indices.length` | `profile.stack.depth` | long | Cheap, saves splitting the string in DQL. |
| `Sample.values` (aggregated shape, 1 element) | `profile.sample.value` | long | The count or duration for this (stack, thread) in this window. Unit is `profile.sample_unit`. |
| `Sample.timestamps_unix_nano` | - | - | **No equivalent.** See 7.3. |
| `Sample.link_index` -> `Link.trace_id` | `LogRecord.trace_id` | bytes16 | **Left unset.** See section 9. |
| `Sample.link_index` -> `Link.span_id` | `LogRecord.span_id` | bytes8 | **Left unset.** See section 9. |
| `Sample.attribute_indices["thread.id"]` | `thread.id` | long | OS thread id / .NET managed thread id - pick one and document it. Mandatory join key. |
| `Sample.attribute_indices["thread.name"]` | `thread.name` | string | From EventPipe thread name where available. |
| `Sample.attribute_indices["cpu.logical_number"]` | `cpu.logical_number` | long | Only if EventPipe exposes it; omit otherwise rather than faking. |

### 6.6 Frame-level fields

| OTLP profiles field | Log attribute | Type | Notes |
|---|---|---|---|
| `Location.attribute_indices["profile.frame.type"]` | `profile.frame.type` | string | `"dotnet"`. Emit as a single value while every frame in the stack is managed. If mixed managed/native stacks appear, drop the record-level attribute and encode per-frame in the folded token (7.5). |
| `Function.name_strindex` -> string | (in `profile.stack.folded`) | - | The frame token. |
| `Function.system_name_strindex` | - | - | No equivalent at sample grain. See 7.6. |
| `Function.filename_strindex`, `Function.start_line`, `Line.line`, `Line.column` | - | - | No equivalent at sample grain. See 7.6. |
| `Location.address`, `Mapping.memory_start`, `Mapping.memory_limit`, `Mapping.file_offset` | - | - | No equivalent. See 7.5. |
| `Mapping.filename_strindex`, `Mapping.attribute_indices[build_id.*]` | - | - | Only needed for unsymbolized frames. See 7.5. |

### 6.7 Every `*_strindex`, `*_index`, `*_indices` field

**No equivalent by construction. Resolve and drop.** See 7.1.

---

## 7. Fields with no sensible log-record equivalent, and what we do instead

### 7.1 All dictionary index fields (`*_strindex`, `*_index`, `*_indices`)
These are integer offsets into tables scoped to a single `ProfilesData` message. Once we
split a profile into independent log records, the tables are gone and the integers are
noise - worse than noise, because they look meaningful.
**Do**: resolve every index to its value at export time. Never persist a raw index. The
one exception is `Sample.stack_index`, replaced by the content-addressed
`profile.stack.hash`, which is stable across windows and across pods - strictly better
than the index for our purposes.

### 7.2 `ProfilesDictionary` itself
Cross-record deduplication has no analogue in a log stream; each log record is
self-contained. OTel claims roughly 40% wire-size reduction from the shared string
dictionary, and we forfeit all of it.
**Do**: accept the denormalization and control volume at the grain instead - one record
per unique `(stack, thread)` per window, never one per raw sample. A 100 Hz profiler on
8 threads for 60 s produces 48,000 raw samples but typically a few hundred unique
`(stack, thread)` pairs. If volume still bites, the escape hatch is the tier-2 frame
dictionary in 7.6, not re-introducing indices.

### 7.3 `Sample.timestamps_unix_nano` (repeated fixed64)
A log record has exactly one timestamp. There is no faithful way to carry an array of
per-observation timestamps.
**Do**: adopt the spec's second blessed shape - "single aggregated value without
timestamps" (`values: [n]`, `timestamps: []`). Set `LogRecord.time_unix_nano` to
`Profile.time_unix_nano` (the window start) and carry `profile.duration_nano`. This is
lossy in exactly one way: we cannot see *when within the window* a stack was on-CPU. That
is an accepted cost of the windowed design, and it round-trips cleanly - a future native
exporter emits `values:[n], timestamps:[]` and loses nothing relative to what we stored.
**Do not** emit an array attribute of timestamps: Dynatrace caps multi-value attributes
at 32 entries, which a 60 s window blows through immediately.

### 7.4 `Sample.values` (repeated int64)
In the alpha a `Profile` has a *singular* `sample_type`, so in the aggregated shape
`values` is one element. Multi-element `values` only arises in per-timestamp shape.
**Do**: `profile.sample.value` as a scalar long. If we later emit a second measure (GC
allocation bytes, contention time), emit a **separate record stream with its own
`profile.sample_type`** - mirroring OTLP's "one Profile per sample type" - rather than
widening the attribute into an array.

### 7.5 `Location.address`, `Mapping.memory_start` / `memory_limit` / `file_offset`, `Mapping.filename`, build IDs
Per-frame uint64 address data. There is no per-frame slot on a log record, and inlining
addresses into every record explodes size and cardinality.
**Do**: for managed .NET frames from EventPipe, symbolization happens in-process, so
these fields carry no information we need - **omit them entirely**. If unsymbolized
frames appear (native/JIT stubs), encode the frame token in the folded string as
`<build_id_htlhash>+0x<file_offset_hex>` so a later symbolication pass has what it needs,
and set `profile.frame.type` handling per 6.6. If we ever do emit `Mapping` data,
`process.executable.build_id.htlhash` is the required one - the spec defines that
algorithm specifically because Alpine strips GNU build IDs, which is precisely our target
platform.

### 7.6 `Function.system_name` / `filename` / `start_line`, `Line.line` / `column`
Per-frame source metadata. Same problem: no per-frame slot.
**Do**: v1 omits them; the folded string carries `Namespace.Type.Method` only, which is
enough for a flame graph. If the viewer later needs file/line, add a **tier-2 record
stream**: `event_name = "profile.frame"`, one record per unique frame per session,
keyed by a `profile.frame.id` that the folded token references. That is the log-record
analogue of `ProfilesDictionary` - dedup once per session rather than per sample - and it
is deliberately deferred, not designed in now.

### 7.7 `Profile.original_payload` / `original_payload_format`
`original_payload` is an arbitrary-size byte blob (the raw nettrace).
**Do**: never send the blob. A nettrace for a 60 s window is megabytes; log ingest is the
wrong transport and it would be opaque in Grail anyway. **Do** send
`profile.original_payload_format = "nettrace"` - it is 20 bytes and records provenance
faithfully. If the raw nettrace must be retained, it belongs in object storage with the
key carried as an attribute; that is a separate decision, not this schema's.

### 7.8 `KeyValueAndUnit.unit_strindex` - attribute units
This is the one genuinely novel thing in the profiles model, and **OTLP logs have no
equivalent whatsoever**: `LogRecord.attributes` is `repeated KeyValue`, key plus value,
no unit field. UCUM units cannot be attached to a log attribute.
**Do**: mirror only where the model actually defines a unit - the two `ValueType` pairs -
as sibling attributes: `profile.sample_type`/`profile.sample_unit` and
`profile.period_type`/`profile.period_unit`. Do **not** invent a `<attr>.unit` companion
for every attribute; that doubles the attribute count against a 500-attribute ceiling for
information nothing consumes. If we ever emit a unit-bearing custom attribute (the
spec's own example is `"allocation_size": 128 By`), encode the unit in the key suffix
(`profile.allocation_size_bytes`) and note it in this file.

### 7.9 The Resource / Scope / Record attribute split
OTLP profiles keeps resource attributes structurally separate; Dynatrace flattens
resource, scope and record attributes into one map on ingest.
**Do**: nothing structural, but keep prefixes disjoint so nothing collides after
flattening. A native-profiles migration re-splits them by prefix, which is why the
resource-level keys in 6.2 are all unprefixed semconv names and everything of ours is
under `profile.`.

### 7.10 `ResourceProfiles.schema_url` / `ScopeProfiles.schema_url`
Constant per exporter build; carries no per-record information.
**Do**: emit `otel.schema_url` once if convenient, or omit. Low value either way.

---

## 8. Record grain - the one place the settled design needs a wording fix

The settled constraint reads "one log record per unique stack per window" **and**
"`thread.id` mandatory on every record". Those two cannot both hold: if a stack is
observed on five threads, either we emit five records (so the grain includes the thread)
or one record whose `thread.id` is arbitrary.

The spec resolves this for us. `Sample` identity is
`{stack_index, set_of(attribute_indices), link_index}`, and in the reference
implementation `thread.id` and `thread.name` are **Sample attributes**. So in OTLP's own
model, two samples on different threads with the same stack are *different Samples*.

**Recommendation - a wording change, not a design change**: restate the grain as **one
log record per unique `(stack, thread)` per window**. Keep `profile.stack.hash` a hash of
the *stack alone*, so `summarize sum(profile.sample.value), by:{profile.stack.hash}`
still collapses across threads at query time and the flame-graph query is unchanged.

This is the faithful mirror: our record *is* an OTLP `Sample`. When the transport swaps,
one record becomes one `Sample` with no restructuring - which is exactly the property the
whole exercise is buying.

Note the same logic applies to any other Sample-level attribute we add
(`cpu.logical_number`, future `process.context.label.*`): adding one to the record
widens the grain. Add sample-level attributes deliberately.

---

## 9. The trace/span slots

The alpha's per-sample linkage is `Sample.link_index` -> `link_table[i]` ->
`Link{trace_id, span_id}`. There is nothing else - no per-sample trace context anywhere
else in the model.

**The settled decision to leave these NULL is correct and the spec supports it
positively, not merely tolerantly.** `link_table[0]` is mandated to be the zero `Link{}`,
and `link_index: 0` is defined as "no link exists". An unlinked sample is a first-class,
legal state - the reference eBPF profiler emits exactly that whenever it has no APM
trace/span for the sampled thread. We are not omitting a required field; we are using the
model's own null.

**Implementation**: leave `LogRecord.trace_id` and `LogRecord.span_id` **unset** (do not
write 16/8 zero-filled arrays, and do not put them in attributes). Dynatrace maps these
native fields to Grail `trace_id`/`span_id` hex, so the slots are reserved and correctly
typed for the day we can fill them, at zero cost now.

**How the spec intends these to get filled, for the record**: OTEP 4719 (Process Context,
merged; `ProcessContext` message shipped in proto v1.11.0) plus **OTEP 4947 (Thread
Context)**. OTEP 4947 defines an ELF thread-local `otel_thread_ctx_v1` pointing at a
28-byte-header record carrying `trace-id[16]`, `span-id[8]`, a `valid` byte, trace-flags,
and an appended custom-attribute buffer, which an out-of-process reader samples while the
thread is stopped. That is the sanctioned path from "a profiler sees a thread" to "the
sample carries a span id".

Three reasons this does not change our plan: it requires a native TLS component in the
SDK and .NET has no such implementation; the OTEP is explicitly optional for SDKs and
still carries open questions; and its own fallback story is thread-level attributes -
i.e. exactly the `thread.id` join we are building. **Guessing span ids would be inventing
data the spec deliberately makes an SDK responsibility.** Thread-level correlation joined
in DQL is the right call, and the migration path is additive: fill `trace_id`/`span_id`
when the SDK can supply them, change nothing else.

## 9.1 `profile.session_id` has no OTLP counterpart

Our on-demand session concept does not exist in the model. The nearest thing is
`Profile.profile_id`, which is per-profile (i.e. per window), not per session. Keep both:
`profile.id` mirrors `profile_id` faithfully, and `profile.session_id` is ours, groups
windows into one triggered session, and would survive a native migration as a
`Profile`-level `KeyValueAndUnit` attribute. Flag: `profile.*` is a namespace OTel owns
(it currently defines only `profile.frame.type`), so a future `profile.session_id` in
semconv could collide. The risk is low and the mirroring benefit is high - keep the name,
but pin our full `profile.*` list in this file (section 6) and re-check it against semconv
at each proto bump.

---

## 10. Worked example record

```json
{
  "timeUnixNano": "1770000000000000000",
  "observedTimeUnixNano": "1770000060123000000",
  "eventName": "profile.sample",
  "severityNumber": 9,
  "severityText": "INFO",
  "body": { "stringValue": "cpu 42 samples in OrderService.Checkout" },
  "attributes": [
    { "key": "profile.schema_version", "value": { "stringValue": "otlp-profiles-v1development/1.11.0" } },

    { "key": "service.name",           "value": { "stringValue": "orders-api" } },
    { "key": "host.name",              "value": { "stringValue": "ip-10-0-3-17" } },
    { "key": "k8s.pod.name",           "value": { "stringValue": "orders-api-7d9f-4kx2p" } },
    { "key": "k8s.namespace.name",     "value": { "stringValue": "prod" } },
    { "key": "container.id",           "value": { "stringValue": "9f2c1a3b8e07" } },
    { "key": "process.pid",            "value": { "intValue": "1" } },

    { "key": "profile.session_id",     "value": { "stringValue": "01JQ8W...ULID" } },
    { "key": "profile.id",             "value": { "stringValue": "4f1c9a2b7e0d3c5f8a6b1d4e2c7f9a03" } },
    { "key": "profile.duration_nano",  "value": { "intValue": "60000000000" } },
    { "key": "profile.sample_type",    "value": { "stringValue": "samples" } },
    { "key": "profile.sample_unit",    "value": { "stringValue": "count" } },
    { "key": "profile.period_type",    "value": { "stringValue": "cpu" } },
    { "key": "profile.period_unit",    "value": { "stringValue": "nanoseconds" } },
    { "key": "profile.period",         "value": { "intValue": "10000000" } },
    { "key": "profile.original_payload_format", "value": { "stringValue": "nettrace" } },

    { "key": "thread.id",              "value": { "intValue": "31" } },
    { "key": "thread.name",            "value": { "stringValue": ".NET ThreadPool Worker" } },

    { "key": "profile.frame.type",     "value": { "stringValue": "dotnet" } },
    { "key": "profile.stack.hash",     "value": { "stringValue": "b7c41e9a2f08d3e6" } },
    { "key": "profile.stack.depth",    "value": { "intValue": "5" } },
    { "key": "profile.stack.folded",   "value": { "stringValue":
        "Program.Main;Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.HttpProtocol.ProcessRequests;OrderService.Checkout;OrderRepository.LoadCart;System.Data.SqlClient.SqlCommand.ExecuteReader" } },
    { "key": "profile.sample.value",   "value": { "intValue": "42" } }
  ]
}
```

Note `trace_id` and `span_id` are absent, not zero-filled.

Attribute count is ~22, well under the 500 ceiling, leaving room for the tier-2 fields if
they are ever added.

---

## 11. Sources

All primary, all read directly rather than summarized from secondary write-ups.

- `opentelemetry/proto/profiles/v1development/profiles.proto`, `main` @ blob
  `876f0187bc2d8105dcae10ad70bbf5b79b42b1ef`, and the same file at tag `v1.10.0`;
  the two were diffed field-by-field.
  https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/profiles/v1development/profiles.proto
- `opentelemetry/proto/logs/v1/logs.proto`, `main`.
- `opentelemetry/proto/collector/profiles/v1development/profiles_service_http.yaml`
  (endpoint path `POST /v1development/profiles`).
- `opentelemetry/proto/processcontext/v1development/process_context.proto` (new in
  v1.11.0).
- Release notes: opentelemetry-proto `v1.10.0` (2026-03-09) and `v1.11.0` (2026-07-21),
  via `gh release view`.
- opentelemetry-specification: `specification/profiles/data-format.md` (Alpha),
  `specification/profiles/mappings.md` (Alpha, build_id + htlhash algorithm),
  `oteps/profiles/4719-process-ctx.md`, `oteps/profiles/4947-thread-ctx.md`.
- semantic-conventions: `model/profile/registry.yaml`, `model/profile/common.yaml`,
  `docs/general/profiles.md`.
- opentelemetry-ebpf-profiler (reference producer):
  `reporter/internal/pdata/generate.go`, `reporter/samples/samples.go` - for the de-facto
  attribute placement (`thread.id`/`thread.name` on Sample, build IDs on Mapping,
  `profile.frame.type` on Location).
- OpenTelemetry blog, "OpenTelemetry Profiles Enters Public Alpha" (2026-03-26) - used
  only to confirm the alpha announcement date; all technical claims above come from the
  proto and spec.
- Dynatrace Docs: "Log Management and Analytics default limits" and "Ingest OTLP logs"
  (attribute/record limits, attribute flattening, trace_id/span_id hex mapping).

## 12. Open items this file does not settle

- Whether `thread.id` carries the OS tid or the .NET managed thread id. EventPipe can
  give either; the DQL join must match whatever the .NET spans/logs side reports. Pin
  this in the exporter ticket and record the answer here.
- Whether Dynatrace surfaces `LogRecord.event_name` as a queryable field or requires a
  duplicate `event.name` attribute. Verify at first ingest.
- The exact hash function for `profile.stack.hash` (xxh64 vs truncated SHA-256). Any
  stable 64-bit choice works; it must be identical in the exporter and any re-emitter.
- Confirmation of Dynatrace's log body size ceiling; the docs give differing figures in
  different pages. Does not affect this schema (body is a short summary) but should be
  nailed down.
