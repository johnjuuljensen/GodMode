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

# Find solution or project file
SLN_PATH="$GODMODE_INPUT_SOLUTIONPATH"
if [ -z "$SLN_PATH" ]; then
    # Auto-detect: prefer .slnx, then .sln, then .csproj in root
    SLN_PATH=$(find . -maxdepth 1 -name '*.slnx' -o -name '*.sln' | head -1)
    if [ -z "$SLN_PATH" ]; then
        SLN_PATH=$(find . -maxdepth 1 -name '*.csproj' | head -1)
    fi
fi

if [ -n "$SLN_PATH" ]; then
    echo "Restoring NuGet packages for $SLN_PATH..."
    dotnet restore "$SLN_PATH"

    if [ "$GODMODE_INPUT_RUNBUILD" = "true" ]; then
        echo "Building $SLN_PATH..."
        dotnet build "$SLN_PATH" --no-restore
    fi
else
    echo "No .sln/.slnx/.csproj found in root. Running dotnet restore in project directory..."
    dotnet restore || echo "Restore skipped (no project file found)"
fi

echo "Project ready at $PROJECT_PATH"
