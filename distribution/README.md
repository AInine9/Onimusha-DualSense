# Onimusha DualSense 1.1

An unofficial mod for the PC version of *Onimusha: Way of the Sword*. It adds sound- and action-based haptic feedback and adaptive-trigger effects for a USB-connected DualSense controller.

## Requirements

- Windows x64, the supported PC version of the game, and one DualSense or DualSense Edge connected by USB. Edge support is provisional and has not been tested on physical hardware.
- [.NET 10 Runtime for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0). The .NET SDK is not required.
- A game-compatible version of [REFramework](https://github.com/praydog/REFramework).
- Steam Input can remain enabled. The DualSense audio device must be enabled in Windows.

Bow charging preserves the game's original vibration and prevents MOD footsteps from masking it. Attack and defense feedback resumes when combat interrupts the charge. Adaptive-trigger resistance is enabled by default.

## Installation and use

1. Extract the ZIP to a writable folder. GitHub distribution ZIPs include `Setup.cmd`, `Start-Mod.cmd`, `Stop-Mod.cmd`, and `Uninstall.cmd`. NexusMods ZIPs intentionally omit them; create those four files using the copy-and-paste instructions on the mod download page.
2. Close the game and run `Setup.cmd`. On the first run, Setup downloads the required external tools and generates haptic data from your local game files.
3. Connect the DualSense by USB and run `Start-Mod.cmd` to start the mod and the game. The mod stops after the game exits.

Change the overall haptic strength in `config.json` with `gain` (0–1; default `1.0`). Set `adaptive_triggers` to `false` to disable trigger resistance; it defaults to `true` when omitted. Restart the mod after changing either setting. Setup writes the `game` path automatically.

`Stop-Mod.cmd` stops the mod without stopping the game. To remove the mod, close the game and run `Uninstall.cmd`; if an earlier version of the mod script was installed, it is restored.

## Files and troubleshooting

`bin` contains the runtime files. After Setup, `data` contains generated haptic sources, logs, and backups. Do not redistribute a used folder containing generated game-derived audio; use the original distribution ZIP instead. Generated haptic WAV files may require about 1 GB and are reused between launches.

If the mod does not respond, check the USB connection, Steam Input, the DualSense audio device, and REFramework. More details are recorded in `data/bridge.log`. If the game is updated and its audio data changes, Setup stops and a version-compatible release is required.

This mod is unofficial and is not affiliated with Capcom, Sony, or the game developers. See `LICENSE` for the mod license and `THIRD_PARTY_NOTICES.txt` for third-party notices.
