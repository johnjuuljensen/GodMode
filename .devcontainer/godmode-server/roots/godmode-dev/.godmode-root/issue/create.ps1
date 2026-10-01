$ErrorActionPreference = 'Stop'

# Per-project: create a git worktree for a GitHub issue, cut from origin/master or from the base
# branch asked for (an epic's branch, for a sub-issue an overseer starts)
$barePath = Join-Path $env:GODMODE_ROOT_PATH "GodMode.git"
$projectPath = $env:GODMODE_PROJECT_PATH
$issueNumber = $env:GODMODE_INPUT_ISSUE_NUMBER
$baseBranch = if ($env:GODMODE_INPUT_BASE_BRANCH) { $env:GODMODE_INPUT_BASE_BRANCH -replace '^(origin/)?', 'origin/' } else { 'origin/master' }
$brief = $env:GODMODE_INPUT_BRIEF

# Fetch latest
git -C $barePath fetch origin
if ($LASTEXITCODE -ne 0) { throw "git fetch failed (exit code $LASTEXITCODE)" }
git -C $barePath rev-parse --verify --quiet "refs/remotes/$baseBranch" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The base branch '$baseBranch' is not on origin." }

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

# A GodMode project is there already (the issue was created before, and its project is still
# around): it is not stale, and removing it would destroy that project's worktree
if (Test-Path (Join-Path $projectPath '.godmode')) {
    throw "Project folder '$projectPath' is in use: it is a GodMode project. Delete that project first, or carry on in it."
}

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

Write-Output "Creating branch '$branch' from $baseBranch..."
git -C $barePath worktree add $projectPath -b $branch $baseBranch
if ($LASTEXITCODE -ne 0) { throw "git worktree add failed (exit code $LASTEXITCODE)" }

# The prompt is the promptTemplate's, made here so it can say the base branch and carry the brief:
# a template that names {brief} would drop the whole prompt when the brief is empty, or has braces
$prompt = "Read GitHub issue #$issueNumber by running ``gh issue view $issueNumber``. Understand the requirements and implement the changes."
if ($baseBranch -ne 'origin/master') {
    # A session that does not know it was cut from an epic's branch opens its pull request against the default one
    $prompt += " Your branch, $branch, was cut from ${baseBranch}: open its pull request against $($baseBranch -replace '^origin/', ''), not the default branch."
}
if ($brief) { $prompt += "`n`n$brief" }

# Write result file so the server picks up the actual path and name; the prompt last, as it may span lines
if ($env:GODMODE_RESULT_FILE) {
    @"
project_path=$projectPath
project_name=Issue #${issueNumber}: $issueTitle
project_prompt=$prompt
"@ | Set-Content -Path $env:GODMODE_RESULT_FILE -NoNewline
}

Write-Output "Worktree ready at $projectPath (branch: $branch, from $baseBranch)"
