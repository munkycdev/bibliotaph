#!/bin/sh
# Starts the server. With REPLICA_URL (abs://account@container/path) its databases live in Blob storage and /data is
# only a working copy; without it, as in a container run on a developer's machine, the data lasts as long as the container.
set -eu
cd /app

if [ -z "${REPLICA_URL:-}" ]; then
  exec dotnet Bibliotaph.Server.dll "$@"
fi

export Server__ReplicaUrl="$REPLICA_URL"
mkdir -p /data/users
# accounts.db comes back before the server starts; each library comes back when its owner first opens it.
litestream restore -config /etc/litestream.yml -if-db-not-exists -if-replica-exists /data/accounts.db
exec litestream replicate -config /etc/litestream.yml -exec "dotnet Bibliotaph.Server.dll"
