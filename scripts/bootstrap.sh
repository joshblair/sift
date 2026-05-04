#!/usr/bin/env bash
# Run once to create the OIDC trust role and SAM artifacts bucket.
# Prerequisites: aws CLI authenticated with AdministratorAccess or equivalent.
set -euo pipefail

GITHUB_ORG="joshblair"
REPO_NAME="sift"
AWS_REGION="us-west-2"
ROLE_NAME="sift-github-actions-deploy"

AWS_ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
SAM_BUCKET="sift-sam-${AWS_ACCOUNT_ID}-${AWS_REGION}"

echo "Account : ${AWS_ACCOUNT_ID}"
echo "Region  : ${AWS_REGION}"
echo "Bucket  : ${SAM_BUCKET}"
echo ""

# ── SAM artifacts bucket ─────────────────────────────────────────────────────
if aws s3api head-bucket --bucket "${SAM_BUCKET}" 2>/dev/null; then
  echo "SAM bucket already exists, skipping."
else
  echo "Creating SAM bucket..."
  aws s3api create-bucket \
    --bucket "${SAM_BUCKET}" \
    --region "${AWS_REGION}" \
    --create-bucket-configuration LocationConstraint="${AWS_REGION}"
  aws s3api put-bucket-versioning \
    --bucket "${SAM_BUCKET}" \
    --versioning-configuration Status=Enabled
  aws s3api put-public-access-block \
    --bucket "${SAM_BUCKET}" \
    --public-access-block-configuration "BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true"
fi

# ── GitHub OIDC provider ──────────────────────────────────────────────────────
OIDC_ARN="arn:aws:iam::${AWS_ACCOUNT_ID}:oidc-provider/token.actions.githubusercontent.com"
if aws iam get-open-id-connect-provider --open-id-connect-provider-arn "${OIDC_ARN}" 2>/dev/null; then
  echo "OIDC provider already exists, skipping."
else
  echo "Creating GitHub Actions OIDC provider..."
  aws iam create-open-id-connect-provider \
    --url "https://token.actions.githubusercontent.com" \
    --client-id-list "sts.amazonaws.com" \
    --thumbprint-list "6938fd4d98bab03faadb97b34396831e3780aea1"
fi

# ── IAM deploy role ───────────────────────────────────────────────────────────
TRUST_POLICY=$(cat <<EOF
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Federated": "${OIDC_ARN}" },
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": {
        "token.actions.githubusercontent.com:aud": "sts.amazonaws.com"
      },
      "StringLike": {
        "token.actions.githubusercontent.com:sub": "repo:${GITHUB_ORG}/${REPO_NAME}:*"
      }
    }
  }]
}
EOF
)

if aws iam get-role --role-name "${ROLE_NAME}" 2>/dev/null; then
  echo "IAM role already exists, skipping creation."
else
  echo "Creating IAM role: ${ROLE_NAME}..."
  aws iam create-role \
    --role-name "${ROLE_NAME}" \
    --assume-role-policy-document "${TRUST_POLICY}" \
    --description "GitHub Actions deploy role for sift"

  for POLICY in \
    "arn:aws:iam::aws:policy/AWSCloudFormationFullAccess" \
    "arn:aws:iam::aws:policy/AWSLambda_FullAccess" \
    "arn:aws:iam::aws:policy/AmazonS3FullAccess" \
    "arn:aws:iam::aws:policy/AmazonAPIGatewayAdministrator" \
    "arn:aws:iam::aws:policy/AmazonCognitoPowerUser" \
    "arn:aws:iam::aws:policy/AmazonRDSFullAccess" \
    "arn:aws:iam::aws:policy/IAMFullAccess"; do
    aws iam attach-role-policy --role-name "${ROLE_NAME}" --policy-arn "${POLICY}"
  done
fi

DEPLOY_ROLE_ARN=$(aws iam get-role --role-name "${ROLE_NAME}" --query Role.Arn --output text)

echo ""
echo "Bootstrap complete. Add these as GitHub Actions variables in your repo settings:"
echo ""
echo "  AWS_REGION       = ${AWS_REGION}"
echo "  SAM_BUCKET       = ${SAM_BUCKET}"
echo "  DEPLOY_ROLE_ARN  = ${DEPLOY_ROLE_ARN}"
