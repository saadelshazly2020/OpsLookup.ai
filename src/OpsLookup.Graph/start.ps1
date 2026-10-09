# Start the OpsLookup agent and open the chat page.
#
#   .\start.ps1            start and open the browser
#   .\start.ps1 -NoBrowser start only
#   .\start.ps1 -Reload    auto-reload on code changes

param(
    [switch]$NoBrowser,
    [switch]$Reload
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$venvPython = Join-Path $PSScriptRoot ".venv\Scripts\python.exe"
$apiUrl     = "http://localhost:8000"

function Fail($message) {
    Write-Host ""
    Write-Host "  $message" -ForegroundColor Red
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Host "  OpsLookup Agent" -ForegroundColor Cyan
Write-Host "  ---------------" -ForegroundColor DarkGray

# --- venv -------------------------------------------------------------------
if (-not (Test-Path $venvPython)) {
    Fail "Virtual environment not found. Run:  python -m venv .venv"
}
if (-not (Test-Path ".venv\Lib\site-packages\langgraph")) {
    Fail "Dependencies missing. Run:  .\.venv\Scripts\pip install -r requirements.txt"
}

# --- .env -------------------------------------------------------------------
if (-not (Test-Path ".env")) {
    Fail ".env not found. Run:  Copy-Item .env.example .env  and fill it in."
}

$envText = Get-Content ".env" -Raw

$missing = @()
foreach ($key in @("API_TOKEN", "LONGCAT_API_KEY")) {
    $match = [regex]::Match($envText, "(?m)^$key=(.*)$")
    if (-not $match.Success -or [string]::IsNullOrWhiteSpace($match.Groups[1].Value)) {
        $missing += $key
    }
}
if ($missing.Count -gt 0) {
    Fail ".env is missing a value for: $($missing -join ', ')"
}

# The Zen gateway needs x-opencode-session + User-Agent; config.py adds both.
if ($envText -match "(?m)^LONGCAT_BASE_URL=https://opencode\.ai/zen") {
    Write-Host "  Model endpoint   OpenCode Zen gateway (session header enabled)" -ForegroundColor Green
}

# --- .NET API ---------------------------------------------------------------
$apiBase = ([regex]::Match($envText, "(?m)^API_BASE_URL=(.*)$")).Groups[1].Value.Trim()
if ($apiBase) {
    try {
        $health = Invoke-RestMethod "$apiBase/health" -TimeoutSec 5 -ErrorAction Stop
        Write-Host "  .NET API        ready  ($apiBase)" -ForegroundColor Green
    } catch {
        Write-Host ""
        Write-Host "  The .NET API is not responding at $apiBase" -ForegroundColor Yellow
        Write-Host "  Start it first:  cd ..\OpsLookup.Api  then  dotnet run" -ForegroundColor Yellow
        Write-Host ""
    }
}

# --- launch -----------------------------------------------------------------
$args = @("-m", "uvicorn", "api:app", "--port", "8000")
if ($Reload) { $args += "--reload" }

Write-Host "  Agent service   starting on $apiUrl" -ForegroundColor Green
Write-Host ""
Write-Host "  Press Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

if (-not $NoBrowser) {
    Start-Job -ScriptBlock {
        param($url)
        Start-Sleep -Seconds 4
        Start-Process $url
    } -ArgumentList $apiUrl | Out-Null
}

& $venvPython @args