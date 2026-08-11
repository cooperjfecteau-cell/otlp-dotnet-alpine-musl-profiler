# Dynatrace event APIs — attaching to a problem, and events on a service entity

Research for wayfinder ticket
[#5](https://github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/issues/5).
Documentation-only: no token exists for `https://YOUR-TENANT.apps.dynatrace.com` yet, so
nothing here has been executed against a live tenant. Every claim is sourced to
`docs.dynatrace.com`. Items that need live confirmation are collected in
[Open items](#open-items-to-verify-once-we-have-a-token).

---

## TL;DR

The broker makes **two POSTs, both to the same endpoint**
(`POST /api/v2/events/ingest`), differing only in payload:

| # | Purpose | `eventType` | Addressed by | Renders as |
|---|---|---|---|---|
| A | Attach to the triggering problem | `CUSTOM_ANNOTATION` | `annotation.problem_ids` property | A comment in the problem's **Comments and insights** tab, with a clickable link |
| B | Mark the service timeline | `CUSTOM_INFO` | `entitySelector` | An info event on the service entity's event feed and as a marker on its charts |

The deep link fits comfortably: property values are capped at **4096 characters** and a
realistic viewer URL is ~180. A ULID is 26 characters of Crockford Base32
(`0-9`, `A-Z` minus `I L O U`), every one of which is an RFC 3986 *unreserved*
character, so it needs no percent-encoding and survives verbatim.

### The assumption that was wrong

> "Attach a **Davis event** to an existing problem."

There is **no API that adds an arbitrary Davis event to a problem you name by ID.**
Problem membership is decided exclusively by Davis correlation (overlapping active
windows, same or topologically-related source entity, `dt.event.allow_davis_merge`) — it
is a *policy input*, not an *address*. You cannot say "put this event on P-12345678".
Worse, the event types you *would* want to send (`CUSTOM_INFO`, `CUSTOM_ANNOTATION`,
`WARNING`) are explicitly non-problem-opening and are not merged into problems as
contributing events.

What **does** exist, and does exactly what the design wants, is a different mechanism
with the same shape: a **problem annotation**. Dynatrace docs describe it as
"a type of Davis event that can be linked with one or multiple detected problems." It is
ingested through the same events endpoint, carries a Markdown body and a dedicated URL
field, and is addressed to a problem by ID via the `annotation.problem_ids` property.

So the owner's design survives — but the name and the field are not what we assumed. Do
not reach for `dt.event.allow_davis_merge` or the Problems API. Reach for
`CUSTOM_ANNOTATION` + `annotation.problem_ids`.

Two other things that turned out differently than a naive reading suggests:

- **`POST /api/v2/problems/{problemId}/comments` is a trap.** It exists, it takes
  `problems.write`, and it is the obvious-looking answer — but "Comments added via
  Problems Classic or Dynatrace Classic API are only visible in Problems Classic."
  A human on `YOUR-TENANT.apps.dynatrace.com` uses the new Problems app and will never see
  it. Do not use it.
- **`event.description` documents two conflicting limits.** The annotation spec says
  10,000 characters; the Events API v2 `properties` map caps *every* value at 4096. Via
  `/api/v2/events/ingest` the 4096 cap is the binding one. Keep descriptions well under
  4096 and this never matters.

---

## Call A — attach to an existing problem (problem annotation)

### Endpoint

```
POST https://YOUR-TENANT.live.dynatrace.com/api/v2/events/ingest
Content-Type: application/json
Authorization: Api-Token {token}
```

> **Host note.** `YOUR-TENANT.apps.dynatrace.com` is the *UI* host. Environment API v2 lives
> on `YOUR-TENANT.live.dynatrace.com`. Both resolve for the same tenant; using the `.apps.`
> host for `/api/v2/...` is a common 404. Confirm at first live call.

### Payload

```json
{
  "eventType": "CUSTOM_ANNOTATION",
  "title": "Continuous profile captured — open flame graph",
  "properties": {
    "annotation.source": "otlp-dotnet-profiler-broker",
    "annotation.id": "profile-session-01JBQ8Z9K3T4M5N6P7Q8R9S0TV",
    "annotation.problem_ids": "{PROBLEM_EVENT_ID}",
    "annotation.url": "https://YOUR-TENANT.apps.dynatrace.com/ui/apps/{APP_ID}/session/01JBQ8Z9K3T4M5N6P7Q8R9S0TV?entity=SERVICE-1234567890ABCDEF&from=1754899200000&to=1754899290000",
    "dt.event.description": "## Profile captured\n\nA 90-second profiling session was started in response to this problem.\n\n| | |\n|---|---|\n| Session | `01JBQ8Z9K3T4M5N6P7Q8R9S0TV` |\n| Window | 2026-08-11T06:00:00Z → 2026-08-11T06:01:30Z |\n| Workload | `checkout-api` (SERVICE-1234567890ABCDEF) |\n| Sources | eBPF node profiler + dotnet-monitor sidecar |\n\n[Open the flame graph](https://YOUR-TENANT.apps.dynatrace.com/ui/apps/{APP_ID}/session/01JBQ8Z9K3T4M5N6P7Q8R9S0TV)"
  }
}
```

Source: the payload shape and field names are taken verbatim from the Dynatrace
[Problems app](https://docs.dynatrace.com/docs/dynatrace-intelligence/problems-app)
annotation example, which prints:

```json
{
  "eventType": "CUSTOM_ANNOTATION",
  "title": "This is an annotation",
  "properties": {
    "annotation.source": "My own AI Agent",
    "annotation.id": "unique-id-12345",
    "annotation.url": "https://example.com",
    "annotation.problem_ids": "-8846223150850373620_1779767520000V2",
    "dt.event.description": "## My own AI agent findings\n\nThis annotation was created by an AI agent to provide more context about the problem."
  }
}
```

### Field reference (Semantic Dictionary, Davis model)

| Field | Type | Required | Meaning |
|---|---|---|---|
| `annotation.id` | string | **yes** | "The unique ID of the comment. Can be any arbitrary string." Re-sending the same ID **overwrites** the previous comment. |
| `annotation.problem_ids` | string[] | **yes** | "The reference to one or more problems." |
| `annotation.source` | string | **yes** | "The source of the comment. Must be set by the event provider." |
| `annotation.url` | string | no | "The external URL to a third-party integration, for example, a link to a ticket." |
| `annotation.user_id` | string | no | UUID of the commenting user — system-provided; do not set. |
| `event.description` | string | no | Markdown comment body, **up to 10,000 characters** per the annotation spec. |
| `event.name` | string | no | "The link label for the `annotation.url`." Up to 1,000 characters; docs recommend ≤100. |
| `event.start` | timestamp | yes | Comment timestamp. Defaults to now if omitted. |

Mapping between the Grail field names above and the `/api/v2/events/ingest` wire format:
`event.name` is sent as the top-level `title`; `event.description` is sent as the
`dt.event.description` property; `event.start` is the top-level `startTime` (UTC ms).
The `annotation.*` fields are sent as plain `properties` keys.

### How the problem is addressed

`annotation.problem_ids` takes the **internal problem event ID**, not the human
`display_id`. Note the format in the official example:

```
-8846223150850373620_1779767520000V2
```

That is `{signed 64-bit hash}_{epoch millis}V2` — the value of `event.id` on a
`dt.davis.problems` record. `display_id` (`P-25081100`) is the *human* identifier and is
**not** what this field wants.

In the Dynatrace workflow that calls our broker, pass it through as:

```
{{ event()["event.id"] }}
```

The Davis-problem trigger exposes the whole `dt.davis.problems` record to `event()`, so
both `event.id` and `display_id` are available. Send `event.id` to the broker as the
problem reference; optionally send `display_id` too, purely for logging and for the
annotation body.

The Semantic Dictionary types this field as `string[]` — "the field also accepts values
of type `array`". But the Events API v2 `properties` map is a `map<string, string>`, so
via `/api/v2/events/ingest` you can only send a string. A single problem ID is the normal
case for us and is exactly what the official example shows. If we ever need to annotate
several problems at once, either issue one call per problem, or use the OpenPipeline
generic event endpoint (below), which accepts real JSON arrays.

### Scopes

| Token type | What to grant |
|---|---|
| Classic API token (`Api-Token dt0c01.…`) | `events.ingest` — "To execute this request, you need an access token with `events.ingest` scope." |
| Platform token / OAuth client | `storage:events:write` — the Problems app docs state annotations require "the event ingest permission (`ALLOW storage:events:write`)" in addition to standard Problems permissions. |

The broker only ever writes, so grant exactly one of these and nothing else. The **viewer
app** separately needs `storage:events:read` + `storage:buckets:read` if it wants to read
its own annotations back.

### How it renders

The annotation appears in the problem details page under the **Comments and insights**
tab, alongside human-typed comments. `event.description` renders as Markdown.
`annotation.url` renders as a link whose label is `event.name` (our `title`).

Annotations do **not** trigger alerts, do **not** open a problem, and do **not** change
the problem's severity or category — "annotation events act the same as the Davis info
events." That is the correct behaviour for us: we are adding context to an incident
someone is already looking at, not creating a second incident.

They are immutable once ingested — "once the events are ingested, they can't be
modified" — but re-sending with the same `annotation.id` overrides the visible comment.
This gives us a clean lifecycle: post one annotation at session start with
`annotation.id = "profile-session-{ULID}"` saying *capture in progress*, then re-post the
same `annotation.id` 90 seconds later with the completed link and the sample count. The
human sees one comment that updates in place, not two.

Only visible in the **new Problems app**. Not in Problems Classic.

### Verifying it landed (once we have a token)

```dql
fetch dt.davis.events, from: -1h
| filter event.type == "CUSTOM_ANNOTATION"
| filter annotation.source == "otlp-dotnet-profiler-broker"
| fields timestamp, event.name, annotation.id, annotation.problem_ids, annotation.url
| sort timestamp desc
```

---

## Call B — custom event on a service entity

### Endpoint

Same endpoint, different payload.

```
POST https://YOUR-TENANT.live.dynatrace.com/api/v2/events/ingest
Content-Type: application/json
Authorization: Api-Token {token}
```

### Payload

```json
{
  "eventType": "CUSTOM_INFO",
  "title": "Profiling session 01JBQ8Z9K3T4M5N6P7Q8R9S0TV",
  "startTime": 1754899200000,
  "endTime": 1754899290000,
  "entitySelector": "type(SERVICE),entityId(\"SERVICE-1234567890ABCDEF\")",
  "properties": {
    "profiler.session_id": "01JBQ8Z9K3T4M5N6P7Q8R9S0TV",
    "profiler.trigger": "davis_problem",
    "profiler.problem_id": "{PROBLEM_EVENT_ID}",
    "profiler.problem_display_id": "P-25081100",
    "profiler.duration_seconds": "90",
    "profiler.sources": "ebpf,dotnet-monitor",
    "profiler.viewer_url": "https://YOUR-TENANT.apps.dynatrace.com/ui/apps/{APP_ID}/session/01JBQ8Z9K3T4M5N6P7Q8R9S0TV",
    "dt.event.description": "90s CPU/GC/contention profile captured for `checkout-api`. Session `01JBQ8Z9K3T4M5N6P7Q8R9S0TV`.",
    "dt.event.allow_davis_merge": "false",
    "dt.event.is_rootcause_relevant": "false"
  }
}
```

### `EventIngest` object — full schema

Reproduced from
[Events API v2 — POST an event](https://docs.dynatrace.com/docs/dynatrace-api/environment-api/events-v2/post-event):

| Element | Type | Required | Description / constraint |
|---|---|---|---|
| `eventType` | string | **Required** | "The type of the event." Enum, see below. |
| `title` | string | **Required** | "The title of the event." |
| `startTime` | integer | Optional | "The start time of the event, in UTC milliseconds. If not set, the current timestamp is used." |
| `endTime` | integer | Optional | "The end time of the event, in UTC milliseconds. If not set, the start time plus timeout is used." |
| `timeout` | integer | Optional | "The timeout of the event, in minutes. If not set, 15 is used. The timeout will automatically be capped to a maximum of 360 minutes (6 hours)." |
| `entitySelector` | string | Optional | "The entity selector, defining a set of Dynatrace entities to be associated with the event. Only entities that have been active within the last 24 hours can be selected." |
| `properties` | object | Optional | "A map of event properties." **Max 100 properties; keys ≤ 100 chars; values ≤ 4096 chars.** |

`eventType` enum:

```
AVAILABILITY_EVENT
CUSTOM_ALERT
CUSTOM_ANNOTATION
CUSTOM_CONFIGURATION
CUSTOM_DEPLOYMENT
CUSTOM_INFO
ERROR_EVENT
MARKED_FOR_TERMINATION
PERFORMANCE_EVENT
RESOURCE_CONTENTION_EVENT
WARNING
```

Backdating limits: problem-opening events max 6 hours in the past; info events max 30
days. `CUSTOM_ANNOTATION`, `CUSTOM_CONFIGURATION`, `CUSTOM_DEPLOYMENT`, `CUSTOM_INFO` and
`MARKED_FOR_TERMINATION` may also be dated up to 7 days in the future. Our 90-second
window is always within a couple of minutes of now, so this never binds.

Response, HTTP 201:

```json
{
  "reportCount": 1,
  "eventIngestResults": [
    { "correlationId": "string", "status": "OK" }
  ]
}
```

`status` is one of `OK`, `INVALID_ENTITY_TYPE`, `INVALID_METADATA`,
`INVALID_TIMESTAMPS`. **A 201 does not mean success** — the broker must inspect
`eventIngestResults[].status`. An event that failed entity mapping returns 201 with
`INVALID_ENTITY_TYPE` and is silently dropped to the environment level. This is the
single most likely way this integration fails quietly in production; log the
`correlationId` and the `status` from every call.

### Why `CUSTOM_INFO` and not something louder

`CUSTOM_INFO` and `WARNING` are the two non-problem-raising types. "While `CUSTOM_INFO`
and `WARNING` events don't open problems, event correlation and deduplication rules apply
to them the same way as the other event types." Anything else in the enum
(`AVAILABILITY_EVENT`, `ERROR_EVENT`, `PERFORMANCE_EVENT`, `RESOURCE_CONTENTION_EVENT`,
`CUSTOM_ALERT`) is problem-opening, and a profiler that opens a second problem every time
it profiles the first one is a paging incident waiting to happen.

`dt.event.allow_davis_merge: false` and `dt.event.is_rootcause_relevant: false` are belt
and braces — we do not want our own bookkeeping event nominated as anyone's root cause.
Alternatives if you want it even quieter: `dt.event.suppress_problem`. The full Davis
control property set is:

| Property | Type | Effect |
|---|---|---|
| `dt.event.allow_davis_merge` | boolean | Merge into an existing problem (`true`) or force a new one (`false`). |
| `dt.event.allow_frequent_issue_detection` | boolean | Let Davis mute this if it becomes noisy. |
| `dt.event.allow_entity_remapping` | boolean | Let Davis remap to an entity type extracted from properties. |
| `dt.event.is_rootcause_relevant` | boolean | Include in / exclude from root cause analysis. |
| `dt.event.suppress_problem` | boolean | Prevent problem creation outright. |
| `dt.event.timeout` | string | Lifetime in minutes before auto-close. |

### Entity addressing

Selector grammar, from
[entity selector](https://docs.dynatrace.com/docs/dynatrace-api/environment-api/entity-v2/entity-selector):

| Form | Syntax |
|---|---|
| By ID | `entityId("id-1","id-2")` — comma-separated, all must share a type |
| By type | `type("SERVICE")` |
| By name | `entityName("x")`, `.startsWith()`, `.equals()`, `.in()` — case-insensitive unless `caseSensitive()` |
| By tag | `tag("[context]key:value")` — matches **any** listed tag |

Multiple criteria are ANDed: `type(SERVICE),entityName.equals(checkout-api)`.

Entity ID format is `{TYPE}-{16 uppercase hex}`, e.g. `SERVICE-1234567890ABCDEF`. The ones
relevant to a .NET workload on Alpine in Kubernetes:

| Type | Represents |
|---|---|
| `SERVICE` | The detected service — the right target for a response-time problem |
| `CLOUD_APPLICATION` | The Kubernetes workload (Deployment/StatefulSet) |
| `CLOUD_APPLICATION_INSTANCE` | The pod |
| `PROCESS_GROUP_INSTANCE` | The .NET process — the right target for a CPU/memory problem |
| `HOST` | The node the eBPF profiler runs on |

Two rules that bite:

1. **The 24-hour rule.** "Only entities that have been active within the last 24 hours can
   be selected." An `entityId(...)` filter reportedly bypasses this; a `type(...)` or
   `tag(...)` selector does not. Since the broker is triggered by a live problem on a live
   entity, the entity is active by construction.
2. **Silent fallback.** "If no entity matches your selector or the selector is omitted
   altogether, the event is mapped to the environment level." Combined with the 201-plus-
   `INVALID_ENTITY_TYPE` behaviour above, a typo in the entity ID produces an event that
   exists but is attached to nothing. Always echo `entitySelector` and the ingest result
   status into the broker's own logs.

**Prefer the problem's own entity ID over anything we compute.** The workflow already
knows it: `{{ event()["dt.smartscape_source.id"] }}` gives the problem's source entity,
and `smartscape.affected_entity.ids` gives the full affected set. Pass one of those to the
broker rather than resolving names.

### How it renders

The event lands on the service entity's event feed, and on entity charts as a timeline
marker spanning `startTime`→`endTime` — the 90-second profiling window is visible as a
band, not a point, which is exactly the affordance we want. Custom apps can render the
same thing with `TimeseriesChart.Annotations` after a `fetch events` query.

It will **not** appear in the problem's **Events** tab. That tab lists events Davis
correlated into the problem, and informational events are not merged as contributing
events. This is precisely why Call A exists and why Call B alone is not sufficient.

Ingest consumes **Davis Data Units from the events pool**, and Grail-stored events are
licensed under the events rate card. At two events per profiling session this is
rounding-error cost, but it is not free, and it argues against emitting an event per
sample or per pod.

---

## Field limits, allowed characters, and whether the link fits

| Field | Limit | Source |
|---|---|---|
| `title` (`event.name`) | 1,000 chars for annotations; docs recommend ≤100 | Problems app |
| `dt.event.description` (`event.description`) | 10,000 chars per the annotation spec, but **4096 via `/api/v2/events/ingest`** because it travels in `properties` | Problems app / Events API v2 |
| `properties` — count | max **100** entries | Events API v2 |
| `properties` — key | max **100** chars | Events API v2 |
| `properties` — value | max **4096** chars | Events API v2 |
| Request body | 10 MB (OpenPipeline scope limit) | OpenPipeline limits |
| Record after processing | 16 MB | OpenPipeline limits |

Dynatrace documents no character-class restriction on property keys or values beyond
reserving the `dt.*` namespace for platform-defined properties. Values are JSON strings;
anything JSON can carry, the field can carry.

### Does the deep link fit? Yes, with ~95% headroom.

Worst realistic URL:

```
https://YOUR-TENANT.apps.dynatrace.com/ui/apps/my.company.flamegraph/session/01JBQ8Z9K3T4M5N6P7Q8R9S0TV?entity=SERVICE-1234567890ABCDEF&from=1754899200000&to=1754899290000
```

That is **166 characters** against a 4096-character budget. Even with an app ID twice as
long, a longer route, and three more query parameters, we stay under 400. There is no
scenario in this design where the link is truncated.

### Does the ULID survive intact? Yes, unconditionally.

A canonical ULID is 26 characters of Crockford Base32: `0123456789ABCDEFGHJKMNPQRSTVWXYZ`
— digits plus uppercase letters excluding `I`, `L`, `O`, `U`. Every one of those is in the
RFC 3986 `unreserved` set (`ALPHA / DIGIT / "-" / "." / "_" / "~"`), so:

- no percent-encoding is required in a path segment or a query value;
- nothing in it is a Markdown metacharacter, so `[label](…{ULID}…)` cannot break;
- nothing in it needs JSON string escaping;
- it is case-stable — do not lowercase it, Crockford decoding is case-insensitive but
  string comparison against Grail records is not.

---

## Recommendation on deep-link encoding

1. **Use the canonical uppercase ULID verbatim.** No base64, no URL-encoding, no wrapping
   in JSON. Any encoding layer is pure downside here: it adds a decode step in the viewer,
   it introduces `+`, `/`, and `=` which *do* need escaping, and it makes the link
   unreadable in the annotation body where a human might want to copy just the session ID.

2. **Session ID in the path, context in the query.**

   ```
   https://{env}.apps.dynatrace.com/ui/apps/{appId}/session/{ULID}?entity={ENTITY_ID}&from={ms}&to={ms}
   ```

   The ULID is the identity of the resource, so it belongs in the path; entity and
   timeframe are view state, so they belong in the query. This also means a link that
   loses its query string still resolves to the right profile.

3. **Put the link in three places, deliberately.**
   - `annotation.url` — the structured, clickable link Dynatrace renders natively.
   - Inside `dt.event.description` as a Markdown link — survives copy/paste of the comment
     text into Slack or a ticket.
   - `profiler.viewer_url` property on the Call B entity event — machine-readable, so DQL
     and the viewer app can find sessions without parsing Markdown.

4. **Also carry the ULID as a bare property** (`profiler.session_id`) on both events. The
   URL is for humans; the bare ID is the join key for DQL against the profile logs. Never
   make a query parse a URL to recover an identifier it could have read directly.

5. **Keep `annotation.id` derived from the ULID** — `profile-session-{ULID}` — so the
   in-place-update behaviour is free and idempotent. A retried broker call overwrites its
   own comment instead of posting a duplicate.

6. **Confirm the app URL shape at build time.** `https://{env}.apps.dynatrace.com/ui/apps/{appId}/…`
   is the established pattern, and page-token links are documented as
   `https://abc12345.apps.dynatrace.com/ui/openApp/app.id?pageToken=…`, but the exact
   routing for our viewer depends on how we configure its router. Do not hard-code the
   URL in the broker: make it a template in broker config
   (`VIEWER_URL_TEMPLATE=https://…/session/{sessionId}?entity={entityId}&from={from}&to={to}`)
   so the deploy can fix it without a rebuild.

---

## Would bizevents be better?

**No — but they are a reasonable complement, and there is one scenario where they win.**

| | Davis annotation + entity event | Business event |
|---|---|---|
| Endpoint | `POST /api/v2/events/ingest` | `POST /api/v2/bizevents/ingest` |
| Scope | `events.ingest` / `storage:events:write` | `bizevents.ingest` / `storage:events:write` |
| Payload | Fixed schema, `properties` map of strings | Arbitrary JSON, no mandatory fields |
| Renders in problem UI | **Yes** — Comments and insights tab | **No** |
| Renders on entity timeline | **Yes** | **No** |
| Attaches to a problem | **Yes**, via `annotation.problem_ids` | No mechanism |
| Queryable | `fetch dt.davis.events` | `fetch bizevents` |
| Payload size | 4096/value, 100 properties | 5 MB per request; nested objects flattened to strings |
| Content types | JSON | JSON, CloudEvents, CloudEvents batch |
| Licensing | Events rate card / DDU events pool | Business events rate card |

The decisive point is the one the ticket asks about: **a bizevent has no UI surface on a
problem.** The entire purpose of this call is that a human investigating a problem sees
that a profile was captured and can click through. A bizevent is invisible to that human;
it is only reachable by someone who already knows to write DQL against `bizevents`, which
is exactly the person who did not need the link. On the primary requirement, bizevents
score zero. The owner's choice is correct and I would not overturn it.

Where a bizevent genuinely wins is **structured session metadata**. Our `properties` map
is `string → string`, capped at 100 entries and 4096 characters each. If we later want to
record per-session telemetry — sample counts per source, dropped-frame ratios, unwinder
failure counts on musl, bytes shipped, per-pod breakdowns — that is a nested object with
numeric fields, and forcing it through a flat string map is genuinely awkward. A bizevent
takes it natively at up to 5 MB, and `fetch bizevents | summarize` over it is the natural
way to answer "how is the profiler itself behaving?"

So the shape I would build:

- **Davis annotation (Call A)** — the human-facing artifact. One per session. Non-optional.
- **Entity `CUSTOM_INFO` (Call B)** — the timeline marker and the machine-readable
  `profiler.session_id` anchor on the entity. One per session.
- **Bizevent — deferred.** Add it only when we actually want profiler self-observability
  metrics, and treat it as a separate concern from the problem-investigation path. It is
  not needed for the reference implementation's core loop.

One caveat worth recording: **all three bill.** Davis events bill under the events rate
card and consume DDUs from the events pool; bizevents bill under the business events rate
card. Given the repo already commits to "a worked cost estimate," these should appear as a
line item — small, but non-zero, and it scales with how trigger-happy the workflow is.

---

## Scope summary

| What | Endpoint | API-token scope | Platform-token / OAuth permission |
|---|---|---|---|
| Broker → annotation on problem | `POST /api/v2/events/ingest` | `events.ingest` | `storage:events:write` |
| Broker → info event on service | `POST /api/v2/events/ingest` | `events.ingest` | `storage:events:write` |
| Viewer app → read events back | `fetch dt.davis.events` / `fetch events` | — | `storage:events:read`, `storage:buckets:read` |
| Viewer app → read profile logs | `fetch logs` | — | `storage:logs:read`, `storage:buckets:read` |
| *(rejected)* problem comment | `POST /api/v2/problems/{id}/comments` | `problems.write` | — |

The broker needs exactly **one** scope: `events.ingest` (or `storage:events:write`). Both
calls use it. Anything more is over-privilege on a service that accepts an inbound webhook.

Not needed and should not be granted: `problems.write`, `entities.read`,
`bizevents.ingest`, `openpipeline.events`.

---

## Alternative path — OpenPipeline generic events

Worth recording but **not recommended for v1**:

```
POST https://YOUR-TENANT.live.dynatrace.com/platform/ingest/v1/events
Scope: openpipeline.events
Body: a JSON object or array of objects, arbitrary keys
Response: 202 Accepted, no body
```

Advantages: accepts real JSON arrays (so `annotation.problem_ids` could carry multiple
problems in one call), no 4096-char-per-value cap, 10 MB request budget.

Disadvantages that decide it: it returns **202 with no body**, so there is no
per-record `status` to check — the `INVALID_ENTITY_TYPE` class of failure becomes
completely invisible. It also bypasses the documented `eventType`/`entitySelector`
contract, so entity mapping has to be done by hand through Grail field names. For two
small events per session where we care a great deal about knowing whether they landed,
`/api/v2/events/ingest` and its `eventIngestResults` is the better trade.

---

## Open items to verify once we have a token

1. **API host.** Confirm `/api/v2/events/ingest` on `YOUR-TENANT.live.dynatrace.com`. If the
   tenant is Grail-only/app-engine-first, verify the classic environment API is reachable
   at all — this is the single assumption that would most change the design.
2. **`annotation.problem_ids` with `event.id`.** Confirm that
   `{{ event()["event.id"] }}` from a Davis-problem workflow trigger is accepted verbatim,
   and that the annotation actually appears on that problem. Cross-check the value format
   against `-8846223150850373620_1779767520000V2`.
3. **Multiple problem IDs.** Test whether a comma-separated string in the `properties` map
   is parsed as an array, or whether one call per problem is required.
4. **`event.description` effective cap.** Send a 5,000-character description and observe
   whether it is rejected, truncated at 4096, or accepted whole. Determines whether the
   10,000-char annotation limit is reachable through this endpoint at all.
5. **Overwrite semantics.** Post twice with the same `annotation.id` and confirm the
   Comments and insights tab shows one comment, not two.
6. **Entity event visibility.** Confirm the `CUSTOM_INFO` event appears on the SERVICE
   entity page and as a chart band, and confirm it does *not* appear in the problem's
   Events tab (this doc asserts it will not).
7. **`eventIngestResults` on a bad selector.** Deliberately send a malformed entity ID and
   confirm the 201 + `INVALID_ENTITY_TYPE` behaviour, so the broker's error handling is
   written against observed reality.
8. **Viewer app URL.** Lock the real deployed route once the flame graph app exists; keep
   it in broker config, not code.

---

## Sources

- [Events API v2 — POST an event](https://docs.dynatrace.com/docs/dynatrace-api/environment-api/events-v2/post-event) — endpoint, `EventIngest` schema, `eventType` enum, property limits, `events.ingest` scope, DDU note, response schema
- [Events API v2 overview](https://docs.dynatrace.com/docs/discover-dynatrace/references/dynatrace-api/environment-api/events-v2)
- [Events API v2 — GET all event properties](https://docs.dynatrace.com/docs/dynatrace-api/environment-api/events-v2/get-event-properties) — `dt.event.allow_davis_merge` and reserved properties
- [Problems app](https://docs.dynatrace.com/docs/dynatrace-intelligence/problems-app) — annotation payload example, `annotation.*` field table, `storage:events:write`, Comments and insights tab, 10,000/1,000-char limits, Problems Classic visibility caveat
- [Davis AI — Semantic Dictionary model](https://docs.dynatrace.com/docs/semantic-dictionary/model/davis) — `event.kind`, `event.type`, `annotation.*` field types and requiredness
- [Event analysis and correlation](https://docs.dynatrace.com/docs/discover-dynatrace/platform/davis-ai/root-cause-analysis/concepts/events) — merge determinants, Davis control properties
- [Categories of Davis events](https://docs.dynatrace.com/docs/platform/davis-ai/basics/events/event-types) — which categories open problems
- [Info events](https://docs.dynatrace.com/docs/platform/davis-ai/basics/events/event-types/info-events) — `CUSTOM_ANNOTATION` semantics
- [Entity selector](https://docs.dynatrace.com/docs/dynatrace-api/environment-api/entity-v2/entity-selector) — selector grammar
- [Event topology extraction and mapping](https://docs.dynatrace.com/docs/ingest-from/extend-dynatrace/extend-topology/events-entity-extraction) — 24-hour rule, environment-level fallback
- [Problems API v2 — POST a comment](https://docs.dynatrace.com/docs/dynatrace-api/environment-api/problems-v2/comments/post-comment) — the rejected alternative
- [Ingest sources in OpenPipeline](https://docs.dynatrace.com/docs/platform/openpipeline/reference/api-ingestion-reference) — endpoint/scope matrix
- [OpenPipeline limits](https://docs.dynatrace.com/docs/platform/openpipeline/reference/limits) — 10 MB request / 16 MB record
- [OpenPipeline Ingest API — POST built-in generic events](https://docs.dynatrace.com/docs/discover-dynatrace/platform/openpipeline/reference/openpipeline-ingest-api/generic-events/events-generic-builtin) — 202-no-body behaviour
- [Ingest business events via API](https://docs.dynatrace.com/docs/observe/business-observability/bo-events-capturing/bo-events-capturing-external-sources) — bizevents endpoint, scopes, 5 MB, nested-object flattening
- [Event triggers for workflows](https://docs.dynatrace.com/docs/analyze-explore-automate/workflows/trigger/event-trigger) — `event()` expression, `event.id` vs `display_id`
- [Visualize events](https://developer.dynatrace.com/develop/visualize-data-in-apps/visualize-events/) — chart annotation rendering, read scopes
