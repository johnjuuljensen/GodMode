#!/bin/bash
set -euo pipefail

RG="${GODMODE_RG:-godmode-rg}"

echo "GodMode instances in $RG:"
echo ""

az containerapp list \
  --resource-group "$RG" \
  --query "[?starts_with(name, 'godmode-')].{Name:name, URL:properties.configuration.ingress.fqdn, Replicas:properties.template.scale.minReplicas, Image:properties.template.containers[0].image}" \
  --output table
