# Per-project: create a git worktree for exploration (read-only-ish, from master)
$ErrorActionPreference = 'Stop'

$barePath = "$env:GODMODE_ROOT_PATH\GodMode.git"
$projectPath = $env:GODMODE_PROJECT_PATH

# A GodMode project is there already: this create does not make it again
if (Test-Path (Join-Path $projectPath '.godmode')) {
    throw "Project folder '$projectPath' is in use: it is a GodMode project. Delete that project first, or carry on in it."
}

# Fetch latest
git -C $barePath fetch origin

$branch = "explore/$env:GODMODE_PROJECT_FOLDER"
Write-Host "Creating branch '$branch' from origin/master..."
git -C $barePath worktree add $projectPath -b $branch origin/master

Write-Host "Worktree ready at $projectPath"
