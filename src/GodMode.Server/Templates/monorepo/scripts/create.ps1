$ErrorActionPreference = 'Stop'

$projectPath = $env:GODMODE_PROJECT_PATH
$packagePath = $env:GODMODE_INPUT_PACKAGEPATH

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

# Verify the package path exists
if ($packagePath -and -not (Test-Path $packagePath)) {
    Write-Output "WARNING: Package path '$packagePath' does not exist in the repo."
}

# Install dependencies at repo root
if ($env:GODMODE_INPUT_INSTALLDEPS -ne "false") {
    if ((Test-Path "pnpm-lock.yaml") -or (Test-Path "pnpm-workspace.yaml")) {
        Write-Output "Detected pnpm workspace, installing dependencies..."
        pnpm install
    } elseif (Test-Path "yarn.lock") {
        Write-Output "Detected yarn workspace, installing dependencies..."
        yarn install
    } elseif (Test-Path "package.json") {
        Write-Output "Installing dependencies with npm..."
        npm install
    } elseif (Test-Path "Cargo.toml") {
        Write-Output "Detected Rust workspace, fetching dependencies..."
        cargo fetch
    } elseif (Get-ChildItem -Path . -Filter "*.sln" -Depth 0) {
        Write-Output "Detected .NET solution, restoring..."
        dotnet restore
    }
}

# Write a note about the target package for Claude
if ($packagePath) {
    $contextDir = Join-Path $projectPath ".godmode"
    New-Item -ItemType Directory -Force -Path $contextDir | Out-Null
    "Primary package: $packagePath" | Set-Content (Join-Path $contextDir "workspace-focus.txt") -Encoding UTF8
    Write-Output "Focus set to: $packagePath"
}

Write-Output "Project ready at $projectPath"
