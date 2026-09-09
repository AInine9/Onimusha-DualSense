# Onimusha DualSense 1.0

An unofficial mod for the PC version of *Onimusha: Way of the Sword*. It adds sound- and action-based haptic feedback and adaptive-trigger effects for a USB-connected DualSense controller.

## Requirements

- Windows x64, the supported PC version of the game, and one DualSense connected by USB.
- [.NET 10 Runtime for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0). The .NET SDK is not required.
- A game-compatible version of [REFramework](https://github.com/praydog/REFramework).
- Steam Input can remain enabled. The DualSense audio device must be enabled in Windows.

## Installation and use

Download the release ZIP from GitHub, extract it to a writable folder, close the game, and run `Setup.cmd`. Setup downloads checksum-pinned tools and generates haptic data from the user's local game files. Then connect the DualSense by USB and run `Start-Mod.cmd`.

See the [distribution README](distribution/README.md) for complete usage and troubleshooting instructions.

## Building and testing

See [BUILD.md](BUILD.md). Release builds target Windows x64 and .NET 10. Development builds include the C# and Lua test harnesses; they are excluded from the public runtime package.

## Legal

This mod is unofficial and is not affiliated with Capcom, Sony, or the game developers. The source code is available under the [MIT License](LICENSE). Third-party components and setup dependencies are documented in [THIRD_PARTY_NOTICES.txt](distribution/THIRD_PARTY_NOTICES.txt).
