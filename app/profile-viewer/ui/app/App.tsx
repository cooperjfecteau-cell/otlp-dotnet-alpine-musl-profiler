import React, { useEffect, useMemo, useRef, useState } from "react";
import { Flex } from "@dynatrace/strato-components/layouts";
import { Heading, Text } from "@dynatrace/strato-components/typography";
import { Button } from "@dynatrace/strato-components/buttons";
import { Select, ToggleButtonGroup } from "@dynatrace/strato-components/forms";
import { ProgressCircle } from "@dynatrace/strato-components/content";
import Colors from "@dynatrace/strato-design-tokens/colors";
import { useDql } from "@dynatrace-sdk/react-hooks";

import {
  buildContentionQuery,
  buildFlameQuery,
  buildSessionListQuery,
  buildSummaryQuery,
  toContentionRows,
  toFlameRows,
  shortLabel,
  toSessionList,
  toSummary,
} from "./dql";
import { buildFlame } from "./flame";
import { FlameGraph } from "./components/FlameGraph";
import { AI_DISCLAIMER, askAssist, buildPrompt } from "./assist";
import { AiIcon } from "@dynatrace/strato-icons";

type Source = "eventpipe" | "ebpf";

export function App() {
  // Deep-linked on session id alone: the session record carries its own window and
  // service, so one parameter is enough and the link stays short enough to embed in
  // a Dynatrace problem annotation.
  const [sessionId, setSessionId] = useState<string>(() => {
    const params = new URLSearchParams(window.location.search);
    return params.get("session") ?? "";
  });

  const [source, setSource] = useState<Source>("eventpipe");
  const [hideParked, setHideParked] = useState(true);

  const containerRef = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(1000);

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const observer = new ResizeObserver(([entry]) => {
      setWidth(Math.max(600, entry.contentRect.width));
    });
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  const has = sessionId.length > 0;

  // useDql has no "no query" state, so gate with `enabled` rather than passing an
  // empty string — an unset session must not fire a scan.
  const sessions = useDql(buildSessionListQuery(), { enabled: !has });
  const summary = useDql(has ? buildSummaryQuery(sessionId) : "", { enabled: has });
  const flame = useDql(has ? buildFlameQuery(sessionId) : "", { enabled: has });
  const contention = useDql(has ? buildContentionQuery(sessionId) : "", { enabled: has });

  const sessionList = useMemo(
    () => toSessionList(sessions.data?.records as Record<string, unknown>[] | undefined),
    [sessions.data]
  );
  const stats = useMemo(
    () => toSummary(summary.data?.records as Record<string, unknown>[] | undefined),
    [summary.data]
  );
  const flameRows = useMemo(
    () => toFlameRows(flame.data?.records as Record<string, unknown>[] | undefined),
    [flame.data]
  );
  const contentionRows = useMemo(
    () => toContentionRows(contention.data?.records as Record<string, unknown>[] | undefined),
    [contention.data]
  );

  const cpuGraph = useMemo(
    () => buildFlame(flameRows, { hideParked, source }),
    [flameRows, hideParked, source]
  );
  const lockGraph = useMemo(
    // Never filtered: a parked thread IS the finding here, not noise.
    () => buildFlame(contentionRows, { hideParked: false }),
    [contentionRows]
  );

  const sourcesPresent = useMemo(() => {
    const set = new Set(flameRows.map((r) => r.source));
    return { ebpf: set.has("ebpf"), eventpipe: set.has("eventpipe") };
  }, [flameRows]);

  const fmt = new Intl.NumberFormat();
  const loading = summary.isLoading || flame.isLoading;

  return (
    <Flex flexDirection="column" gap={16} padding={24}>
      <Flex flexDirection="column" gap={4}>
        <Heading level={1}>Profile Viewer</Heading>
        <Text style={{ color: Colors.Text.Neutral.Subdued }}>
          .NET profiling sessions captured on Alpine/musl. Records arrive as OTLP logs because
          Dynatrace does not ingest the OpenTelemetry profiles signal yet.
        </Text>
      </Flex>

      {!has && (
        <Flex flexDirection="column" gap={8}>
          <Text>Pick a recent session, or open this app from a problem annotation.</Text>
          {sessions.isLoading && <ProgressCircle />}
          <Select
            name="session"
            value=""
            onChange={(v) => typeof v === "string" && setSessionId(v)}
          >
            <Select.Content>
              {sessionList.map((s) => (
                <Select.Option key={s.sessionId} value={s.sessionId}>
                  {shortLabel(s)}
                </Select.Option>
              ))}
            </Select.Content>
          </Select>

          {/* The dropdown truncates, so the full detail lives here where it has
              room. */}
          {sessionList.length > 0 && (
            <Text style={{ color: Colors.Text.Neutral.Subdued, fontSize: 12 }}>
              {sessionList.length} recent session(s) · newest{" "}
              {sessionList[0].sessionId} ({fmt.format(sessionList[0].records)} records)
            </Text>
          )}
        </Flex>
      )}

      {has && (
        <>
          {/* Numbers first, graph second. On the reference session, 60.9 seconds of
              blocked lock wait in a 220-second window explained the problem more
              directly than any flame graph did. */}
          {stats && (
            <Flex gap={24} flexWrap="wrap">
              <Stat label="Service" value={stats.service} />
              {/* NOT labelled "CPU". EventPipe's SampleProfiler is a wall-clock
                  sampler across every thread, so this figure includes parked ones
                  and routinely exceeds the wall duration of the session many times
                  over — 3,039,036 ms across 53 threads in a 220-second window on the
                  reference session. Calling that "CPU" would be a lie the reader has
                  no way to catch. */}
              <Stat
                label="Sampled thread-time"
                value={`${fmt.format(Math.round(stats.cpuMs))} ms across all threads`}
              />
              <Stat label="Threads" value={fmt.format(stats.threads)} />
              <Stat
                label="GC"
                value={`${fmt.format(stats.gcCount)} collections · ${stats.gcMs} ms`}
              />
              <Stat
                label="Lock contention"
                value={`${fmt.format(stats.lockWaits)} waits · ${fmt.format(
                  Math.round(stats.lockMs)
                )} ms`}
                emphasis={stats.lockMs > 1000}
              />
            </Flex>
          )}

          {stats && stats.truncated > 0 && (
            <Text style={{ color: Colors.Text.Warning.Default }}>
              {fmt.format(stats.truncated)} stacks were truncated at 30,000 characters. Deep
              paths are under-represented in the graph below.
            </Text>
          )}

          <Flex gap={12} alignItems="center" flexWrap="wrap">
            {/* Never merged. eBPF samples at 19 Hz node-wide, EventPipe at ~1 kHz
                in-process; the weights are not commensurable, so a combined graph
                would be arithmetic nonsense dressed up as insight. */}
            <ToggleButtonGroup value={source} onChange={(v) => setSource(v as Source)}>
              <ToggleButtonGroup.Item value="eventpipe" disabled={!sourcesPresent.eventpipe}>
                EventPipe
              </ToggleButtonGroup.Item>
              <ToggleButtonGroup.Item value="ebpf" disabled={!sourcesPresent.ebpf}>
                eBPF
              </ToggleButtonGroup.Item>
            </ToggleButtonGroup>

            <Button variant="default" onClick={() => setHideParked((s) => !s)}>
              {hideParked ? "Show parked threads" : "Hide parked threads"}
            </Button>

            <Button variant="default" onClick={() => setSessionId("")}>
              Change session
            </Button>

            {/* AI trigger, per the AI presence pattern: AiIcon prefix, and the
                label reads "[imperative verb] [object]". Disabled until there is
                actually a profile to reason about — offering AI over an empty
                graph invites a confidently wrong answer about nothing. */}
            <Button
              variant="default"
              onClick={() =>
                askAssist(
                  buildPrompt(
                    sessionId,
                    stats,
                    cpuGraph,
                    cpuGraph.total + cpuGraph.parkedWeight > 0
                      ? cpuGraph.parkedWeight / (cpuGraph.total + cpuGraph.parkedWeight)
                      : 0
                  )
                )
              }
              disabled={flameRows.length === 0}
            >
              <Button.Prefix>
                <AiIcon />
              </Button.Prefix>
              Explain this profile
            </Button>
          </Flex>

          {/* Required wherever AI output is offered. Kept next to the trigger
              rather than buried in a footer, so it is read before the click and
              not after. */}
          {flameRows.length > 0 && (
            <Text style={{ color: Colors.Text.Neutral.Subdued, fontSize: 12 }}>
              {AI_DISCLAIMER}
            </Text>
          )}

          {/* Hidden loudly, never silently: parked weight is exactly what a latency
              investigation wants when the answer is "everything was waiting". */}
          {hideParked && cpuGraph.parkedWeight > 0 && (
            <Text style={{ color: Colors.Text.Neutral.Subdued }}>
              {fmt.format(cpuGraph.parkedWeight)} samples across {cpuGraph.parkedStacks} stacks
              are threads parked rather than running, and are hidden.{" "}
              {cpuGraph.total > 0 &&
                `That is ${(
                  (cpuGraph.parkedWeight / (cpuGraph.total + cpuGraph.parkedWeight)) *
                  100
                ).toFixed(0)}% of sampled weight.`}
            </Text>
          )}

          <div ref={containerRef} style={{ width: "100%" }}>
            {loading && (
              <Flex alignItems="center" gap={8} padding={24}>
                <ProgressCircle />
                <Text>Querying Grail…</Text>
              </Flex>
            )}
            {flame.error && (
              <Text style={{ color: Colors.Text.Critical.Default }}>
                Query failed: {String(flame.error)}
              </Text>
            )}
            {!loading && !flame.error && flameRows.length === 0 && (
              <Text style={{ color: Colors.Text.Neutral.Subdued }}>
                No profile records for this session. EventPipe publishes only after rundown and
                parsing complete, which can lag the window by a few minutes.
              </Text>
            )}
            {!loading && !flame.error && flameRows.length > 0 && (
              <FlameGraph result={cpuGraph} width={width} unit="samples" />
            )}
          </div>

          {contentionRows.length > 0 && (
            <Flex flexDirection="column" gap={8}>
              <Heading level={2}>Lock contention</Heading>
              <Text style={{ color: Colors.Text.Neutral.Subdued }}>
                Weighted by time blocked, not sample count. eBPF cannot produce this — it sees a
                thread parked and nothing about why.
              </Text>
              <FlameGraph result={lockGraph} width={width} unit="wait" />
            </Flex>
          )}
        </>
      )}
    </Flex>
  );
}

function Stat({
  label,
  value,
  emphasis,
}: {
  label: string;
  value: string;
  emphasis?: boolean;
}) {
  return (
    <Flex flexDirection="column" gap={2}>
      <Text style={{ color: Colors.Text.Neutral.Subdued, fontSize: 12 }}>{label}</Text>
      <Text
        style={{
          fontSize: 18,
          fontWeight: 600,
          color: emphasis ? Colors.Text.Warning.Default : Colors.Text.Neutral.Default,
        }}
      >
        {value}
      </Text>
    </Flex>
  );
}
