#!/bin/sh
# Starts the edge. Two things the environment cannot express directly:
#
# - An image built without REVISION has GameEdge__Revision set to an empty string, which the edge
#   refuses. Empty means "not built from a known commit", so it is removed and /health/deployed
#   answers 503 with a null revision instead.
# - Terminate mode signs with a private key read from a file (GameEdge:Handoff:SigningKeyPath). A
#   stack passes the key as GAME_EDGE_HANDOFF_SIGNING_KEY_PEM_B64, the base64 of its PEM (Infra's
#   tools/New-SigningKey.ps1 makes it). It is written to the signing key path, readable by this
#   user only, and removed from the environment before the edge starts. The path should be on a
#   tmpfs, so the key never reaches a layer or a volume. The value is never printed.
set -eu

if [ -z "${GameEdge__Revision:-}" ]; then
  unset GameEdge__Revision
fi

if [ -n "${GAME_EDGE_HANDOFF_SIGNING_KEY_PEM_B64:-}" ]; then
  key_path="${GameEdge__Handoff__SigningKeyPath:?GAME_EDGE_HANDOFF_SIGNING_KEY_PEM_B64 is set, so GameEdge__Handoff__SigningKeyPath must be too}"
  mkdir -p "$(dirname "$key_path")"
  (umask 077 && printf '%s' "$GAME_EDGE_HANDOFF_SIGNING_KEY_PEM_B64" | base64 -d > "$key_path")
  unset GAME_EDGE_HANDOFF_SIGNING_KEY_PEM_B64
fi

exec dotnet SubZeroDev.Platform.GameEdge.dll "$@"
