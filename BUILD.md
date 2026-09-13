# Build and package

Use Windows x64 and the .NET 10 SDK. Run these commands from the source root.

## Build from source

```powershell
dotnet restore OnimushaDualSense --configfile NuGet.Config
dotnet publish OnimushaDualSense -c Release -o work/build/publish --configfile NuGet.Config
```

The commands above use the .NET SDK installed on your machine and create a Windows x64 runtime in `work/build/publish`.

## Package a build

```powershell
./tools/package-release.ps1 -PublishDirectory work/build/publish -OutputDirectory work/github-release
./tools/package-release.ps1 -PublishDirectory work/build/publish -OutputDirectory work/nexus-release -NexusMod
```

The published runtime requires the [.NET 10 Runtime for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0); end users do not need the SDK.

## Package formats

Package `work/github-release` for GitHub; it includes the three public CMD launchers. Package `work/nexus-release` for NexusMods; `-NexusMod` omits all CMD launchers. Do not include generated game audio, logs, backups, or development tools in any package. `UseAppHost=false` produces a .NET DLL; PortAudio is the bundled native DLL. Keep build and package outputs under `work/` and choose a new output directory each time you run the packaging script.

## Runtime checks

Normal startup loads prepared WAV files with a 64 MiB sample cache. The mixer applies `gain` once at output. To force regeneration, run `dotnet bin/OnimushaDualSense.dll prepare-waves --force` from the extracted mod folder.

`run --seconds 3` checks device connection and cleanup. Live game notifications can produce feedback, so close the game before testing idle silence. Automated checks do not replace in-game testing of timing and feel.

GitHub distribution ZIPs include `Setup.cmd`, `Start-Mod.cmd`, and `Uninstall.cmd`. `Start-Mod.cmd` remains open while the MOD runs; close that window to stop the MOD. NexusMods distribution ZIPs are created with `-NexusMod` and contain none of those launchers; users create those files using the copy-and-paste instructions on the mod page. Generated game audio, logs, and backups remain excluded from every package.

See `distribution/LICENSE` and `distribution/THIRD_PARTY_NOTICES.txt` for license information.
