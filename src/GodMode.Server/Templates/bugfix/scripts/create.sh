#!/bin/bash
set -e

PROJECT_PATH="$GODMODE_PROJECT_PATH"
DEFAULT_BRANCH="{{defaultBranch}}"

echo "Cloning {{repoUrl}}..."
git clone "{{repoUrl}}" "$PROJECT_PATH"
cd "$PROJECT_PATH"

# Create bugfix branch from default branch
BRANCH="bugfix/$GODMODE_PROJECT_ID"
echo "Creating branch '$BRANCH' from 'origin/$DEFAULT_BRANCH'..."
git checkout -b "$BRANCH" "origin/$DEFAULT_BRANCH"

echo "Ready to fix bugs at $PROJECT_PATH (branch: $BRANCH)"
