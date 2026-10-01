$ErrorActionPreference = 'Stop'

# Per-project: the worktree of an epic's branch, epic/<n>-<slug> (named as ac-gwt-issue names it, so
# either tool finds the other's), where the epic's overseer lives. A branch that exists, here or on
# origin, is checked out as it is, so a new overseer of a running epic carries on with it; a new one
# is cut from origin/master. Either is pushed when origin lacks it: the sub-issues' workers are cut
# from origin/<branch>.
$barePath = Join-Path $env:GODMODE_ROOT_PATH "GodMode.git"
$issueNumber = $env:GODMODE_INPUT_ISSUE_NUMBER
$brief = $env:GODMODE_INPUT_BRIEF

git -C $barePath fetch origin
if ($LASTEXITCODE -ne 0) { throw "git fetch failed (exit code $LASTEXITCODE)" }

$issue = gh issue view $issueNumber --repo johnjuuljensen/GodMode --json number,title,labels | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "gh issue view failed (exit code $LASTEXITCODE)" }
if ($issue.labels.name -notcontains 'epic') { throw "Issue #$issueNumber is not labelled epic: start it with the issue action." }
Write-Output "Epic #${issueNumber}: $($issue.title)"

$slug = ($issue.title.ToLower() -replace '[^a-z0-9]+', '-').Trim('-')
if ($slug.Length -gt 50) { $slug = $slug.Substring(0, 50).TrimEnd('-') }
if (-not $slug) { $slug = 'issue' }
$branch = "epic/$issueNumber-$slug"
$projectPath = Join-Path $env:GODMODE_ROOT_PATH ($branch -replace '/', '-')

if (Test-Path (Join-Path $projectPath '.godmode')) {
    throw "Project folder '$projectPath' is in use: it is a GodMode project. Delete that project first, or carry on in it."
}
if (Test-Path $projectPath) {
    Write-Output "Removing stale directory '$projectPath'..."
    Remove-Item -Recurse -Force $projectPath
    git -C $barePath worktree prune
}

# A local branch of the epic is never deleted here: it may hold merges origin does not have yet
git -C $barePath rev-parse --verify --quiet "refs/remotes/origin/$branch" | Out-Null
$onOrigin = $LASTEXITCODE -eq 0
if (git -C $barePath branch --list $branch) {
    Write-Output "Checking out '$branch' as it is here..."
    git -C $barePath worktree add $projectPath $branch
} elseif ($onOrigin) {
    Write-Output "Checking out '$branch' as origin has it..."
    git -C $barePath worktree add $projectPath -b $branch "origin/$branch"
} else {
    Write-Output "Creating '$branch' from origin/master..."
    git -C $barePath worktree add $projectPath -b $branch origin/master
}
if ($LASTEXITCODE -ne 0) { throw "git worktree add failed (exit code $LASTEXITCODE)" }
if ($onOrigin) {
    git -C $projectPath branch --quiet --set-upstream-to "origin/$branch"
} else {
    Write-Output "Pushing '$branch' to origin..."
    git -C $projectPath push -u origin $branch
    if ($LASTEXITCODE -ne 0) { throw "git push failed (exit code $LASTEXITCODE)" }
}

$prompt = "Use the ``fleet-overseer`` skill. You own epic #$issueNumber and nothing else, as a GodMode session with the fleet's tools. " +
    "Your branch is $branch, checked out here and on origin: every sub-issue's pull request targets it, you merge those, " +
    "and the pull request from it to master is the user's. Read the epic first: ``gh issue view $issueNumber``."
if ($brief) { $prompt += "`n`n$brief" }

# The prompt last, as it may span lines
if ($env:GODMODE_RESULT_FILE) {
    @"
project_path=$projectPath
project_name=Epic #${issueNumber}: $($issue.title)
project_prompt=$prompt
"@ | Set-Content -Path $env:GODMODE_RESULT_FILE -NoNewline
}

Write-Output "Worktree ready at $projectPath (branch: $branch)"
