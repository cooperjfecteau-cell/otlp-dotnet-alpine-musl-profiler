import React, { useMemo, useRef, useState } from "react";
import Colors from "@dynatrace/strato-design-tokens/colors";
import { toBlocks, type BuildResult, type FlameNode } from "../flame";

interface Props {
  result: BuildResult;
  width: number;
  /** How the weight should be described in the tooltip. */
  unit: "samples" | "wait";
  onSelect?: (frame: FlameNode) => void;
}

const ROW_HEIGHT = 18;
const ROW_GAP = 1;

/**
 * Colour by frame origin rather than at random.
 *
 * The conventional flame graph uses random warm hues, which carries no information.
 * Here the palette answers the question this project exists to ask: is this frame
 * managed .NET, the runtime itself, the kernel, or unresolved native code? On
 * Alpine that last category is large and permanent, and colouring it distinctly is
 * more honest than letting it blend in.
 */
function frameColour(name: string): string {
  if (name.includes("+0x")) return Colors.Charts.Categorical.Color09.Default; // unsymbolised
  if (name.startsWith("System.") || name.startsWith("Microsoft.")) {
    return Colors.Charts.Categorical.Color03.Default; // framework
  }
  if (/^[a-z_]+$/.test(name) || name.startsWith("el0") || name.startsWith("do_")) {
    return Colors.Charts.Categorical.Color06.Default; // kernel
  }
  return Colors.Charts.Categorical.Color01.Default; // application
}

interface HoverState {
  x: number;
  y: number;
  node: FlameNode;
}

export function FlameGraph({ result, width, unit, onSelect }: Props) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [hover, setHover] = useState<HoverState | null>(null);

  // Sub-pixel blocks cost more to draw than they convey. At 1200px wide this drops
  // frames under about one pixel.
  const minWidth = 1 / Math.max(width, 1);
  const blocks = useMemo(() => toBlocks(result.root, minWidth), [result, minWidth]);

  const height = (result.maxDepth + 1) * (ROW_HEIGHT + ROW_GAP) + 8;
  const fmt = new Intl.NumberFormat();

  function describe(node: FlameNode): string {
    const pct = result.total > 0 ? ((node.value / result.total) * 100).toFixed(1) : "0";
    if (unit === "wait") {
      return `${(node.value / 1_000_000).toFixed(1)} ms blocked (${pct}%)`;
    }
    return `${fmt.format(node.value)} samples (${pct}%)`;
  }

  if (result.total === 0) {
    return (
      <div style={{ padding: 24, color: Colors.Text.Neutral.Subdued }}>
        Nothing to show with the current filters.
      </div>
    );
  }

  return (
    <div style={{ position: "relative", width: "100%" }}>
      <svg ref={svgRef} width={width} height={height} role="img" aria-label="Flame graph">
        {blocks.map((node, i) => {
          const x = node.x0 * width;
          const w = Math.max((node.x1 - node.x0) * width, 1);
          const y = (node.depth - 1) * (ROW_HEIGHT + ROW_GAP);
          // Rendering text into a block narrower than a few characters produces
          // unreadable slivers; the tooltip carries the name instead.
          const showLabel = w > 34;

          return (
            <g
              key={`${node.depth}-${node.name}-${i}`}
              onMouseMove={(e) => {
                const rect = svgRef.current?.getBoundingClientRect();
                setHover({
                  x: e.clientX - (rect?.left ?? 0),
                  y: e.clientY - (rect?.top ?? 0),
                  node,
                });
              }}
              onMouseLeave={() => setHover(null)}
              onClick={() => onSelect?.(node)}
              style={{ cursor: onSelect ? "pointer" : "default" }}
            >
              <rect
                x={x}
                y={y}
                width={w}
                height={ROW_HEIGHT}
                fill={frameColour(node.name)}
                stroke={Colors.Background.Surface.Default}
                strokeWidth={0.5}
                rx={1}
                opacity={node.parked ? 0.45 : 1}
              />
              {showLabel && (
                <text
                  x={x + 4}
                  y={y + ROW_HEIGHT - 5}
                  fontSize={11}
                  fill={Colors.Text.Neutral.Default}
                  style={{ pointerEvents: "none", userSelect: "none" }}
                >
                  {truncate(node.name, Math.floor(w / 6.2))}
                </text>
              )}
            </g>
          );
        })}
      </svg>

      {hover && (
        <div
          style={{
            position: "absolute",
            left: Math.min(hover.x + 12, width - 380),
            top: hover.y + 16,
            maxWidth: 380,
            padding: "8px 10px",
            background: Colors.Background.Container.Neutral.Emphasized,
            border: `1px solid ${Colors.Border.Neutral.Default}`,
            borderRadius: 4,
            fontSize: 12,
            color: Colors.Text.Neutral.Default,
            pointerEvents: "none",
            zIndex: 10,
            wordBreak: "break-all",
          }}
        >
          <div style={{ fontWeight: 600, marginBottom: 4 }}>{hover.node.name}</div>
          <div style={{ color: Colors.Text.Neutral.Subdued }}>{describe(hover.node)}</div>
          {hover.node.parked && (
            <div style={{ color: Colors.Text.Warning.Default, marginTop: 4 }}>
              Thread parked — not consuming CPU
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function truncate(s: string, max: number): string {
  if (max <= 1) return "";
  if (s.length <= max) return s;
  // Keep the tail: the distinguishing part of a .NET frame is the method name, and
  // the namespace prefix is usually shared with its neighbours.
  return "…" + s.slice(-(max - 1));
}
