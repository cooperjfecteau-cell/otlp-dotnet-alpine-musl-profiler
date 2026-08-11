# Alpine/musl .NET image — the whole point of the project.
#
# Framework-dependent publish, deliberately. The eBPF profiler locates the runtime
# by the path regex /(\d+)\.(\d+).(\d+)/libcoreclr.so$, which only holds for a
# framework-dependent layout. Switching to self-contained or single-file for a
# smaller image would silently blind the profiler while the app kept working.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

COPY DemoApp.csproj .
ARG TARGETARCH
RUN dotnet restore -a $TARGETARCH

COPY . .
RUN dotnet publish -a $TARGETARCH --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app
COPY --from=build /app .

# dotnet-monitor connects to this socket in Listen mode from the sidecar.
# `nosuspend` is load-bearing: the default when the modifier is omitted is
# `suspend`, which makes the runtime block at startup waiting for a diagnostic
# client — turning a sidecar crashloop into an application outage.
ENV DOTNET_DiagnosticPorts=/diag/dotnet-monitor.sock,nosuspend

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "DemoApp.dll"]
