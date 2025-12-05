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

## Support

- Best contact is email via the Homepage link or the ASCOM driver troubleshooting page footer.
- You may be asked for a driver log when reporting bugs: https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Troubleshooting#the-driver-dll-log
- Help links:
  - My camera is not supported (https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Installation#if-you-dont-have-a-supported-camera-model);
  - Supported cameras (https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Supported-Cameras);
  - Other known issues (https://github.com/dougforpres/ASCOMSonyCameraDriver/wiki/Troubleshooting)

## Metadata

- Version: 1.0.0.4
- Author: Doug Henderson
- Contributors: Lucas Lepski ([@ShurkanTwo](https://github.com/shurkanTwo))
- Minimum NINA version: 3.2.0.3001
- License: MPL-2.0 (https://www.mozilla.org/en-US/MPL/2.0/)
- Homepage: https://retro.kiwi
- Changelog: https://github.com/dougforpres/NINASonyCameraPlugin/blob/master/CHANGELOG.md
