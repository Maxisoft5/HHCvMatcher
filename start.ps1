[CmdletBinding()]
param(
    [switch]$Demo,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
Push-Location -LiteralPath $projectRoot
$previousDemo = $env:Hh__Demo
$previousStorage = $env:Storage__Path
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET SDK 10: https://dotnet.microsoft.com/download/dotnet/10.0' }
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'Install Node.js 24 LTS: https://nodejs.org/' }
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.cache/dotnet'
    $env:NUGET_PACKAGES = Join-Path $projectRoot '.cache/nuget'
    $env:npm_config_cache = Join-Path $projectRoot '.cache/npm'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

    if (-not $NoBuild) {
        dotnet restore --configfile (Join-Path $projectRoot 'NuGet.Config')
        if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
        Push-Location -LiteralPath (Join-Path $projectRoot 'web')
        try {
            npm ci --no-fund
            if ($LASTEXITCODE -ne 0) { throw 'npm ci failed. Stop Vite/dev servers before reinstalling packages; see README.md.' }
            npm run build
            if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
        } finally { Pop-Location }
        $webRoot = Join-Path $projectRoot 'src/HhScout.Api/wwwroot'
        New-Item -ItemType Directory -Force -Path $webRoot | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $projectRoot 'web/dist') | Copy-Item -Destination $webRoot -Recurse -Force
        dotnet build -c Release --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'src/HhScout.Api/wwwroot/index.html'))) {
        throw 'Frontend is not built. Run ./start.ps1 without -NoBuild.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'src/HhScout.Api/bin/Release/net10.0/HhScout.Api.dll'))) {
        throw 'Release backend is not built. Run ./start.ps1 without -NoBuild.'
    }
    $env:Hh__Demo = if ($Demo) { 'true' } else { 'false' }
    $env:Storage__Path = if ($Demo) { 'data/scout.demo.db' } else { 'data/scout.db' }
    if ($Demo) {
        Write-Host 'DEMO: synthetic jobs and employers. No requests to hh.ru.' -ForegroundColor Yellow
    } else {
        Write-Host 'Installing/checking Playwright Chromium...' -ForegroundColor Cyan
        dotnet run -c Release --no-build --no-restore --no-launch-profile --project src/HhScout.Api -- --install-browser
        if ($LASTEXITCODE -ne 0) { throw 'Chromium installation failed. Check network access to cdn.playwright.dev; see README.md.' }
        Write-Host 'BROWSER: enter your hh.ru email and the code in the local UI. No API token or redirect URL is needed.' -ForegroundColor Cyan
    }
    Write-Host 'Open http://127.0.0.1:5077/ in your browser. Stop: Ctrl+C.' -ForegroundColor Green
    dotnet run -c Release --no-build --no-restore --no-launch-profile --project src/HhScout.Api
} finally {
    $env:Hh__Demo = $previousDemo
    $env:Storage__Path = $previousStorage
    Pop-Location
}
