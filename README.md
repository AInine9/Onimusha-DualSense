# Onimusha DualSense 1.1

An unofficial mod for the PC version of *Onimusha: Way of the Sword*. It adds sound- and action-based haptic feedback and adaptive-trigger effects for a USB-connected DualSense controller.

## Requirements

- Windows x64, the supported PC version of the game, and one DualSense or DualSense Edge connected by USB. Edge support is provisional and has not been tested on physical hardware.
- [.NET 10 Runtime for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0). The .NET SDK is not required.
- A game-compatible version of [REFramework](https://github.com/praydog/REFramework).
- Steam Input can remain enabled. The DualSense audio device must be enabled in Windows.

Bow charging preserves the game's original vibration and prevents MOD footsteps from masking it. Attack and defense feedback resumes when combat interrupts the charge. Adaptive-trigger resistance is enabled by default.

## Installation and use

Download the GitHub release ZIP, extract it to a writable folder, close the game, and run `Setup.cmd`; its four public launchers are included. The NexusMods ZIP is the launcher-free variant and uses the copy-and-paste CMD snippets on the mod download page. Setup downloads checksum-pinned tools and generates haptic data from the user's local game files. Then connect the DualSense by USB and run `Start-Mod.cmd`.

See the [distribution README](distribution/README.md) for complete usage and troubleshooting instructions.

## Building and testing

See [BUILD.md](BUILD.md) for a runtime build and the GitHub/NexusMods package formats. Release builds target Windows x64 and .NET 10; local inspector and test harnesses are not part of the public repository or runtime package.

## Legal

This mod is unofficial and is not affiliated with Capcom, Sony, or the game developers. The source code is available under the [MIT License](LICENSE). Third-party components and setup dependencies are documented in [THIRD_PARTY_NOTICES.txt](distribution/THIRD_PARTY_NOTICES.txt).
