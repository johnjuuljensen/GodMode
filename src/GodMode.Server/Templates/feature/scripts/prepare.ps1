$ErrorActionPreference = 'Stop'

$repoName = [System.IO.Path]::GetFileNameWithoutExtension("{{repoUrl}}")
$barePath = Join-Path $env:GODMODE_ROOT_PATH "$repoName.git"

if (Test-Path $barePath) {
    Write-Output "Bare repo already exists, fetching updates..."
    git -C $barePath fetch origin
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed" }
} else {
    Write-Output "Cloning bare repo from {{repoUrl}}..."
    git clone --bare "{{repoUrl}}" $barePath
    if ($LASTEXITCODE -ne 0) { throw "git clone --bare failed" }

    git -C $barePath config remote.origin.fetch "+refs/heads/*:refs/remotes/origin/*"
    git -C $barePath fetch origin
    if ($LASTEXITCODE -ne 0) { throw "git fetch failed" }
}

Write-Output "Setup complete. Bare repo at: $barePath"
