$ErrorActionPreference = 'Stop'

# One-time setup: create bare clone of GodMode repo
$barePath = Join-Path $env:GODMODE_ROOT_PATH "GodMode.git"

if (Test-Path $barePath) {
    Write-Output "Bare repo already exists, fetching updates..."
    git -C $barePath fetch origin
} else {
    Write-Output "Cloning bare repo from GitHub..."
    git clone --bare https://github.com/johnjuuljensen/GodMode $barePath

    # Configure fetch refspec so origin/* refs are available for worktrees
    git -C $barePath config remote.origin.fetch "+refs/heads/*:refs/remotes/origin/*"
    git -C $barePath fetch origin
}

Write-Output "Setup complete. Bare repo at: $barePath"
