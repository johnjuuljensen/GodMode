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
  curl -s -X POST "$API" \
    -H "Authorization: Bearer $RAILWAY_TOKEN" \
    -H "Content-Type: application/json" \
    -d "{\"query\": $(echo "$query" | python3 -c 'import sys,json; print(json.dumps(sys.stdin.read()))')}"
}

# 1. Create project
echo "--> Creating project: $PROJECT_NAME"
PROJECT_RESULT=$(gql "mutation { projectCreate(input: { name: \"$PROJECT_NAME\" }) { id } }")
PROJECT_ID=$(echo "$PROJECT_RESULT" | python3 -c "import sys,json; print(json.loads(sys.stdin.read())['data']['projectCreate']['id'])" 2>/dev/null)

if [ -z "$PROJECT_ID" ] || [ "$PROJECT_ID" = "null" ]; then
  echo "Error creating project. Response: $PROJECT_RESULT"
  exit 1
fi
echo "  Project ID: $PROJECT_ID"

# 2. Get default environment
ENV_ID=$(gql "{ project(id: \"$PROJECT_ID\") { environments { edges { node { id } } } } }" \
  | python3 -c "import sys,json; print(json.loads(sys.stdin.read())['data']['project']['environments']['edges'][0]['node']['id'])")
echo "  Environment ID: $ENV_ID"

# 3. Create service from image
echo "--> Creating service from image: $IMAGE"
SERVICE_ID=$(gql "mutation { serviceCreate(input: { projectId: \"$PROJECT_ID\", name: \"$SERVICE_NAME\", source: { image: \"$IMAGE\" } }) { id } }" \
  | python3 -c "import sys,json; print(json.loads(sys.stdin.read())['data']['serviceCreate']['id'])")
echo "  Service ID: $SERVICE_ID"

# 4. Set environment variables
echo "--> Setting environment variables"
gql "mutation { variableCollectionUpsert(input: { projectId: \"$PROJECT_ID\", environmentId: \"$ENV_ID\", serviceId: \"$SERVICE_ID\", variables: { PORT: \"31337\", Urls: \"http://0.0.0.0:31337\", ProjectRootsDir: \"/app/projects\", Authentication__Google__ClientId: \"$GOOGLE_CLIENT_ID\", Authentication__Google__ClientSecret: \"$GOOGLE_CLIENT_SECRET\", Authentication__Google__AllowedEmail: \"$EMAIL\", ANTHROPIC_API_KEY: \"$ANTHROPIC_KEY\" } }) }" > /dev/null

# 5. Create volume for persistent workspace
echo "--> Creating volume for /app/projects"
gql "mutation { volumeCreate(input: { projectId: \"$PROJECT_ID\", environmentId: \"$ENV_ID\", serviceId: \"$SERVICE_ID\", mountPath: \"/app/projects\" }) { id } }" > /dev/null

# 6. Generate public domain
echo "--> Generating public domain"
DOMAIN=$(gql "mutation { serviceDomainCreate(input: { serviceId: \"$SERVICE_ID\", environmentId: \"$ENV_ID\" }) { domain } }" \
  | python3 -c "import sys,json; print(json.loads(sys.stdin.read())['data']['serviceDomainCreate']['domain'])" 2>/dev/null || echo "unknown")

echo ""
echo "Done!"
echo "  Project: $PROJECT_NAME"
echo "  URL:     https://$DOMAIN"
echo "  Email:   $EMAIL"
