$ErrorActionPreference = 'Stop'

# Per-project: report the pull request of the project's branch, and how far its issue's sub-issues have got,
# to GodMode. Runs in the project folder on every turn's end and every 10 minutes while the pull request is
# open. Prints one JSON object:
#   {"pullRequest": {"url": "...", "number": 12, "state": "draft|open|merged|closed", "review": "none|changes_requested|approved"},
#    "progress": {"resolved": 4, "total": 12}}
# each left out when there is none: {} when there is genuinely neither. No pull request: no branch (not a
# repository, detached HEAD), gh finds none for the branch, or git or gh is not installed. No progress: the
# branch is not an issue's (<kind>/<n>-<slug>), or its issue has no sub-issues to count. Any other gh failure
# (network, rate limit, an expired login) exits non-zero with its stderr, so GodMode keeps what it knew and
# keeps polling.
#
# Progress (issue #545): the branch's issue's sub-issues, for an epic or an issue split by hand alike. One closed
# as not planned (or a duplicate) is left out of the total. One counts as resolved when it is closed, or when a
# pull request for it is merged into this branch: an epic's sub-issues stay open until the epic reaches master.
# A pull request is for sub-issue <n> when its branch is <kind>/<n>-…, its title starts IS#<n>, or its body
# says Fixes/Closes/Resolves #<n>.

function Write-Report($pullRequest, $progress) {
    $report = [ordered]@{}
    if ($pullRequest) { $report.pullRequest = $pullRequest }
    if ($progress) { $report.progress = $progress }
    $report | ConvertTo-Json -Compress -Depth 3
    exit 0
}

# Runs gh; returns its stdout, or $null when its stderr matches $none. Any other failure exits non-zero.
function Invoke-Gh([string[]] $ghArgs, [string] $none) {
    $output = & gh @ghArgs 2>&1
    $exitCode = $LASTEXITCODE
    $stderr = ($output | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" }) -join "`n"
    $stdout = ($output | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join "`n"
    if ($exitCode -eq 0) { return $stdout }
    if ($none -and $stderr -match $none) { return $null }
    $tail = ($stderr -split "`n" | Select-Object -Last 5) -join "`n"
    [Console]::Error.WriteLine("gh $($ghArgs[0..1] -join ' ') failed (exit $exitCode): $tail")
    exit 1
}

if (-not (Get-Command git -ErrorAction SilentlyContinue) -or -not (Get-Command gh -ErrorAction SilentlyContinue)) { Write-Report }

git rev-parse --is-inside-work-tree 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Report }
$branch = git symbolic-ref -q --short HEAD 2>$null
if ($LASTEXITCODE -ne 0) { Write-Report }

$env:GH_PROMPT_DISABLED = '1'

# The pull request
$pullRequest = $null
$json = Invoke-Gh @('pr', 'view', '--json', 'url,number,state,isDraft,reviewDecision') 'no (open )?pull requests found'
if ($json) {
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
    $pullRequest = [ordered]@{ url = $pr.url; number = [int]$pr.number; state = $state; review = $review }
}

# The progress of the branch's issue's sub-issues
$progress = $null
if ($branch -match '^[a-z]+/(\d+)-') {
    $issue = $Matches[1]
    $lines = Invoke-Gh @('api', "repos/{owner}/{repo}/issues/$issue/sub_issues?per_page=100", '--paginate',
        '--jq', '.[] | {number, state, state_reason}') 'HTTP 404'
    $subIssues = @($lines -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    $counted = @($subIssues | Where-Object { -not ($_.state -eq 'closed' -and $_.state_reason -in 'not_planned', 'duplicate') })
    if ($counted.Count -gt 0) {
        $open = @($counted | Where-Object { $_.state -ne 'closed' })
        $merged = @()
        if ($open.Count -gt 0) {
            $mergedJson = Invoke-Gh @('pr', 'list', '--base', $branch, '--state', 'merged', '--limit', '500', '--json', 'title,headRefName,body') $null
            $merged = @($mergedJson | ConvertFrom-Json)
        }
        $mergedInto = @($open | Where-Object {
            $n = $_.number
            $merged | Where-Object {
                $_.headRefName -match "^[^/]+/$n-" -or $_.title -match "^IS#$n\b" -or $_.body -match "(?i)\b(fix(es|ed)?|close[sd]?|resolve[sd]?) #$n\b"
            } | Select-Object -First 1
        })
        $progress = [ordered]@{ resolved = $counted.Count - $open.Count + $mergedInto.Count; total = $counted.Count }
    }
}

Write-Report $pullRequest $progress
