# Building

Use Windows x64 and the .NET 10 SDK. Run these commands from the source root:

```powershell
dotnet publish OnimushaDualSense -c Release -o publish --configfile NuGet.Config
./tools/package-release.ps1 -PublishDirectory publish -OutputDirectory github-release
./tools/package-release.ps1 -PublishDirectory publish -OutputDirectory nexus-release -NexusMod
```

Package `github-release` for GitHub; it includes the four public CMD launchers. Package `nexus-release` for NexusMods; `-NexusMod` omits all CMD launchers. Do not include generated game audio, logs, backups, or development tools in any package. `UseAppHost=false` produces a .NET DLL; PortAudio is the bundled native DLL. Choose a new output directory each time you run the packaging script.

The public repository intentionally contains only the runtime source and release packaging. The local inspector, test harnesses, recordings, and diagnostic traces are kept outside version control and are not required to build the release package.

Normal startup loads prepared WAV files with a 64 MiB sample cache. The mixer applies `gain` once at output. To force regeneration, run `dotnet bin/OnimushaDualSense.dll prepare-waves --force` from the extracted mod folder.

`run --seconds 3` checks device connection and cleanup. Live game notifications can produce feedback, so close the game before testing idle silence. Automated checks do not replace in-game testing of timing and feel.

## Runtime checks

GitHub distribution ZIPs include `Setup.cmd`, `Start-Mod.cmd`, `Stop-Mod.cmd`, and `Uninstall.cmd`. NexusMods distribution ZIPs are created with `-NexusMod` and contain none of those launchers; users create those files using the copy-and-paste instructions on the mod page. Generated game audio, logs, and backups remain excluded from every package.

See `distribution/LICENSE` and `distribution/THIRD_PARTY_NOTICES.txt` for license information.
