# syntax=docker/dockerfile:1
ARG DOTNET_SDK_VERSION=10.0.401
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/IncidentDesk.Api/IncidentDesk.Api.csproj --locked-mode
RUN dotnet publish src/IncidentDesk.Api/IncidentDesk.Api.csproj \
    --configuration Release --no-restore --output /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/keys \
    && chown app:app /app/keys
COPY --from=build /out .
COPY --chmod=755 scripts/docker-entrypoint.sh /app/docker-entrypoint.sh
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["/app/docker-entrypoint.sh"]
