$ErrorActionPreference = 'Stop'

if ($env:GODMODE_FORCE -ne "true") {
    Set-Location $env:GODMODE_PROJECT_PATH
    $status = git status --porcelain
    if ($status) {
        Write-Error "Project has uncommitted changes. Commit and push before deleting."
        throw "Uncommitted changes"
    }

    $branch = git rev-parse --abbrev-ref HEAD
    $upstream = git rev-parse --abbrev-ref --symbolic-full-name "@{u}" 2>$null
    if ($upstream) {
        $unpushed = git log "$upstream..HEAD" --oneline 2>$null
        if ($unpushed) {
            Write-Error "Branch '$branch' has unpushed commits."
            throw "Unpushed commits"
        }
    }
}

Write-Output "Removing project directory..."
Remove-Item -Recurse -Force $env:GODMODE_PROJECT_PATH
Write-Output "Teardown complete."
