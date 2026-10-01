<#
.SYNOPSIS
    Waits until a pull request the overseer watches changes, or until a time runs out, then exits.
    Run it as a background task (Bash run_in_background): claude starts a turn of its own when the
    task exits, which is how an overseer that is a GodMode session wakes.

.DESCRIPTION
    Polls `gh pr list` for the pull requests into -Base (and/or the ones named by -PullRequest) and
    exits on the first change to any of them: one opened, marked ready or back to draft, a review
    decision, a review (latestReviews), a comment, a push to one that is not a draft, merged or
    closed. A draft's pushes are not watched: a worker pushes at every boundary. Prints what
    changed. At -Minutes it prints "timeout" and exits too, so the overseer also looks at its
    sessions (list_sessions) on a schedule. A failed poll (network, rate limit) is retried at the
    next one.

    What changed between the overseer's last read and the first poll is not seen: arm it last in a
    turn, after your own GitHub writes and a last look at the pull requests you wait on.

.EXAMPLE
    pwsh -NoProfile -File .claude/skills/fleet-overseer/wake.ps1 -Base epic/387-the-fleet -Minutes 20
    pwsh -NoProfile -File .claude/skills/fleet-overseer/wake.ps1 -PullRequest 400,399
#>
param(
    [string]$Base,
    # A string, split here: through -File, 400,399 would bind to an [int[]] as one number
    [string]$PullRequest = '',
    [int]$Minutes = 20,
    [int]$IntervalSeconds = 60
)
$ErrorActionPreference = 'Stop'
$Base = $Base -replace '^origin/', ''
$numbers = @($PullRequest -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { [int]($_ -replace '^#', '') })
if (-not $Base -and $numbers.Count -eq 0) { throw "Give -Base, -PullRequest, or both." }
$env:GH_PROMPT_DISABLED = '1'

function Get-Snapshot {
    $arguments = @('pr', 'list', '--state', 'all', '--limit', '200', '--json', 'number,state,isDraft,reviewDecision,headRefOid,comments,latestReviews')
    if ($Base) { $arguments += '--base', $Base }
    $json = & gh @arguments
    if ($LASTEXITCODE -ne 0) { return $null }
    $snapshot = @{}
    foreach ($pr in $json | ConvertFrom-Json) {
        if ($numbers.Count -and $numbers -notcontains $pr.number) { continue }
        $head = if ($pr.isDraft) { '' } else { " head=$($pr.headRefOid.Substring(0, 7))" }
        $reviews = @($pr.latestReviews | ForEach-Object { "$($_.state)@$($_.submittedAt)" }) -join ','
        $snapshot["$($pr.number)"] = "PR#$($pr.number) $($pr.state)$(if ($pr.isDraft) { ' draft' }) review=$($pr.reviewDecision)$head comments=$($pr.comments.Count) reviews=[$reviews]"
    }
    $snapshot
}

$deadline = (Get-Date).AddMinutes($Minutes)
$before = $null
while (-not $before) {
    $before = Get-Snapshot
    if (-not $before) { if ((Get-Date) -ge $deadline) { 'timeout'; exit 0 }; Start-Sleep -Seconds $IntervalSeconds }
}
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds ([Math]::Max(1, [Math]::Min($IntervalSeconds, ($deadline - (Get-Date)).TotalSeconds)))
    $now = Get-Snapshot
    if (-not $now) { continue }
    $changed = @($now.Keys | Where-Object { $before[$_] -ne $now[$_] } | ForEach-Object { $now[$_] })
    if ($changed) { "changed:"; $changed; exit 0 }
}
'timeout'
