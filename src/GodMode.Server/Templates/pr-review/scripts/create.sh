#!/bin/bash
set -e

PROJECT_PATH="$GODMODE_PROJECT_PATH"
PR_NUM="$GODMODE_INPUT_PRNUMBER"

echo "Cloning {{repoUrl}}..."
git clone "{{repoUrl}}" "$PROJECT_PATH"
cd "$PROJECT_PATH"

# Check out the PR
if command -v gh &>/dev/null; then
    echo "Checking out PR #${PR_NUM} via gh CLI..."
    gh pr checkout "$PR_NUM"

    # Fetch PR context
    echo "Fetching PR details..."
    mkdir -p .godmode
    PR_TITLE=$(gh pr view "$PR_NUM" --json title -q '.title' 2>/dev/null || echo "")
    PR_BODY=$(gh pr view "$PR_NUM" --json body -q '.body' 2>/dev/null || echo "")
    PR_DIFF=$(gh pr diff "$PR_NUM" 2>/dev/null | head -500 || echo "")
    PR_FILES=$(gh pr view "$PR_NUM" --json files -q '.files[].path' 2>/dev/null || echo "")

    cat > .godmode/pr-context.md << PR_EOF
# Pull Request #${PR_NUM}

## ${PR_TITLE}

### Description
${PR_BODY}

### Changed Files
${PR_FILES}

### Diff (first 500 lines)
\`\`\`diff
${PR_DIFF}
\`\`\`
PR_EOF
    echo "PR context written to .godmode/pr-context.md"
else
    echo "gh CLI not found, fetching PR ref manually..."
    git fetch origin "pull/${PR_NUM}/head:pr-${PR_NUM}"
    git checkout "pr-${PR_NUM}"
fi

echo "Ready to review PR #${PR_NUM}"
