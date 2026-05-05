#!/usr/bin/env bash
# End-to-end smoke test: upload → pipeline → chat
#
# Prerequisites:
#   export API_URL="https://<id>.execute-api.us-west-2.amazonaws.com/dev"
#   export API_TOKEN="<cognito access token>"   # from browser devtools or aws cognito-idp
#
# Quick way to get a token for a test user (requires aws CLI + cognito user pool):
#   API_TOKEN=$(aws cognito-idp initiate-auth \
#     --auth-flow USER_PASSWORD_AUTH \
#     --client-id $USER_POOL_CLIENT_ID \
#     --auth-parameters USERNAME=admin@acme-demo.com,PASSWORD=YourPassword \
#     --query AuthenticationResult.AccessToken --output text)

set -euo pipefail

API_URL="${API_URL:?Set API_URL}"
API_TOKEN="${API_TOKEN:?Set API_TOKEN}"
AUTH="Authorization: Bearer $API_TOKEN"
MAX_WAIT=120  # seconds to wait for pipeline

pass() { echo "  ✓ $1"; }
fail() { echo "  ✗ $1"; exit 1; }

echo ""
echo "=== Sift smoke test ==="
echo "API: $API_URL"
echo ""

# ── 1. List documents (baseline) ─────────────────────────────────────────────
echo "1. List documents"
STATUS=$(curl -sf -o /dev/null -w "%{http_code}" -H "$AUTH" "$API_URL/documents")
[ "$STATUS" = "200" ] && pass "GET /documents → 200" || fail "GET /documents → $STATUS"

# ── 2. Get presigned upload URL ───────────────────────────────────────────────
echo "2. Request presigned upload URL"
UPLOAD_RESP=$(curl -sf -X POST "$API_URL/documents/upload-url" \
  -H "$AUTH" -H "Content-Type: application/json" \
  -d '{"filename":"smoke-test.txt","fileType":"txt"}')
UPLOAD_URL=$(echo "$UPLOAD_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin)['uploadUrl'])")
DOC_ID=$(echo "$UPLOAD_RESP"    | python3 -c "import sys,json; print(json.load(sys.stdin)['documentId'])")
[ -n "$DOC_ID" ] && pass "Got documentId: $DOC_ID" || fail "No documentId in response"

# ── 3. Upload file directly to S3 ────────────────────────────────────────────
echo "3. Upload test file to S3"
TEST_CONTENT="Acme Corp refund policy: customers may return items within 30 days of purchase for a full refund. Items must be in original condition. Digital products are non-refundable. Contact support@acme-demo.com to initiate a return."
STATUS=$(curl -sf -o /dev/null -w "%{http_code}" -X PUT "$UPLOAD_URL" \
  -H "Content-Type: text/plain" \
  --data "$TEST_CONTENT")
[ "$STATUS" = "200" ] && pass "PUT to S3 → 200" || fail "PUT to S3 → $STATUS"

# ── 4. Poll until pipeline completes ─────────────────────────────────────────
echo "4. Polling for pipeline completion (max ${MAX_WAIT}s)..."
ELAPSED=0
while [ $ELAPSED -lt $MAX_WAIT ]; do
  DOC_STATUS=$(curl -sf -H "$AUTH" "$API_URL/documents/$DOC_ID" \
    | python3 -c "import sys,json; print(json.load(sys.stdin)['status'])")
  echo "   ${ELAPSED}s — status: $DOC_STATUS"
  if [ "$DOC_STATUS" = "ready" ]; then
    pass "Document processed in ${ELAPSED}s"
    break
  elif [ "$DOC_STATUS" = "failed" ]; then
    fail "Pipeline failed"
  fi
  sleep 5
  ELAPSED=$((ELAPSED + 5))
done
[ "$DOC_STATUS" = "ready" ] || fail "Timed out after ${MAX_WAIT}s (status: $DOC_STATUS)"

# ── 5. Chat query ─────────────────────────────────────────────────────────────
echo "5. RAG chat query"
CHAT_RESP=$(curl -sf -X POST "$API_URL/chat" \
  -H "$AUTH" -H "Content-Type: application/json" \
  -d '{"question":"What is the refund policy?"}')
ANSWER=$(echo "$CHAT_RESP"    | python3 -c "import sys,json; print(json.load(sys.stdin)['answer'])")
NUM_CITATIONS=$(echo "$CHAT_RESP" | python3 -c "import sys,json; print(len(json.load(sys.stdin)['citations']))")
[ -n "$ANSWER" ]            && pass "Got answer (${#ANSWER} chars)" || fail "Empty answer"
[ "$NUM_CITATIONS" -gt "0" ] && pass "Got $NUM_CITATIONS citation(s)" || fail "No citations returned"

# ── 6. Cleanup ────────────────────────────────────────────────────────────────
echo "6. Delete test document"
STATUS=$(curl -sf -o /dev/null -w "%{http_code}" -X DELETE -H "$AUTH" "$API_URL/documents/$DOC_ID")
[ "$STATUS" = "204" ] && pass "DELETE /documents/$DOC_ID → 204" || fail "DELETE → $STATUS"

echo ""
echo "=== All checks passed ==="
