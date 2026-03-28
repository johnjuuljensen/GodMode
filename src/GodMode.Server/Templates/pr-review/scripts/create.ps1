$ErrorActionPreference = 'Stop'

$projectPath = $env:GODMODE_PROJECT_PATH
$prNum = $env:GODMODE_INPUT_PRNUMBER

Write-Output "Cloning {{repoUrl}}..."
git clone "{{repoUrl}}" $projectPath
if ($LASTEXITCODE -ne 0) { throw "git clone failed" }
Set-Location $projectPath

$ghPath = Get-Command gh -ErrorAction SilentlyContinue
if ($ghPath) {
    Write-Output "Checking out PR #$prNum via gh CLI..."
    gh pr checkout $prNum
    if ($LASTEXITCODE -ne 0) { throw "gh pr checkout failed" }

    # Fetch PR context
    Write-Output "Fetching PR details..."
    $contextDir = Join-Path $projectPath ".godmode"
    New-Item -ItemType Directory -Force -Path $contextDir | Out-Null

    try {
        $prTitle = gh pr view $prNum --json title -q '.title' 2>$null
        $prBody = gh pr view $prNum --json body -q '.body' 2>$null
        $prFiles = gh pr view $prNum --json files -q '.files[].path' 2>$null
        $prDiff = gh pr diff $prNum 2>$null | Select-Object -First 500

        $contextFile = Join-Path $contextDir "pr-context.md"
        @"
# Pull Request #$prNum

## $prTitle

### Description
$prBody

### Changed Files
$prFiles

### Diff (first 500 lines)
``````diff
$($prDiff -join "`n")
``````
"@ | Set-Content $contextFile -Encoding UTF8
        Write-Output "PR context written to .godmode/pr-context.md"
    } catch {
        Write-Output "Could not fetch PR details"
    }
} else {
    Write-Output "gh CLI not found, fetching PR ref manually..."
    git fetch origin "pull/$prNum/head:pr-$prNum"
    if ($LASTEXITCODE -ne 0) { throw "git fetch PR failed" }
    git checkout "pr-$prNum"
    if ($LASTEXITCODE -ne 0) { throw "git checkout failed" }
}

Write-Output "Ready to review PR #$prNum"
