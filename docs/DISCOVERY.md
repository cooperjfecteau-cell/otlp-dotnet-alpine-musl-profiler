# Discovery

How to fill in the [cost model](COST_MODEL.md) with facts instead of guesses.

The model has eleven inputs, which feels like a lot to estimate. It isn't: **three of them
determine most of the answer, and all three are things you can count in about ten minutes.** The
input that feels hardest to estimate — how much data a profiling session produces — is worth
about 11% of the total, and you can measure it exactly with one pilot session for a quarter of a
cent.

Work in that order. Don't start by trying to predict data volume.

---

## Where the number actually comes from

Each row perturbs one input against the 10-pod baseline in `COST_MODEL.md` and holds the rest
fixed:

| Input | Perturbation | Effect on total | Effort to discover |
|---|---|---:|---|
| **Node cost** | $49 → $98/month | **+96%** | Trivial — cloud bill |
| **EdgeConnect node needed?** | on → off | **−58%** | Trivial — `kubectl get nodes` |
| **Pods profiled** | 10 → 20 | **+33%** | Trivial — `kubectl get pods` |
| **Collector scoped?** | scoped → unscoped | **+33%** | It's a config choice, not a discovery |
| Session data volume | **4× estimation error** | +11% | One pilot session |
| Sessions per pod/day | 4 → 8 | +4% | One DQL query |
| Retention | 7 → 30 days | +0.4% | It's a policy choice |
| Flame graph views/day | 20 → 100 | +0.1% | Don't bother — see below |

Read the top three again: they are **what your nodes cost, what architecture they are, and how
many pods get a sidecar.** None of them require the pipeline to be running, and none of them are
estimates.

Being 4× wrong about the genuinely uncertain input moves the total less than being 2× wrong about
pod count. That is why this is easier than it looks.

> **One exception, and it is the important one.** Everything above assumes queries are bounded by
> `profile.session_id`. Unbounded, query cost scales with fleet size *and* view count
> simultaneously — it is the only term in the model that compounds, and at 100 pods it overtakes
> every other line. This isn't a discovery question, it's a build requirement. See
> [lever 3](COST_MODEL.md#the-four-levers-in-order-of-leverage).

---

## Step 1 — Count the cluster (10 minutes, no deployment)

### Nodes, architecture, instance types

```bash
kubectl get nodes -o custom-columns=\
'NAME:.metadata.name,ARCH:.status.nodeInfo.architecture,TYPE:.metadata.labels.node\.kubernetes\.io/instance-type,CPU:.status.capacity.cpu,MEM:.status.capacity.memory'
```

This settles three model inputs at once:

- **`nodes`** — the collector DaemonSet runs on every one.
- **`node_vCPU` / `node_GiB`** — the denominators in `sidecar_frac`.
- **`ARCH`** — if any value is `arm64`, you need the **x86 EdgeConnect node**, which is the
  single largest swing in the model at small fleet sizes. If you are entirely on `amd64`, set
  EdgeConnect to zero and delete 58% of the estimate.

Price the instance type from your cloud bill or the vendor's on-demand rate to get **`node_$`**.
Use your effective rate — Savings Plans, RIs, or Spot change this input more than any profiling
decision will.

### Count the pods

```bash
kubectl get pods -A -o jsonpath=\
'{range .items[*]}{.metadata.namespace}{"/"}{.metadata.name}{"\t"}{range .spec.containers[*]}{.image}{" "}{end}{"\n"}{end}' \
  | grep -Ei 'dotnet|aspnet'
```

Image names are a starting point, not an answer — plenty of .NET services ship under a product
image name. Cross-check against whatever you use as a service catalogue.

**Confirm musl** on a candidate before assuming this pipeline is even the right tool. If it comes
back glibc, standard .NET profiling already works and you do not need any of this:

```bash
kubectl exec -n NAMESPACE POD -c CONTAINER -- sh -c \
  'head -1 /etc/os-release; ls /lib/ld-musl-* 2>/dev/null || echo "glibc — you may not need this pipeline"'
```

### Check memory headroom

Not a cost input, but discover it now rather than during an incident. EventPipe's
`BufferSizeInMB` is charged against the **application** container's limit, so a pod already near
its ceiling will OOMKill under profiling:

```bash
kubectl get pods -A -o custom-columns=\
'NS:.metadata.namespace,POD:.metadata.name,MEMLIM:.spec.containers[*].resources.limits.memory'
```

Any candidate whose limit leaves less than ~256Mi of headroom needs its limit raised before you
profile it.

---

## Step 2 — Get the session rate from Dynatrace (5 minutes)

Sessions are triggered by Davis problems, so **your historical problem rate *is* the
sessions-per-day input.** You don't have to estimate it, and you shouldn't — it's already been
recorded for you.

```bash
dtctl query -f docs/queries/discover-problem-rate.dql
```

Real output from a live tenant, 30-day window:

| namespace | workload | problems | sessions_per_day |
|---|---|---:|---:|
| dynatrace | dynatrace-operator | 204 | **6.80** |
| astroshop | checkout | 8 | **0.27** |
| astroshop | frontend | 7 | **0.23** |
| astroshop | payment | 7 | **0.23** |
| astroshop | accounting | 5 | **0.17** |

Note how low these are. The cost model defaults to **4 sessions/pod/day**, which is roughly **16×**
what a healthy workload actually produces. That default is deliberately pessimistic; your real
number will almost certainly be lower, which pushes the answer even further toward "compute
dominates, data is noise."

The one outlier is instructive too: a workload firing 6.8 problems/day is a workload with an
alerting problem, and fixing that is worth more than any profiling optimization.

> **Note:** you can also run this in a notebook — `docs/queries/*.dql` are plain DQL. `dtctl` is
> just convenient for scripting.

### What Dynatrace *cannot* tell you

```bash
dtctl query -f docs/queries/discover-technologies.dql
```

This enumerates the runtimes OneAgent has detected. It is useful context, but **a zero for .NET
is not evidence you have no .NET** — OneAgent's .NET detection on Alpine/musl is precisely the gap
this pipeline exists to fill, so musl .NET workloads frequently report as `LIBC`, `CONTAINERD`, or
nothing. Count from the cluster side (Step 1), and treat a Dynatrace zero as weak evidence *for*
needing this, not against.

---

## Step 3 — Measure one session instead of estimating volume (one afternoon)

This is the step that replaces the model's least certain input with ground truth, and it costs
**$0.0025**.

1. Deploy the collector and attach the sidecars to **one** pod, per
   [GETTING_STARTED](GETTING_STARTED.md) steps 5–7.
2. Trigger one session against it — by hand through the broker is fine, no workflow needed.
3. Run the measurement:

```dql
fetch logs, from:-6h
| filter isNotNull(profile.session_id)
| fieldsAdd half = if(isNull(profile.source), "ebpf", else:profile.source)
| fieldsAdd bytes = stringLength(profile.stack.folded) + 400
| summarize records = count(),
            total_kb = round(sum(bytes) / 1024.0, decimals: 0),
            by:{profile.session_id, half}
```

Divide `total_kb` by 1024 to get your own **`session_MiB`**, and substitute it for the model's
12.3 MiB baseline. Every remaining number is then arithmetic on facts.

**Do this before you deploy fleet-wide, not after.** A workload with deeper stacks, more threads,
or heavier contention than the reference app will produce more per session, and one pod tells you
by how much.

### Measuring the scope factor, if you want it

`F` — the penalty for letting the eBPF DaemonSet profile the whole node instead of your services —
is set to 10 in the model as an order-of-magnitude judgement, not a measurement. If you want your
real number, run the collector **unfiltered** for five minutes during the pilot and compare:

```dql
fetch logs, from:-15m
| filter isNotNull(profile.session_id)
| summarize records = count(), by:{service.name}
| sort records desc
```

`F` = total records ÷ records for your services. Then turn the filter on and leave it on.

---

## The worksheet

| Model input | Source | Settled by |
|---|---|---|
| `nodes`, `node_vCPU`, `node_GiB` | `kubectl get nodes` | Step 1 |
| `node_$` | Cloud bill, at your effective rate | Step 1 |
| EdgeConnect node needed? | Node `ARCH` — arm64 means yes | Step 1 |
| `N` pods profiled | `kubectl get pods` + musl confirmation | Step 1 |
| `S` sessions/pod/day | `discover-problem-rate.dql` | Step 2 |
| `session_MiB` | One pilot session | Step 3 |
| `F` scope factor | Config choice; measurable in Step 3 | Step 3 |
| `D` duration, `H` sample rate | Policy. Defaults of 90 s / 19 Hz resolved 12-frame chains end to end | — |
| `R` retention | Policy. Barely affects cost — 7→30 days is +0.4% | — |
| `V` views/day | **Don't discover this.** Bounded, 100 views/day is +0.1%. Bound your queries and the input stops mattering | — |
| `P_i`, `P_r`, `P_q` | Your DPS rate card | — |

Feed the results into the [interactive calculator](COST_MODEL.md#interactive-calculator).

---

## If you can't run any of this yet

Scoping a customer environment you have no access to, in rough order of value:

1. **Ask what their nodes are and what they pay for them.** Instance type plus effective rate
   gets you most of the answer, and arm64-vs-amd64 decides the EdgeConnect line.
2. **Ask how many .NET services are on Alpine.** Not how many pods — services. Multiply by
   replica count later.
3. **Assume 0.25 sessions/pod/day**, not 4. That matches observed problem rates far better than
   the model's pessimistic default.
4. **Use the 12.3 MiB baseline unchanged.** Being wrong by 4× costs you 11% of the estimate,
   which is inside the noise on the node pricing anyway.

Quote the result as a range, and say which inputs it is sensitive to. With the top three settled
and the rest at defaults, the estimate is comfortably good enough to decide with.
