#!/bin/bash
set -euo pipefail

USERNAME="${1:?Usage: update-user.sh <username> [image-tag]}"
IMAGE_TAG="${2:-latest}"
RG="${GODMODE_RG:-godmode-rg}"
APP_NAME="godmode-$USERNAME"
IMAGE="ghcr.io/johnjuuljensen/godmode:$IMAGE_TAG"

echo "==> Updating $APP_NAME to $IMAGE"
az containerapp update \
  --name "$APP_NAME" \
  --resource-group "$RG" \
  --image "$IMAGE" \
  --output none

echo "Done: $APP_NAME updated"
