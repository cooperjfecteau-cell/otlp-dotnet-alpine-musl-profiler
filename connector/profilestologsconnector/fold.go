package profilestologsconnector

import (
	"hash/fnv"
	"strconv"
	"strings"

	"go.opentelemetry.io/collector/pdata/pprofile"
)

// foldedStack renders a Stack as a root-first, semicolon-separated frame list.
//
// The spec stores location_indices LEAF-FIRST, which is the opposite of how every
// flame graph tool expects folded stacks. Getting this backwards produces a graph
// that renders upside down and looks plausible, so the reversal is the single most
// important line in this file.
//
// A Location may carry several Lines when the compiler inlined callees into it.
// Those are also emitted leaf-first within the location, so they reverse too.
func foldedStack(dic pprofile.ProfilesDictionary, stack pprofile.Stack) (folded string, depth int) {
	locIdx := stack.LocationIndices()
	strTable := dic.StringTable()
	fnTable := dic.FunctionTable()
	locTable := dic.LocationTable()

	frames := make([]string, 0, locIdx.Len())

	for i := locIdx.Len() - 1; i >= 0; i-- {
		li := int(locIdx.At(i))
		if li < 0 || li >= locTable.Len() {
			continue
		}
		loc := locTable.At(li)
		lines := loc.Lines()

		if lines.Len() == 0 {
			// Unsymbolized native frame. #7 measured these at 100% of native ELF on
			// Alpine, because the runtime .so files are stripped and no debuginfo is
			// published. Emitting module+address is honest and still useful; dropping
			// them would silently shorten stacks.
			frames = append(frames, unsymbolizedFrame(dic, loc))
			continue
		}

		for j := lines.Len() - 1; j >= 0; j-- {
			line := lines.At(j)
			fi := int(line.FunctionIndex())
			if fi < 0 || fi >= fnTable.Len() {
				continue
			}
			si := int(fnTable.At(fi).NameStrindex())
			if si < 0 || si >= strTable.Len() {
				continue
			}
			name := strTable.At(si)
			if name == "" {
				name = "<anonymous>"
			}
			frames = append(frames, name)
		}
	}

	return strings.Join(frames, ";"), len(frames)
}

// unsymbolizedFrame names a frame we could not resolve, preserving the module and
// offset so it is still greppable and can be symbolized later out of band.
func unsymbolizedFrame(dic pprofile.ProfilesDictionary, loc pprofile.Location) string {
	module := "<unknown>"
	mi := int(loc.MappingIndex())
	mapTable := dic.MappingTable()
	strTable := dic.StringTable()
	if mi >= 0 && mi < mapTable.Len() {
		fi := int(mapTable.At(mi).FilenameStrindex())
		if fi >= 0 && fi < strTable.Len() && strTable.At(fi) != "" {
			module = strTable.At(fi)
			if idx := strings.LastIndexByte(module, '/'); idx >= 0 && idx+1 < len(module) {
				module = module[idx+1:]
			}
		}
	}
	return module + "+0x" + strconv.FormatUint(loc.Address(), 16)
}

// stackHash hashes the folded stack. Deliberately the stack ALONE, never including
// the thread: records are grained by (stack, thread), but the flame-graph query
// collapses across threads by grouping on this hash. Mixing the thread in here
// would silently break that collapse.
func stackHash(folded string) string {
	h := fnv.New64a()
	_, _ = h.Write([]byte(folded))
	return strconv.FormatUint(h.Sum64(), 16)
}

// truncateFolded enforces our own ceiling below the platform's.
//
// Dynatrace truncates attribute values at exactly 32,768 characters SILENTLY -
// measured in #27. Since the stacks that overflow are the deepest ones, silent
// truncation biases every flame graph toward shallow paths while looking healthy.
// So we cut first, deliberately, and flag it.
//
// Cuts from the ROOT end: leaf frames carry the hotspot and are what the graph is
// read for. Losing the outermost frames costs context; losing the innermost costs
// the answer.
func truncateFolded(folded string, max int) (out string, truncated bool) {
	if max <= 0 || len(folded) <= max {
		return folded, false
	}
	cut := folded[len(folded)-max:]
	// Never leave a half-eaten frame name at the root end.
	if i := strings.IndexByte(cut, ';'); i >= 0 && i+1 < len(cut) {
		cut = cut[i+1:]
	}
	return cut, true
}
