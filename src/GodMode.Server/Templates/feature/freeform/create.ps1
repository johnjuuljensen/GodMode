$ErrorActionPreference = 'Stop'

$repoName = [System.IO.Path]::GetFileNameWithoutExtension("{{repoUrl}}")
$barePath = Join-Path $env:GODMODE_ROOT_PATH "$repoName.git"
$projectPath = $env:GODMODE_PROJECT_PATH
$defaultBranch = "{{defaultBranch}}"

# Fetch latest
git -C $barePath fetch origin
if ($LASTEXITCODE -ne 0) { throw "git fetch failed" }

# Clean up stale state
if (Test-Path $projectPath) {
    Remove-Item -Recurse -Force $projectPath
}

# Create feature branch
$branch = "feature/$env:GODMODE_PROJECT_ID"

# Remove stale branch if exists
$existing = git -C $barePath branch --list $branch
if ($existing) { git -C $barePath branch -D $branch }

Write-Output "Creating worktree with branch '$branch' from 'origin/$defaultBranch'..."
git -C $barePath worktree add $projectPath -b $branch "origin/$defaultBranch"
if ($LASTEXITCODE -ne 0) { throw "git worktree add failed" }

Write-Output "Worktree ready at $projectPath (branch: $branch)"
