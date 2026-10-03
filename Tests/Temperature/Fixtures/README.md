# A7 III temperature fixture

`a7iii-metadata.bin` is the first 42,806 bytes of the CC0 Sony ILCE-7M3
compressed 14-bit sample from [raw.pixls.us](https://raw.pixls.us/getfile.php/2414/nice/sample.ARW).
It preserves the original TIFF header, Exif IFD, and complete Sony maker note;
image pixels and the embedded preview are omitted. It is not a decodable ARW.

Original ARW SHA-256:
`250784580ea527442c09004417bb0eead484f2bf3ee8f9121a776ac65bb50d0f`.

ExifTool reports `CameraTemperature = 28 C`, `BatteryTemperature = 33.9 C`,
and `AmbientTemperature = 22 C`. The test deliberately uses the camera reading.
Sony's Tag9403 validity flag (byte 4), signed Celsius value (byte 5), and byte
enciphering are documented in [ExifTool's Sony source](https://github.com/exiftool/exiftool/blob/master/lib/Image/ExifTool/Sony.pm).
No ExifTool executable or native decoder is needed by the plugin or tests.
