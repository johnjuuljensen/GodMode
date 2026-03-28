#!/bin/bash
set -e

PROJECT_PATH="$GODMODE_PROJECT_PATH"

# Clone
if [ -n "$GODMODE_INPUT_BRANCH" ]; then
    echo "Cloning {{repoUrl}} (branch: $GODMODE_INPUT_BRANCH)..."
    git clone --branch "$GODMODE_INPUT_BRANCH" "{{repoUrl}}" "$PROJECT_PATH"
else
    echo "Cloning {{repoUrl}}..."
    git clone "{{repoUrl}}" "$PROJECT_PATH"
fi

cd "$PROJECT_PATH"

# Auto-detect package manager and install
if [ -f "pnpm-lock.yaml" ]; then
    echo "Detected pnpm, installing dependencies..."
    pnpm install
elif [ -f "yarn.lock" ]; then
    echo "Detected yarn, installing dependencies..."
    yarn install
elif [ -f "bun.lockb" ] || [ -f "bun.lock" ]; then
    echo "Detected bun, installing dependencies..."
    bun install
elif [ -f "package.json" ]; then
    echo "Installing dependencies with npm..."
    npm install
else
    echo "No package.json found, skipping install."
fi

# Optionally run build
if [ "$GODMODE_INPUT_RUNBUILD" = "true" ]; then
    echo "Running build..."
    if [ -f "pnpm-lock.yaml" ]; then
        pnpm run build
    elif [ -f "yarn.lock" ]; then
        yarn build
    else
        npm run build
    fi
fi

echo "Project ready at $PROJECT_PATH"
