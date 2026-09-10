# Onimusha DualSense 1.1.1

An unofficial mod for the PC version of *Onimusha: Way of the Sword*. It adds sound- and action-based haptic feedback and adaptive-trigger effects for a USB-connected DualSense controller.

## Requirements

- Windows x64, the supported PC version of the game, and one DualSense or DualSense Edge connected by USB. Edge support is provisional and has not been tested on physical hardware.
- [.NET 10 Runtime for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0). The .NET SDK is not required for installation.
- A game-compatible version of [REFramework](https://github.com/praydog/REFramework).
- Steam Input can remain enabled. The DualSense audio device must be enabled in Windows.

Bow charging preserves the game's original vibration and prevents MOD footsteps from masking it. Attack and defense feedback resumes when combat interrupts the charge. Adaptive-trigger resistance is enabled by default.

## Install

Download the GitHub release ZIP, extract it to a writable folder, close the game, and run `Setup.cmd`. The GitHub package includes the four public launchers. The NexusMods package is launcher-free and uses the copy-and-paste CMD snippets on the mod download page. Setup downloads checksum-pinned tools and generates haptic data from the user's local game files. Connect the DualSense by USB, then run `Start-Mod.cmd`. Set `auto_launch_game` to `false` in `config.json` when the game should be started separately from Steam.

See the [distribution README](distribution/README.md) for complete installation, configuration, and troubleshooting instructions.

## Build and test

See [BUILD.md](BUILD.md) for the runtime build and the GitHub/NexusMods package formats. Release builds target Windows x64 and .NET 10.

For installation, configuration, and troubleshooting, see the [distribution README](distribution/README.md).

## Legal

This mod is unofficial and is not affiliated with Capcom, Sony, or the game developers. The source code is available under the [MIT License](LICENSE). Third-party components and setup dependencies are documented in [THIRD_PARTY_NOTICES.txt](distribution/THIRD_PARTY_NOTICES.txt).
