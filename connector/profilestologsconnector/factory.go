package profilestologsconnector

import (
	"context"

	"go.opentelemetry.io/collector/component"
	"go.opentelemetry.io/collector/connector"
	"go.opentelemetry.io/collector/connector/xconnector"
	"go.opentelemetry.io/collector/consumer"
)

const (
	scopeName    = "github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/connector/profilestologsconnector"
	scopeVersion = "0.1.0"
)

var componentType = component.MustNewType("profilestologs")

// NewFactory builds the profiles-to-logs connector factory.
//
// Stability is Development deliberately: the OTLP profiles proto is still
// package v1development with every message marked Alpha, so claiming anything
// higher would misrepresent what a consumer is depending on.
func NewFactory() connector.Factory {
	return xconnector.NewFactory(
		componentType,
		func() component.Config { return createDefaultConfig() },
		xconnector.WithProfilesToLogs(createProfilesToLogs, component.StabilityLevelDevelopment),
	)
}

func createProfilesToLogs(
	_ context.Context,
	set connector.Settings,
	cfg component.Config,
	next consumer.Logs,
) (xconnector.Profiles, error) {
	c := cfg.(*Config)

	pl := &profilesToLogs{
		cfg:    c,
		next:   next,
		logger: set.Logger,
	}
	if c.Gating.Enabled {
		pl.sessions = newSessionStore(c.Gating.SessionFile, c.Gating.ReloadInterval, set.Logger)
	}
	return pl, nil
}
