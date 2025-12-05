# Sony Camera Plugin

## 1.0.0.4
* Added the `UpdateSubSampleArea` implementation and bumped the minimum NINA version so the plugin loads in 3.2.
* Restored the ISO/Gain UI by notifying NINA whenever the camera connection updates ISO data and by handling cameras without ISO option lists.
* Probed the registry ISO property (`0xFFFE`) so older Sony bodies still populate the gain dropdown.
* Logged clearer errors when gain min/max cannot be determined and improved the GitHub workflow to publish the release DLL artifact.

## 1.0.0.3
Updated to support new device property "DisplayName" required in NINA 3

## 1.0.0.2
Rebuilt using .NET Core 8 for NINA 3

## 1.0.0.1
Initial release - Supports all functionality provided by the ASCOM version, plus:
* LiveView support
* Actual ISO values displayed in Gain drop-down vs a numeric index (1, 2, 3, etc)

Note that NINA saves the raw ARW files and does not generate FITS files for DSLRs (per the note in NINA: Options.Imaging).
