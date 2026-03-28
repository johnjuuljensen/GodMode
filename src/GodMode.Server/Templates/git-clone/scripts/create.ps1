$ErrorActionPreference = 'Stop'

$projectPath = $env:GODMODE_PROJECT_PATH

if ($env:GODMODE_INPUT_BRANCH) {
    Write-Output "Cloning {{repoUrl}} (branch: $($env:GODMODE_INPUT_BRANCH))..."
    git clone --branch $env:GODMODE_INPUT_BRANCH "{{repoUrl}}" $projectPath
} else {
    Write-Output "Cloning {{repoUrl}}..."
    git clone "{{repoUrl}}" $projectPath
}
if ($LASTEXITCODE -ne 0) { throw "git clone failed (exit code $LASTEXITCODE)" }

Write-Output "Clone ready at $projectPath"
