[CmdletBinding()]
param(
    # Also start the tool automatically every time you log in to Windows.
    [switch]$AtLogin
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$build = Join-Path $root 'build'
$source = Join-Path $root 'tools\PresenterHotkey.cs'
$exe = Join-Path $build 'PresenterHotkey.exe'
$config = Join-Path $build 'presenter-hotkey.json'
$sampleConfig = Join-Path $root 'config.sample.json'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$referenceRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0'
$uiaClient = Join-Path $referenceRoot 'UIAutomationClient.dll'
$uiaTypes = Join-Path $referenceRoot 'UIAutomationTypes.dll'
$windowsBase = Join-Path $referenceRoot 'WindowsBase.dll'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "Windows C# compiler not found: $compiler"
}

foreach ($ref in @($uiaClient, $uiaTypes, $windowsBase)) {
    if (-not (Test-Path -LiteralPath $ref)) {
        throw "Required .NET reference not found: $ref"
    }
}

New-Item -ItemType Directory -Path $build -Force | Out-Null

# Default config (only created if missing, so your edits are preserved).
if (-not (Test-Path -LiteralPath $config)) {
    if (-not (Test-Path -LiteralPath $sampleConfig)) {
        throw "Sample config not found: $sampleConfig"
    }
    Copy-Item -LiteralPath $sampleConfig -Destination $config -Force
    Write-Host "Created local config from sample: $config" -ForegroundColor Green
}

# Stop any running instance before recompiling.
Get-Process -Name 'PresenterHotkey' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

& $compiler /nologo /target:winexe "/out:$exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    "/reference:$uiaClient" `
    "/reference:$uiaTypes" `
    "/reference:$windowsBase" `
    $source
if ($LASTEXITCODE -ne 0) {
    throw 'PresenterHotkey compilation failed.'
}

if ($AtLogin) {
    $startup = [Environment]::GetFolderPath('Startup')
    $lnk = Join-Path $startup 'PresenterHotkey.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($lnk)
    $shortcut.TargetPath = $exe
    $shortcut.WorkingDirectory = $build
    $shortcut.WindowStyle = 7
    $shortcut.Description = 'INPHIC presenter -> ChatGPT voice shortcuts'
    $shortcut.Save()
    Write-Host "Auto-start at login enabled: $lnk" -ForegroundColor Green
}

Start-Process -FilePath $exe -WindowStyle Hidden
Write-Host 'PresenterHotkey is running (look for the tray icon).' -ForegroundColor Green
Write-Host 'Double-press presenter Up  => Ctrl+Shift+D toggle in ChatGPT; hold/release Ctrl+Shift+D in Codex' -ForegroundColor Cyan
Write-Host '                              PRISM tab: focus Ask anything, then Win+H voice typing' -ForegroundColor Cyan
Write-Host '                              Claude desktop: Win+H Windows voice typing toggle' -ForegroundColor Cyan
Write-Host 'Double-press presenter Down => click Send when possible; otherwise Enter' -ForegroundColor Cyan
Write-Host '                              Claude with voice typing open: stop Win+H first, then send' -ForegroundColor Cyan
Write-Host 'ArrowUp/ArrowDown are intercepted; single presses are ignored.' -ForegroundColor Yellow
Write-Host "Local config: $config" -ForegroundColor Yellow
Write-Host 'To stop: right-click the tray icon -> Exit, or run Stop-PresenterHotkey.ps1' -ForegroundColor Yellow
