# Start the embedded agent and open the page.

param(
    [switch]$NoBrowser,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
Set-Location $root

$apiBase = "http://localhost:5100"
$dataApi = "http://localhost:5250"

function Fail($message) {
    Write-Host ""
    Write-Host "  $message" -ForegroundColor Red
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Host "  OpsLookup.NetCorePythonNetApp" -ForegroundColor Cyan
Write-Host "  ------------------------------" -ForegroundColor DarkGray

# --- python environment ------------------------------------------------------
$venv = Join-Path $root "Python\.venv\Scripts\python.exe"
if (-not (Test-Path $venv)) {
    Fail "Virtual environment missing. Run:  cd Python; python -m venv .venv; .\.venv\Scripts\pip install -r requirements.txt"
}
if (-not (Test-Path "Python\.venv\Lib\site-packages\langgraph")) {
    Fail "Python dependencies missing. Run:  cd Python; .\.venv\Scripts\pip install -r requirements.txt"
}

if (-not (Test-Path "Python\.env")) {
    Fail "Python\.env missing. Run:  Copy-Item Python\.env.example Python\.env  and fill it in."
}

$envText = Get-Content "Python\.env" -Raw
$missing = @()
foreach ($key in @("API_TOKEN", "LONGCAT_API_KEY")) {
    $m = [regex]::Match($envText, "(?m)^$key=(.*)$")
    if (-not $m.Success -or [string]::IsNullOrWhiteSpace($m.Groups[1].Value)) {
        $missing += $key
    }
}
if ($missing.Count -gt 0) {
    Fail "Python\.env is missing a value for: $($missing -join ', ')"
}

Write-Host "  Python          ready" -ForegroundColor Green

# --- data api ----------------------------------------------------------------
try {
    $null = Invoke-RestMethod "$dataApi/health" -TimeoutSec 5 -ErrorAction Stop
    Write-Host "  Data API        ready  ($dataApi)" -ForegroundColor Green
} catch {
    Write-Host ""
    Write-Host "  The data API is not responding at $dataApi" -ForegroundColor Yellow
    Write-Host "  Start it first:  cd ..\OpsLookup.Api  then  dotnet run" -ForegroundColor Yellow
    Write-Host ""
}

# --- page --------------------------------------------------------------------
if (-not $SkipBuild) {
    if (-not (Test-Path "ClientApp\node_modules")) {
        Write-Host "  Page            installing npm packages" -ForegroundColor Yellow
        Push-Location "ClientApp"
        npm install 2>&1 | Out-Null
        Pop-Location
    }
    Push-Location "ClientApp"
    $build = npm run build 2>&1
    Pop-Location
    if ($LASTEXITCODE -ne 0) {
        Write-Host $build
        Fail "ClientApp build failed."
    }
    Write-Host "  Page            built into wwwroot" -ForegroundColor Green
}

Write-Host "  App             starting on $apiBase" -ForegroundColor Green
Write-Host ""
Write-Host "  First start takes 60-120s while Python imports. /api/health" -ForegroundColor DarkGray
Write-Host "  reports pythonReady until it is done, and the page still works." -ForegroundColor DarkGray
Write-Host ""
Write-Host "  Press Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

if (-not $NoBrowser) {
    Start-Job -ScriptBlock {
        param($url)
        Start-Sleep -Seconds 15
        Start-Process $url
    } -ArgumentList $apiBase | Out-Null
}

dotnet run --urls $apiBase