<#
.SYNOPSIS
    Waits until a pull request the overseer watches changes, or until a time runs out, then exits.
    Run it as a background task (Bash run_in_background): claude starts a turn of its own when the
    task exits, which is how an overseer that is a GodMode session wakes.

.DESCRIPTION
    Polls `gh pr list` for the pull requests into -Base (and/or the ones named by -PullRequest) and
    exits on the first change to any of them: one opened, marked ready or back to draft, a review
    decision, a push (its head commit), a comment, merged or closed. Prints what changed. At
    -Minutes it prints "timeout" and exits too, so the overseer also looks at its sessions
    (list_sessions) on a schedule. A failed poll (network, rate limit) is retried at the next one.

.EXAMPLE
    pwsh -NoProfile -File .claude/skills/fleet-overseer/wake.ps1 -Base epic/387-the-fleet -Minutes 20
#>
param(
    [string]$Base,
    [int[]]$PullRequest = @(),
    [int]$Minutes = 20,
    [int]$IntervalSeconds = 60
)
$ErrorActionPreference = 'Stop'
if (-not $Base -and $PullRequest.Count -eq 0) { throw "Give -Base, -PullRequest, or both." }
$env:GH_PROMPT_DISABLED = '1'

function Get-Snapshot {
    $arguments = @('pr', 'list', '--state', 'all', '--limit', '200', '--json', 'number,state,isDraft,reviewDecision,headRefOid,comments')
    if ($Base) { $arguments += '--base', $Base }
    $json = & gh @arguments
    if ($LASTEXITCODE -ne 0) { return $null }
    $snapshot = @{}
    foreach ($pr in $json | ConvertFrom-Json) {
        if ($PullRequest.Count -and $PullRequest -notcontains $pr.number) { continue }
        $snapshot["$($pr.number)"] = "PR#$($pr.number) $($pr.state)$(if ($pr.isDraft) { ' draft' }) review=$($pr.reviewDecision) head=$($pr.headRefOid.Substring(0, 7)) comments=$($pr.comments.Count)"
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
    Start-Sleep -Seconds $IntervalSeconds
    $now = Get-Snapshot
    if (-not $now) { continue }
    $changed = @($now.Keys | Where-Object { $before[$_] -ne $now[$_] } | ForEach-Object { $now[$_] })
    if ($changed) { "changed:"; $changed; exit 0 }
}
'timeout'
