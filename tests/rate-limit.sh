#!/usr/bin/env bash
# Demonstrates the per-client rate limits against a running stack (default or load-balanced setup).
#
#   tests/rate-limit.sh [base-url]        default: http://localhost:5000
#
# Rate limit counters are reset between sections so each one starts from a clean budget.
set -euo pipefail

BASE_URL="${1:-http://localhost:5000}"
REDIS_CONTAINER="${REDIS_CONTAINER:-redis-container}"

section() { printf '\n=== %s\n' "$1"; }

# Deletes only rate limit keys, leaving any other Redis data (e.g. cache entries) untouched
reset_counters() {
  # Keys contain spaces (e.g. "GET api/wallets"), so delete them one line at a time rather than via xargs
  docker exec "$REDIS_CONTAINER" sh -c "redis-cli --scan --pattern 'rl:*' | while IFS= read -r key; do redis-cli del \"\$key\" > /dev/null; done"
}

new_uuid() { uuidgen 2>/dev/null || cat /proc/sys/kernel/random/uuid; }

# Prints the status code, plus the serving replica when running behind nginx
status_line='%{http_code} %header{x-upstream}\n'

if ! curl -s -o /dev/null "$BASE_URL/api/wallets/1"; then
  echo "API is not reachable at $BASE_URL. Start it with 'docker compose up -d' first." >&2
  exit 1
fi

reset_counters
WALLET_ID=$(curl -s -X POST "$BASE_URL/api/wallets" -H 'Content-Type: application/json' -d '{"currency":"EUR","initialBalance":100}' \
  | sed -E 's/.*"id":([0-9]+).*/\1/')
echo "Using wallet $WALLET_ID"

section "GET /api/wallets/{id}: limit 10 per second. Expect 10 x 200, then 429"
reset_counters
for _ in $(seq 12); do curl -s -o /dev/null -w "$status_line" "$BASE_URL/api/wallets/$WALLET_ID"; done

section "Rejected response: 429 with Retry-After and a JSON body"
reset_counters
for _ in $(seq 10); do curl -s -o /dev/null "$BASE_URL/api/wallets/$WALLET_ID"; done
curl -si "$BASE_URL/api/wallets/$WALLET_ID"
echo

section "POST /api/wallets: limit 5 per minute. Expect 5 x 201, then 429"
reset_counters
for _ in $(seq 7); do
  curl -s -o /dev/null -w '%{http_code} retry-after=%header{retry-after}\n' \
    -X POST "$BASE_URL/api/wallets" -H 'Content-Type: application/json' -d '{"currency":"EUR"}'
done

section "POST /api/wallets/{id}/adjustbalance: limit 30 per minute. Expect 30 x 200, 5 x 429"
reset_counters
for _ in $(seq 35); do
  curl -s -o /dev/null -w '%{http_code}\n' -X POST \
    "$BASE_URL/api/wallets/$WALLET_ID/adjustbalance?amount=1&currency=EUR&strategy=AddFundsStrategy" \
    -H "Idempotency-Key: $(new_uuid)"
done | sort | uniq -c

section "Endpoints have separate budgets: GET still works after exhausting adjustbalance. Expect 200"
curl -s -o /dev/null -w '%{http_code}\n' "$BASE_URL/api/wallets/$WALLET_ID"

section "50 concurrent GETs: the atomic Redis check lets exactly 10 through"
reset_counters
seq 50 | xargs -P 50 -I{} curl -s -o /dev/null -w '%{http_code}\n' "$BASE_URL/api/wallets/$WALLET_ID" | sort | uniq -c

section "Rate limit counters in Redis (client IP | endpoint)"
docker exec "$REDIS_CONTAINER" redis-cli --scan --pattern 'rl:*' | grep -v ':exp$' || true
