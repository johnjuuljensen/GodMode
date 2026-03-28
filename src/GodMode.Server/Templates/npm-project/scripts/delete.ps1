$ErrorActionPreference = 'Stop'
Write-Output "Removing project directory..."
Remove-Item -Recurse -Force $env:GODMODE_PROJECT_PATH
Write-Output "Teardown complete."
