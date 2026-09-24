$ErrorActionPreference = 'Stop'

# Per-project: report the pull request of the project's branch to GodMode. Runs in the project folder
# on every turn's end and every 10 minutes while the pull request is open. Prints one JSON object:
#   {"pullRequest": {"url": "...", "number": 12, "state": "draft|open|merged|closed", "review": "none|changes_requested|approved"}}
# or {} when the branch has no pull request, or gh is missing or not logged in.

function Write-NoPullRequest {
    '{}'
    exit 0
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Write-NoPullRequest }

$json = gh pr view --json url,number,state,isDraft,reviewDecision 2>$null
if ($LASTEXITCODE -ne 0 -or -not $json) { Write-NoPullRequest }
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
