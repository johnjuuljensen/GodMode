$ErrorActionPreference = 'Stop'

# Per-project: report the pull request of the project's branch to GodMode. Runs in the project folder
# on every turn's end and every 10 minutes while the pull request is open. Prints one JSON object:
#   {"pullRequest": {"url": "...", "number": 12, "state": "draft|open|merged|closed", "review": "none|changes_requested|approved"}}
# or {} when there is genuinely no pull request: no branch (not a repository, detached HEAD), gh finds
# none for the branch, or git or gh is not installed. Any other gh failure (network, rate limit, an
# expired login) exits non-zero with its stderr, so GodMode keeps what it knew and keeps polling.

function Write-NoPullRequest {
    '{}'
    exit 0
}

if (-not (Get-Command git -ErrorAction SilentlyContinue) -or -not (Get-Command gh -ErrorAction SilentlyContinue)) { Write-NoPullRequest }

git rev-parse --is-inside-work-tree 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { Write-NoPullRequest }
git symbolic-ref -q HEAD 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { Write-NoPullRequest }

$env:GH_PROMPT_DISABLED = '1'
$output = gh pr view --json url,number,state,isDraft,reviewDecision 2>&1
$exitCode = $LASTEXITCODE
$stderr = ($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
$json = ($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join "`n"

if ($exitCode -ne 0) {
    if ($stderr -match 'no (open )?pull requests found') { Write-NoPullRequest }
    $tail = ($stderr -split "`n" | Select-Object -Last 5) -join "`n"
    [Console]::Error.WriteLine("gh pr view failed (exit $exitCode): $tail")
    exit 1
}
$pr = $json | ConvertFrom-Json

$state = switch ($pr.state) {
    'OPEN' { if ($pr.isDraft) { 'draft' } else { 'open' } }
    'MERGED' { 'merged' }
    'CLOSED' { 'closed' }
    default { throw "gh reported an unknown pull request state '$($pr.state)'" }
}
$review = switch ($pr.reviewDecision) {
    'CHANGES_REQUESTED' { 'changes_requested' }
    'APPROVED' { 'approved' }
    default { 'none' }  # REVIEW_REQUIRED, or no review policy
}

[ordered]@{
    pullRequest = [ordered]@{ url = $pr.url; number = [int]$pr.number; state = $state; review = $review }
} | ConvertTo-Json -Compress
