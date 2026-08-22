# Sony Camera Plugin

## 1.0.0.5

Cancel handling (#4):

- Added native cancel option in plugin settings and defaulted to off to avoid crashes.
- Serialized capture cancel/start/status checks and treated busy starts as soft cancels.
- Prevented new exposures from starting when the camera is busy; improved abort behavior without native cancel.

No call into the driver can hang NINA any more:

- Every call is now bounded by a 30s watchdog (60s for opening a camera). Nothing the plugin asks the driver to do is a long operation, so anything past that is the driver's worker thread stuck on a command the camera will never answer. Previously that blocked NINA's imaging thread with no timeout anywhere - one observed session had `StartExposure` never return and sit there for three hours before the process died.
- When the watchdog fires the camera is reported disconnected and no further calls are made, so the sequence fails visibly and keeps going instead of going silent. The stuck thread cannot be recovered; NINA has to be restarted before the camera can be used again, and the log and the error message both say so.
- Disconnecting no longer waits behind a stuck call: it gives up after 5s and cleans up locally rather than freezing the UI.

Image download:

- `GetCaptureStatus` is a one-shot for a completed capture: the first call that reports `COMPLETE` fetches the frame from the camera and then resets the driver's own status to `CANCELLED`, so a second call returns 3 and the frame is gone. The copy therefore has to happen on that call and cannot be deferred. This is now documented in the code with the driver-log evidence, so it does not get "optimised" away again.
- Taking the frame clears it, so a cancelled or failed exposure can no longer be handed the previously downloaded image. Anything left over from an earlier capture is dropped when a new exposure starts.
- Setting the exposure time is checked, but `ERROR_NOT_SUPPORTED` is treated as a warning rather than a failure: the driver returns it for bodies with no learnt exposure-time list, where the shutter speed set on the camera has always been used as-is.

Driver access is now serialized end to end:

- Every call into `SonyMTPCamera.dll` goes through one lock in `SonyDriver`. The camera and the focuser share a device handle and NINA polls both on their own timers, so gain reads, battery reads, focus reads, capture control and image download were all reaching the driver concurrently.
- Closing the camera invalidates the handle under that same lock, and calls made against a stale handle are refused instead of reaching the driver. Closing a device while another thread was mid-command was the case that freed memory out from under an in-flight read.
- When the camera stops answering (unplugged, powered off, USB dropped) the driver handle is closed once and the camera is reported as disconnected, rather than being polled indefinitely.
- Property reads that NINA polls on a timer (battery, gain, focus position, focus limit) no longer queue behind an image download; they time out and report the last known value.

Other fixes:

- The minimum-driver-version check compared the build and revision fields the wrong way round, so a 1.0.0.x driver passed the 1.0.1.17 minimum and then failed later with `Unable to locate method 'GetSupportedDeviceCount'`. (#2)
- `StartCapture` ignored the result of setting the exposure time, so a rejected shutter speed silently produced a frame at the previous exposure - this is what stops the flat wizard's exposure search from converging. (#5)
- A cancelled or failed exposure could hand NINA the previously downloaded frame, because the driver only refreshes its image buffer on completion.
- Failures while attaching a lens or moving focus no longer leave the focuser stuck reporting "moving", and the focuser now raises `PropertyChanged`.
- Removed the unresolvable `ReachFramework` reference, which raised MSB3245/MSB3243 on every build. The build now produces no compiler or MSBuild warnings; the remaining NU1701 warnings come from legacy packages that only ship .NET Framework targets.

## 1.0.0.4

- Added the `UpdateSubSampleArea` implementation and bumped the minimum NINA version so the plugin loads in 3.2.
- Restored the ISO/Gain UI by notifying NINA whenever the camera connection updates ISO data and by handling cameras without ISO option lists.
- Probed the registry ISO property (`0xFFFE`) so older Sony bodies still populate the gain dropdown.
- Logged clearer errors when gain min/max cannot be determined and improved the GitHub workflow to publish the release DLL artifact.

## 1.0.0.3

Updated to support new device property "DisplayName" required in NINA 3

## 1.0.0.2

Rebuilt using .NET Core 8 for NINA 3

## 1.0.0.1

Initial release - Supports all functionality provided by the ASCOM version, plus:

- LiveView support
- Actual ISO values displayed in Gain drop-down vs a numeric index (1, 2, 3, etc)

Note that NINA saves the raw ARW files and does not generate FITS files for DSLRs (per the note in NINA: Options.Imaging).
