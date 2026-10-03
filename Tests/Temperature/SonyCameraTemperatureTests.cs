using System;
using System.Buffers.Binary;
using System.IO;
using NINA.RetroKiwi.Plugin.SonyCamera.Drivers;
using Xunit;

public class SonyCameraTemperatureTests {
    [Fact]
    public void RealA7IIIMetadataMatchesExifToolCameraReading() {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "a7iii-metadata.bin"));
        Assert.Equal(28, SonyCameraTemperature.Read(bytes));
        // The same photo reports battery 33.9 C and ambient 22 C. Neither is
        // the camera temperature returned here.
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void HandlesTiffByteOrdersAndBothSonyMakerNoteLayouts(bool littleEndian, bool header) {
        Assert.Equal(28, SonyCameraTemperature.Read(CreateImage(28, littleEndian, header)));
    }

    [Theory]
    [InlineData(-20)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(80)]
    public void ReadsSignedCelsiusIncludingValidZero(int temperature) {
        Assert.Equal(temperature, SonyCameraTemperature.Read(CreateImage(temperature)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(130)]
    [InlineData(148)]
    [InlineData(255)]
    public void InvalidSonyValidityFlagLeavesReadingUnavailable(int flag) {
        byte[] image = CreateImage(28);
        image[102] = Encipher((byte)flag);
        Assert.True(double.IsNaN(SonyCameraTemperature.Read(image)));
    }

    [Fact]
    public void MissingTagAndOtherManufacturersLeaveReadingUnavailable() {
        byte[] image = CreateImage(28);
        image[40] = (byte)'N';
        Assert.True(double.IsNaN(SonyCameraTemperature.Read(image)));
        image = CreateImage(28);
        Write16(image, 82, 0x9406, true); // BatteryTemperature, not CameraTemperature.
        Assert.True(double.IsNaN(SonyCameraTemperature.Read(image)));
        Assert.True(double.IsNaN(SonyCameraTemperature.Read(null)));
    }

    [Fact]
    public void EveryTruncatedRequiredStructureLeavesReadingUnavailable() {
        byte[] image = CreateImage(28);
        for (int length = 0; length < 104; length++) {
            Assert.True(double.IsNaN(SonyCameraTemperature.Read(image.AsSpan(0, length).ToArray())));
        }
    }

    [Theory]
    [InlineData(4)] // Main IFD pointer.
    [InlineData(18)] // Make string pointer.
    [InlineData(30)] // Exif IFD pointer.
    [InlineData(58)] // Maker note pointer.
    [InlineData(86)] // Temperature tag length.
    [InlineData(90)] // Temperature tag pointer.
    public void OverflowingOffsetsAndLengthsAreRejectedWithoutThrowing(int field) {
        byte[] image = CreateImage(28);
        Write32(image, field, uint.MaxValue, true);
        Assert.True(double.IsNaN(SonyCameraTemperature.Read(image)));
    }

    [Fact]
    public void TemperaturePayloadMustBeInsideDeclaredMakerNote() {
        byte[] image = CreateImage(28);
        Write32(image, 90, 120, true);
        image[124] = Encipher(32);
        image[125] = Encipher(28);
        Assert.True(double.IsNaN(SonyCameraTemperature.Read(image)));
    }

    [Fact]
    public void WrongTiffMagicTagTypeAndHugeDirectoryCountsAreRejected() {
        byte[] image = CreateImage(28);
        Write16(image, 2, 43, true);
        Assert.True(double.IsNaN(SonyCameraTemperature.Read(image)));
        image = CreateImage(28);
        Write16(image, 84, 4, true);
        Assert.True(double.IsNaN(SonyCameraTemperature.Read(image)));
        image = CreateImage(28);
        Write16(image, 80, ushort.MaxValue, true);
        Assert.True(double.IsNaN(SonyCameraTemperature.Read(image)));
    }

    private static byte[] CreateImage(int temperature, bool littleEndian = true, bool header = false) {
        var data = new byte[128];
        data[0] = data[1] = (byte)(littleEndian ? 'I' : 'M');
        Write16(data, 2, 42, littleEndian);
        Write32(data, 4, 8, littleEndian);
        Write16(data, 8, 2, littleEndian);
        Entry(data, 10, 0x010F, 2, 5, 40, littleEndian);
        "SONY\0"u8.CopyTo(data.AsSpan(40));
        Entry(data, 22, 0x8769, 4, 1, 48, littleEndian);
        Write16(data, 48, 1, littleEndian);
        Entry(data, 50, 0x927C, 7, header ? 36u : 24u, 80, littleEndian);
        int makerIfd = header ? 92 : 80;
        if (header) "SONY DSC \0\0\0"u8.CopyTo(data.AsSpan(80));
        Write16(data, makerIfd, 1, littleEndian);
        Entry(data, makerIfd + 2, 0x9403, 7, 6, (uint)(makerIfd + 18), littleEndian);
        data[makerIfd + 22] = Encipher(32);
        data[makerIfd + 23] = Encipher(unchecked((byte)temperature));
        return data;
    }

    private static byte Encipher(byte value) => value >= 249 ? value : (byte)(value * value * value % 249);
    private static void Entry(byte[] data, int offset, ushort tag, ushort type, uint count, uint value, bool littleEndian) {
        Write16(data, offset, tag, littleEndian);
        Write16(data, offset + 2, type, littleEndian);
        Write32(data, offset + 4, count, littleEndian);
        Write32(data, offset + 8, value, littleEndian);
    }
    private static void Write16(byte[] data, int offset, ushort value, bool littleEndian) {
        if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
        else BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset, 2), value);
    }
    private static void Write32(byte[] data, int offset, uint value, bool littleEndian) {
        if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
        else BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
    }
}
