$ErrorActionPreference = 'Stop'

# Per-project: remove git worktree and local branch (only if all changes are pushed)
$barePath = Join-Path $env:GODMODE_ROOT_PATH "GodMode.git"
$projectPath = $env:GODMODE_PROJECT_PATH
$projectId = $env:GODMODE_PROJECT_FOLDER

# A shared folder (the overseers') is other sessions' too: the server removes only this session's state
if ($env:GODMODE_SHARED_FOLDER -eq 'true') {
    Write-Output "The folder is shared, so it stays: only the session's state goes."
    exit 0
}

if ($env:GODMODE_FORCE -ne "true") {
    # Check for uncommitted changes
    $status = git -C $projectPath status --porcelain
    if ($status) {
        [Console]::Error.WriteLine("ERROR: Project has uncommitted changes. Commit and push before deleting.")
        exit 1
    }

    # Check for unpushed commits on the current branch
    $branch = git -C $projectPath rev-parse --abbrev-ref HEAD
    $upstream = git -C $projectPath rev-parse --abbrev-ref --symbolic-full-name "@{u}" 2>$null

    if (-not $upstream) {
        # No upstream set - check if branch has any commits beyond origin/master
        $unpushed = git -C $projectPath log origin/master..HEAD --oneline 2>$null
        if ($unpushed) {
            [Console]::Error.WriteLine("ERROR: Branch '$branch' has commits not pushed to any remote.")
            [Console]::Error.WriteLine($unpushed)
            exit 1
        }
    } else {
        $unpushed = git -C $projectPath log "$upstream..HEAD" --oneline 2>$null
        if ($unpushed) {
            [Console]::Error.WriteLine("ERROR: Branch '$branch' has unpushed commits.")
            [Console]::Error.WriteLine($unpushed)
            exit 1
        }
    }

    Write-Output "All changes are pushed. Removing worktree..."
} else {
    Write-Output "Force delete requested. Skipping git checks. Removing worktree..."
    $branch = git -C $projectPath rev-parse --abbrev-ref HEAD
}

# Remove the worktree via the bare repo
git -C $barePath worktree remove $projectPath --force

# Delete the local branch if it was auto-created (project/*)
# An epic's branch only when nothing on it is unpushed, checked above: a force delete keeps it, as an
# epic overseer may have merged into it without pushing yet
if ($branch -like "project/*" -or $branch -like "issue-*" -or ($branch -like "epic/*" -and $env:GODMODE_FORCE -ne "true")) {
    Write-Output "Deleting local branch '$branch'..."
    git -C $barePath branch -D $branch 2>$null
}

Write-Output "Teardown complete."
