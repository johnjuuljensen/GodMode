$ErrorActionPreference = 'Stop'

$repoName = [System.IO.Path]::GetFileNameWithoutExtension("{{repoUrl}}")
$barePath = Join-Path $env:GODMODE_ROOT_PATH "$repoName.git"
$projectPath = $env:GODMODE_PROJECT_PATH

if ($env:GODMODE_FORCE -ne "true") {
    $status = git -C $projectPath status --porcelain
    if ($status) {
        Write-Error "Project has uncommitted changes. Commit and push before deleting."
        throw "Uncommitted changes"
    }

    $branch = git -C $projectPath rev-parse --abbrev-ref HEAD
    $upstream = git -C $projectPath rev-parse --abbrev-ref --symbolic-full-name "@{u}" 2>$null

    if (-not $upstream) {
        $unpushed = git -C $projectPath log "origin/{{defaultBranch}}..HEAD" --oneline 2>$null
    } else {
        $unpushed = git -C $projectPath log "$upstream..HEAD" --oneline 2>$null
    }

    if ($unpushed) {
        Write-Error "Branch '$branch' has unpushed commits:`n$unpushed"
        throw "Unpushed commits"
    }

    Write-Output "All changes are pushed. Removing worktree..."
} else {
    Write-Output "Force delete requested. Removing worktree..."
    $branch = git -C $projectPath rev-parse --abbrev-ref HEAD 2>$null
}

git -C $barePath worktree remove $projectPath --force

if ($branch -and $branch.StartsWith("feature/")) {
    Write-Output "Deleting local branch '$branch'..."
    git -C $barePath branch -D $branch 2>$null
}

Write-Output "Teardown complete."
