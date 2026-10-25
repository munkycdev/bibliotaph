#!/bin/sh
# Prints a one-time invitation link: bibliotaph-invite [--admin] [--note <text>]. infra/invite.ps1 runs it with
# az containerapp exec, which starts outside /app and without the server's start script.
set -eu
cd /app
if [ -n "${REPLICA_URL:-}" ]; then export Server__ReplicaUrl="$REPLICA_URL"; fi
exec dotnet Bibliotaph.Server.dll invite "$@"
