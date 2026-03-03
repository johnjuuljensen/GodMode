$ErrorActionPreference = 'Stop'

# Per-project: create a git worktree from the bare repo
$barePath = Join-Path $env:GODMODE_ROOT_PATH "GodMode.git"
$projectPath = $env:GODMODE_PROJECT_PATH

# Fetch latest
git -C $barePath fetch origin
if ($LASTEXITCODE -ne 0) { throw "git fetch failed (exit code $LASTEXITCODE)" }

# Clean up stale state from previous failed attempts
if (Test-Path $projectPath) {
    Write-Output "Removing stale directory '$projectPath'..."
    Remove-Item -Recurse -Force $projectPath
}

# Determine branch: use existing if specified, otherwise create new
$inputBranch = $env:GODMODE_INPUT_BRANCH
if ($inputBranch) {
    # Check if branch exists locally, otherwise try origin/<branch>
    $localRef = git -C $barePath branch --list $inputBranch
    if ($localRef) {
        Write-Output "Checking out existing local branch '$inputBranch'..."
        git -C $barePath worktree add $projectPath $inputBranch
    } else {
        $remoteRef = git -C $barePath branch -r --list "origin/$inputBranch"
        if (-not $remoteRef) {
            Write-Error "Branch '$inputBranch' not found locally or on remote."
            throw "Branch '$inputBranch' does not exist"
        }
        Write-Output "Creating local branch '$inputBranch' tracking 'origin/$inputBranch'..."
        git -C $barePath worktree add $projectPath -b $inputBranch "origin/$inputBranch"
    }
} else {
    $branch = "project/$env:GODMODE_PROJECT_ID"
    # Remove stale branch if it exists
    $existingBranch = git -C $barePath branch --list $branch
    if ($existingBranch) {
        Write-Output "Removing stale branch '$branch'..."
        git -C $barePath branch -D $branch
    }
    Write-Output "Creating new branch '$branch' from origin/master..."
    git -C $barePath worktree add $projectPath -b $branch origin/master
}
if ($LASTEXITCODE -ne 0) { throw "git worktree add failed (exit code $LASTEXITCODE)" }

Write-Output "Worktree ready at $projectPath"
