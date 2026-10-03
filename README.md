# Sony Camera Plugin

Native NINA plugin for Sony mirrorless/DSLR bodies over the Sony MTP interface, so you can control the camera directly from NINA without ASCOM.

## Features

- Supports many Sony cameras (supported list: https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Supported-Cameras)
- Connects Sony bodies and surfaces their model name in NINA
- LiveView streaming (16-bit monochrome, where supported) plus normal captures saved as ARW through NINA's RAW converter so full metadata is preserved
- Gain dropdown populated with actual ISO values (including Auto when present); ISO is controllable via the Gain property
- Battery level reporting; exposes camera pixel size and resolution
- Camera-controlled lenses exposed as a focuser inside NINA (supported lenses: https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Supported-Lenses)
- Handles cameras that do not expose ISO options until they are "learned" in-camera

## Requirements

- NINA 3.2 or newer (built against `NINA.Plugin` 3.2.x)
- Windows 10 1809 or newer (.NET 8, WPF)
- Sony body set to PC Remote/MTP mode
- This plugin uses the core (non-ASCOM) files from the Sony ASCOM driver v1.0.1.17 or later; install that driver from https://github.com/dougforpres/ASCOMSonyCameraDriver/releases so the shared components are present.

## Installation

Install via the N.I.N.A. plugin manager or manually download and copy the DLL file into `%LOCALAPPDATA%\\NINA\\Plugins\\<Major>.<Minor>.<Hotfix>\\Sony Camera Plugin\\` from:
https://github.com/dougforpres/NINASonyCameraPlugin/releases

## Usage notes

- ISO/gain list may be empty until the camera has learned ISO options; set an ISO on-camera once if needed.
- Temperature is not reported (the plugin does not request processed ARW metadata).
- Binning and sub-sampling are not supported; captures use the full sensor frame.
- For exposures <= 30s the driver chooses the nearest built-in shutter speed; longer exposures fall back to Bulb.
- Powering off/unplugging the camera while connected can crash NINA via Windows' MTP stack (PortableDeviceApi.dll access violation); disconnect in NINA first to avoid it.
- “Enable native cancel” is off by default and saved per NINA profile. With it off, abort cancels the wait and discards the exposure, while the camera finishes capturing; another exposure can start once the camera finishes. Enabling it allows native cancellation during aborts, which may crash NINA on some camera models and Windows drivers. New exposure requests never cancel an existing capture.

## Simulator development

The plugin includes an opt-in **Sony Camera Simulator** that runs the actual Sony camera driver without SonyMTPCamera.dll or a physical camera. It generates distinct 320 × 240, 14-bit Bayer images and exposes ISO controls, exposure timing, readout, cancellation, and reconnect behavior.

Launch NINA from PowerShell with simulator mode enabled:

```powershell
$env:NINA_SONY_SIMULATOR = "1"
& "C:\Program Files\N.I.N.A. - Nighttime Imaging 'N' Astronomy\NINA.exe"
```

Adjust the executable path if your installation differs. Select **Sony Camera Simulator** in NINA's camera list. This mode replaces Sony camera discovery and skips Sony lens discovery, so the native DLL is not loaded. Close NINA and remove the environment variable to return to physical Sony equipment:

```powershell
Remove-Item Env:NINA_SONY_SIMULATOR -ErrorAction SilentlyContinue
Remove-Item Env:NINA_SONY_SIMULATOR_SCENARIO -ErrorAction SilentlyContinue
```

Set `$env:NINA_SONY_SIMULATOR_SCENARIO` before launching NINA to exercise a failure scenario:

| Value | Behavior |
| --- | --- |
| Unset | Normal exposure and readout. |
| `slow-readout` | Ten seconds of readout after each exposure. |
| `ignored-cancel` | Native cancel requests leave the capture running. |
| `status-failure` | The first capture status read throws an error; later reads recover. |
| `failed-capture` | Captures finish in the failed state without an image. |

Synthetic frames use NINA's image-array path. This simulator does not exercise ARW decoding, LiveView, Sony USB/MTP communication, or native access violations.

Run the portable capture and simulator tests with .NET 8:

```sh
dotnet test Tests/CaptureController.Tests.csproj --configuration Release
```

Run the actual plugin integration tests on Windows:

```powershell
dotnet test Tests/PluginIntegration/PluginIntegration.Tests.csproj --configuration Release
```

The integration tests use the production camera driver, NINA profile settings, and NINA exposure objects with the simulated backend. A manual clock advances long exposures immediately. Both suites run in Windows CI; the portable suite also runs on Linux. On Linux, cross-compile with `dotnet build NINASonyCameraPlugin.csproj --configuration Release -p:EnableWindowsTargeting=true`.

## Support

- Best contact is email via the Homepage link or the ASCOM driver troubleshooting page footer.
- You may be asked for a driver log when reporting bugs: https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Troubleshooting#the-driver-dll-log
- Help links:
  - My camera is not supported (https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Installation#if-you-dont-have-a-supported-camera-model);
  - Supported cameras (https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Supported-Cameras);
  - Other known issues (https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Troubleshooting)

## Metadata

- Version: 1.0.0.5
- Author: Doug Henderson
- Contributors: Lucas Lepski [@ShurkanTwo](https://github.com/shurkanTwo)
- Minimum NINA version: 3.2.0.3001
- License: MPL-2.0 (https://www.mozilla.org/en-US/MPL/2.0/)
- Homepage: https://retro.kiwi
- Changelog: https://github.com/dougforpres/NINASonyCameraPlugin/blob/master/CHANGELOG.md
