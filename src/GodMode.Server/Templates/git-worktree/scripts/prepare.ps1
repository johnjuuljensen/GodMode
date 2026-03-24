$ErrorActionPreference = 'Stop'

# One-time setup: create bare clone of the repository
$repoName = [System.IO.Path]::GetFileNameWithoutExtension("{{repoUrl}}")
$barePath = Join-Path $env:GODMODE_ROOT_PATH "$repoName.git"

if (Test-Path $barePath) {
    Write-Output "Bare repo already exists, fetching updates..."
    git -C $barePath fetch origin
} else {
    Write-Output "Cloning bare repo from {{repoUrl}}..."
    git clone --bare "{{repoUrl}}" $barePath

    # Configure fetch refspec so origin/* refs are available for worktrees
    git -C $barePath config remote.origin.fetch "+refs/heads/*:refs/remotes/origin/*"
    git -C $barePath fetch origin
}

Write-Output "Setup complete. Bare repo at: $barePath"
