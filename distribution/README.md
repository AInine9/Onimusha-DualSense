# Onimusha DualSense 1.0

An unofficial mod for the PC version of *Onimusha: Way of the Sword*. It adds sound- and action-based haptic feedback and adaptive-trigger effects for a USB-connected DualSense controller.

## Requirements

- Windows x64, the supported PC version of the game, and one DualSense connected by USB.
- [.NET 10 Runtime for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0). The .NET SDK is not required.
- A game-compatible version of [REFramework](https://github.com/praydog/REFramework).
- Steam Input disabled for this game, with the DualSense audio device enabled in Windows.

## Installation and use

1. Extract the ZIP to a writable folder.
2. Close the game and run `Setup.cmd`. On the first run, Setup downloads the required external tools and generates haptic data from your local game files.
3. Connect the DualSense by USB and run `Start-Mod.cmd` to start the mod and the game. The mod stops after the game exits.

Change the overall haptic strength in `config.json` with `gain` (0–1; default `1.0`). Restart the mod after changing it. Setup writes the `game` path automatically.

`Stop-Mod.cmd` stops the mod without stopping the game. To remove the mod, close the game and run `Uninstall.cmd`; if an earlier version of the mod script was installed, it is restored.

## Files and troubleshooting

`bin` contains the runtime files. After Setup, `data` contains generated haptic sources, logs, and backups. Do not redistribute a used folder containing generated game-derived audio; use the original distribution ZIP instead. Generated haptic WAV files may require about 1 GB and are reused between launches.

If the mod does not respond, check the USB connection, Steam Input, the DualSense audio device, and REFramework. More details are recorded in `data/bridge.log`. If the game is updated and its audio data changes, Setup stops and a version-compatible release is required.

This mod is unofficial and is not affiliated with Capcom, Sony, or the game developers. See `LICENSE` for the mod license and `THIRD_PARTY_NOTICES.txt` for third-party notices.
