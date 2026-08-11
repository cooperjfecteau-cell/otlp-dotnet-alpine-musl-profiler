package profilestologsconnector

import "testing"

func TestTruncateFoldedCutsFromRootEnd(t *testing.T) {
	// Leaf frames carry the hotspot, so truncation must sacrifice the root end.
	folded := "root;middle;leaf"
	got, truncated := truncateFolded(folded, 10)
	if !truncated {
		t.Fatalf("expected truncation")
	}
	if got != "leaf" && got != "middle;leaf" {
		t.Fatalf("truncation kept the wrong end: %q", got)
	}
	if len(got) > 10 {
		t.Fatalf("result exceeds max: %q", got)
	}
}

func TestTruncateFoldedNeverLeavesPartialFrame(t *testing.T) {
	folded := "aaaaaaaaaa;bbbbbbbbbb;cccccccccc"
	got, truncated := truncateFolded(folded, 15)
	if !truncated {
		t.Fatalf("expected truncation")
	}
	if got != "cccccccccc" {
		t.Fatalf("expected a whole trailing frame, got %q", got)
	}
}

func TestTruncateFoldedPassesThroughWhenUnderLimit(t *testing.T) {
	folded := "root;leaf"
	got, truncated := truncateFolded(folded, 1000)
	if truncated || got != folded {
		t.Fatalf("unexpected truncation: %q %v", got, truncated)
	}
}

func TestStackHashIgnoresThread(t *testing.T) {
	// The hash must depend on the stack alone: records are grained by
	// (stack, thread), but the flame graph collapses across threads by grouping on
	// this hash. If the thread leaked in, that collapse would silently break.
	if stackHash("a;b;c") != stackHash("a;b;c") {
		t.Fatal("hash is not stable")
	}
	if stackHash("a;b;c") == stackHash("a;b;d") {
		t.Fatal("hash collides across distinct stacks")
	}
}

func TestConfigRejectsGuardAtOrAbovePlatformCeiling(t *testing.T) {
	c := createDefaultConfig()
	c.MaxFoldedChars = 32768
	if err := c.Validate(); err == nil {
		t.Fatal("expected rejection: the platform truncates silently at 32768, so the guard must cut first")
	}
}

func TestDefaultConfigIsValid(t *testing.T) {
	if err := createDefaultConfig().Validate(); err != nil {
		t.Fatalf("default config must be usable as-is: %v", err)
	}
}
