param(
    [string]$BaseUrl = 'http://127.0.0.1:8787',
    [switch]$Live
)

# Opt-in integration test: creates two small test threads and consumes three
# model responses. Pairing tokens stay in memory and are never printed or saved.
if (-not $Live) { throw 'Use -Live to explicitly run the three real message-delivery checks.' }
$ErrorActionPreference = 'Stop'
$pairingText = Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'CodexLanConsole\pairing.txt') -Raw
$codeMatch = [regex]::Match($pairingText, 'Pairing code:\s*(\d{6})')
if (-not $codeMatch.Success) { throw 'A current local pairing code is required.' }
$pair = Invoke-RestMethod -Uri "$BaseUrl/api/pair" -Method Post -ContentType 'application/json' `
    -Body (@{code=$codeMatch.Groups[1].Value;deviceName='Bridge delivery regression'} | ConvertTo-Json) -TimeoutSec 10
$headers = @{Authorization="Bearer $($pair.token)"}

function Invoke-Bridge([string]$Path, [object]$Body = $null) {
    if ($null -eq $Body) {
        return Invoke-RestMethod -Uri "$BaseUrl/api$Path" -Headers $headers -TimeoutSec 35
    }
    return Invoke-RestMethod -Uri "$BaseUrl/api$Path" -Headers $headers -Method Post `
        -ContentType 'application/json; charset=utf-8' -Body ($Body | ConvertTo-Json -Depth 12 -Compress) -TimeoutSec 35
}

function Send-Probe([string]$ThreadId, [hashtable]$Options, [string]$Label) {
    $body = @{text='Please reply with only ok. Do not call any tools.';clientUserMessageId=[guid]::NewGuid().ToString()}
    foreach ($key in $Options.Keys) { $body[$key]=$Options[$key] }
    $sent = Invoke-Bridge "/threads/$ThreadId/messages" $body
    $receiptId = $sent.receipt.id
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $previous = ''
    do {
        # Read while turn/start is still in flight. Older Bridge builds turned
        # the brief first-history initialization response into a false 501.
        $initialDetail = Invoke-Bridge "/threads/${ThreadId}?paged=true&limit=2"
        if ($initialDetail.thread.id -ne $ThreadId) { throw 'Live detail returned the wrong thread.' }
        $receipt = (Invoke-Bridge "/threads/$ThreadId/commands/$receiptId").receipt
        if ($receipt.status -ne $previous) {
            Write-Host "$Label : $($receipt.status) at $([math]::Round($watch.Elapsed.TotalSeconds,1))s"
            $previous = $receipt.status
        }
        if ($receipt.status -in @('failed','cancelled','dispatchUncertain')) { throw "$Label : $($receipt.lastError)" }
        if ($receipt.status -eq 'delivered') { break }
        Start-Sleep -Milliseconds 750
    } while ($watch.Elapsed.TotalSeconds -lt 60)
    if ($receipt.status -ne 'delivered') { throw "$Label did not reach delivered." }
    do {
        $detail = Invoke-Bridge "/threads/${ThreadId}?paged=true&limit=2"
        $turn = @($detail.thread.turns) | Where-Object { $_.id -eq $receipt.acceptedTurnId } | Select-Object -First 1
        if ($turn.status -in @('failed','interrupted')) { throw "$Label turn ended as $($turn.status)" }
        if ($turn.status -eq 'completed') {
            $reply = @($turn.items | Where-Object { $_.type -eq 'agentMessage' } | ForEach-Object { $_.text }) -join "`n"
            if ($reply.Trim() -ne 'ok') { throw "$Label completed without the expected reply: $reply" }
            Write-Host "$Label : completed with ok"
            return [pscustomobject]@{test=$Label;threadId=$ThreadId;receiptId=$receiptId;status=$receipt.status;turnStatus=$turn.status;reply=$reply;seconds=[math]::Round($watch.Elapsed.TotalSeconds,1)}
        }
        Start-Sleep -Seconds 2
    } while ($watch.Elapsed.TotalSeconds -lt 120)
    throw "$Label did not produce a completed reply within 120 seconds. Inspect thread $ThreadId before retrying."
}

$health = Invoke-RestMethod -Uri "$BaseUrl/api/health" -TimeoutSec 5
Write-Host "Testing Bridge $($health.version)"
$first = (Invoke-Bridge '/threads' @{}).thread.id
$results = @()
$results += Send-Probe $first @{} 'Fresh thread, minimal message'
Start-Sleep -Seconds 7
$summary = Invoke-Bridge '/summary'
if (@($summary.threadAccess | Where-Object { $_.threadId -eq $first }).Count -gt 0) {
    throw 'Completed thread access was not released.'
}
Write-Host 'Completed thread access released'
$results += Send-Probe $first @{} 'Follow-up after access release'
$full = @{permissions='danger-full-access';approvalPolicy='never';approvalsReviewer='auto_review'}
$second = (Invoke-Bridge '/threads' $full).thread.id
# Exceeds the former five-second release bug; an empty page read must also work.
Start-Sleep -Seconds 7
$empty = Invoke-Bridge "/threads/${second}?paged=true&limit=2"
if (@($empty.thread.turns).Count -ne 0) { throw 'Fresh second thread is unexpectedly nonempty.' }
$override = $full.Clone()
$override.model = 'gpt-6-astra'
$override.reasoningEffort = 'low'
$results += Send-Probe $second $override 'Delayed first message, full access and model override'
$results | ConvertTo-Json -Depth 5
