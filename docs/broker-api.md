# Broker API — prototype

Resolves [#14](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/14).
Concrete enough to react to; not built yet.

The broker is the single endpoint a Dynatrace workflow calls to say "profile this service
for 90 seconds because of this problem". Everything else — session identity, pod discovery,
gating, EventPipe collection, and the event push — happens behind it.

## What it does *not* do any more

Two jobs fell away as earlier decisions landed, and it is worth being explicit so the
implementation does not resurrect them:

- **It does not start eBPF collection.** eBPF samples continuously. The broker only opens a
  *gate*; the samples were already flowing.
- **It does not stop anything early.** `dotnet-monitor` cannot terminate a trace prematurely
  and still produce a usable nettrace (#4), so duration is fixed at request time. There is no
  `DELETE /sessions/{id}` that shortens a run — only one that closes the gate.

## Endpoints

### `POST /sessions`

```json
{
  "service":         "dotnet-profiler-demo",
  "namespace":       "dotnet-profiler",
  "durationSeconds": 90,
  "problemEventId":  "-8846223150850373620_1779767520000V2",
  "entityId":        "SERVICE-1A2B3C4D5E6F7890"
}
```

`problemEventId` is the **internal event id**, available in a workflow as
`{{ event()["event.id"] }}` — *not* the `P-…` display id, which fails silently (#5).

Responds `202 Accepted` immediately — never blocking for the profile duration:

```json
{
  "sessionId":       "01JVX8QK3M7Y2N4P6R8T0W2Z5A",
  "state":           "collecting",
  "startedAt":       "2026-08-11T20:14:03Z",
  "expectedReadyAt": "2026-08-11T20:15:33Z",
  "pods":            ["dotnet-profiler-demo-7997b85798-lbshm"],
  "viewerUrl":       "https://YOUR-TENANT.apps.dynatrace.com/ui/apps/<app-id>/session/01JVX8QK3M7Y2N4P6R8T0W2Z5A"
}
```

### `GET /sessions/{id}`

Same body, current `state`. **`GET /sessions`** lists active sessions.

### `DELETE /sessions/{id}`

Closes the gate early. Does **not** abort an in-flight EventPipe trace — that would produce
an unusable nettrace — so the managed half still completes and publishes.

## Session states

`started` and `ready` are deliberately separate, because they are not the same moment (#4):
symbol rundown can push nettrace availability well past the end of the collection window.

| State | Meaning |
|---|---|
| `collecting` | Gate open, eBPF records flowing, EventPipe trace running |
| `processing` | Window elapsed; nettrace being parsed and folded by the sidecar |
| `ready` | All records published; the viewer link resolves to complete data |
| `partial` | eBPF landed, EventPipe failed. **Still useful** — publish and say so |
| `failed` | Nothing landed |

`partial` earns its place: the two halves fail independently, and a sidecar problem on one pod
should not discard a perfectly good node-level profile.

## What happens on `POST`

1. **Mint a ULID.** Not the problem id — one problem can trigger several profiles.
2. **Resolve pods** for the service in the namespace.
3. **Open the gate**: patch the `profiler-sessions` ConfigMap with the session. Every
   collector DaemonSet pod observes it; no per-pod fan-out.
4. **Start EventPipe** per pod via `dotnet-monitor`: `POST /trace?durationSeconds=90` with
   the providers from #4 — `Microsoft-DotNETCore-SampleProfiler` plus
   `Microsoft-Windows-DotNETRuntime` keywords `0x410F40B9`.
5. **Push "capture in progress"** — a `CUSTOM_ANNOTATION` keyed on the session ULID, plus a
   `CUSTOM_INFO` on the service entity.
6. **Return 202.**

Then, when the window closes: flip the gate off, wait for the sidecar to publish, and
**overwrite the same annotation** with the finished deep link. Annotations are idempotent on
`annotation.id` (#5), so this is an update rather than a second comment — and retries are free.

## Two failure modes that must not be silent

**The event ingest endpoint returns HTTP 201 even when entity mapping fails.** The real status
is in `eventIngestResults[].status`. The broker must inspect the array; trusting the status
code means the annotation quietly lands at environment level and nobody sees it on the problem
(#5).

**ConfigMap propagation is not instant.** A mounted ConfigMap takes up to ~a minute to reach
the kubelet's view, on top of the connector's own reload interval. So the gate opens *some
time after* the 202. Either report `expectedReadyAt` with that slack included, or have the
broker wait for observed propagation before declaring `collecting`. Pretending it is
synchronous will produce sessions whose first seconds are missing, intermittently, which is
the worst kind of bug to chase later.

## Authentication

Still open, and deliberately not decided here — it depends on how the Dynatrace workflow is
allowed to reach in-cluster services. Candidates: a shared secret in a header, an OAuth client
credential, or mTLS. Whatever is chosen, the endpoint mutates cluster state and triggers
billable ingest, so it cannot be left unauthenticated the way `dotnet-monitor`'s own API is
(that one is bound to localhost and reachable only by the sidecar).

## Open question for the reviewer

**Should `POST /sessions` be idempotent on `problemEventId`?** A Dynatrace workflow can retry,
and a problem that re-opens can fire again. Without idempotency you get several overlapping
sessions for one problem, each paying full ingest. With it, you cannot deliberately profile
the same problem twice. My inclination is to make it idempotent within a short window — say,
reject a new session for the same `problemEventId` while one is still `collecting` — and
return the existing session's 202 body instead.
