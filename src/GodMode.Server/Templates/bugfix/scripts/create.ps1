$ErrorActionPreference = 'Stop'

$projectPath = $env:GODMODE_PROJECT_PATH
$defaultBranch = "{{defaultBranch}}"

Write-Output "Cloning {{repoUrl}}..."
git clone "{{repoUrl}}" $projectPath
if ($LASTEXITCODE -ne 0) { throw "git clone failed" }

Set-Location $projectPath

# Create bugfix branch from default branch
$branch = "bugfix/$env:GODMODE_PROJECT_ID"
Write-Output "Creating branch '$branch' from 'origin/$defaultBranch'..."
git checkout -b $branch "origin/$defaultBranch"
if ($LASTEXITCODE -ne 0) { throw "git checkout failed" }

Write-Output "Ready to fix bugs at $projectPath (branch: $branch)"
