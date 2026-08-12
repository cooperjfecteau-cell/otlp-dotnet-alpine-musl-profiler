/**
 * Dynatrace Assist integration, following the AI presence pattern.
 *
 * https://developer.dynatrace.com/design/patterns/ai-presence/
 *
 * The pattern exists so a user always knows when AI is involved — it is a
 * transparency requirement, not decoration, and supports compliance with
 * frameworks like the EU AI Act. Three obligations it places on us:
 *
 *   1. AI triggers are prefixed with `AiIcon` and read "[verb] [object]".
 *   2. The disclaimer below must be shown wherever AI output is offered.
 *   3. AI-generated content is visually distinguished from measured data.
 *
 * That third one matters more here than in most apps. This viewer's entire value
 * is that its numbers are measured; an AI summary sitting next to them without
 * distinction would undermine the thing that makes the tool trustworthy.
 */

import { sendIntent } from "@dynatrace-sdk/navigation";
import type { SessionSummary } from "./dql";
import type { BuildResult, FlameNode } from "./flame";

/** Required by the AI presence pattern wherever AI output is offered. */
export const AI_DISCLAIMER =
  "Dynatrace Intelligence uses AI. Always verify important information and decisions.";

/**
 * Intent contract, read from the tenant rather than guessed:
 *   dtctl get intents → dynatrace.davis.copilot/ask-question
 *   required: prompt (string); optional: contexts (array), execute (boolean)
 */
const ASSIST_APP_ID = "dynatrace.davis.copilot";
const ASSIST_INTENT_ID = "ask-question";

/** The heaviest frames, which is what a question about a profile is really about. */
function topFrames(graph: BuildResult, count: number): FlameNode[] {
  const leaves: FlameNode[] = [];
  const walk = (n: FlameNode) => {
    if (n.depth > 0 && n.children.length === 0) leaves.push(n);
    n.children.forEach(walk);
  };
  walk(graph.root);
  return leaves.sort((a, b) => b.value - a.value).slice(0, count);
}

/**
 * Builds the prompt from what was actually measured.
 *
 * Deliberately states the numbers rather than asking Assist to fetch them: the
 * app already has them, and a prompt carrying real values gets a grounded answer
 * instead of a plausible-sounding guess. It also names the sampling caveat,
 * because a model told "3,039,036 ms of CPU in a 220-second window" without being
 * told that is wall-clock across all threads will confidently explain a
 * contradiction that does not exist.
 */
export function buildPrompt(
  sessionId: string,
  stats: SessionSummary | null,
  graph: BuildResult,
  parkedShare: number
): string {
  const lines: string[] = [];

  lines.push(
    `I am investigating a .NET profiling session captured from an Alpine/musl container.`,
    `Session ${sessionId}${stats ? ` for service "${stats.service}"` : ""}.`,
    ``
  );

  if (stats) {
    lines.push(`Measured over the session window:`);
    lines.push(`- ${stats.threads} threads sampled`);
    lines.push(
      `- ${Math.round(stats.cpuMs).toLocaleString()} ms of sampled thread-time. NOTE: this is` +
        ` wall-clock sampling across every thread, including parked ones, so it is not CPU time` +
        ` and legitimately exceeds the wall duration of the session.`
    );
    lines.push(`- ${Math.round(parkedShare * 100)}% of sampled weight was threads parked, not running`);
    if (stats.gcCount > 0) {
      lines.push(`- ${stats.gcCount} garbage collections totalling ${stats.gcMs} ms`);
    }
    if (stats.lockWaits > 0) {
      lines.push(
        `- ${stats.lockWaits.toLocaleString()} blocking lock waits totalling` +
          ` ${Math.round(stats.lockMs).toLocaleString()} ms`
      );
    }
    if (stats.truncated > 0) {
      lines.push(
        `- ${stats.truncated} stacks were truncated, so the deepest call paths are` +
          ` under-represented`
      );
    }
    lines.push(``);
  }

  const top = topFrames(graph, 8);
  if (top.length > 0) {
    lines.push(`Heaviest frames after excluding parked threads:`);
    for (const f of top) {
      const pct = graph.total > 0 ? ((f.value / graph.total) * 100).toFixed(1) : "0";
      lines.push(`- ${pct}% — ${f.name}`);
    }
    lines.push(``);
  }

  lines.push(
    `What is this workload spending its time on, and what would you investigate first?`,
    `Note that native frames shown as "module+0xaddress" cannot be symbolized on Alpine,`,
    `so treat them as unresolved rather than as missing information.`
  );

  return lines.join("\n");
}

/**
 * Opens Assist with the prompt.
 *
 * `execute: false` so the user sees the question before it runs. An AI trigger
 * that fires immediately removes the moment where someone can notice the prompt
 * is wrong, which is exactly the transparency the pattern is protecting.
 */
export function askAssist(prompt: string): void {
  sendIntent(
    { prompt, execute: false },
    {
      // "recommended" is the operative word: if Assist is not installed, the
      // platform falls back to offering whatever app can handle a prompt rather
      // than failing. That fallback is why this is a recommendation and not a
      // hardcoded navigation.
      recommendedAppId: ASSIST_APP_ID,
      recommendedIntentId: ASSIST_INTENT_ID,
      // `keyProperties` is deliberately omitted. It is optional, and the SDK's
      // overload resolution rejects every spelling of it here — the selected
      // overload types it as `undefined`. Not worth contorting the call for an
      // option that changes nothing about behaviour.
    }
  );
}
