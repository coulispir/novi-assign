curl -sS -X POST http://localhost:5000/api/wallets -H 'Content-Type: application/json' -d '{"currency":"EUR","initialBalance":100}'

curl -sS http://localhost:5000/api/wallets/1

curl -sS 'http://localhost:5000/api/wallets/1?currency=USD'

curl -sS -X POST 'http://localhost:5000/api/wallets/1/adjustbalance?amount=10&currency=EUR&strategy=AddFundsStrategy' -H "Idempotency-Key: $(uuidgen)"

curl -sS -X POST 'http://localhost:5000/api/wallets/1/adjustbalance?amount=10&currency=EUR&strategy=SubtractFundsStrategy' -H "Idempotency-Key: $(uuidgen)"

curl -sS -X POST 'http://localhost:5000/api/wallets/1/adjustbalance?amount=10&currency=EUR&strategy=ForceSubtractFundsStrategy' -H "Idempotency-Key: $(uuidgen)"

# Idempotency: sending the same key twice applies the adjustment once; the second response carries "Idempotent-Replayed: true"
KEY=$(uuidgen)
curl -sS -i -X POST 'http://localhost:5000/api/wallets/1/adjustbalance?amount=5&currency=EUR&strategy=AddFundsStrategy' -H "Idempotency-Key: $KEY"
curl -sS -i -X POST 'http://localhost:5000/api/wallets/1/adjustbalance?amount=5&currency=EUR&strategy=AddFundsStrategy' -H "Idempotency-Key: $KEY"

# Reusing the key with different parameters is rejected with 422
curl -sS -i -X POST 'http://localhost:5000/api/wallets/1/adjustbalance?amount=7&currency=EUR&strategy=AddFundsStrategy' -H "Idempotency-Key: $KEY"
