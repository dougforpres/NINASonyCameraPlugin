using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.RetroKiwi.Plugin.SonyCamera.Drivers;
using Xunit;

public class SimulatedSonyCameraTests {
    [Fact]
    public void EnumerateConnectAndChangeIsoWithoutLoadingNativeDriver() {
        var camera = new SimulatedSonyCameraBackend();
        var device = Assert.Single(camera.Cameras());
        var info = camera.OpenCamera(device.Id);
        Assert.Equal("Sony Camera Simulator", device.Model);
        Assert.Equal(320, info.ImageSize.Width);
        Assert.Equal(240, info.ImageSize.Height);
        Assert.Equal(14, info.BitsPerPixel);
        Assert.False(info.SupportsPreview());
        Assert.Equal(new uint[] { 100, 200, 400, 800, 1600, 3200 }, info.GetPropertyInfo(0xFFFE).Options().Select(x => x.Value));
        camera.SetProperty(info.Handle, 0xD21E, 800);
        Assert.Equal(800u, camera.GetProperty(info.Handle, 0xD21E).Value);
        Assert.Equal(100u, camera.GetProperty(info.Handle, 53784).Value);
        Assert.Throws<InvalidOperationException>(() => camera.SetProperty(info.Handle, 0xD21E, 123));
    }

    [Fact]
    public void ExposureProgressesThroughNativeStatesAndCompleteConsumesCapture() {
        var clock = new ManualTimeProvider();
        var camera = new SimulatedSonyCameraBackend(clock);
        uint handle = camera.OpenCamera(SimulatedSonyCameraBackend.DeviceId).Handle;
        camera.StartCapture(handle, 1);
        Assert.Equal(0x8001u, camera.GetCaptureStatus(handle));
        clock.Advance(TimeSpan.FromMilliseconds(30));
        Assert.Equal(1u, camera.GetCaptureStatus(handle));
        Assert.Throws<InvalidOperationException>(() => camera.StartCapture(handle, 1));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0x8002u, camera.GetCaptureStatus(handle));
        clock.Advance(TimeSpan.FromMilliseconds(130));
        Assert.Equal(0x8003u, camera.GetCaptureStatus(handle));
        clock.Advance(TimeSpan.FromMilliseconds(20));
        Assert.Equal(4u, camera.GetCaptureStatus(handle));
        Assert.Equal(2u, camera.GetCaptureStatus(handle));
        var frame = camera.GetLastImage();
        Assert.Equal(320 * 240, frame.Pixels.Length);
        Assert.Equal(1, frame.Pixels[0]);
        Assert.All(frame.Pixels, pixel => Assert.InRange(pixel, (ushort)0, (ushort)16383));
    }

    [Fact]
    public async Task SoftAbortAllowsPhysicalCaptureToFinishAndNextExposureIsFresh() {
        var clock = new ManualTimeProvider();
        var camera = new SimulatedSonyCameraBackend(clock);
        uint handle = camera.OpenCamera(SimulatedSonyCameraBackend.DeviceId).Handle;
        var controller = new CaptureController(() => camera.GetCaptureStatus(handle),
            duration => camera.StartCapture(handle, duration), () => { camera.CancelCapture(handle); return true; }, () => false);
        controller.Start(120);
        controller.Abort();
        await Assert.ThrowsAsync<TaskCanceledException>(() => controller.WaitUntilReady(CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => controller.Start(1));
        clock.Advance(TimeSpan.FromSeconds(121));
        controller.Start(1);
        clock.Advance(TimeSpan.FromSeconds(2));
        await controller.WaitUntilReady(CancellationToken.None);
        Assert.Equal(2, camera.GetLastImage().Pixels[0]);
        Assert.Equal(0, camera.NativeCancelCalls);
    }

    [Fact]
    public void AcceptedNativeAbortEndsCaptureWithoutProducingImage() {
        var camera = new SimulatedSonyCameraBackend();
        uint handle = camera.OpenCamera(SimulatedSonyCameraBackend.DeviceId).Handle;
        camera.StartCapture(handle, 120);
        camera.CancelCapture(handle);
        Assert.Equal(3u, camera.GetCaptureStatus(handle));
        Assert.Throws<InvalidOperationException>(() => camera.GetLastImage());
        camera.StartCapture(handle, 1);
        Assert.Equal(2, camera.CaptureStarts);
    }

    [Fact]
    public void IgnoredNativeAbortRemainsBusyUntilExposureAndReadoutFinish() {
        var clock = new ManualTimeProvider();
        var camera = new SimulatedSonyCameraBackend(clock) { IgnoreNativeCancel = true, ReadoutDelay = TimeSpan.FromSeconds(10) };
        uint handle = camera.OpenCamera(SimulatedSonyCameraBackend.DeviceId).Handle;
        camera.StartCapture(handle, 1);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        camera.CancelCapture(handle);
        Assert.Equal(1u, camera.GetCaptureStatus(handle));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0x8002u, camera.GetCaptureStatus(handle));
        Assert.Throws<InvalidOperationException>(() => camera.StartCapture(handle, 1));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(4u, camera.GetCaptureStatus(handle));
    }

    [Fact]
    public void StatusFailuresLeaveCaptureRunningAndCanRecover() {
        var clock = new ManualTimeProvider();
        var camera = new SimulatedSonyCameraBackend(clock);
        uint handle = camera.OpenCamera(SimulatedSonyCameraBackend.DeviceId).Handle;
        camera.StartCapture(handle, 1);
        camera.StatusReadFailures = 1;
        Assert.Throws<InvalidOperationException>(() => camera.GetCaptureStatus(handle));
        Assert.Equal(0x8001u, camera.GetCaptureStatus(handle));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(4u, camera.GetCaptureStatus(handle));
    }

    [Fact]
    public void FailedCaptureHasNoImageAndDoesNotResurrectWhenFailureIsDisabled() {
        var clock = new ManualTimeProvider();
        var camera = new SimulatedSonyCameraBackend(clock) { FailCapture = true };
        uint handle = camera.OpenCamera(SimulatedSonyCameraBackend.DeviceId).Handle;
        camera.StartCapture(handle, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2u, camera.GetCaptureStatus(handle));
        camera.FailCapture = false;
        Assert.Equal(2u, camera.GetCaptureStatus(handle));
        Assert.Throws<InvalidOperationException>(() => camera.GetLastImage());
        camera.StartCapture(handle, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(4u, camera.GetCaptureStatus(handle));
    }

    [Fact]
    public void CompletedCaptureCannotFailOrReturnToReadoutBeforeImageIsCollected() {
        var clock = new ManualTimeProvider();
        var camera = new SimulatedSonyCameraBackend(clock);
        uint handle = camera.OpenCamera(SimulatedSonyCameraBackend.DeviceId).Handle;
        camera.StartCapture(handle, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        // Cancellation observes Complete without consuming its image.
        camera.CancelCapture(handle);
        camera.FailCapture = true;
        camera.ReadoutDelay = TimeSpan.FromSeconds(10);
        Assert.Equal(4u, camera.GetCaptureStatus(handle));
        Assert.Equal(1, camera.GetLastImage().Pixels[0]);
        // Updated settings still apply to subsequent captures.
        camera.StartCapture(handle, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(0x8002u, camera.GetCaptureStatus(handle));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2u, camera.GetCaptureStatus(handle));
        Assert.Throws<InvalidOperationException>(() => camera.GetLastImage());
    }

    [Fact]
    public void ReconnectInvalidatesOldHandleAndDiscardsLastImage() {
        var clock = new ManualTimeProvider();
        var camera = new SimulatedSonyCameraBackend(clock);
        uint oldHandle = camera.OpenCamera(SimulatedSonyCameraBackend.DeviceId).Handle;
        camera.StartCapture(oldHandle, 1);
        clock.Advance(TimeSpan.FromSeconds(2));
        camera.GetCaptureStatus(oldHandle);
        camera.CloseCamera(oldHandle);
        Assert.Throws<InvalidOperationException>(() => camera.GetLastImage());
        uint newHandle = camera.OpenCamera(SimulatedSonyCameraBackend.DeviceId).Handle;
        Assert.NotEqual(oldHandle, newHandle);
        Assert.Throws<InvalidOperationException>(() => camera.GetCaptureStatus(oldHandle));
        Assert.Throws<InvalidOperationException>(() => camera.GetLastImage());
    }

    [Fact]
    public void RepeatedExposuresReturnDistinctFramesWithoutRealTimeDelays() {
        var clock = new ManualTimeProvider();
        var camera = new SimulatedSonyCameraBackend(clock);
        uint handle = camera.OpenCamera(SimulatedSonyCameraBackend.DeviceId).Handle;
        ushort[] previous = null;
        for (int frame = 1; frame <= 100; frame++) {
            camera.StartCapture(handle, 120);
            Assert.Throws<InvalidOperationException>(() => camera.GetLastImage());
            clock.Advance(TimeSpan.FromSeconds(121));
            Assert.Equal(4u, camera.GetCaptureStatus(handle));
            ushort[] pixels = camera.GetLastImage().Pixels;
            Assert.Equal(frame, pixels[0]);
            Assert.NotSame(previous, pixels);
            previous = pixels;
        }
        Assert.Equal(100, camera.CaptureStarts);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(3601f)]
    public void InvalidDurationCannotReplaceExistingCapture(float duration) {
        var camera = new SimulatedSonyCameraBackend();
        uint handle = camera.OpenCamera(SimulatedSonyCameraBackend.DeviceId).Handle;
        Assert.Throws<ArgumentOutOfRangeException>(() => camera.StartCapture(handle, duration));
        Assert.Equal(0, camera.CaptureStarts);
    }
}
