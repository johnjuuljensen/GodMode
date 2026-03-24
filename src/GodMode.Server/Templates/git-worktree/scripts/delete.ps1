$ErrorActionPreference = 'Stop'

# Per-project: remove git worktree and local branch (only if all changes are pushed)
$repoName = [System.IO.Path]::GetFileNameWithoutExtension("{{repoUrl}}")
$barePath = Join-Path $env:GODMODE_ROOT_PATH "$repoName.git"
$projectPath = $env:GODMODE_PROJECT_PATH

if ($env:GODMODE_FORCE -ne "true") {
    $status = git -C $projectPath status --porcelain
    if ($status) {
        [Console]::Error.WriteLine("ERROR: Project has uncommitted changes. Commit and push before deleting.")
        exit 1
    }

    $branch = git -C $projectPath rev-parse --abbrev-ref HEAD
    $upstream = git -C $projectPath rev-parse --abbrev-ref --symbolic-full-name "@{u}" 2>$null

    if (-not $upstream) {
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

git -C $barePath worktree remove $projectPath --force

if ($branch -like "project/*") {
    Write-Output "Deleting local branch '$branch'..."
    git -C $barePath branch -D $branch 2>$null
}

Write-Output "Teardown complete."
