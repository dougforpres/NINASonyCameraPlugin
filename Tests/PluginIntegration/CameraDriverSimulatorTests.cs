using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NINA.Core.Enum;
using NINA.Equipment.Model;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.RetroKiwi.Plugin.SonyCamera;
using NINA.RetroKiwi.Plugin.SonyCamera.Drivers;
using Xunit;

public class CameraDriverSimulatorTests {
    private sealed class Fixture : IDisposable {
        internal readonly ManualTimeProvider Clock = new ManualTimeProvider();
        internal readonly SimulatedSonyCameraBackend Backend;
        internal readonly Mock<IProfileService> Profile = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
        internal readonly Mock<IExposureDataFactory> ExposureFactory = new Mock<IExposureDataFactory>();
        internal readonly PluginOptionsAccessor Options;
        internal readonly CameraDriver Driver;
        internal PluginSettings ActiveSettings = new PluginSettings();
        internal ushort[] DownloadedPixels;
        internal int Warnings;

        internal Fixture() {
            Backend = new SimulatedSonyCameraBackend(Clock);
            Profile.SetupGet(x => x.ActiveProfile.PluginSettings).Returns(() => ActiveSettings);
            Options = new PluginOptionsAccessor(Profile.Object, SonyCamera.PluginGuid);
            ExposureFactory.Setup(x => x.CreateImageArrayExposureData(It.IsAny<ushort[]>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<ImageMetaData>()))
                .Returns((ushort[] pixels, int width, int height, int bitDepth, bool isBayered, ImageMetaData metadata) => {
                    DownloadedPixels = pixels;
                    return new ImageArrayExposureData(pixels, width, height, bitDepth, isBayered, metadata,
                        new Mock<IImageDataFactory>().Object);
                });
            Driver = new CameraDriver(Profile.Object, ExposureFactory.Object, Backend.Cameras().Single(), Options,
                Backend, message => Warnings++);
        }

        internal void Start(double duration = 1) => Driver.StartExposure(new CaptureSequence { ExposureTime = duration });
        internal void Finish(double seconds = 2) => Clock.Advance(TimeSpan.FromSeconds(seconds));
        public void Dispose() => Driver.Disconnect();
    }

    [Fact]
    public async Task ActualDriverConnectsAndExposesCameraPropertiesAndIso() {
        using var test = new Fixture();
        Assert.False(test.Driver.Connected);
        Assert.True(await test.Driver.Connect(CancellationToken.None));
        Assert.Equal("Sony Camera Simulator", test.Driver.DisplayName);
        Assert.Equal(SimulatedSonyCameraBackend.DeviceId, test.Driver.Id);
        Assert.Equal(320, test.Driver.CameraXSize);
        Assert.Equal(240, test.Driver.CameraYSize);
        Assert.Equal(14, test.Driver.BitDepth);
        Assert.Equal(100, test.Driver.BatteryLevel);
        Assert.Equal(new int[] { 100, 200, 400, 800, 1600, 3200 }, test.Driver.Gains);
        test.Driver.Gain = 1600;
        Assert.Equal(1600, test.Driver.Gain);
        test.Driver.Disconnect();
        Assert.False(test.Driver.Connected);
    }

    [Fact]
    public async Task RepeatedAndConcurrentConnectsPreserveTheActiveCamera() {
        using var test = new Fixture();
        var connections = await Task.WhenAll(test.Driver.Connect(CancellationToken.None),
            test.Driver.Connect(CancellationToken.None));
        Assert.All(connections, connected => Assert.True(connected));
        Assert.True(await test.Driver.Connect(CancellationToken.None));
        Assert.True(test.Driver.Connected);
        test.Start();
        test.Finish();
        await test.Driver.WaitUntilExposureIsReady(CancellationToken.None);
        await test.Driver.DownloadExposure(CancellationToken.None);
        Assert.Equal(1, test.DownloadedPixels[0]);
        test.Driver.Disconnect();
        Assert.True(await test.Driver.Connect(CancellationToken.None));
    }

    [Fact]
    public async Task CancelledConnectDoesNotOpenOrLoseAnExistingCamera() {
        using var test = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Driver.Connect(cancellation.Token));
        Assert.False(test.Driver.Connected);
        Assert.True(await test.Driver.Connect(CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test.Driver.Connect(cancellation.Token));
        Assert.True(test.Driver.Connected);
        Assert.Equal(400, test.Driver.Gain);
    }

    [Fact]
    public async Task ActualDriverWaitsDownloadsAndCreatesNinaExposureData() {
        using var test = new Fixture();
        Assert.True(await test.Driver.Connect(CancellationToken.None));
        test.Start();
        test.Finish();
        await test.Driver.WaitUntilExposureIsReady(CancellationToken.None);
        var image = Assert.IsType<ImageArrayExposureData>(await test.Driver.DownloadExposure(CancellationToken.None));
        Assert.Equal(320, image.Width);
        Assert.Equal(240, image.Height);
        Assert.Equal(14, image.BitDepth);
        Assert.True(image.IsBayered);
        Assert.Equal(320 * 240, test.DownloadedPixels.Length);
        Assert.Equal(1, test.DownloadedPixels[0]);
        Assert.Equal(0, test.Backend.NativeCancelCalls);
    }

    [Fact]
    public async Task SoftAbortWarnsOnceAndBusyRestartDoesNotLoseCancellation() {
        using var test = new Fixture();
        Assert.True(await test.Driver.Connect(CancellationToken.None));
        test.Start(120);
        test.Driver.AbortExposure();
        test.Driver.StopExposure();
        var error = Assert.ThrowsAny<Exception>(() => test.Start());
        Assert.Contains("busy", error.Message);
        Assert.False(error is OperationCanceledException);
        await Assert.ThrowsAsync<TaskCanceledException>(() => test.Driver.WaitUntilExposureIsReady(CancellationToken.None));
        Assert.Equal(1, test.Warnings);
        Assert.Equal(0, test.Backend.NativeCancelCalls);
        test.Finish(121);
        test.Start();
        test.Finish();
        await test.Driver.WaitUntilExposureIsReady(CancellationToken.None);
        await test.Driver.DownloadExposure(CancellationToken.None);
        Assert.Equal(2, test.DownloadedPixels[0]);
    }

    [Fact]
    public async Task TokenCancellationRunsActualAbortCallbackAndKeepsCancellationException() {
        using var test = new Fixture();
        Assert.True(await test.Driver.Connect(CancellationToken.None));
        test.Start(120);
        using var cancellation = new CancellationTokenSource();
        var wait = test.Driver.WaitUntilExposureIsReady(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, test.Warnings);
        Assert.Equal(0, test.Backend.NativeCancelCalls);
    }

    [Fact]
    public async Task NativeCancelOptionIsPersistedAndReadFromCurrentProfileAtAbortTime() {
        using var test = new Fixture();
        Assert.True(await test.Driver.Connect(CancellationToken.None));
        test.Start();
        test.Options.SetValueString(nameof(SonyCamera.EnableNativeCancel), bool.TrueString);
        test.Driver.AbortExposure();
        test.Driver.AbortExposure();
        Assert.Equal(1, test.Backend.NativeCancelCalls);
        await Assert.ThrowsAsync<TaskCanceledException>(() => test.Driver.WaitUntilExposureIsReady(CancellationToken.None));

        var firstProfile = test.ActiveSettings;
        test.ActiveSettings = new PluginSettings();
        test.Start();
        test.Driver.AbortExposure();
        Assert.Equal(1, test.Backend.NativeCancelCalls);
        Assert.Equal(1, test.Warnings);
        test.Finish();
        test.ActiveSettings = firstProfile;
        Assert.Equal(bool.TrueString, test.Options.GetValueString(nameof(SonyCamera.EnableNativeCancel), bool.FalseString));
        test.Start();
        test.Driver.AbortExposure();
        Assert.Equal(2, test.Backend.NativeCancelCalls);
    }

    [Fact]
    public async Task IgnoredNativeCancelStillBlocksRestartUntilCameraFinishes() {
        using var test = new Fixture();
        test.Backend.IgnoreNativeCancel = true;
        test.Options.SetValueString(nameof(SonyCamera.EnableNativeCancel), bool.TrueString);
        Assert.True(await test.Driver.Connect(CancellationToken.None));
        test.Start(120);
        test.Driver.AbortExposure();
        Assert.Contains("busy", Assert.ThrowsAny<Exception>(() => test.Start()).Message);
        await Assert.ThrowsAsync<TaskCanceledException>(() => test.Driver.WaitUntilExposureIsReady(CancellationToken.None));
        Assert.Equal(1, test.Backend.NativeCancelCalls);
        test.Finish(121);
        test.Start();
    }

    [Fact]
    public async Task CaptureAndStatusFailuresAreErrorsRatherThanSuccessfulImages() {
        using var test = new Fixture();
        Assert.True(await test.Driver.Connect(CancellationToken.None));
        test.Backend.FailCapture = true;
        test.Start();
        test.Finish();
        var failed = await Assert.ThrowsAnyAsync<Exception>(() => test.Driver.WaitUntilExposureIsReady(CancellationToken.None));
        Assert.False(failed is OperationCanceledException);
        Assert.Throws<InvalidOperationException>(() => test.Backend.GetLastImage());
        test.Backend.FailCapture = false;
        test.Start();
        test.Backend.StatusReadFailures = 1;
        var unreadable = await Assert.ThrowsAnyAsync<Exception>(() => test.Driver.WaitUntilExposureIsReady(CancellationToken.None));
        Assert.False(unreadable is OperationCanceledException);
        test.Finish();
        await test.Driver.WaitUntilExposureIsReady(CancellationToken.None);
        await test.Driver.DownloadExposure(CancellationToken.None);
        Assert.Equal(2, test.DownloadedPixels[0]);
    }

    [Fact]
    public async Task ReconnectDiscardsPreviousImageAndSubsamplingUsesReportedSensorSize() {
        using var test = new Fixture();
        Assert.True(await test.Driver.Connect(CancellationToken.None));
        test.Start();
        test.Finish();
        await test.Driver.WaitUntilExposureIsReady(CancellationToken.None);
        test.Driver.Disconnect();
        Assert.True(await test.Driver.Connect(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => test.Driver.DownloadExposure(CancellationToken.None));
        test.Driver.UpdateSubSampleArea();
        Assert.Equal(320, test.Driver.SubSampleWidth);
        Assert.Equal(240, test.Driver.SubSampleHeight);
    }

    [Fact]
    public async Task RepeatedActualPluginExposuresDeliverDistinctNinaImages() {
        using var test = new Fixture();
        Assert.True(await test.Driver.Connect(CancellationToken.None));
        for (int frame = 1; frame <= 20; frame++) {
            test.Start();
            test.Finish();
            await test.Driver.WaitUntilExposureIsReady(CancellationToken.None);
            Assert.IsType<ImageArrayExposureData>(await test.Driver.DownloadExposure(CancellationToken.None));
            Assert.Equal(frame, test.DownloadedPixels[0]);
        }
        Assert.Equal(20, test.Backend.CaptureStarts);
    }

    [Fact]
    public async Task RawBackendImageStillUsesExistingArwFactoryPath() {
        using var test = new Fixture();
        byte[] bytes = { 1, 2, 3, 4 };
        var backend = new Mock<ISonyCameraBackend>();
        backend.Setup(x => x.OpenCamera(It.IsAny<string>())).Returns(test.Backend.OpenCamera(SimulatedSonyCameraBackend.DeviceId));
        backend.Setup(x => x.GetLastImage()).Returns(new SonyExposureFrame { RawBytes = bytes });
        var expected = new RAWExposureData(new Mock<IRawConverter>().Object, bytes, "arw", 14,
            new ImageMetaData(), new Mock<IImageDataFactory>().Object);
        test.ExposureFactory.Setup(x => x.CreateRAWExposureData(It.IsAny<RawConverterEnum>(),
            It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<ImageMetaData>())).Returns(expected);
        var driver = new CameraDriver(test.Profile.Object, test.ExposureFactory.Object,
            test.Backend.Cameras().Single(), test.Options, backend.Object);
        try {
            Assert.True(await driver.Connect(CancellationToken.None));
            Assert.Same(expected, await driver.DownloadExposure(CancellationToken.None));
            var invocation = Assert.Single(test.ExposureFactory.Invocations);
            Assert.Equal("CreateRAWExposureData", invocation.Method.Name);
            var parameters = invocation.Method.GetParameters();
            Assert.Same(bytes, invocation.Arguments[Array.FindIndex(parameters, p => p.Name == "rawBytes")]);
            Assert.Equal("arw", invocation.Arguments[Array.FindIndex(parameters, p => p.Name == "rawType")]);
            Assert.Equal(14, invocation.Arguments[Array.FindIndex(parameters, p => p.Name == "bitDepth")]);
        } finally {
            driver.Disconnect();
        }
    }
}
