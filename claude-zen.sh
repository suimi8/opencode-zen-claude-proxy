#!/bin/zsh

set -euo pipefail

SCRIPT_PATH="$0"
while [[ -L "$SCRIPT_PATH" ]]; do
  LINK_TARGET="$(readlink "$SCRIPT_PATH")"
  if [[ "$LINK_TARGET" == /* ]]; then
    SCRIPT_PATH="$LINK_TARGET"
  else
    SCRIPT_DIR="$(cd "$(dirname "$SCRIPT_PATH")" && pwd)"
    SCRIPT_PATH="$SCRIPT_DIR/$LINK_TARGET"
  fi
done

ROOT="$(cd "$(dirname "$SCRIPT_PATH")" && pwd)"
ENV_FILE="$ROOT/.env.zen"
SETTINGS_FILE="$ROOT/zen-claude-settings.json"
LOG_FILE="$(mktemp -t claude-zen-proxy.XXXXXX.log)"
proxy_pid=""

cleanup() {
  if [[ -n "$proxy_pid" ]] && kill -0 "$proxy_pid" 2>/dev/null; then
    kill "$proxy_pid" 2>/dev/null || true
    wait "$proxy_pid" 2>/dev/null || true
  fi
  rm -f "$LOG_FILE"
}

trap cleanup EXIT INT TERM

if [[ ! -f "$ENV_FILE" ]]; then
  echo "claude-zen: missing environment file: $ENV_FILE" >&2
  echo "Create it with: cp .env.zen.example .env.zen" >&2
  echo "Then edit .env.zen and set UPSTREAM_API_KEY." >&2
  exit 1
fi

set -a
source "$ENV_FILE"
set +a

if lsof -nP -iTCP:${PORT} -sTCP:LISTEN >/dev/null 2>&1; then
  echo "claude-zen: port ${PORT} is already in use. Stop the existing process first." >&2
  exit 1
fi

cd "$ROOT"
npm start >"$LOG_FILE" 2>&1 &
proxy_pid=$!

for _ in {1..80}; do
  if curl -fsS -H "x-api-key: ${PROXY_API_KEY}" "http://${HOST}:${PORT}/health" >/dev/null 2>&1; then
    break
  fi
  sleep 0.25
done

if ! curl -fsS -H "x-api-key: ${PROXY_API_KEY}" "http://${HOST}:${PORT}/health" >/dev/null 2>&1; then
  echo "claude-zen: proxy failed to start." >&2
  cat "$LOG_FILE" >&2
  exit 1
fi

ANTHROPIC_BASE_URL="http://${HOST}:${PORT}" \
ANTHROPIC_API_KEY="${PROXY_API_KEY}" \
ANTHROPIC_MODEL="${ANTHROPIC_MODEL_ALIAS}" \
claude --settings "$SETTINGS_FILE" "$@"
