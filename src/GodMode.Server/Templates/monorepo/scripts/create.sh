#!/bin/bash
set -e

PROJECT_PATH="$GODMODE_PROJECT_PATH"
PACKAGE_PATH="$GODMODE_INPUT_PACKAGEPATH"

# Clone
if [ -n "$GODMODE_INPUT_BRANCH" ]; then
    echo "Cloning {{repoUrl}} (branch: $GODMODE_INPUT_BRANCH)..."
    git clone --branch "$GODMODE_INPUT_BRANCH" "{{repoUrl}}" "$PROJECT_PATH"
else
    echo "Cloning {{repoUrl}}..."
    git clone "{{repoUrl}}" "$PROJECT_PATH"
fi

cd "$PROJECT_PATH"

# Verify the package path exists
if [ -n "$PACKAGE_PATH" ] && [ ! -d "$PACKAGE_PATH" ]; then
    echo "WARNING: Package path '$PACKAGE_PATH' does not exist in the repo."
    echo "Available top-level directories:"
    ls -d */ 2>/dev/null || echo "(none)"
fi

# Install dependencies at repo root
if [ "$GODMODE_INPUT_INSTALLDEPS" != "false" ]; then
    if [ -f "pnpm-lock.yaml" ] || [ -f "pnpm-workspace.yaml" ]; then
        echo "Detected pnpm workspace, installing dependencies..."
        pnpm install
    elif [ -f "yarn.lock" ]; then
        echo "Detected yarn workspace, installing dependencies..."
        yarn install
    elif [ -f "package.json" ]; then
        echo "Installing dependencies with npm..."
        npm install
    elif [ -f "Cargo.toml" ]; then
        echo "Detected Rust workspace, fetching dependencies..."
        cargo fetch
    elif ls *.sln *.slnx 1>/dev/null 2>&1; then
        echo "Detected .NET solution, restoring..."
        dotnet restore
    fi
fi

# Write a note about the target package for Claude
if [ -n "$PACKAGE_PATH" ]; then
    mkdir -p .godmode
    echo "Primary package: $PACKAGE_PATH" > .godmode/workspace-focus.txt
    echo "Focus set to: $PACKAGE_PATH"
fi

echo "Project ready at $PROJECT_PATH"
