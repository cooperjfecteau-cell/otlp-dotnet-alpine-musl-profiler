package profilestologsconnector

import (
	"errors"
	"time"
)

// Config controls how OTLP profiles become log records.
//
// Defaults are chosen so that running this connector with an empty config block
// produces correct, cost-bounded output rather than something that needs tuning
// before it is safe.
type Config struct {
	// MaxFoldedChars bounds the folded stack we emit. Must stay below the
	// platform's silent 32,768-character attribute ceiling (#27); the margin
	// absorbs multi-byte frame names, since the platform counts differently than
	// Go's len() on a UTF-8 string.
	MaxFoldedChars int `mapstructure:"max_folded_chars"`

	// SchemaVersion is stamped on every record. The OTLP profiles proto is still
	// v1development with every message marked Alpha, so field names can still move
	// and a consumer needs to know which shape it is reading.
	SchemaVersion string `mapstructure:"schema_version"`

	// ResourceMarker attributes are set at resource level on every emitted record.
	// OpenPipeline's bucketAssignment matcher tests these to route records into a
	// dedicated bucket - a record cannot select its own bucket (#6).
	ResourceMarker map[string]string `mapstructure:"resource_marker"`

	// Gating controls whether per-stack records are emitted continuously or only
	// during an active session.
	//
	// The intended deployment is gated: the always-on cost tier is the metrics
	// connector, and this expensive per-stack tier runs only when a Dynatrace
	// workflow triggers a session. With gating enabled and no active session, this
	// connector emits NOTHING - not a reduced sample, not a summary. Anything else
	// reintroduces the cost the two-tier design exists to control.
	Gating GatingConfig `mapstructure:"gating"`
}

type GatingConfig struct {
	Enabled bool `mapstructure:"enabled"`

	// SessionFile is watched for the active session set. A file is used rather than
	// an HTTP endpoint because this runs as a DaemonSet: the broker writes one
	// ConfigMap and every node observes it, instead of the broker enumerating pods
	// and fanning out N calls it must then retry individually.
	SessionFile string `mapstructure:"session_file"`

	// ReloadInterval bounds how stale the session set may be. Kubernetes ConfigMap
	// propagation to a mounted volume is itself on the order of a minute, so this
	// being fast does not make the whole path fast - see the README.
	ReloadInterval time.Duration `mapstructure:"reload_interval"`
}

func createDefaultConfig() *Config {
	return &Config{
		MaxFoldedChars: 30000,
		SchemaVersion:  "otlp-profiles-v1development/1",
		ResourceMarker: map[string]string{
			"dt.openpipeline.source": "dotnet-profiler",
		},
		Gating: GatingConfig{
			Enabled:        true,
			SessionFile:    "/etc/profiler-sessions/sessions.json",
			ReloadInterval: 5 * time.Second,
		},
	}
}

func (c *Config) Validate() error {
	if c.MaxFoldedChars <= 0 {
		return errors.New("max_folded_chars must be positive")
	}
	if c.MaxFoldedChars >= 32768 {
		return errors.New(
			"max_folded_chars must be below 32768: the platform truncates attribute " +
				"values at exactly 32768 characters silently, so the guard must cut first")
	}
	if c.Gating.Enabled {
		if c.Gating.SessionFile == "" {
			return errors.New("gating.session_file is required when gating is enabled")
		}
		if c.Gating.ReloadInterval <= 0 {
			return errors.New("gating.reload_interval must be positive")
		}
	}
	return nil
}
