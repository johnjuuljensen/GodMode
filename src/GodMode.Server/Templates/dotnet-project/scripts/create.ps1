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

# Find solution or project file
$slnPath = $env:GODMODE_INPUT_SOLUTIONPATH
if (-not $slnPath) {
    $slnPath = Get-ChildItem -Path . -Filter "*.slnx" -Depth 0 | Select-Object -First 1 -ExpandProperty FullName
    if (-not $slnPath) {
        $slnPath = Get-ChildItem -Path . -Filter "*.sln" -Depth 0 | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $slnPath) {
        $slnPath = Get-ChildItem -Path . -Filter "*.csproj" -Depth 0 | Select-Object -First 1 -ExpandProperty FullName
    }
}

if ($slnPath) {
    Write-Output "Restoring NuGet packages for $slnPath..."
    dotnet restore $slnPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }

    if ($env:GODMODE_INPUT_RUNBUILD -eq "true") {
        Write-Output "Building $slnPath..."
        dotnet build $slnPath --no-restore
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
    }
} else {
    Write-Output "No .sln/.slnx/.csproj found in root. Running dotnet restore..."
    dotnet restore 2>$null
}

Write-Output "Project ready at $projectPath"
