param([string]$BaseUrl = 'http://127.0.0.1:8787')
$ErrorActionPreference = 'Stop'
# No authentication, model invocation or user-data changes. HTTP 200 alone is
# insufficient: the SPA fallback can return the wrong application with status 200.
foreach ($route in @('/commute', '/commute/', '/commute/?panel=settings', '/commute/index.html')) {
    $page = Invoke-WebRequest -UseBasicParsing "$BaseUrl$route" -TimeoutSec 15
    if ($page.StatusCode -ne 200 -or -not $page.Content.Contains('id="planForm"') -or $page.Content.Contains('id="threadList"')) {
        throw "Wrong application served at $route"
    }
    Write-Output "OK commute document: $route"
}
foreach ($route in @('/', '/?page=threads', '/?page=remote', '/?page=settings')) {
    $page = Invoke-WebRequest -UseBasicParsing "$BaseUrl$route" -TimeoutSec 15
    if (-not $page.Content.Contains('class="app-nav console-nav"') -or -not $page.Content.Contains('id="newTaskDialog"') -or -not $page.Content.Contains('1.9.0')) {
        throw "Outdated Console document served at $route"
    }
    Write-Output "OK Console document: $route"
}
foreach ($route in @('/console-theme.css?v=2', '/notification-navigation.js?v=1', '/commute/commute.js?v=4', '/commute/commute.css?v=3', '/app.js?v=54', '/styles.css?v=53')) {
    $asset = Invoke-WebRequest -UseBasicParsing "$BaseUrl$route" -TimeoutSec 15
    if ($asset.Headers['Content-Type'] -match 'text/html' -or $asset.RawContentLength -lt 100) {
        throw "Expected code asset, received fallback page: $route"
    }
    Write-Output "OK asset: $route"
}
