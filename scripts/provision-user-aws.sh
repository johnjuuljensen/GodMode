#!/bin/bash
set -euo pipefail

USERNAME="${1:?Usage: provision-user-aws.sh <username> <email> <anthropic_key>}"
EMAIL="${2:?}"
ANTHROPIC_KEY="${3:?}"

CLUSTER="${GODMODE_ECS_CLUSTER:-godmode-cluster}"
REGION="${AWS_REGION:-eu-west-1}"
IMAGE="${GODMODE_IMAGE:-ghcr.io/johnjuuljensen/godmode:latest}"
GOOGLE_CLIENT_ID="${GOOGLE_CLIENT_ID:?Set GOOGLE_CLIENT_ID}"
GOOGLE_CLIENT_SECRET="${GOOGLE_CLIENT_SECRET:?Set GOOGLE_CLIENT_SECRET}"
SUBNET_IDS="${GODMODE_SUBNET_IDS:?Set GODMODE_SUBNET_IDS (comma-separated)}"
SECURITY_GROUP="${GODMODE_SECURITY_GROUP:?Set GODMODE_SECURITY_GROUP}"

SERVICE_NAME="godmode-$USERNAME"
TASK_FAMILY="godmode-$USERNAME"

echo "==> Provisioning GodMode on AWS ECS for $EMAIL as $SERVICE_NAME"

# 1. Register task definition
echo "--> Registering task definition: $TASK_FAMILY"
cat > /tmp/godmode-task-def.json << TASKEOF
{
  "family": "$TASK_FAMILY",
  "networkMode": "awsvpc",
  "requiresCompatibilities": ["FARGATE"],
  "cpu": "1024",
  "memory": "2048",
  "executionRoleArn": "${GODMODE_EXECUTION_ROLE:?Set GODMODE_EXECUTION_ROLE}",
  "containerDefinitions": [
    {
      "name": "godmode",
      "image": "$IMAGE",
      "essential": true,
      "portMappings": [{ "containerPort": 31337, "protocol": "tcp" }],
      "environment": [
        { "name": "Urls", "value": "http://0.0.0.0:31337" },
        { "name": "ProjectRootsDir", "value": "/app/projects" },
        { "name": "Authentication__Google__ClientId", "value": "$GOOGLE_CLIENT_ID" },
        { "name": "Authentication__Google__AllowedEmail", "value": "$EMAIL" }
      ],
      "secrets": [
        { "name": "Authentication__Google__ClientSecret", "valueFrom": "${GODMODE_SECRET_ARN_GOOGLE_SECRET:?}" },
        { "name": "ANTHROPIC_API_KEY", "valueFrom": "${GODMODE_SECRET_ARN_ANTHROPIC:?}" }
      ],
      "logConfiguration": {
        "logDriver": "awslogs",
        "options": {
          "awslogs-group": "/ecs/godmode",
          "awslogs-region": "$REGION",
          "awslogs-stream-prefix": "$USERNAME"
        }
      }
    }
  ]
}
TASKEOF

aws ecs register-task-definition --cli-input-json file:///tmp/godmode-task-def.json --region "$REGION" --output text --query 'taskDefinition.taskDefinitionArn'

# 2. Check if service exists
EXISTING=$(aws ecs describe-services --cluster "$CLUSTER" --services "$SERVICE_NAME" --region "$REGION" \
  --query "services[?status=='ACTIVE'].serviceName" --output text 2>/dev/null || echo "")

if [ -z "$EXISTING" ]; then
  echo "--> Creating ECS service: $SERVICE_NAME"
  aws ecs create-service \
    --cluster "$CLUSTER" \
    --service-name "$SERVICE_NAME" \
    --task-definition "$TASK_FAMILY" \
    --desired-count 1 \
    --launch-type FARGATE \
    --network-configuration "awsvpcConfiguration={subnets=[$SUBNET_IDS],securityGroups=[$SECURITY_GROUP],assignPublicIp=ENABLED}" \
    --region "$REGION" \
    --output none
else
  echo "--> Updating ECS service: $SERVICE_NAME"
  aws ecs update-service \
    --cluster "$CLUSTER" \
    --service "$SERVICE_NAME" \
    --task-definition "$TASK_FAMILY" \
    --force-new-deployment \
    --region "$REGION" \
    --output none
fi

echo ""
echo "Done!"
echo "  Service: $SERVICE_NAME"
echo "  Cluster: $CLUSTER"
echo "  Email:   $EMAIL"
echo ""
echo "Note: ECS Fargate assigns a public IP on each deploy. Use an ALB or Cloud Map for a stable URL."
