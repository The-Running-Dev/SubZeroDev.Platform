#!/bin/sh
# Starts game-service. The handoff verifier reads public keys from files named <key id>.pem in
# GAME_SERVICE_HANDOFF_KEYS_DIR. A stack passes the edge's verification key as
# GAME_SERVICE_HANDOFF_VERIFICATION_KEY_PEM_B64 (the base64 of the public key PEM) with its key id
# in GAME_SERVICE_HANDOFF_KEY_ID; it is written there before the service starts. A public key is not
# secret, but the directory should still be a tmpfs, so an old key cannot outlive the stack's
# configuration. Rotation (game-edge README, "The handoff key and its rotation") is a change to these
# two values, applied to game-service before the edge.
set -eu

if [ -n "${GAME_SERVICE_HANDOFF_VERIFICATION_KEY_PEM_B64:-}" ]; then
  keys_dir="${GAME_SERVICE_HANDOFF_KEYS_DIR:?GAME_SERVICE_HANDOFF_VERIFICATION_KEY_PEM_B64 is set, so GAME_SERVICE_HANDOFF_KEYS_DIR must be too}"
  key_id="${GAME_SERVICE_HANDOFF_KEY_ID:?GAME_SERVICE_HANDOFF_VERIFICATION_KEY_PEM_B64 is set, so GAME_SERVICE_HANDOFF_KEY_ID must be too}"
  case "$key_id" in
    *[!A-Za-z0-9._-]* | .* ) echo "GAME_SERVICE_HANDOFF_KEY_ID must be letters, digits, '.', '_' or '-', not starting with '.'." >&2; exit 1 ;;
  esac
  mkdir -p "$keys_dir"
  printf '%s' "$GAME_SERVICE_HANDOFF_VERIFICATION_KEY_PEM_B64" | base64 -d > "$keys_dir/$key_id.pem"
  unset GAME_SERVICE_HANDOFF_VERIFICATION_KEY_PEM_B64
fi

exec node_modules/.bin/tsx src/main.ts "$@"
