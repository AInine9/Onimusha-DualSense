param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$NexusMod
)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$release = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $release) { throw 'Choose a new output directory.' }
$bin = Join-Path $release 'bin'
New-Item -ItemType Directory -Path $bin -Force | Out-Null
foreach ($name in @('OnimushaDualSense.dll','OnimushaDualSense.deps.json','OnimushaDualSense.runtimeconfig.json')) {
    Copy-Item -LiteralPath (Join-Path $PublishDirectory $name) -Destination $bin
}
foreach ($name in @('libportaudio64bit.dll','defense_haptics.json')) {
    Copy-Item -LiteralPath (Join-Path $project ('distribution/' + $name)) -Destination $bin
}
Copy-Item -LiteralPath (Join-Path $project 'reframework/autorun/onimusha_dualsense_bridge.lua') -Destination $bin
foreach ($name in @('README.md','LICENSE','THIRD_PARTY_NOTICES.txt','config.json')) {
    Copy-Item -LiteralPath (Join-Path $project ('distribution/' + $name)) -Destination $release
}

if (-not $NexusMod) {
    $launchers = [ordered]@{
        'Setup.cmd' = 'setup'
        'Start-Mod.cmd' = 'launch'
        'Stop-Mod.cmd' = 'stop'
        'Uninstall.cmd' = 'uninstall'
    }
    foreach ($item in $launchers.GetEnumerator()) {
        $lines = @('@echo off','setlocal','cd /d "%~dp0"','set "modDotnet=%ProgramFiles%\dotnet\dotnet.exe"',
            'if not exist "%modDotnet%" set "modDotnet=dotnet"',
            ('"%modDotnet%" "%~dp0bin\OnimushaDualSense.dll" ' + $item.Value + ' %*'),
            'set "modExit=%ERRORLEVEL%"')
        if ($item.Key -in @('Setup.cmd','Uninstall.cmd')) { $lines += 'pause' }
        else { $lines += 'if not "%modExit%"=="0" pause' }
        $lines += 'exit /b %modExit%'
        [IO.File]::WriteAllText((Join-Path $release $item.Key), ($lines -join "`r`n") + "`r`n", [Text.Encoding]::ASCII)
    }
}

$allowedLaunchers = if ($NexusMod) {
    @()
} else {
    @('Setup.cmd','Start-Mod.cmd','Stop-Mod.cmd','Uninstall.cmd')
}
$blocked = Get-ChildItem -LiteralPath $release -Recurse -File | Where-Object {
    $_.Extension -in '.cmd','.bat','.exe','.ps1','.vbs','.lnk','.msi' -and
    -not ($_.Directory.FullName -eq $release -and $_.Name -in $allowedLaunchers)
}
if ($blocked) { throw 'Distribution must not contain executable launchers or scripts.' }
Write-Output $release
