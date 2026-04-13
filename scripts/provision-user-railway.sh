#!/bin/bash
set -euo pipefail

USERNAME="${1:?Usage: provision-user-railway.sh <username> <email> <anthropic_key>}"
EMAIL="${2:?}"
ANTHROPIC_KEY="${3:?}"

RAILWAY_TOKEN="${RAILWAY_TOKEN:?Set RAILWAY_TOKEN}"
IMAGE="${GODMODE_IMAGE:-ghcr.io/johnjuuljensen/godmode:latest}"
GOOGLE_CLIENT_ID="${GOOGLE_CLIENT_ID:?Set GOOGLE_CLIENT_ID}"
GOOGLE_CLIENT_SECRET="${GOOGLE_CLIENT_SECRET:?Set GOOGLE_CLIENT_SECRET}"
API="https://backboard.railway.com/graphql/v2"

PROJECT_NAME="godmode-$USERNAME"
SERVICE_NAME="server"

echo "==> Provisioning GodMode on Railway for $EMAIL as $PROJECT_NAME"

gql() {
  local query="$1"
  local escaped
  escaped=$(python3 -c "import sys,json; print(json.dumps(sys.stdin.read()))" <<< "$query")
  local response
  response=$(curl -s -X POST "$API" \
    -H "Authorization: Bearer $RAILWAY_TOKEN" \
    -H "Content-Type: application/json" \
    -d "{\"query\": $escaped}")
  echo "$response"
}

extract() {
  python3 -c "
import sys, json
try:
    d = json.loads(sys.stdin.read())
    keys = '$1'.split('.')
    for k in keys:
        if isinstance(d, list): d = d[int(k)]
        else: d = d[k]
    print(d)
except Exception as e:
    print(f'EXTRACT_ERROR: {e}', file=sys.stderr)
    sys.exit(1)
"
}

# 0. Get workspace ID
echo "--> Getting workspace..."
WORKSPACE_RESULT=$(gql "{ me { workspaces { id name } } }")
echo "    Workspaces: $WORKSPACE_RESULT"
WORKSPACE_ID=$(echo "$WORKSPACE_RESULT" | extract "data.me.workspaces.0.id") || {
  echo "Error getting workspace."
  exit 1
}
echo "  Workspace ID: $WORKSPACE_ID"

# 1. Create project
echo "--> Creating project: $PROJECT_NAME"
PROJECT_RESULT=$(gql "mutation { projectCreate(input: { name: \"$PROJECT_NAME\", workspaceId: \"$WORKSPACE_ID\" }) { id } }")
echo "    API response: $PROJECT_RESULT"
PROJECT_ID=$(echo "$PROJECT_RESULT" | extract "data.projectCreate.id") || {
  echo "Error creating project."
  exit 1
}

if [ -z "$PROJECT_ID" ] || [ "$PROJECT_ID" = "null" ]; then
  echo "Error creating project. Response: $PROJECT_RESULT"
  exit 1
fi
echo "  Project ID: $PROJECT_ID"

# 2. Get default environment
ENV_RESULT=$(gql "{ project(id: \"$PROJECT_ID\") { environments { edges { node { id } } } } }")
echo "    Environments: $ENV_RESULT"
ENV_ID=$(echo "$ENV_RESULT" | extract "data.project.environments.edges.0.node.id" 2>/dev/null) || \
ENV_ID=$(echo "$ENV_RESULT" | extract "data.project.environments.0.id" 2>/dev/null) || {
  echo "Error getting environment."
  exit 1
}
echo "  Environment ID: $ENV_ID"

# 3. Create service from image
echo "--> Creating service from image: $IMAGE"
SERVICE_RESULT=$(gql "mutation { serviceCreate(input: { projectId: \"$PROJECT_ID\", name: \"$SERVICE_NAME\", source: { image: \"$IMAGE\" } }) { id } }")
SERVICE_ID=$(echo "$SERVICE_RESULT" | extract "data.serviceCreate.id") || {
  echo "Error creating service. Response: $SERVICE_RESULT"
  exit 1
}
echo "  Service ID: $SERVICE_ID"

# 4. Set environment variables
echo "--> Setting environment variables"
ENV_SET_RESULT=$(gql "mutation { variableCollectionUpsert(input: { projectId: \"$PROJECT_ID\", environmentId: \"$ENV_ID\", serviceId: \"$SERVICE_ID\", variables: { PORT: \"31337\", Urls: \"http://0.0.0.0:31337\", ProjectRootsDir: \"/app/projects\", Authentication__Google__ClientId: \"$GOOGLE_CLIENT_ID\", Authentication__Google__ClientSecret: \"$GOOGLE_CLIENT_SECRET\", Authentication__Google__AllowedEmail: \"$EMAIL\", ANTHROPIC_API_KEY: \"$ANTHROPIC_KEY\" } }) }")
echo "    Env result: $(echo "$ENV_SET_RESULT" | head -c 200)"

# 5. Create volume for persistent workspace (/app/projects survives deploys)
echo "--> Creating persistent volume for /app/projects"
VOLUME_RESULT=$(gql "mutation { volumeCreate(input: { projectId: \"$PROJECT_ID\", environmentId: \"$ENV_ID\", serviceId: \"$SERVICE_ID\", mountPath: \"/app/projects\" }) { id } }")
echo "    Volume result: $VOLUME_RESULT"

# 6. Generate public domain
echo "--> Generating public domain"
DOMAIN_RESULT=$(gql "mutation { serviceDomainCreate(input: { serviceId: \"$SERVICE_ID\", environmentId: \"$ENV_ID\" }) { domain } }")
DOMAIN=$(echo "$DOMAIN_RESULT" | extract "data.serviceDomainCreate.domain" 2>/dev/null || echo "unknown")

echo ""
echo "Done!"
echo "  Project: $PROJECT_NAME"
echo "  URL:     https://$DOMAIN"
echo "  Email:   $EMAIL"
