#!/bin/bash
# One-time setup: create bare clone of the repository
set -e

REPO_NAME=$(basename "{{repoUrl}}" .git)
BARE_PATH="$GODMODE_ROOT_PATH/$REPO_NAME.git"

if [ -d "$BARE_PATH" ]; then
    echo "Bare repo already exists, fetching updates..."
    git -C "$BARE_PATH" fetch origin
else
    echo "Cloning bare repo from {{repoUrl}}..."
    git clone --bare "{{repoUrl}}" "$BARE_PATH"

    # Configure fetch refspec so origin/* refs are available for worktrees
    git -C "$BARE_PATH" config remote.origin.fetch "+refs/heads/*:refs/remotes/origin/*"
    git -C "$BARE_PATH" fetch origin
fi

echo "Setup complete. Bare repo at: $BARE_PATH"
