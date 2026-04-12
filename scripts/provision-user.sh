#!/bin/bash
set -euo pipefail

USERNAME="${1:?Usage: provision-user.sh <username> <email> <anthropic_key>}"
EMAIL="${2:?}"
ANTHROPIC_KEY="${3:?}"

RG="${GODMODE_RG:-godmode-rg}"
ACA_ENV="${GODMODE_ACA_ENV:-godmode-env}"
STORAGE_ACCOUNT="${GODMODE_STORAGE_ACCOUNT:-godmodestorage}"
IMAGE="${GODMODE_IMAGE:-ghcr.io/johnjuuljensen/godmode:latest}"
GOOGLE_CLIENT_ID="${GOOGLE_CLIENT_ID:?Set GOOGLE_CLIENT_ID}"
GOOGLE_CLIENT_SECRET="${GOOGLE_CLIENT_SECRET:?Set GOOGLE_CLIENT_SECRET}"

APP_NAME="godmode-$USERNAME"
SHARE_NAME="godmode-$USERNAME-data"
STORAGE_LINK_NAME="godmode-$USERNAME-storage"

echo "==> Provisioning GodMode for $EMAIL as $APP_NAME"

# 1. Create Azure Files share (idempotent)
echo "--> Creating storage share: $SHARE_NAME"
az storage share create \
  --name "$SHARE_NAME" \
  --account-name "$STORAGE_ACCOUNT" \
  --output none 2>/dev/null || true

# 2. Link the share to the ACA environment (idempotent)
echo "--> Linking storage to environment"
STORAGE_KEY=$(az storage account keys list \
  --account-name "$STORAGE_ACCOUNT" \
  --resource-group "$RG" \
  --query "[0].value" -o tsv)

az containerapp env storage set \
  --name "$ACA_ENV" \
  --resource-group "$RG" \
  --storage-name "$STORAGE_LINK_NAME" \
  --azure-file-account-name "$STORAGE_ACCOUNT" \
  --azure-file-account-key "$STORAGE_KEY" \
  --azure-file-share-name "$SHARE_NAME" \
  --access-mode ReadWrite \
  --output none

# 3. Check if container app already exists
EXISTING=$(az containerapp show \
  --name "$APP_NAME" \
  --resource-group "$RG" \
  --query "name" -o tsv 2>/dev/null || echo "")

if [ -z "$EXISTING" ]; then
  echo "--> Creating new Container App: $APP_NAME"

  az containerapp create \
    --name "$APP_NAME" \
    --resource-group "$RG" \
    --environment "$ACA_ENV" \
    --image "$IMAGE" \
    --target-port 31337 \
    --ingress external \
    --min-replicas 1 \
    --max-replicas 1 \
    --cpu 1.0 \
    --memory 2.0Gi \
    --registry-server ghcr.io \
    --registry-username "${GHCR_USERNAME:-}" \
    --registry-password "${GHCR_TOKEN:-}" \
    --env-vars \
      "Urls=http://0.0.0.0:31337" \
      "ProjectRootsDir=/app/projects" \
      "Authentication__Google__ClientId=$GOOGLE_CLIENT_ID" \
      "Authentication__Google__AllowedEmail=$EMAIL" \
    --secrets \
      "google-client-secret=$GOOGLE_CLIENT_SECRET" \
      "anthropic-key=$ANTHROPIC_KEY" \
    --output none

  # Set secret-referenced env vars
  az containerapp update \
    --name "$APP_NAME" \
    --resource-group "$RG" \
    --set-env-vars \
      "Authentication__Google__ClientSecret=secretref:google-client-secret" \
      "ANTHROPIC_API_KEY=secretref:anthropic-key" \
    --output none

  # Attach persistent volume for /app/projects
  echo "--> Attaching persistent volume"
  cat > /tmp/aca-volume-$USERNAME.yaml << YAMLEOF
properties:
  template:
    volumes:
      - name: workspace
        storageName: $STORAGE_LINK_NAME
        storageType: AzureFile
    containers:
      - name: godmode
        image: $IMAGE
        resources:
          cpu: 1.0
          memory: 2.0Gi
        volumeMounts:
          - volumeName: workspace
            mountPath: /app/projects
YAMLEOF

  az containerapp update \
    --name "$APP_NAME" \
    --resource-group "$RG" \
    --yaml /tmp/aca-volume-$USERNAME.yaml \
    --output none
  rm -f /tmp/aca-volume-$USERNAME.yaml

else
  echo "--> Updating existing Container App: $APP_NAME"
  az containerapp update \
    --name "$APP_NAME" \
    --resource-group "$RG" \
    --image "$IMAGE" \
    --output none

  az containerapp secret set \
    --name "$APP_NAME" \
    --resource-group "$RG" \
    --secrets \
      "google-client-secret=$GOOGLE_CLIENT_SECRET" \
      "anthropic-key=$ANTHROPIC_KEY" \
    --output none
fi

# 4. Print summary
FQDN=$(az containerapp show \
  --name "$APP_NAME" \
  --resource-group "$RG" \
  --query "properties.configuration.ingress.fqdn" -o tsv)

echo ""
echo "Done!"
echo "  App:     $APP_NAME"
echo "  URL:     https://$FQDN"
echo "  Email:   $EMAIL"
echo "  Storage: $SHARE_NAME -> /app/projects"
