# Sony Camera Plugin

## 1.0.0.4
* Implemented the new `UpdateSubSampleArea` contract required by NINA 3.2 and reset the imaging area to the full sensor so the plugin can load again.
* Updated the minimum NINA version metadata to 3.2.0.3001.

## 1.0.0.3
Updated to support new device property "DisplayName" required in NINA 3

## 1.0.0.2
Rebuilt using .NET Core 8 for NINA 3

## 1.0.0.1
Initial release - Supports all functionality provided by the ASCOM version, plus:
* LiveView support
* Actual ISO values displayed in Gain drop-down vs a numeric index (1, 2, 3, etc)

Note that NINA saves the raw ARW files and does not generate FITS files for DSLRs (per the note in NINA: Options.Imaging).
