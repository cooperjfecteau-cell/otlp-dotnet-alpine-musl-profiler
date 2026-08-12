# Getting started

Profiling .NET workloads that run in **Alpine/musl** containers, into Dynatrace.

This is the happy path. Follow it top to bottom and you will get flame graphs for a .NET
service, triggered on demand from a Dynatrace workflow. When something does not behave as
described here, read [LESSONS_LEARNED.md](./LESSONS_LEARNED.md) — nearly every failure mode
we hit has an error message that points somewhere other than the cause.

**Time**: about two hours the first time, most of it waiting for builds and Kubernetes.

> ### Verified 2026-08-11/12 — versions matter here
>
> This guide was written against specific versions, and several instructions depend on
> behaviour that could change. **Check these before assuming a step is wrong on your side.**
>
> | | |
> |---|---|
> | Kubernetes | EKS `1.31`, AL2023 kernel `6.1.176`, containerd `2.2.4` |
> | Collector | `0.158.0`, eBPF profiler `v0.0.202632` |
> | .NET | `9.0` on Alpine `3.23`, musl `1.2.5` |
> | dotnet-monitor | `10.0.3` — **no musl image exists**; glibc sidecar is intentional |
> | EdgeConnect | `v1.744.0` — **amd64 only**, needs an x86 node on Graviton |
> | TraceEvent | `3.2.5` |
> | dtctl / dt-app | `0.37.0` / `1.13.1` |
> | OTLP profiles proto | `v1.10.0`, package `v1development`, **Alpha** |
>
> **The single biggest assumption**: Dynatrace does not ingest the OpenTelemetry profiles
> signal, which is the entire reason profile data arrives here as logs. The signal reached
> public Alpha in March 2026. If Dynatrace now supports it natively, **most of this pipeline
> is unnecessary** — the record schema deliberately mirrors the OTLP profiles model so that
> migrating is a transport change rather than a redesign. Check before you build.
>
> Full provenance, including which findings are expected to expire and which are durable, is
> in [LESSONS_LEARNED.md](./LESSONS_LEARNED.md#read-this-first-everything-here-is-point-in-time).

---

## What you get

Two independent profilers feeding one picture:

| Component | Gives you | Cost |
|---|---|---|
| **eBPF profiler** (DaemonSet) | Managed .NET, kernel and native frames, whole-node, zero instrumentation | Continuous, cheap |
| **EventPipe sidecar** | GC, lock contention, allocation, line numbers, inlined frames | On demand, expensive |

Both emit the same record shape, so they reassemble into one flame graph. A broker gates the
expensive half so it only runs when you ask for it, and a Dynatrace workflow can ask for it
automatically when Davis raises a problem.

Profile data arrives as **OTLP logs**, because Dynatrace does not ingest the OpenTelemetry
profiles signal yet. The record schema mirrors the OTLP profiles data model so that when it
does, this becomes a transport change rather than a re-model.

---

## Prerequisites

### Cluster

- Kubernetes 1.29+ (native sidecars are used; 1.31 is what this was built on)
- **Kernel with BTF** — `/sys/kernel/btf/vmlinux` must exist. Amazon Linux 2023 has it;
  Amazon Linux 2 largely does not, and the profiler will not load.
- Ability to run a **privileged** DaemonSet. `unprivileged_bpf_disabled=1` and
  `perf_event_paranoid=2` are typical, and mean `CAP_BPF` alone is not enough.
- **If you want the Dynatrace workflow trigger**: at least one **amd64** node.
  Dynatrace publishes no arm64 EdgeConnect image. On an arm64-only cluster you can run
  everything else, but the workflow leg needs one x86 node.

Verify BTF before anything else:

```bash
kubectl run btf-check --rm -it --restart=Never --overrides='{"spec":{"hostPID":true,"containers":[{"name":"p","image":"busybox","securityContext":{"privileged":true},"command":["sh","-c","ls -l /host/sys/kernel/btf/vmlinux && ls /host/sys/kernel/tracing >/dev/null && echo TRACEFS_OK"],"volumeMounts":[{"name":"sys","mountPath":"/host/sys"}]}],"volumes":[{"name":"sys","hostPath":{"path":"/sys"}}]}}' --image=busybox
```

### Your application

- .NET 8 or 9 on an Alpine base image
- **Published framework-dependent.** Not self-contained, not single-file, not NativeAOT —
  the profiler locates the runtime by the path pattern `/<version>/libcoreclr.so`, and other
  layouts silently break unwinding while the app keeps working.

### Dynatrace

- A tenant, and permission to create: a Grail bucket, an API token, an EdgeConnect
  configuration, a credential vault entry, and a workflow.
- **Several of these cannot be created from a token** — see step 1.

---

## Step 1 — Understand what you must click

Four things in this setup can only be created through the Dynatrace UI, because the API
refuses them even when the token carries the named scope:

| Thing | Why |
|---|---|
| Grail bucket | `403 Required permissions not met` — IAM policy, not token scope |
| EdgeConnect configuration | Creating it mints an OAuth client, needs `oauth2:clients:manage` |
| Credential vault entry | No API access with a normal ingest token |
| Workflow **actor** identity | Inherited from whoever authenticated; cannot be reassigned |

Plan for this. You cannot fully automate first-time setup, and discovering that four separate
times is worse than being told once.

---

## Step 2 — Create the Grail bucket

Profile data is high volume and worthless after the investigation. Give it a short-retention
bucket of its own so its cost is visible rather than buried in your log bill.

**Settings → Storage Management → Add bucket**

- Name: `profiling_dotnet_7d`
- Table: `logs`
- Retention: `7` days

> Do **not** use *Retain with Included Queries*: it carries a 10-day minimum and costs ~28x on
> storage. Profiling is write-heavy and read-light, so paying that to make the cheap half free
> is the wrong trade.

Routing records into it is a separate step — see [step 8](#step-8--route-records-into-your-bucket).

---

## Step 3 — Create the API token

**Settings → Access tokens → Generate new token**, with exactly:

- `openTelemetryTrace.ingest`
- `logs.ingest`
- `events.ingest`

That is the complete runtime set. Note the **two hosts**, which are not interchangeable:

```
https://<tenant>.live.dynatrace.com    ← API v2 ingest. The .apps host 404s on these paths.
https://<tenant>.apps.dynatrace.com    ← Platform: apps, dtctl, storage, DQL.
```

---

## Step 4 — Build and publish the images

Fork this repo and let CI build. Four images are produced:

| Image | What it is |
|---|---|
| `otelcol-dotnet-profiler` | Collector distribution: eBPF receiver + both connectors |
| `profile-agent` | Drives dotnet-monitor, parses nettrace, exports OTLP |
| `broker` | The one endpoint a workflow calls |
| `demo-app` | Sample workload, optional |

They publish to GHCR under your fork. If your repo is **private**, the packages are private
too — repo visibility does not propagate — so create a pull secret:

```bash
kubectl -n <namespace> create secret docker-registry ghcr \
  --docker-server=ghcr.io --docker-username=<user> --docker-password=<PAT with read:packages>
```

---

## Step 5 — Deploy the collector

```bash
kubectl create namespace dotnet-profiler

kubectl -n dotnet-profiler create secret generic dynatrace \
  --from-literal=DT_API_TOKEN='dt0c01.…' \
  --from-literal=DT_ENDPOINT='https://<tenant>.live.dynatrace.com' \
  --from-literal=DT_PLATFORM_URL='https://<tenant>.apps.dynatrace.com'

kubectl apply -f deploy/collector/rbac.yaml
kubectl apply -f deploy/collector/collector.yaml
```

Edit `deploy/collector/collector.yaml` first for your environment:

- `nodeSelector` — remove the `arm64` pin unless your nodes are Graviton
- `k8s.cluster.name` in the `resource/cluster` processor
- `samples_per_second` — 19 is a reasonable default; 99 is ~5x the data and cost

Confirm it started:

```bash
kubectl -n dotnet-profiler logs ds/dotnet-profiler-collector | grep -E "Attached|ready"
```

You want `eBPF tracer loaded`, `Attached tracer program`, `Attached sched monitor`, and
`Everything is ready`.

---

## Step 6 — Attach the sidecars to your application

Your app pod needs three containers. See `deploy/demo-app/demo-app.yaml` for a complete
working example; the parts that matter:

```yaml
initContainers:
  - name: dotnet-monitor
    image: mcr.microsoft.com/dotnet/monitor:10.0.3
    restartPolicy: Always          # native sidecar: must own the socket before the app starts
    args: ["collect", "--no-auth"]
    env:
      - { name: DOTNETMONITOR_DiagnosticPort__ConnectionMode, value: Listen }
      - { name: DOTNETMONITOR_DiagnosticPort__EndpointName,  value: /diag/dotnet-monitor.sock }
      - { name: DOTNETMONITOR_Urls, value: "http://127.0.0.1:52323" }

containers:
  - name: app
    env:
      - name: OTEL_SERVICE_NAME
        value: your-service            # REQUIRED — profiles are attributed by this
      - name: DOTNET_DiagnosticPorts
        value: /diag/dotnet-monitor.sock,nosuspend    # `nosuspend` is load-bearing
    resources:
      limits:
        memory: 1Gi                    # see the warning below

  - name: profile-agent
    image: ghcr.io/<you>/…/profile-agent:latest
    env:
      - { name: OTEL_SERVICE_NAME,      value: your-service }
      - { name: DOTNET_MONITOR_URL,     value: "http://127.0.0.1:52323" }
      - { name: PROFILER_SESSION_FILE,  value: /etc/profiler-sessions/sessions.json }
      - { name: OTEL_EXPORTER_OTLP_ENDPOINT, value: "http://dotnet-profiler-collector.dotnet-profiler.svc.cluster.local:4317" }
```

> **`BufferSizeInMB` is charged against your application container's memory limit, not the
> sidecar's.** Profiling can OOMKill the workload it is observing, and nothing in the failure
> points at the profiler. The default here is 128 MB; give the app headroom for it.

> **Omitting `nosuspend` is worse.** The default is `suspend`, which makes the runtime block at
> startup waiting for a diagnostic client — turning a sidecar crashloop into an application
> outage.

---

## Step 7 — Deploy the broker

```bash
kubectl -n dotnet-profiler create secret generic broker-auth \
  --from-literal=BROKER_TOKEN="$(openssl rand -base64 32)"

kubectl apply -f deploy/broker/broker.yaml
```

The broker mints a session id, writes the gate, and pushes events back to Dynatrace. It is
**single-replica by design** — concurrency rules live in memory, so a second replica would
enforce nothing.

Test it without Dynatrace:

```bash
kubectl -n dotnet-profiler run t --rm -it --image=curlimages/curl --restart=Never -- \
  curl -s -w '\n%{http_code}\n' -X POST \
  http://profiler-broker.dotnet-profiler.svc.cluster.local/sessions \
  -H "Authorization: Bearer $BROKER_TOKEN" -H 'Content-Type: application/json' \
  -d '{"service":"your-service","durationSeconds":90}'
```

`202` means it worked. Watch the flow:

```bash
kubectl -n dotnet-profiler logs -l app=your-app -c profile-agent -f
```

**Expect a lag.** eBPF records appear within seconds; EventPipe takes 2–3 minutes more,
because it cannot publish until the capture, symbol rundown, and nettrace parsing all finish.
On top of that, the gate is a mounted ConfigMap and takes up to ~95 seconds to reach the
kubelet. Seeing one half and not the other is normal.

---

## Step 8 — Route records into your bucket

**A log record cannot select its own bucket.** Routing happens in OpenPipeline, and there are
**two independent match points** — miss either and records land in `default_logs` at 35-day
retention.

**Settings → OpenPipeline → Logs**

1. **Dynamic routing**: add a rule matching
   `matchesValue(dt.openpipeline.source, "dotnet-profiler")` → your pipeline.
2. In that pipeline's **Storage** stage: add a `bucketAssignment` processor with the same
   matcher → bucket `profiling_dotnet_7d`.

Verify:

```
fetch logs, from:-1h
| filter isNotNull(profile.stack.folded)
| summarize count(), by:{dt.system.bucket}
```

---

## Step 9 — Install the viewer

```bash
cd app/profile-viewer
npm install
npm rebuild esbuild "@swc/core"     # npm 11 gates postinstall scripts
```

`app.config.json` ships with `environmentUrl` set to a placeholder, so either edit it or pass
the target on the command line:

```bash
npx dt-app deploy --environment-url https://<tenant>.apps.dynatrace.com
```

> **Bump `version` on every deploy.** Redeploying the same version with changed content fails
> with *"same version is already installed with a different checksum"*.

Open it, pick a session, and you should see a flame graph.

### Dynatrace Assist

The viewer includes an **"Explain this profile"** trigger that opens Dynatrace Assist with a
prompt built from the session's measured numbers — thread count, parked share, GC and lock
totals, and the heaviest frames by name.

It follows the [AI presence pattern](https://developer.dynatrace.com/design/patterns/ai-presence/):
`AiIcon` prefix, imperative label, and the required disclaimer shown next to the trigger
rather than in a footer, so it is read before the click.

Two things worth keeping if you modify it:

- **The prompt carries the numbers rather than asking Assist to fetch them**, and explicitly
  warns that sampled thread-time is wall-clock across all threads. Without that warning a model
  will confidently explain why "3,039,036 ms of CPU" occurred in a 220-second window — a
  contradiction that does not exist.
- **`execute: false`** so the question is visible before it runs. An AI trigger that fires
  immediately removes the moment where someone notices the prompt is wrong.

Requires the Assist app (`dynatrace.davis.copilot`) to be installed. If it is not, the platform
offers whatever app can handle a prompt rather than failing.

---

## Step 10 — Wire up the workflow (optional)

This is the only part that needs EdgeConnect, and the only part that needs an amd64 node.

**a. Create the EdgeConnect configuration** — UI, *Settings → EdgeConnect*:
- Host pattern: `profiler-broker.dotnet-profiler.svc.cluster.local`
- Save the OAuth **client id and secret** it gives you.

**b. Deploy EdgeConnect:**

```bash
kubectl -n dotnet-profiler create secret generic edgeconnect-oauth \
  --from-literal=oauth-client-id='dt0s10.…' \
  --from-literal=oauth-client-secret='…' \
  --from-literal=oauth-client-resource='urn:dtenvironment:<tenant>'

kubectl apply -f deploy/edgeconnect/edgeconnect.yaml
```

Edit the manifest for your tenant host. Note the env vars use **double underscores** for
nested fields (`EDGE_CONNECT_OAUTH__CLIENT_ID`); single underscores are accepted in the log
output and then fail with `missing field 'oauth'`.

Look for `Connection established` / `Connection verified`.

**c. Store the broker token in the credential vault** — *Settings → Credential vault*:
- Type: **Token**, value: your `BROKER_TOKEN`
- Scope: **AppEngine**, app access: **Workflows**
- **Uncheck owner-only access**, or only its creator can run the workflow
- Note the assigned `CREDENTIALS_VAULT-…` id

**d. Create the workflow:**

Edit `deploy/workflow/profile-on-problem.yaml` — set `credentialId`, the service name, and
your entity tags. Then create it in the UI, or:

```bash
dtctl create workflow -f deploy/workflow/profile-on-problem.yaml
```

Then, in the Workflows app: grant **Authorization settings** for the user who will run it,
and make sure that is the same identity `dtctl` authenticated as. The actor is fixed at
creation and cannot be reassigned afterwards.

---

## What it costs

> **These are Dynatrace data costs only** — log ingest, retention and query, at DPS published
> list rates (`$0.20`/GiB ingest, `$0.0007`/GiB-day retain, `$0.0035`/GiB scanned). Substitute
> your own rate card. **They are not the total cost of running this.** See
> [what is not included](#what-these-numbers-exclude) below.

| Scenario | Dynatrace data cost |
|---|---|
| One 90-second profile, one pod | **$0.0006** |
| 100-pod fleet snapshot | **$0.06** |
| Continuous, 10 pods, 30 days | **~$26** |

### What these numbers exclude

| Excluded | Roughly |
|---|---|
| **The x86 node for EdgeConnect** | ~$15/month for a `t3.small` on-demand. Only needed if you want the workflow trigger on an arm64 cluster. |
| **Compute the profiler consumes** | The collector DaemonSet requests 100m CPU / 256Mi per node; each profiled pod adds a `dotnet-monitor` and an agent sidecar (50m / 128Mi each). On a busy node this is real capacity, not rounding. |
| **EventPipe's memory against your app** | `BufferSizeInMB` is charged to the *application* container. Budget for it in the app's limit, or profiling will OOMKill the workload. |
| **Your existing cluster** | EKS control plane, nodes, EBS, data transfer — you were paying these anyway; only the x86 node above is incremental. |
| **CI** | Multi-arch container builds. Free on public GitHub repos, billed against your plan on private ones. |
| **Any Dynatrace licensing** beyond data | Host units, OneAgent, or whatever else your contract covers. |

The honest summary: **the Dynatrace data cost is small and the compute overhead is the part
worth watching**, particularly the sidecar memory charged against your application.

**Ingest is ~94% of the bill.** The two levers that matter:

1. **Scope to your workloads.** The DaemonSet profiles the *whole node* — without a filter you
   pay to profile every unrelated process on it.
2. **Sample rate.** 19 Hz vs 99 Hz is ~5x.

**Queries are the tail risk, not ingest.** An unbounded scan of a 7-day bucket at 100 pods is
~$1.48 per run — on a one-minute dashboard refresh that is ~$2,100/day. Always bound queries
by `profile.session_id`. The viewer does; anything you build should too.

---

## Known limitations

State these to your stakeholders before they find them:

- **Correlation is thread-level, not per-sample.** Profile samples join to spans on thread id
  plus time window. The OTLP model's per-sample trace/span fields are deliberately left null
  rather than guessed at.
- **Native frames never symbolize on Alpine.** Managed and kernel frames resolve at 100%;
  native ELF at 0%, because Alpine strips and no debuginfo is published for Microsoft's musl
  runtime. They appear as `module+0xaddress`. This is permanent, not pending.
- **Allocation type attribution is unavailable** at `Informational` event level; enabling
  `Verbose` for it turns verbose on for every keyword and is a large volume increase.
- **`BufferSizeInMB: 128` is an unmeasured default.** It has not OOMKilled anything in testing,
  but that is not a measurement. Measure it against your workload.
- **The images are built for `linux/arm64` and `linux/amd64`** — check
  `.github/workflows/build.yml` matches your node architecture before deploying.
