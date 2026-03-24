$ErrorActionPreference = 'Stop'

$projectPath = $env:GODMODE_PROJECT_PATH

# Clone
if ($env:GODMODE_INPUT_BRANCH) {
    Write-Output "Cloning {{repoUrl}} (branch: $($env:GODMODE_INPUT_BRANCH))..."
    git clone --branch $env:GODMODE_INPUT_BRANCH "{{repoUrl}}" $projectPath
} else {
    Write-Output "Cloning {{repoUrl}}..."
    git clone "{{repoUrl}}" $projectPath
}
if ($LASTEXITCODE -ne 0) { throw "git clone failed" }

Set-Location $projectPath

# Auto-detect package manager and install
if (Test-Path "pnpm-lock.yaml") {
    Write-Output "Detected pnpm, installing dependencies..."
    pnpm install
} elseif (Test-Path "yarn.lock") {
    Write-Output "Detected yarn, installing dependencies..."
    yarn install
} elseif ((Test-Path "bun.lockb") -or (Test-Path "bun.lock")) {
    Write-Output "Detected bun, installing dependencies..."
    bun install
} elseif (Test-Path "package.json") {
    Write-Output "Installing dependencies with npm..."
    npm install
} else {
    Write-Output "No package.json found, skipping install."
}

# Optionally run build
if ($env:GODMODE_INPUT_RUNBUILD -eq "true") {
    Write-Output "Running build..."
    if (Test-Path "pnpm-lock.yaml") {
        pnpm run build
    } elseif (Test-Path "yarn.lock") {
        yarn build
    } else {
        npm run build
    }
}

Write-Output "Project ready at $projectPath"
