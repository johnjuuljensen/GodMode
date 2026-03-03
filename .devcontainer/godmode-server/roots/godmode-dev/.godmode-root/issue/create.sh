#!/bin/bash
# Per-project: create a git worktree for a GitHub issue
set -e

BARE_PATH="$GODMODE_ROOT_PATH/GodMode.git"
PROJECT_PATH="$GODMODE_PROJECT_PATH"
ISSUE_NUMBER="$GODMODE_INPUT_ISSUE_NUMBER"

# Fetch latest
git -C "$BARE_PATH" fetch origin

# Get issue title via GitHub CLI
ISSUE_TITLE=$(gh issue view "$ISSUE_NUMBER" --repo johnjuuljensen/GodMode --json title --jq '.title')
echo "Issue #$ISSUE_NUMBER: $ISSUE_TITLE"

# Slugify title for branch name
SLUG=$(echo "$ISSUE_TITLE" | tr '[:upper:]' '[:lower:]' | sed 's/[^a-z0-9]/-/g; s/-\+/-/g; s/^-//; s/-$//')
BRANCH="issue-${ISSUE_NUMBER}-${SLUG}"

# Truncate branch name if too long
BRANCH="${BRANCH:0:60}"
BRANCH="${BRANCH%-}"

echo "Creating branch '$BRANCH' from origin/master..."
git -C "$BARE_PATH" worktree add "$PROJECT_PATH" -b "$BRANCH" origin/master

echo "Worktree ready at $PROJECT_PATH (branch: $BRANCH)"
