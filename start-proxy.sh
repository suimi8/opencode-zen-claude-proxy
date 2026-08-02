#!/bin/zsh

set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"

set -a
if [[ -f "$ROOT/.env.local" ]]; then
  source "$ROOT/.env.local"
fi
set +a

cd "$ROOT"
exec npm start
