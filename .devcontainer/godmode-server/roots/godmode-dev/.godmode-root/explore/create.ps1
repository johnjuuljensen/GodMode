# Per-project: create a git worktree for exploration (read-only-ish, from master)
$ErrorActionPreference = 'Stop'

$barePath = "$env:GODMODE_ROOT_PATH\GodMode.git"
$projectPath = $env:GODMODE_PROJECT_PATH

# Fetch latest
git -C $barePath fetch origin

$branch = "explore/$env:GODMODE_PROJECT_ID"
Write-Host "Creating branch '$branch' from origin/master..."
git -C $barePath worktree add $projectPath -b $branch origin/master

Write-Host "Worktree ready at $projectPath"
