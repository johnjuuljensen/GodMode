$ErrorActionPreference = 'Stop'

# Per-project: the coordinator overseers' folder, {root}/overseer, which they all share
# (sharedFolder): a detached worktree of origin/master. A checkout, so a coordinator has the repo's
# CLAUDE.md and the fleet skills in .claude/skills; detached, so it holds no branch to push or to
# delete. Coordinators never edit, commit or merge in it: a trial merge goes in a worktree of its own.
$barePath = Join-Path $env:GODMODE_ROOT_PATH "GodMode.git"
$projectPath = Join-Path $env:GODMODE_ROOT_PATH "overseer"

git -C $barePath fetch origin
if ($LASTEXITCODE -ne 0) { throw "git fetch failed (exit code $LASTEXITCODE)" }

if (-not (Test-Path (Join-Path $projectPath '.git'))) {
    if (Test-Path $projectPath) { throw "'$projectPath' is there and is not a worktree: move it away first." }
    Write-Output "Creating the overseers' folder, a detached worktree of origin/master..."
    # A folder removed by hand is still registered, and git would refuse to add it again
    git -C $barePath worktree prune
    git -C $barePath worktree add --detach $projectPath origin/master
    if ($LASTEXITCODE -ne 0) { throw "git worktree add failed (exit code $LASTEXITCODE)" }
} elseif (git -C $projectPath symbolic-ref -q HEAD) {
    # A worktree with a branch checked out is some session's own, whatever its folder is called
    throw "'$projectPath' has $(git -C $projectPath symbolic-ref --short HEAD) checked out, so it is not the overseers' folder: move it away first."
} elseif (git -C $projectPath status --porcelain) {
    Write-Output "The overseers' folder has changes, so it stays where it is: $(git -C $projectPath rev-parse --short HEAD)"
} else {
    # A new coordinator reads master's skills as they are now
    git -C $projectPath checkout --quiet --detach origin/master
    if ($LASTEXITCODE -ne 0) { throw "git checkout failed (exit code $LASTEXITCODE)" }
    Write-Output "The overseers' folder is at origin/master: $(git -C $projectPath rev-parse --short HEAD)"
}

$prompt = "Use the ``fleet-overseer`` skill. You are a coordinator overseer: a GodMode session with the fleet's tools, " +
    "in a folder other coordinators share, a detached checkout of origin/master that you never edit, commit or merge in. " +
    "You report to the user.`n`n$env:GODMODE_INPUT_PROMPT"

if ($env:GODMODE_RESULT_FILE) {
    @"
project_path=$projectPath
project_prompt=$prompt
"@ | Set-Content -Path $env:GODMODE_RESULT_FILE -NoNewline
}
