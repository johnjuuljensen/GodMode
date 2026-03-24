#!/bin/bash
set -e

PROJECT_PATH="$GODMODE_PROJECT_PATH"
ISSUE_NUM="$GODMODE_INPUT_ISSUENUMBER"
BASE_BRANCH="${GODMODE_INPUT_BASEBRANCH:-}"

# Clone the repo
echo "Cloning https://github.com/johnjuuljensen/GodMode.git..."
git clone "https://github.com/johnjuuljensen/GodMode.git" "$PROJECT_PATH"
cd "$PROJECT_PATH"

# Detect default branch if base not specified
if [ -z "$BASE_BRANCH" ]; then
    BASE_BRANCH=$(git symbolic-ref refs/remotes/origin/HEAD 2>/dev/null | sed 's@^refs/remotes/origin/@@' || echo "main")
fi

# Create issue branch
BRANCH_NAME="issue-${ISSUE_NUM}"
echo "Creating branch '$BRANCH_NAME' from '$BASE_BRANCH'..."
git checkout -b "$BRANCH_NAME" "origin/$BASE_BRANCH"

# Try to fetch issue context via gh CLI (non-fatal)
if command -v gh &>/dev/null && [ -n "$ISSUE_NUM" ]; then
    echo "Fetching issue #${ISSUE_NUM} from GitHub..."
    TITLE=$(gh issue view "$ISSUE_NUM" --json title -q '.title' 2>/dev/null || echo "")
    BODY=$(gh issue view "$ISSUE_NUM" --json body -q '.body' 2>/dev/null || echo "")
    if [ -n "$TITLE" ]; then
        echo "Issue: $TITLE"
        # Write context file for Claude
        mkdir -p .godmode
        cat > .godmode/issue-context.md << ISSUE_EOF
# GitHub Issue #${ISSUE_NUM}

## ${TITLE}

${BODY}
ISSUE_EOF
        echo "Issue context written to .godmode/issue-context.md"
    fi
fi

echo "Ready to work on issue #${ISSUE_NUM}"
