module github.com/cooperjfecteau-cell/otlp-dotnet-alpine-musl-profiler/connector/profilestologsconnector

go 1.24.0

// Versions track the upstream otelcol-ebpf-profiler distribution manifest at
// 0.158.0, so this connector drops into that distribution without forcing a
// version bump on anything else. CI runs `go mod tidy` and uploads the resolved
// go.mod/go.sum as an artifact -- if these pins are wrong, take the resolved
// versions from there rather than guessing again.
require (
	go.opentelemetry.io/collector/component v1.64.0
	go.opentelemetry.io/collector/connector v0.158.0
	go.opentelemetry.io/collector/connector/xconnector v0.158.0
	go.opentelemetry.io/collector/consumer v1.64.0
	go.opentelemetry.io/collector/pdata v1.64.0
	go.opentelemetry.io/collector/pdata/pprofile v0.158.0
	go.uber.org/zap v1.27.0
)
