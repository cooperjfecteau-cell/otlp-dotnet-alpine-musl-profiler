# Runtime image for the profiling collector distribution.
#
# Debian slim rather than Alpine or distroless-static, deliberately: the eBPF
# profiler may link against glibc depending on build flags, and a musl base would
# fail at exec time with an error that looks nothing like the cause. The irony of a
# glibc base in a musl-profiling project is noted in the docs -- this container is
# ours, and what it profiles is unaffected by what it links against.
FROM debian:bookworm-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates \
 && rm -rf /var/lib/apt/lists/*

ARG TARGETARCH=arm64
COPY _build/otelcol-dotnet-profiler-${TARGETARCH} /usr/local/bin/otelcol-dotnet-profiler

# No USER directive on purpose. The eBPF profiler needs privileged execution --
# the cluster has unprivileged_bpf_disabled=1 and perf_event_paranoid=2, so it
# requires CAP_BPF + CAP_PERFMON + CAP_SYS_ADMIN at minimum. Dropping to a
# non-root user here would only move the failure later and make it harder to read.

ENTRYPOINT ["/usr/local/bin/otelcol-dotnet-profiler"]
