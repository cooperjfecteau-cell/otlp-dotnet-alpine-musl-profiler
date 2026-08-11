FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
WORKDIR /src

COPY Broker.csproj .
ARG TARGETARCH
RUN dotnet restore -a $TARGETARCH

COPY . .
RUN dotnet publish -a $TARGETARCH --no-restore -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine
WORKDIR /app
COPY --from=build /app .

# Nothing profiles this container, so unlike the demo app there is no reason to
# avoid a hardened runtime posture here.
USER $APP_UID

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Broker.dll"]
