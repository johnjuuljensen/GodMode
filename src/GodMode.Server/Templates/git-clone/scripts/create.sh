#!/bin/bash
set -e

PROJECT_PATH="$GODMODE_PROJECT_PATH"

if [ -n "$GODMODE_INPUT_BRANCH" ]; then
    echo "Cloning {{repoUrl}} (branch: $GODMODE_INPUT_BRANCH)..."
    git clone --branch "$GODMODE_INPUT_BRANCH" "{{repoUrl}}" "$PROJECT_PATH"
else
    echo "Cloning {{repoUrl}}..."
    git clone "{{repoUrl}}" "$PROJECT_PATH"
fi

echo "Clone ready at $PROJECT_PATH"
