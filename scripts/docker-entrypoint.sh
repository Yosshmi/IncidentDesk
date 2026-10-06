#!/bin/sh
set -eu

export ASPNETCORE_HTTP_PORTS="${PORT:-${ASPNETCORE_HTTP_PORTS:-8080}}"

if [ "${INCIDENTDESK_BOOTSTRAP_DEMO:-false}" = "true" ] && [ "$#" -eq 0 ]; then
    dotnet IncidentDesk.Api.dll --migrate --seed-demo
fi

exec dotnet IncidentDesk.Api.dll "$@"
