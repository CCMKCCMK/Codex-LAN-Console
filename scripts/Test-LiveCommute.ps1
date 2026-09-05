param([string]$BaseUrl = 'http://127.0.0.1:8787')
$ErrorActionPreference = 'Stop'
# Uses public transit queries only. No model calls, trip writes, preference changes or notifications.
$text = Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'CodexLanConsole\pairing.txt') -Raw
$code = [regex]::Match($text, 'Pairing code:\s*(\d{6})').Groups[1].Value
$pair = Invoke-RestMethod "$BaseUrl/api/pair" -Method Post -ContentType 'application/json' -Body (@{code=$code;deviceName='Commute integration check'} | ConvertTo-Json) -TimeoutSec 10
$headers = @{Authorization="Bearer $($pair.token)"}
$health = Invoke-RestMethod "$BaseUrl/api/health" -TimeoutSec 10
$state = Invoke-RestMethod "$BaseUrl/api/commute/state" -Headers $headers -TimeoutSec 10
Write-Output "Bridge $($health.version); reminder default=$($state.state.settings.remindersEnabled)"
foreach ($direction in @('toCampus','toHome')) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $body = @{direction=$direction;arriveBy=$false} | ConvertTo-Json
    $plan = Invoke-RestMethod "$BaseUrl/api/commute/plan" -Headers $headers -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 55
    if (-not @($plan.options).Count) { throw "$direction returned no usable options" }
    if (-not ($plan.options | Where-Object { $_.mode -eq 'walk' -and $_.available })) { throw 'Walking route missing' }
    if (($plan.options | Where-Object { $_.id -eq $plan.recommendedId }).available -ne $true) { throw 'Recommendation requires unavailable vehicle' }
    Write-Output "$direction in $([math]::Round($watch.Elapsed.TotalSeconds,1)) seconds"
    $plan.options | Select-Object mode,title,minutes,available,basis | Format-Table | Out-String | Write-Output
}
$live = Invoke-RestMethod "$BaseUrl/api/commute/live?direction=toCampus" -Headers $headers -TimeoutSec 55
if (@($live.stops).Count -lt 2) { throw 'Live route stop data unavailable' }
Write-Output "Official live feed: $(@($live.stops).Count) stops, $(@($live.departures).Count) departures, $(@($live.vehicles).Count) fresh vehicles"
$page = Invoke-WebRequest "$BaseUrl/commute/" -UseBasicParsing -TimeoutSec 10
if ($page.StatusCode -ne 200 -or -not $page.Content.Contains('id="planForm"')) { throw 'Commute page unavailable or wrong fallback document' }
foreach ($asset in @('commute.js?v=4','commute.css?v=3','vendor/leaflet.js','vendor/leaflet.css','manifest.webmanifest')) {
    $r = Invoke-WebRequest "$BaseUrl/commute/$asset" -UseBasicParsing -TimeoutSec 10
    if ($r.StatusCode -ne 200 -or $r.Headers['Content-Type'] -match 'text/html') { throw "Asset unavailable: $asset" }
}
Write-Output 'Commute live integration checks passed.'
