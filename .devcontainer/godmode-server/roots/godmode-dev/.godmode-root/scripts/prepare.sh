#!/bin/bash
# One-time setup: create bare clone of GodMode repo
set -e

BARE_PATH="$GODMODE_ROOT_PATH/GodMode.git"

if [ -d "$BARE_PATH" ]; then
    echo "Bare repo already exists, fetching updates..."
    git -C "$BARE_PATH" fetch origin
else
    echo "Cloning bare repo from GitHub..."
    git clone --bare https://github.com/johnjuuljensen/GodMode "$BARE_PATH"

    # Configure fetch refspec so origin/* refs are available for worktrees
    git -C "$BARE_PATH" config remote.origin.fetch "+refs/heads/*:refs/remotes/origin/*"
    git -C "$BARE_PATH" fetch origin
fi

echo "Setup complete. Bare repo at: $BARE_PATH"
