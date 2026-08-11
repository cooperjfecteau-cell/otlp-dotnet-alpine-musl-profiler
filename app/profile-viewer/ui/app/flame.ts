/**
 * Folded stacks to a laid-out flame graph.
 *
 * The producers hand us `root;mid;leaf` strings with a weight; all tree building
 * happens here rather than in DQL, because reconstructing a tree in DQL means
 * recursive splitting and the signal is billed by bytes scanned.
 */

export interface FlameRow {
  /** `root;mid;leaf`, root-first. */
  folded: string;
  /** Sample count, or wait nanoseconds for the contention view. */
  weight: number;
  source: "ebpf" | "eventpipe";
}

export interface FlameNode {
  name: string;
  /** Total weight of this subtree. */
  value: number;
  depth: number;
  /** Fraction of the root's weight, 0..1 — the rendered width. */
  x0: number;
  x1: number;
  children: FlameNode[];
  /** True when this frame is a thread parked rather than running. */
  parked: boolean;
}

export interface BuildResult {
  root: FlameNode;
  total: number;
  maxDepth: number;
  /** Weight excluded by the idle filter, so the UI can say what it hid. */
  parkedWeight: number;
  parkedStacks: number;
}

/**
 * Frames that mean "this thread is asleep".
 *
 * EventPipe's SampleProfiler is a WALL-CLOCK sampler: it samples every thread,
 * including parked ones. Measured on a reference session, 54% of all sample weight
 * was threads doing nothing, and the single largest stack was
 * LowLevelLifoSemaphore.WaitNative at 28% of the profile.
 *
 * Rendering that unfiltered puts "waiting" at the top of the flame graph and buries
 * the actual work, so the viewer filters it by default — loudly, never silently.
 */
const PARKED_FRAMES = [
  "LowLevelLifoSemaphore",
  "WaitHandle.WaitOneNoCheck",
  "Monitor.Wait",
  "Thread.Sleep",
  "SpinWait",
  "ManualResetEventSlim.Wait",
  "SemaphoreSlim.Wait",
  "Interop+Sys.Read",
  "epoll_wait",
  "PortableThreadPool+WorkerThread.WaitForRequest",
];

export function isParkedStack(folded: string): boolean {
  // Tested against the whole path, not just the leaf: a thread parked three frames
  // above its leaf is still parked, and leaf-only matching missed those.
  return PARKED_FRAMES.some((f) => folded.includes(f));
}

export interface BuildOptions {
  /** Hide stacks that represent a parked thread. Default on — see PARKED_FRAMES. */
  hideParked: boolean;
  /** Only include rows from this producer. */
  source?: "ebpf" | "eventpipe";
}

export function buildFlame(rows: FlameRow[], opts: BuildOptions): BuildResult {
  let parkedWeight = 0;
  let parkedStacks = 0;

  const included = rows.filter((r) => {
    if (opts.source && r.source !== opts.source) return false;
    if (isParkedStack(r.folded)) {
      parkedWeight += r.weight;
      parkedStacks++;
      return !opts.hideParked;
    }
    return true;
  });

  const root: FlameNode = {
    name: "all", value: 0, depth: 0, x0: 0, x1: 1, children: [], parked: false,
  };

  for (const row of included) {
    if (row.weight <= 0) continue;
    const frames = row.folded.split(";").filter((f) => f.length > 0);
    if (frames.length === 0) continue;

    root.value += row.weight;
    let node = root;

    for (const frame of frames) {
      let child = node.children.find((c) => c.name === frame);
      if (!child) {
        child = {
          name: frame,
          value: 0,
          depth: node.depth + 1,
          x0: 0,
          x1: 0,
          children: [],
          parked: PARKED_FRAMES.some((p) => frame.includes(p)),
        };
        node.children.push(child);
      }
      child.value += row.weight;
      node = child;
    }
  }

  const maxDepth = layout(root, 0, root.value);
  return { root, total: root.value, maxDepth, parkedWeight, parkedStacks };
}

/**
 * Assigns each node a horizontal span proportional to its weight.
 *
 * Children are sorted by weight so the graph is stable between renders — without
 * it, insertion order shuffles blocks around on every refresh and the same profile
 * looks different each time you open it.
 */
function layout(node: FlameNode, x0: number, total: number): number {
  node.x0 = x0;
  node.x1 = total > 0 ? x0 + node.value / total : x0;

  node.children.sort((a, b) => b.value - a.value || a.name.localeCompare(b.name));

  let cursor = x0;
  let deepest = node.depth;
  for (const child of node.children) {
    deepest = Math.max(deepest, layout(child, cursor, total));
    cursor += total > 0 ? child.value / total : 0;
  }
  return deepest;
}

/** Flattens the tree to a render list, dropping slivers too narrow to see. */
export function toBlocks(root: FlameNode, minWidth: number): FlameNode[] {
  const out: FlameNode[] = [];
  const walk = (n: FlameNode) => {
    // The root is the container, not a frame; skip it but keep its children.
    if (n.depth > 0) {
      if (n.x1 - n.x0 < minWidth) return; // Sub-pixel: drawing it costs more than it shows.
      out.push(n);
    }
    n.children.forEach(walk);
  };
  walk(root);
  return out;
}

/** Grail returns numerics as strings in some encodings; normalise defensively. */
export function num(value: unknown): number {
  if (typeof value === "number") return value;
  if (typeof value === "string") {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : 0;
  }
  return 0;
}
