#!/usr/bin/env bash
# Deploys a one-shot Lambda into the VPC, runs the DB migration, then deletes the stack.
set -euo pipefail

export PATH="$HOME/.sam-venv/bin:$HOME/.dotnet:$PATH"

REGION="us-west-2"
ENV="dev"
STACK_NAME="sift-migrate-${ENV}"
SAM_BUCKET="sift-sam-709085484102-us-west-2"

# Database stack exports
SECRET_ARN="arn:aws:secretsmanager:us-west-2:709085484102:secret:sift-dev-db-credentials-GCgfaw"
DB_HOST="sift-database-dev-dbcluster-0pthpvpg4ib3.cluster-cgyr35jzavkc.us-west-2.rds.amazonaws.com"
LAMBDA_SG="sg-05addc56af0c44662"
PRIVATE_SUBNET_1="subnet-02fc02fb9864124f7"
PRIVATE_SUBNET_2="subnet-0df679b08df9ded93"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
MIGRATE_DIR="$SCRIPT_DIR/migrate"

echo "==> Building migration stack (--use-container builds psycopg2 for Amazon Linux)..."
cd "$REPO_ROOT"
sam build \
  --template-file infrastructure/template-migrate.yaml \
  --build-dir .aws-sam/migrate \
  --use-container

sam deploy \
  --template-file .aws-sam/migrate/template.yaml \
  --stack-name "$STACK_NAME" \
  --s3-bucket "$SAM_BUCKET" \
  --s3-prefix migrate \
  --capabilities CAPABILITY_NAMED_IAM \
  --no-confirm-changeset \
  --no-fail-on-empty-changeset \
  --region "$REGION" \
  --parameter-overrides \
    "Env=${ENV}" \
    "SecretArn=${SECRET_ARN}" \
    "DbHost=${DB_HOST}" \
    "LambdaSecurityGroupId=${LAMBDA_SG}" \
    "PrivateSubnet1Id=${PRIVATE_SUBNET_1}" \
    "PrivateSubnet2Id=${PRIVATE_SUBNET_2}"

echo ""
echo "==> Invoking migration Lambda..."
RESPONSE_FILE="/tmp/migrate-response.json"

aws lambda invoke \
  --function-name "sift-${ENV}-migrate" \
  --log-type Tail \
  --region "$REGION" \
  --query 'LogResult' \
  --output text \
  "$RESPONSE_FILE" | base64 -d

echo ""
echo "Response:"
cat "$RESPONSE_FILE"
echo ""

# Check for error in response
if grep -q '"errorMessage"' "$RESPONSE_FILE"; then
  echo "ERROR: Migration Lambda returned an error. Stack preserved for debugging."
  exit 1
fi

echo "==> Migration successful. Cleaning up stack..."
aws cloudformation delete-stack \
  --stack-name "$STACK_NAME" \
  --region "$REGION"

echo "Stack deletion initiated. Migration complete."
