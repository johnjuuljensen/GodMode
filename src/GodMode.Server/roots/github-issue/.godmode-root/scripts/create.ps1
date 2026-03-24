$ErrorActionPreference = 'Stop'

$projectPath = $env:GODMODE_PROJECT_PATH
$issueNum = $env:GODMODE_INPUT_ISSUENUMBER
$baseBranch = $env:GODMODE_INPUT_BASEBRANCH

# Clone the repo
Write-Output "Cloning https://github.com/johnjuuljensen/GodMode.git..."
git clone "https://github.com/johnjuuljensen/GodMode.git" $projectPath
if ($LASTEXITCODE -ne 0) { throw "git clone failed" }
Set-Location $projectPath

# Detect default branch if base not specified
if (-not $baseBranch) {
    $baseBranch = git symbolic-ref refs/remotes/origin/HEAD 2>$null | ForEach-Object { $_ -replace '^refs/remotes/origin/', '' }
    if (-not $baseBranch) { $baseBranch = "main" }
}

# Create issue branch
$branchName = "issue-$issueNum"
Write-Output "Creating branch '$branchName' from '$baseBranch'..."
git checkout -b $branchName "origin/$baseBranch"
if ($LASTEXITCODE -ne 0) { throw "git checkout failed" }

# Try to fetch issue context via gh CLI (non-fatal)
$ghPath = Get-Command gh -ErrorAction SilentlyContinue
if ($ghPath -and $issueNum) {
    Write-Output "Fetching issue #$issueNum from GitHub..."
    try {
        $title = gh issue view $issueNum --json title -q '.title' 2>$null
        $body = gh issue view $issueNum --json body -q '.body' 2>$null
        if ($title) {
            Write-Output "Issue: $title"
            $contextDir = Join-Path $projectPath ".godmode"
            New-Item -ItemType Directory -Force -Path $contextDir | Out-Null
            $contextFile = Join-Path $contextDir "issue-context.md"
            @"
# GitHub Issue #$issueNum

## $title

$body
"@ | Set-Content $contextFile -Encoding UTF8
            Write-Output "Issue context written to .godmode/issue-context.md"
        }
    } catch {
        Write-Output "Could not fetch issue (gh CLI may not be authenticated)"
    }
}

Write-Output "Ready to work on issue #$issueNum"
