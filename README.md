# Onimusha DualSense

An unofficial DualSense haptics mod for the PC version of Onimusha: Way of the Sword.
It adds sound- and action-based haptic feedback together with adaptive-trigger effects.

This mod does not reproduce the PS5’s haptic feedback or the game’s official haptic effects.
It generates haptic files from the game’s sound effects and plays them as a mod.

## Requirements

- Windows x64, the supported PC version of the game, and one DualSense or DualSense Edge connected by USB or Bluetooth. Bluetooth HID haptics are supported for compatible Windows Bluetooth collections; USB remains the recommended setup. Edge support is provisional and has not been tested on physical hardware.
- [.NET 10 Runtime for Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0). The .NET SDK is not required for installation.
- A game-compatible version of [REFramework](https://github.com/praydog/REFramework).
- Steam Input can remain enabled. The DualSense audio device must be enabled in Windows.

## Install

Installation
1. Install a compatible version of REFramework in the game folder.
2. Download mod and unpack
3. Run `Setup.cmd`.
4. If Setup cannot find the game automatically, enter the path to the game folder containing `OnimushaWotS.exe` when prompted. Enter the folder path, not the `.exe` path.
5. Wait while Setup downloads the required external tools and generates haptic data from your local game files.
6. Connect the DualSense controller by USB.
7. Run `Start-Mod.cmd`.

## Usage
1. Connect the DualSense controller by USB.
2. Run `Start-Mod.cmd`.
3. The MOD runs while the Start-Mod console window is open. To stop the MOD, close this window.
4. Detailed diagnostics are saved to `data/bridge.log` instead of scrolling through this window.
5. By default, the mod starts Onimusha: Way of the Sword from Steam and remains active while the game is running.
6. To start the game yourself, run `Start-Mod.cmd --no-game` and launch Onimusha: Way of the Sword manually from Steam.

## Configuration
Edit `config.json` in the mod folder:
- `gain` — Controls the overall haptic strength. The accepted range is `0.0` to `1.0`, and the default value is `1.0`.
- `adaptive_triggers` —  it defaults to true, while false disables trigger resistance.
- `adaptive_trigger_strength`  — The accepted range is `0.0` to `1.0`, and the default value is `1.0`.
- `auto_launch_game` — Whether to launch the game automatically when the mod starts. It defaults to `true`; set it to `false` to start the game manually from Steam after running `Start-Mod.cmd`.

## Uninstall
1. Close the game.
2. Run `Uninstall.cmd`.
3. Delete the extracted mod folder if you no longer need it.

## Troubleshooting
If the mod does not respond, check the following:
- The DualSense is connected by USB or Bluetooth, and only one supported controller is visible.
- For USB, the DualSense audio device is enabled in Windows.
- Bluetooth mode is supported through direct HID haptics and does not require a four-channel audio endpoint, but it requires a compatible Bluetooth HID collection (the current DualSense path reports at least 142 output bytes).
- REFramework is installed and compatible with the current game version.
- Steam Input and the game's controller settings are working correctly.

Additional diagnostic information is written to:
`data/bridge.log`
Send me the log if you encounter some issues.

## Build and test

See [BUILD.md](BUILD.md) for the runtime build and the GitHub/NexusMods package formats. Release builds target Windows x64 and .NET 10.

## Legal

This mod is unofficial and is not affiliated with Capcom, Sony, or the game developers. The source code is available under the [MIT License](LICENSE). Third-party components and setup dependencies are documented in [THIRD_PARTY_NOTICES.txt](distribution/THIRD_PARTY_NOTICES.txt).

## Nexus Mod
https://www.nexusmods.com/onimushawayofthesword/mods/137  
I have not shared the GitHub URL anywhere other than Nexus Mods. If you see it posted on any other site, please be aware that it was not shared by me.
