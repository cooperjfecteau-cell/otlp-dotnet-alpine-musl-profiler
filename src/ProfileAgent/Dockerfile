# Profile agent: drives dotnet-monitor, parses nettrace, exports OTLP logs.
#
# Alpine/musl, unlike the dotnet-monitor sidecar it talks to. That asymmetry is
# deliberate and worth understanding:
#
#   - dotnet-monitor has no musl image past 7.3.4, so it runs glibc (#4).
#   - TraceEvent DOES work on musl/arm64 (#8), so this container does not have to.
#
# The two communicate over HTTP on localhost, and dotnet-monitor reaches the app
# over a Unix socket carrying diagnostic IPC — a byte stream, not an ABI — so libc
# never has to match across any of the three containers.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

COPY ProfileAgent.csproj .
ARG TARGETARCH
RUN dotnet restore -a $TARGETARCH

COPY . .
RUN dotnet publish -a $TARGETARCH --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0-alpine
WORKDIR /app
COPY --from=build /app .

# TraceLog.CreateFromEventPipeDataFile writes an intermediate .etlx at roughly 9x
# the nettrace size (#8). This needs to be a real volume with room, not a token
# emptyDir — running out of space here fails in a way that looks nothing like disk.
ENV PROFILER_WORK_DIR=/var/tmp/profiles
RUN mkdir -p /var/tmp/profiles

ENTRYPOINT ["dotnet", "ProfileAgent.dll"]
