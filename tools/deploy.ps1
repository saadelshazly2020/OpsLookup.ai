<#
.SYNOPSIS
    Deploys the Ticketing AI Assistant to IIS: database, schema, credentials and
    both web applications.

.DESCRIPTION
    Everything that used to be done by hand, in the order it has to happen.
    Safe to re-run: each step checks before it acts and reports what it skipped.

    This exists because none of it was written down. Provisioning this stack
    involved a venv, a database, migrations, a SQL principal, two config files
    and app-pool settings -- and the order matters enough that getting it wrong
    produces a different, misleading error each time.

.PARAMETER SigningKey
    HMAC signing key, minimum 32 characters. Generated if not supplied. Reused
    on re-runs so existing tokens keep working.

.PARAMETER ServiceSecret
    Shared secret the agent uses to exchange for a bearer token. Generated if
    not supplied, and matched automatically across both applications.

.PARAMETER DataRoot
    Where logs and the conversation checkpoint database are written. Defaults to
    %ProgramData%\AI.TicketingAssistant. Must not be inside the web root: the app
    pool cannot write there, and each publish would destroy the diagnostics from
    the previous one.

.EXAMPLE
    .\deploy.ps1 -LongCatApiKey oc_sk_xxx

.NOTES
    Run elevated. App-pool environment variables cannot be set otherwise, and
    that is where secrets belong.
#>
[CmdletBinding()]
param(
    [string] $ApiSitePath      = "C:\inetpub\wwwroot\AI.TicketingAssistant.Api",
    [string] $AgentSitePath    = "C:\inetpub\wwwroot\AI.TicketingAssistant.PythonNetApp",
    [int]    $ApiPort          = 3030,
    [int]    $AgentPort        = 3031,
    [string] $SqlServer        = "localhost\SQLEXPRESS",
    [string] $Database         = "OpsLookup",
    [string] $ApiAppPool       = "AI.TicketingAssistant.Api",
    [string] $AgentAppPool     = "AI.TicketingAssistant.PythonNetApp",
    [string] $SigningKey,
    [string] $ServiceSecret,
    [string] $LongCatApiKey,
    [string] $DataRoot,
    [switch] $SkipPublish,
    [switch] $Seed,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$script:RepoRoot = Split-Path -Parent $PSScriptRoot
$Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# ---------------------------------------------------------------- helpers
function Write-Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }
function Write-Ok($message)   { Write-Host "    OK  $message" -ForegroundColor DarkGreen }
function Write-Skip($message) { Write-Host "    --  $message" -ForegroundColor DarkGray }
function Write-Warn($message) { Write-Host "    !!  $message" -ForegroundColor Yellow }

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).
        IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-SqlCmd {
    $found = Get-Command sqlcmd -ErrorAction SilentlyContinue
    if ($found) { return $found.Source }
    $hit = Get-ChildItem "C:\Program Files\Microsoft SQL Server" -Filter sqlcmd.exe `
                -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { return $hit.FullName }
    throw "sqlcmd.exe not found. Install the SQL Server Command Line Utilities."
}

function Get-RandomSecret([int]$Length = 48) {
    # Alphanumeric only. Deliberately excludes quotes, backslashes and other
    # characters that are awkward in JSON, XML and SQL string literals.
    $alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789"
    -join (1..$Length | ForEach-Object { $alphabet[[int](Get-Random -Maximum $alphabet.Length)] })
}

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, $Utf8NoBom)
}

function Invoke-Sql([string]$Sqlcmd, [string]$Query, [string]$Database_ = "master", [switch]$NoThrow) {
    $args = @("-S", $SqlServer, "-E", "-b")
    if ($Database_ -ne "master") { $args += @("-d", $Database_) }
    $args += @("-Q", $Query)

    $output = & $Sqlcmd @args 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) {
        if ($NoThrow) { return $output }
        throw "SQL failed (exit $LASTEXITCODE): $Query`n$($output -join "`n")"
    }
    return $output
}

<#
    Runs a scalar query and returns just the value.

    Two sqlcmd behaviours have to be dealt with or the result is silently wrong:
    it appends "(1 rows affected)" unless NOCOUNT is set, and it can emit more
    than one line. Parsing that blindly yields something that never compares
    equal to 0, so every "does this exist?" check would be answered no and a
    first-time deployment would never create its database.
#>
function Invoke-SqlScalar([string]$Sqlcmd, [string]$Query, [string]$Database_ = "master") {
    $args = @("-S", $SqlServer, "-E", "-b", "-h", "-1", "-W")
    if ($Database_ -ne "master") { $args += @("-d", $Database_) }
    $args += @("-Q", "SET NOCOUNT ON; $Query")

    $output = & $Sqlcmd @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "SQL failed (exit $LASTEXITCODE): $Query" }
    return ("$($output)" | Select-Object -First 1).Trim()
}

# ------------------------------------------------------- 1. prerequisites
Write-Step "Checking prerequisites"

$required = @(
    @{ Name = "dotnet"; Hint = "Install the .NET 9 SDK" }
    @{ Name = "python"; Hint = "Install Python 3.14 (64-bit)" }
)
foreach ($tool in $required) {
    if (-not (Get-Command $tool.Name -ErrorAction SilentlyContinue)) {
        throw "$($tool.Name) not found on PATH. $($tool.Hint)"
    }
    Write-Ok "$($tool.Name) present"
}

$sqlcmd = Get-SqlCmd
Write-Ok "sqlcmd: $sqlcmd"

try {
    Invoke-Sql $sqlcmd "SELECT 1" | Out-Null
    Write-Ok "SQL Server reachable at $SqlServer"
} catch {
    throw "Cannot reach SQL Server '$SqlServer'. $($_.Exception.Message)"
}

$isAdmin = Test-Elevated
if ($isAdmin) { Write-Ok "running elevated" }
else { Write-Warn "NOT elevated - app-pool environment variables cannot be set" }

# ---------------------------------------------------------- 2. secrets
Write-Step "Resolving secrets"

# Read whatever is already deployed before generating, so a re-run does not
# invalidate every token already in flight.
function Get-ExistingSecret([string]$Path, [string]$JsonPath) {
    if (-not (Test-Path $Path)) { return $null }
    try {
        $json = Get-Content $Path -Raw | ConvertFrom-Json
        $node = $json
        foreach ($part in $JsonPath.Split('.')) { $node = $node.$part }
        if ($node -and "$node".Length -ge 32) { return "$node" }
    } catch { }
    return $null
}

# Once secrets move into the app pool they are deliberately absent from the
# settings file, so looking only there would generate a fresh pair on every
# elevated run and quietly invalidate every token already issued. Check the
# app pool too.
function Get-AppPoolSecret([string]$Pool, [string]$Name) {
    if (-not $isAdmin) { return $null }
    try {
        $xml = & "$env:windir\System32\inetsrv\appcmd.exe" list config "/section:system.applicationPools/$Pool" 2>$null
        $text = ($xml -join "`n")
        $match = [regex]::Match($text, [regex]::Escape($Name) + '"\s+value="([^"]*)"')
        if ($match.Success -and $match.Groups[1].Value.Length -ge 32) {
            return $match.Groups[1].Value
        }
    } catch { }
    return $null
}

if (-not $SigningKey) {
    $SigningKey = Get-AppPoolSecret $ApiAppPool "Jwt__SigningKey"
    if ($SigningKey) { Write-Ok "reusing the signing key already in the app pool" }
}

if (-not $SigningKey) {
    $SigningKey = Get-ExistingSecret "$ApiSitePath\appsettings.Production.json" "Jwt.SigningKey"
}
if (-not $SigningKey) {
    $SigningKey = Get-ExistingSecret "$ApiSitePath\appsettings.json" "Jwt.SigningKey"
}
if (-not $SigningKey -or $SigningKey.Length -lt 32) {
    if ($SigningKey) { Write-Warn "existing signing key too short, generating a new one" }
    $SigningKey = Get-RandomSecret 64
    Write-Ok "generated a new signing key"
} else {
    Write-Ok "reusing the existing signing key"
}

if (-not $ServiceSecret) {
    $fromApi = Get-ExistingSecret "$ApiSitePath\appsettings.Production.json" "Jwt.ServiceSecret"
    $fromAgent = Get-ExistingSecret "$AgentSitePath\appsettings.Production.json" "Jwt.ServiceSecret"
    $fromPool = Get-AppPoolSecret $ApiAppPool "Jwt__ServiceSecret"
    if ($fromApi -and $fromAgent -and $fromApi -eq $fromAgent) { $ServiceSecret = $fromApi }
    elseif ($fromApi -or $fromAgent) {
        throw "The two applications have different service secrets. Pass -ServiceSecret to set one explicitly, or -Force to replace both."
    }
    elseif ($fromPool) {
        $ServiceSecret = $fromPool
        Write-Ok "reusing the service secret already in the app pool"
    }
}
if (-not $ServiceSecret) {
    $ServiceSecret = Get-RandomSecret 48
    Write-Ok "generated a new service secret"
} else {
    Write-Ok "service secret resolved"
}

if ($SigningKey.Length -lt 32) {
    throw "SigningKey must be at least 32 characters (got $($SigningKey.Length))."
}

# ------------------------------------------------------------- 3. database
Write-Step "Preparing the database"

$dbExists = Invoke-SqlScalar $sqlcmd "SELECT COUNT(*) FROM sys.databases WHERE name = N'$Database'"

if ($dbExists -eq "0") {
    Invoke-Sql $sqlcmd "CREATE DATABASE [$Database]" | Out-Null
    Write-Ok "created database $Database"
} else {
    Write-Skip "database $Database already exists"
}

$apiIdentity = "IIS AppPool\$ApiAppPool"
$principalSql = $apiIdentity.Replace("'", "''")

$loginCount = Invoke-SqlScalar $sqlcmd "SELECT COUNT(*) FROM sys.server_principals WHERE name = N'$principalSql'"
if ($loginCount -eq "0") {
    Invoke-Sql $sqlcmd "CREATE LOGIN [$apiIdentity] FROM WINDOWS" | Out-Null
    Write-Ok "created login for $apiIdentity"
} else {
    Write-Skip "login already exists"
}

# db_owner is NOT granted. Migrations are applied at deploy time by this script,
# so the application only ever needs DML. db_owner would let it reconfigure its
# own database and, through it, escalate.
Invoke-Sql $sqlcmd @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$principalSql')
    CREATE USER [$apiIdentity] FOR LOGIN [$apiIdentity];
"@ $Database | Out-Null
Write-Ok "database user present"

foreach ($role in @("db_datareader", "db_datawriter")) {
    Invoke-Sql $sqlcmd @"
IF IS_ROLEMEMBER('$role', N'$principalSql') <> 1
    ALTER ROLE [$role] ADD MEMBER [$apiIdentity];
"@ $Database | Out-Null
}

# Guarded, because a bare DROP MEMBER errors out when the principal is not a
# member -- which is the normal state on every run after the first. Without the
# guard the script throws here and dies before reaching the config step, so a
# second run fails exactly where the first run succeeded.
Invoke-Sql $sqlcmd @"
IF IS_ROLEMEMBER('db_owner', N'$principalSql') = 1
    EXEC sp_droprolemember N'db_owner', N'$principalSql';
"@ $Database -NoThrow | Out-Null
Write-Ok "granted db_datareader + db_datawriter (db_owner removed)"

# ------------------------------------------------------------ 4. migrations
Write-Step "Applying migrations"

$apiProject = Get-ChildItem $script:RepoRoot -Recurse -Filter "AI.TicketingAssistant.Api.csproj" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $apiProject) { throw "AI.TicketingAssistant.Api.csproj not found under $script:RepoRoot" }

$conn = "Server=$SqlServer;Database=$Database;Integrated Security=True;MultipleActiveResultSets=true;TrustServerCertificate=True"
$env:ConnectionStrings__ContractDb = $conn
try {
    & dotnet ef database update --project $apiProject.FullName --no-build 2>&1 |
        Where-Object { $_ -match "Applying|Done|error" } | ForEach-Object { Write-Host "    $_" }
    if ($LASTEXITCODE -ne 0) { throw "dotnet ef database update failed" }
    Write-Ok "schema up to date"
} finally {
    Remove-Item Env:\ConnectionStrings__ContractDb -ErrorAction SilentlyContinue
}

if ($Seed) {
    $seedSql = Join-Path $PSScriptRoot "seed-dev-data.sql"
    if (Test-Path $seedSql) {
        & $sqlcmd -S $SqlServer -d $Database -E -b -i $seedSql | Out-Null
        Write-Ok "seed data inserted"
    } else {
        Write-Warn "-Seed requested but seed-dev-data.sql not found"
    }
}

# --------------------------------------------------------------- 5. publish
# --------------------------------------------------------------- 5. publish

# Guard, before anything ships. The two Python trees are deliberate physical
# copies that must stay byte-identical, and nothing enforces that. They drifted
# once already: graph/build.py in the embedded copy had older Arabic wording
# ("رقم الهوية الوطنية", "الوكالة") while the UI used "الرقم الاجتماعي" and
# "محامي", so the deployed agent contradicted the button the operator clicked.
# A hash check is cheap; finding that in production is not.
Write-Step "Checking the Python copies are in sync"

$graphRoot = Join-Path $script:RepoRoot "src\OpsLookup.Graph"
$embedRoot = Join-Path $script:RepoRoot "src\OpsLookup.NetCorePythonNetApp\Python"

function Get-PythonHashes([string]$Root) {
    $map = @{}
    if (-not (Test-Path $Root)) { return $map }
    Get-ChildItem $Root -Recurse -Filter *.py |
        Where-Object { $_.FullName -notmatch '\\\.venv\\|\\__pycache__\\' } |
        ForEach-Object { $map[$_.FullName.Substring($Root.Length)] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    return $map
}

$graphHashes = Get-PythonHashes $graphRoot
$embedHashes = Get-PythonHashes $embedRoot

# Expected structural differences: api.py is the dev FastAPI entry point and
# host.py is the IIS host. They are not copies of each other and must not be.
$expectedOnlyInGraph  = @("\api.py")
$expectedOnlyInEmbed  = @("\host.py")

$drifted = @()
foreach ($relative in $graphHashes.Keys) {
    if ($expectedOnlyInGraph -contains $relative) { continue }
    if (-not $embedHashes.ContainsKey($relative)) {
        $drifted += "missing from the embedded copy: $relative"
    } elseif ($embedHashes[$relative] -ne $graphHashes[$relative]) {
        $drifted += "content differs: $relative"
    }
}
foreach ($relative in $embedHashes.Keys) {
    if ($expectedOnlyInEmbed -contains $relative) { continue }
    if (-not $graphHashes.ContainsKey($relative)) { $drifted += "missing from Graph: $relative" }
}

if ($drifted.Count -gt 0) {
    Write-Host "    The two Python trees have drifted:" -ForegroundColor Red
    $drifted | ForEach-Object { Write-Host "      - $_" -ForegroundColor Red }
    throw "Sync the trees before deploying. Copy from OpsLookup.Graph to OpsLookup.NetCorePythonNetApp\Python and re-run."
}
Write-Ok "$($graphHashes.Count - $expectedOnlyInGraph.Count) shared .py files byte-identical"

if (-not $SkipPublish) {
    Write-Step "Publishing applications"

    # w3wp holds the running assemblies open, so publishing straight into the
    # site folder fails with MSB3027 "the file is locked by w3wp.exe" -- and
    # MSBuild retries it ten times first, so it looks like a hang.
    #
    # app_offline.htm is the documented way to release that lock without
    # stopping the app pool, which needs elevation. Dropping the file in the
    # root makes IIS serve a holding page and recycle the worker; removing it
    # afterwards lets the new deployment start.
    $offlinePage = "<!DOCTYPE html><html><head><meta charset='utf-8'><title>Updating</title></head><body><h1>Updating - please retry in a minute</h1></body></html>"
    foreach ($site in @($ApiSitePath, $AgentSitePath)) {
        Write-Utf8NoBom (Join-Path $site "app_offline.htm") $offlinePage
    }
    Write-Host "    Sites taken offline to release the file lock..."
    Start-Sleep -Seconds 5

    try {
        Write-Host "    API..."
        $apiOutput = & dotnet publish (Join-Path $script:RepoRoot "src\OpsLookup.Api\AI.TicketingAssistant.Api.csproj") `
            -c Release -o $ApiSitePath -v q --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            # The build output is not discarded on failure. A publish that fails
            # with "publishing failed" and no reason is the worst possible outcome.
            throw "publishing the API failed:`n$((($apiOutput | Select-Object -Last 12) -join "`n"))"
        }
        Write-Ok "API published"

        Write-Host "    agent (includes the Python environment)..."
        $agentOutput = & dotnet publish (Join-Path $script:RepoRoot "src\OpsLookup.NetCorePythonNetApp\AI.TicketingAssistant.PythonNetApp.csproj") `
            -c Release -o $AgentSitePath -p:IncludePythonVenv=true -v q --nologo 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "publishing the agent failed:`n$((($agentOutput | Select-Object -Last 12) -join "`n"))"
        }
        Write-Ok "agent published"
    } finally {
        foreach ($site in @($ApiSitePath, $AgentSitePath)) {
            $offline = Join-Path $site "app_offline.htm"
            if (Test-Path $offline) { Remove-Item $offline -Force }
        }
        Write-Host "    Sites back online." -ForegroundColor DarkGray
    }
} else {
    Write-Skip "publish skipped"
}

# The publish overwrites web.config, so it is rewritten here rather than being
# edited by hand afterwards. Note what is NOT in it: an <environmentVariables>
# block. This IIS rejects those with 500.19 / 0x8007000d, which is a long and
# deeply misleading debugging session if you have to rediscover it.
Write-Step "Writing web.config"

foreach ($site in @(
    @{ Path = $ApiSitePath;   Dll = "AI.TicketingAssistant.Api.dll" }
    @{ Path = $AgentSitePath; Dll = "AI.TicketingAssistant.PythonNetApp.dll" }
)) {
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <handlers>
      <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
    </handlers>
    <aspNetCore processPath="dotnet" arguments=".\$($site.Dll)" stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" hostingModel="inprocess" />
  </system.webServer>
</configuration>
"@
    Write-Utf8NoBom (Join-Path $site.Path "web.config") $xml
    Write-Ok "web.config written (UTF-8, no BOM -- IIS rejects a BOM with 500.19)"
}

# ---------------------------------------------------------------- 6. config

# Secrets go into the app pool first, and only end up in a file if that fails.
# The ordering matters: a secret written under C:\inetpub\wwwroot is one
# misconfiguration away from being served, and the earlier version of this script
# wrote it unconditionally and then told you to remove it afterwards.
Write-Step "Placing secrets"

$secretInAppPool = @{}

if ($isAdmin) {
    function Set-AppPoolVariable([string]$Pool, [string]$Name, [string]$Value) {
        & "$env:windir\System32\inetsrv\appcmd.exe" set config -section:"system/applicationPools/$Pool" `
            "/[environmentVariables]/add:name=$Name" "/value=$Value" 2>&1 | Out-Null
        return ($LASTEXITCODE -eq 0)
    }

    foreach ($pool in @($ApiAppPool, $AgentAppPool)) {
        if (Set-AppPoolVariable $pool "Jwt__ServiceSecret" $ServiceSecret) {
            $secretInAppPool["service"] = $true
            Write-Ok "Jwt__ServiceSecret set on '$pool'"
        } else {
            $secretInAppPool["service"] = $false
            Write-Warn "could not set Jwt__ServiceSecret on '$pool'"
        }
    }
    if (Set-AppPoolVariable $ApiAppPool "Jwt__SigningKey" $SigningKey) {
        $secretInAppPool["signing"] = $true
        Write-Ok "Jwt__SigningKey set on '$ApiAppPool'"
    } else {
        $secretInAppPool["signing"] = $false
        Write-Warn "could not set Jwt__SigningKey"
    }
} else {
    $secretInAppPool["service"] = $false
    $secretInAppPool["signing"] = $false
}

$serviceInFile = -not $secretInAppPool["service"]
$signingInFile = -not $secretInAppPool["signing"]

if ($serviceInFile -or $signingInFile) {
    Write-Warn "some secrets had to be written to appsettings.Production.json under the web root."
    Write-Warn "re-run this script elevated to move them into app-pool environment variables."
}

$apiSettings = [ordered]@{
    ConnectionStrings = [ordered]@{
        ContractDb = $conn
    }
    Jwt = [ordered]@{
        Issuer              = "opslookup"
        Audience            = "opslookup"
        ServiceTokenMinutes = 720
        ServiceSubject      = "ticketing-agent"
    }
}
# Omitted entirely rather than written blank. An empty SigningKey would still
# look like configuration, and the Production startup check would reject it with
# a message that does not point at the app pool.
if ($signingInFile) { $apiSettings.Jwt["SigningKey"] = $SigningKey }
if ($serviceInFile) { $apiSettings.Jwt["ServiceSecret"] = $ServiceSecret }
Write-Utf8NoBom (Join-Path $ApiSitePath "appsettings.Production.json") `
    ($apiSettings | ConvertTo-Json -Depth 6)
Write-Ok "API appsettings.Production.json"

$agentSettings = [ordered]@{
    # 127.0.0.1, not localhost. Under the app-pool identity "localhost" resolves
    # to ::1 first, the data API is not listening there, and httpx does not fall
    # back to IPv4 the way a browser does -- so every lookup failed with a
    # connection error while the API was plainly up.
    Lookup = [ordered]@{ BaseUrl = "http://127.0.0.1:$ApiPort" }
    Python = [ordered]@{
        Directory = "Python"
        Venv      = "Python/.venv"
    }
}
if ($serviceInFile) { $agentSettings["Jwt"] = [ordered]@{ ServiceSecret = $ServiceSecret } }
Write-Utf8NoBom (Join-Path $AgentSitePath "appsettings.Production.json") `
    ($agentSettings | ConvertTo-Json -Depth 6)
Write-Ok "agent appsettings.Production.json"

if (-not $serviceInFile -and -not $signingInFile) {
    Write-Ok "no secrets in the web root"
}

# ----------------------------------------------------------- 7. Python .env
Write-Step "Writing the Python environment file"

$key = $LongCatApiKey
if (-not $key) {
    $existing = Join-Path $AgentSitePath "Python\.env"
    if (Test-Path $existing) {
        $m = Select-String -Path $existing -Pattern '^LONGCAT_API_KEY=(.+)$'
        if ($m) { $key = $m.Matches[0].Groups[1].Value }
    }
}
if (-not $key) {
    throw "No LongCat API key. Pass -LongCatApiKey or set one in the deployed Python\.env first."
}

# No API_TOKEN here on purpose: the .NET host mints a bearer token per turn and
# hands it to Python. The agent must never hold a credential of its own.
$pythonEnv = @"
# Written by deploy.ps1. Regenerated on every run -- do not hand-edit.
# There is deliberately no API_TOKEN: the .NET host obtains a token per turn
# via POST /auth/service-token and passes it in, so the agent has no
# credential-issuing capability of its own.

API_BASE_URL=http://127.0.0.1:$ApiPort
OPERATOR_ID=op-4471

LONGCAT_API_KEY=$key
LONGCAT_BASE_URL=https://opencode.ai/zen/go/v1
LONGCAT_MODEL=longcat-2.5-preview-free

CHECKPOINT_PATH=checkpoints.sqlite
RECURSION_LIMIT=10
REQUEST_TIMEOUT_SECONDS=20
LOG_LEVEL=INFO
"@
Write-Utf8NoBom (Join-Path $AgentSitePath "Python\.env") $pythonEnv
Write-Ok "Python\.env written (no API_TOKEN)"

# --------------------------------------------------- 7b. writable data dir
#
# Logs and the SQLite checkpointer must not live under the web root. Two reasons,
# and the second is the one that bites later: the app pool identity cannot write
# there, and every publish overwrites them along with the code they describe, so
# the diagnostics that would explain a failed deployment are destroyed by it.
#
# The app also resolves this itself (PythonBridge.ApplyDataDirectory) and falls
# back through ProgramData to the temp folder, so this step is not strictly
# required. It exists because creating the directory *as the deployer* and
# granting the app pool access is more reliable than hoping the identity can
# create it at runtime, and because the ACL is what stops this failing silently.
Write-Step "Preparing the data directory"

$dataRoot = if ($DataRoot) { $DataRoot } else {
    Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) "AI.TicketingAssistant"
}
$dataDir = Join-Path $dataRoot "data"
# host.py puts its logs at $OPSLOOKUP_DATA_DIR/logs, i.e. under the data
# directory -- not beside it. Creating dataRoot\logs instead produced an empty
# directory that never received anything, which looked like logging was broken.
$logsDir = Join-Path $dataDir "logs"

foreach ($dir in @($dataRoot, $dataDir, $logsDir)) {
    if (-not (Test-Path $dir)) {
        try {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        } catch {
            throw "Could not create '$dir'. Pass -DataRoot to choose a location you can write to. ($($_.Exception.Message))"
        }
    }
}
Write-Ok "data directory exists at $dataDir"

# Modify on this folder and everything under it. Created unelevated this still
# succeeds, because the deployer owns the folder they just made and therefore
# have WRITE_DAC on it.
$agentIdentity = "IIS AppPool\$AgentAppPool"
$aclOk = $true
try {
    & icacls $dataDir /grant "${agentIdentity}:(OI)(CI)M" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { $aclOk = $false }
    & icacls $logsDir /grant "${agentIdentity}:(OI)(CI)M" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { $aclOk = $false }
} catch { $aclOk = $false }

if ($aclOk) {
    Write-Ok "granted '$agentIdentity' modify access"
} else {
    # Not fatal: the app falls back through ProgramData to the temp folder. But
    # say it, because the fallback is worse -- temp can be cleaned mid-run, and
    # the resulting log loss is exactly what hurts when something goes wrong.
    Write-Warn "could not grant '$agentIdentity' access; the app will fall back"
    Write-Warn "to %TEMP%. Logs and checkpoints may not survive."
}

# ------------------------------------------------------------- 8. verify
Write-Step "Verifying"

# Touching web.config is the fallback: it notifies the module to recycle
# without needing elevation, so a non-elevated run still picks up new config.
Write-Host "    Releasing the application pools..."
$recycled = $false
if ($isAdmin) {
    foreach ($pool in @($ApiAppPool, $AgentAppPool)) {
        & "$env:windir\System32\inetsrv\appcmd.exe" recycle apppool /apppool.name:$pool 2>&1 | Out-Null
    }
    $recycled = $true
    Write-Ok "pools recycled"
} else {
    # An actual rewrite, not a LastWriteTime update. Touching the timestamp
    # alone does not notify the module; the file contents have to change on
    # disk before it recycles.
    foreach ($site in @($ApiSitePath, $AgentSitePath)) {
        $config = Join-Path $site "web.config"
        if (Test-Path $config) {
            $text = [System.IO.File]::ReadAllText($config)
            Write-Utf8NoBom $config $text
        }
    }
    Write-Skip "not elevated - recycled by rewriting web.config instead"
}
Write-Host "    The agent takes 60-120s to warm up. Waiting..."

$deadline = (Get-Date).AddSeconds(180)
$agentReady = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 10
    try {
        $health = Invoke-RestMethod "http://localhost:$AgentPort/api/health" -TimeoutSec 15
        if ($health.pythonReady) { $agentReady = $true; break }
    } catch { }
}
if ($agentReady) { Write-Ok "agent warm: pythonReady=true" }
else { Write-Warn "agent did not warm within 180s - check /api/health" }

$apiHealth = $false
try { $null = Invoke-RestMethod "http://localhost:$ApiPort/health" -TimeoutSec 15; $apiHealth = $true } catch { }
if ($apiHealth) { Write-Ok "API responding" } else { Write-Warn "API not responding" }

# The dev-token endpoint must not exist outside Development. If this ever
# returns anything other than 404, anyone who can reach the port can mint a
# token and read every contract and attorney row.
$devTokenClosed = $false
try {
    Invoke-WebRequest "http://localhost:$ApiPort/auth/dev-token?operatorId=probe&minutes=525600" `
        -Method Post -TimeoutSec 15 | Out-Null
} catch {
    if ([int]$_.Exception.Response.StatusCode -eq 404) { $devTokenClosed = $true }
}
if ($devTokenClosed) { Write-Ok "dev-token is closed outside Development (404)" }
else { Write-Warn "dev-token responded - this MUST be 404 outside Development" }

$serviceTokenOk = $false
try {
    $token = Invoke-RestMethod "http://localhost:$ApiPort/auth/service-token" -Method Post `
        -Headers @{ "X-Service-Secret" = $ServiceSecret } -TimeoutSec 15
    $rows = Invoke-RestMethod "http://localhost:$ApiPort/api/contracts?socialId=1234567890" `
        -Headers @{ Authorization = "Bearer $($token.accessToken)" } -TimeoutSec 15
    $serviceTokenOk = $true
} catch { }
if ($serviceTokenOk) {
    Write-Ok "service token works ($($rows.Count) contract rows returned)"
} else {
    Write-Warn "service token did not work - check that both apps share the secret"
}

# Take a turn, then check where the state actually landed. Asserting the
# directory exists proves nothing -- the failure mode being guarded against is
# Python writing there anyway despite it existing, which is what happened when
# OPSLOOKUP_DATA_DIR was simply never set.
$turnOk = $false
try {
    $probe = Join-Path ([System.IO.Path]::GetTempPath()) "deploy-probe-$([guid]::NewGuid().ToString('N')).json"
    Write-Utf8NoBom $probe '{"ticketId":"deploy-probe","message":"What contracts are held for social ID 1234567890?","locale":"en"}'
    $reply = Invoke-RestMethod "http://localhost:$AgentPort/api/chat" -Method Post `
        -ContentType "application/json" -InFile $probe -TimeoutSec 180
    Remove-Item $probe -Force -ErrorAction SilentlyContinue
    $turnOk = -not $reply.lookupFailed
} catch { }
if ($turnOk) { Write-Ok "end-to-end turn returned real data" }
else { Write-Warn "end-to-end turn failed or returned no data" }

$checkpoint = Join-Path $dataDir "checkpoints.sqlite"
if (Test-Path $checkpoint) {
    $stamp = (Get-Item $checkpoint).LastWriteTime
    Write-Ok "checkpoints writing to $dataDir (updated $stamp)"
} else {
    Write-Warn "no checkpoint file in $dataDir"
    $stray = Join-Path $AgentSitePath "Python\checkpoints.sqlite"
    if (Test-Path $stray) {
        Write-Warn "it is in the WEB ROOT instead: $stray"
        Write-Warn "set Python:DataRoot in appsettings.Production.json and re-run."
    }
}

$strayLogs = Join-Path $AgentSitePath "Python\logs"
if (Test-Path $strayLogs) {
    Write-Warn "stale log directory from an earlier deployment: $strayLogs"
    Remove-Item $strayLogs -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path $strayLogs)) { Write-Ok "removed" }
}

$strayCheckpoint = Join-Path $AgentSitePath "Python\checkpoints.sqlite"
if (Test-Path $strayCheckpoint) {
    Write-Warn "stale checkpoint database from an earlier deployment"
    Write-Warn "$strayCheckpoint -- conversation history before today is in it."
    Write-Warn "Move it to $dataDir if you want to keep it; deleting it clears all history."
}

# ------------------------------------------------------------- summary
Write-Host "`n" -NoNewline
Write-Host "Done." -ForegroundColor Green
Write-Host "  API    http://localhost:$ApiPort"
Write-Host "  Agent  http://localhost:$AgentPort"
Write-Host "  Data   $dataDir"
Write-Host ""
Write-Host "Still to do:" -ForegroundColor DarkGray
Write-Host "  1. Rotate the LongCat API key - it has been exposed in chat." -ForegroundColor DarkGray
Write-Host "  2. Re-run elevated so secrets move out of the web root." -ForegroundColor DarkGray
Write-Host "  3. Expect ~2 min of 503s after every recycle (embedded Python)." -ForegroundColor DarkGray
Write-Host ""