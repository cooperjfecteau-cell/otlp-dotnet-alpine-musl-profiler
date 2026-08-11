/**
 * Queries for one profiling session.
 *
 * Every query is bounded by session_id, never by a time range alone. Query cost is
 * billed on bytes scanned, and an unbounded scan of the profile bucket measured at
 * $1.48 per run at 100 pods — enough that a dashboard refresh would cost more per
 * day than the entire pipeline costs per month.
 */

import { num, type FlameRow } from "./flame";

/** DQL string literals are double-quoted; escape anything that would break out. */
function quote(value: string): string {
  return `"${value.replace(/\\/g, "\\\\").replace(/"/g, '\\"')}"`;
}

/**
 * A session is a bounded window, but records land some minutes after it closes —
 * EventPipe publishes only once rundown and parsing finish. A generous lookback
 * costs little because the session_id filter does the real narrowing.
 */
const LOOKBACK = "-24h";

export function buildSummaryQuery(sessionId: string): string {
  return `fetch logs, from:${LOOKBACK}
| filter profile.session_id == ${quote(sessionId)}
| summarize records    = count(),
            samples    = sum(toLong(profile.sample_count)),
            cpu_ms     = round(sum(toLong(profile.cpu_ns)) / 1000000.0, decimals: 0),
            gc_count   = countIf(profile.event_type == "gc"),
            gc_ms      = round(sum(toLong(gc.duration_ns)) / 1000000.0, decimals: 1),
            lock_waits = sum(toLong(contention.count)),
            lock_ms    = round(sum(toLong(contention.total_duration_ns)) / 1000000.0, decimals: 1),
            threads    = countDistinctExact(thread.id),
            truncated  = countIf(profile.stack.truncated == "true"),
            by:{service.name}`;
}

/**
 * Flame graph input.
 *
 * Grouped on stack hash rather than the folded string: the key is 16 characters
 * instead of up to 30,000. Measured on a real session, the top 400 stacks carry
 * 99.94% of total weight, so the limit is not meaningfully lossy.
 *
 * takeFirst() will NOT wrap an expression — takeFirst(toLong(x)) returns zero rows
 * rather than erroring, which is why depth is derived client-side instead.
 */
export function buildFlameQuery(sessionId: string): string {
  return `fetch logs, from:${LOOKBACK}
| filter profile.session_id == ${quote(sessionId)}
| filter isNotNull(profile.stack.folded)
| filter profile.event_type != "contention" or isNull(profile.event_type)
| summarize samples = sum(toLong(profile.sample_count)),
            folded  = takeFirst(profile.stack.folded),
            source  = takeFirst(profile.source),
            by:{profile.stack.hash}
| sort samples desc
| limit 400`;
}

/**
 * Contention, weighted by blocked nanoseconds rather than sample count.
 *
 * Same folding, same renderer, different weight — and the thing eBPF structurally
 * cannot produce. On the reference session this was 60.9 seconds of blocked wait in
 * a 220-second window.
 */
export function buildContentionQuery(sessionId: string): string {
  return `fetch logs, from:${LOOKBACK}
| filter profile.session_id == ${quote(sessionId)}
| filter profile.event_type == "contention"
| summarize wait_ns = sum(toLong(contention.total_duration_ns)),
            waits   = sum(toLong(contention.count)),
            folded  = takeFirst(profile.stack.folded),
            by:{profile.stack.hash}
| sort wait_ns desc
| limit 200`;
}

/** Recent sessions, for the picker when the app is opened without a deep link. */
export function buildSessionListQuery(): string {
  return `fetch logs, from:${LOOKBACK}
| filter isNotNull(profile.session_id)
| summarize records = count(),
            first   = min(timestamp),
            service = takeFirst(service.name),
            by:{profile.session_id}
| sort first desc
| limit 25`;
}

export interface SessionSummary {
  service: string;
  records: number;
  samples: number;
  cpuMs: number;
  gcCount: number;
  gcMs: number;
  lockWaits: number;
  lockMs: number;
  threads: number;
  truncated: number;
}

export function toSummary(records: Record<string, unknown>[] | undefined): SessionSummary | null {
  if (!records || records.length === 0) return null;
  const r = records[0];
  return {
    service: String(r["service.name"] ?? "unknown"),
    records: num(r.records),
    samples: num(r.samples),
    cpuMs: num(r.cpu_ms),
    gcCount: num(r.gc_count),
    gcMs: num(r.gc_ms),
    lockWaits: num(r.lock_waits),
    lockMs: num(r.lock_ms),
    threads: num(r.threads),
    truncated: num(r.truncated),
  };
}

export function toFlameRows(records: Record<string, unknown>[] | undefined): FlameRow[] {
  if (!records) return [];
  return records
    .filter((r) => typeof r.folded === "string" && (r.folded as string).length > 0)
    .map((r) => ({
      folded: r.folded as string,
      weight: num(r.samples),
      // The eBPF connector leaves profile.source unset; only the agent stamps it.
      source: (r.source === "eventpipe" ? "eventpipe" : "ebpf") as "ebpf" | "eventpipe",
    }));
}

export function toContentionRows(records: Record<string, unknown>[] | undefined): FlameRow[] {
  if (!records) return [];
  return records
    .filter((r) => typeof r.folded === "string" && (r.folded as string).length > 0)
    .map((r) => ({
      folded: r.folded as string,
      weight: num(r.wait_ns),
      source: "eventpipe" as const,
    }));
}

export interface SessionListEntry {
  sessionId: string;
  service: string;
  records: number;
  /** Start of the session, used as the primary label — see shortLabel. */
  startedAt: Date | null;
}

export function toSessionList(records: Record<string, unknown>[] | undefined): SessionListEntry[] {
  if (!records) return [];
  return records
    .map((r) => ({
      sessionId: String(r["profile.session_id"] ?? ""),
      service: String(r.service ?? "unknown"),
      records: num(r.records),
      startedAt: r.first ? new Date(String(r.first)) : null,
    }))
    .filter((s) => s.sessionId.length > 0);
}

/**
 * Label for the session picker.
 *
 * Leads with the timestamp because neither of the obvious choices distinguishes
 * anything: every session from one workload repeats the same service name, and
 * ULIDs are time-sortable so their PREFIXES are shared too — `01KZSFV1…` and
 * `01KZSFG1…` are different sessions that look identical until the eighth
 * character. The dropdown truncates long labels, so anything shared has to come
 * last.
 *
 * The ULID's tail is its random component, which is what actually tells two
 * sessions apart at a glance.
 */
export function shortLabel(s: SessionListEntry): string {
  const when = s.startedAt
    ? s.startedAt.toLocaleString(undefined, {
        month: "short",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
      })
    : "unknown time";
  const tail = s.sessionId.slice(-6);
  return `${when} · …${tail} · ${s.service}`;
}
