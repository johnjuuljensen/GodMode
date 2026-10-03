$ErrorActionPreference = 'Stop'

# Root-level: what an issue is, for voice to check the action it reads back against (#473). Runs in the root
# with the issue in GODMODE_INPUT_ISSUE, and prints one JSON object: {"title": "...", "labels": ["epic", ...]}.
# Any gh failure exits non-zero, and voice reads the create back unchecked.

$env:GH_PROMPT_DISABLED = '1'
$issueNumber = ($env:GODMODE_INPUT_ISSUE -replace '^#', '').Trim()
if ($issueNumber -notmatch '^\d+$') { throw "Not an issue number: '$($env:GODMODE_INPUT_ISSUE)'" }

$issue = gh issue view $issueNumber --repo johnjuuljensen/GodMode --json title,labels | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "gh issue view failed (exit code $LASTEXITCODE)" }

[ordered]@{ title = $issue.title; labels = [string[]]@($issue.labels | ForEach-Object { $_.name }) } | ConvertTo-Json -Compress
