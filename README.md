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
- For exposures <= 30s the driver chooses the nearest built-in shutter speed; longer exposures fall back to Bulb. The camera only has discrete shutter speeds, so a request of 0.2365s is taken at 0.25s - routines that search for an exposure will settle on the nearest step rather than the exact value they asked for.
- Changing the exposure between back-to-back frames can take a few seconds: the shutter speed can only be stepped one click at a time and the camera ignores the first step or two while it is still finishing the previous frame. The plugin waits for the change to land and logs how long it took. Frames at an unchanged exposure are not affected.
- Avoid AUTO ISO (gain 0) for flats and lights. The camera picks its own sensitivity per frame, so the measured ADU is not a function of exposure time and exposure searches will report a non-linear response.
- Powering off/unplugging the camera while connected can still crash NINA via Windows' MTP stack (PortableDeviceApi.dll access violation); always disconnect in NINA first. The plugin now stops talking to a camera that has stopped responding and reports it as disconnected, which closes the window in which this happens, but an access violation raised inside the native driver cannot be caught from managed code - if it happens on the very first call after the camera vanishes, NINA still goes down.
- The “Enable native cancel” option calls the camera’s CancelCapture; stability varies by body and Windows driver stack. Leave it off if you see crashes when aborting exposures.
- If the camera stops answering the driver mid-session, the plugin gives up on the call after 30 seconds, reports the camera as disconnected and stops issuing commands. The exposure fails and the sequence carries on rather than hanging. The stuck driver thread cannot be recovered, so restart NINA before reconnecting the camera.
- Make sure the installed Sony driver really is 1.0.1.17 or newer. Up to 1.0.0.5 the plugin's version check compared the build and revision fields the wrong way round, so an old 1.0.0.x driver was accepted and then failed in confusing ways. The check is now correct and will refuse to load against a driver that is too old.

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
