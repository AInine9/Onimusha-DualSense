param(
    [Parameter(Mandatory=$true)][string]$PublishDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$Developer
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
$launchers = [ordered]@{'Setup'='setup'; 'Start-Mod'='launch'; 'Stop-Mod'='stop'; 'Uninstall'='uninstall'}
if ($Developer) { $launchers['Inspect-Haptics']='inspect' }
foreach ($entry in $launchers.GetEnumerator()) {
    $lines = @('@echo off','setlocal','cd /d "%~dp0"','set "modDotnet=%ProgramFiles%\dotnet\dotnet.exe"',
        'if not exist "%modDotnet%" set "modDotnet=dotnet"',
        ('"%modDotnet%" "%~dp0bin\OnimushaDualSense.dll" ' + $entry.Value + ' %*'),
        'set "modExit=%ERRORLEVEL%"')
    if ($entry.Key -in 'Setup','Uninstall') { $lines += 'pause' } else { $lines += 'if not "%modExit%"=="0" pause' }
    $lines += 'exit /b %modExit%'
    [IO.File]::WriteAllText((Join-Path $release ($entry.Key + '.cmd')), ($lines -join "`r`n") + "`r`n", [Text.Encoding]::ASCII)
}
if ($Developer) {
    New-Item -ItemType Directory -Path (Join-Path $release 'data') | Out-Null
    foreach ($name in @('finisher-replay.json','defense-replay.json')) {
        Copy-Item -LiteralPath (Join-Path $project ('dev/' + $name)) -Destination (Join-Path $release 'data')
    }
}
Write-Output $release
