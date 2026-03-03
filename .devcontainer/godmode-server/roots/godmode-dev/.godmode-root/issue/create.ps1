$ErrorActionPreference = 'Stop'

# Per-project: create a git worktree for a GitHub issue
$barePath = Join-Path $env:GODMODE_ROOT_PATH "GodMode.git"
$projectPath = $env:GODMODE_PROJECT_PATH
$issueNumber = $env:GODMODE_INPUT_ISSUE_NUMBER

# Fetch latest
git -C $barePath fetch origin
if ($LASTEXITCODE -ne 0) { throw "git fetch failed (exit code $LASTEXITCODE)" }

# Get issue title via GitHub CLI
$issueTitle = gh issue view $issueNumber --repo johnjuuljensen/GodMode --json title --jq '.title'
if ($LASTEXITCODE -ne 0) { throw "gh issue view failed (exit code $LASTEXITCODE)" }
Write-Output "Issue #${issueNumber}: $issueTitle"

# Slugify title for branch name
$slug = $issueTitle.ToLower() -replace '[^a-z0-9]', '-' -replace '-+', '-' -replace '^-|-$', ''
$branch = "issue-${issueNumber}-${slug}"

# Truncate branch name if too long
if ($branch.Length -gt 60) { $branch = $branch.Substring(0, 60).TrimEnd('-') }

# Use the branch name as the folder name (more descriptive than just issue_N)
$projectPath = Join-Path $env:GODMODE_ROOT_PATH $branch

# Clean up stale state from previous failed attempts
if (Test-Path $projectPath) {
    Write-Output "Removing stale directory '$projectPath'..."
    Remove-Item -Recurse -Force $projectPath
}
$existingBranch = git -C $barePath branch --list $branch
if ($existingBranch) {
    Write-Output "Removing stale branch '$branch'..."
    git -C $barePath branch -D $branch
}

Write-Output "Creating branch '$branch' from origin/master..."
git -C $barePath worktree add $projectPath -b $branch origin/master
if ($LASTEXITCODE -ne 0) { throw "git worktree add failed (exit code $LASTEXITCODE)" }

# Write result file so the server picks up the actual path and name
if ($env:GODMODE_RESULT_FILE) {
    @"
project_path=$projectPath
project_name=Issue #${issueNumber}: $issueTitle
"@ | Set-Content -Path $env:GODMODE_RESULT_FILE -NoNewline
}

Write-Output "Worktree ready at $projectPath (branch: $branch)"
