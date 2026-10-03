using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.RetroKiwi.Plugin.SonyCamera.Drivers;
using Xunit;

public class CaptureControllerTests {
    private sealed class Camera {
        internal uint Status = 2; // The native driver reports Failed when no capture exists.
        internal bool NativeCancelEnabled;
        internal bool CancelSucceeds = true;
        internal int Starts;
        internal int Cancels;
        internal Action BeforeStart;
        internal Action BeforeReadNativeCancel;
        internal Func<uint> ReadStatus;
        internal readonly CaptureController Capture;

        internal Camera() {
            Capture = new CaptureController(
                () => ReadStatus == null ? Status : ReadStatus(),
                exposure => {
                    BeforeStart?.Invoke();
                    Starts++;
                    Status = 1;
                },
                () => {
                    Cancels++;
                    return CancelSucceeds;
                },
                () => {
                    BeforeReadNativeCancel?.Invoke();
                    return NativeCancelEnabled;
                },
                cancelLockTimeoutMs: 20);
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(0x8001u)]
    [InlineData(0x8002u)]
    [InlineData(0x8003u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(99u)]
    public void BusyOrUnknownStartNeverCancelsPreviousExposure(uint status) {
        var camera = new Camera { Status = status, NativeCancelEnabled = true };
        Assert.Throws<InvalidOperationException>(() => camera.Capture.Start(1));
        Assert.Equal(0, camera.Starts);
        Assert.Equal(0, camera.Cancels);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    public void IdleStartDoesNotCallNativeCancel(uint status) {
        var camera = new Camera { Status = status, NativeCancelEnabled = true };
        camera.Capture.Start(1);
        Assert.Equal(1, camera.Starts);
        Assert.Equal(0, camera.Cancels);
    }

    [Fact]
    public async Task SoftAbortStopsWaitingAndRejectsRestartUntilCameraFinishes() {
        var camera = new Camera();
        camera.Capture.Start(1);
        Assert.Equal(CaptureController.AbortResult.SoftCancelRequested, camera.Capture.Abort());
        Assert.Equal(CaptureController.AbortResult.AlreadyRequested, camera.Capture.Abort());
        Assert.Throws<InvalidOperationException>(() => camera.Capture.Start(1));
        await Assert.ThrowsAsync<TaskCanceledException>(() => camera.Capture.WaitUntilReady(CancellationToken.None));
        Assert.Equal(0, camera.Cancels);

        camera.Status = 4;
        camera.Capture.Start(1);
        camera.Status = 4;
        await camera.Capture.WaitUntilReady(CancellationToken.None);
        Assert.Equal(2, camera.Starts);
    }

    [Fact]
    public async Task NativeAbortIsIssuedOnceAndNeverReturnsAnImage() {
        var camera = new Camera { NativeCancelEnabled = true };
        camera.Capture.Start(1);
        Assert.Equal(CaptureController.AbortResult.NativeCancelRequested, camera.Capture.Abort());
        Assert.Equal(CaptureController.AbortResult.AlreadyRequested, camera.Capture.Abort());
        camera.Status = 4;
        await Assert.ThrowsAsync<TaskCanceledException>(() => camera.Capture.WaitUntilReady(CancellationToken.None));
        Assert.Equal(1, camera.Cancels);
    }

    [Theory]
    [InlineData(3u, typeof(TaskCanceledException))]
    [InlineData(2u, typeof(InvalidOperationException))]
    [InlineData(0xFFFFFFFFu, typeof(InvalidOperationException))]
    [InlineData(99u, typeof(InvalidOperationException))]
    public async Task CancelledFailedAndUnknownCapturesNeverReportReady(uint status, Type errorType) {
        var camera = new Camera();
        camera.Capture.Start(1);
        camera.Status = status;
        var error = await Record.ExceptionAsync(() => camera.Capture.WaitUntilReady(CancellationToken.None));
        Assert.IsType(errorType, error);
    }

    [Fact]
    public async Task CompleteStatusIsReadOnlyOnceBecauseNativeReadConsumesTheImage() {
        var camera = new Camera();
        camera.Capture.Start(1);
        int reads = 0;
        camera.ReadStatus = () => ++reads == 1 ? 4u : 2u;
        await camera.Capture.WaitUntilReady(CancellationToken.None);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task CancellationDuringStatusReadWinsOverImageCompletion() {
        var camera = new Camera();
        camera.Capture.Start(1);
        using var cancellation = new CancellationTokenSource();
        camera.ReadStatus = () => {
            cancellation.Cancel();
            return 4;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => camera.Capture.WaitUntilReady(cancellation.Token));
    }

    [Fact]
    public async Task TokenCancellationInterruptsPolling() {
        var camera = new Camera();
        camera.Capture.Start(1);
        using var cancellation = new CancellationTokenSource();
        var wait = camera.Capture.WaitUntilReady(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void NativeCancelSettingIsReadAtAbortTime() {
        var camera = new Camera();
        camera.Capture.Start(1);
        camera.NativeCancelEnabled = true;
        Assert.Equal(CaptureController.AbortResult.NativeCancelRequested, camera.Capture.Abort());
        Assert.Equal(1, camera.Cancels);
    }

    [Fact]
    public async Task FailedNativeCancelFallsBackToSoftCancellation() {
        var camera = new Camera { NativeCancelEnabled = true, CancelSucceeds = false };
        camera.Capture.Start(1);
        Assert.Equal(CaptureController.AbortResult.SoftCancelRequested, camera.Capture.Abort());
        await Assert.ThrowsAsync<TaskCanceledException>(() => camera.Capture.WaitUntilReady(CancellationToken.None));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(99u)]
    public async Task UnknownStatusDuringAbortSkipsNativeCancel(uint status) {
        var camera = new Camera { NativeCancelEnabled = true };
        camera.Capture.Start(1);
        camera.Status = status;
        Assert.Equal(CaptureController.AbortResult.SoftCancelRequested, camera.Capture.Abort());
        Assert.Equal(0, camera.Cancels);
        await Assert.ThrowsAsync<TaskCanceledException>(() => camera.Capture.WaitUntilReady(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationAfterPollingBeginsInterruptsWaitWithoutAToken() {
        var camera = new Camera();
        camera.Capture.Start(1);
        var wait = camera.Capture.WaitUntilReady(CancellationToken.None);
        camera.Capture.Abort();
        await Assert.ThrowsAsync<TaskCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AbortDuringBlockedNativeStartReturnsWithoutCallingNativeCancel() {
        var camera = new Camera { NativeCancelEnabled = true };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        camera.BeforeStart = () => {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var start = Task.Factory.StartNew(() => camera.Capture.Start(1), TaskCreationOptions.LongRunning);
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var abort = Task.Run(() => camera.Capture.Abort());
            Assert.Equal(CaptureController.AbortResult.SoftCancelRequested,
                await abort.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, camera.Cancels);
        } finally {
            release.Set();
        }
        await start;
        await Assert.ThrowsAsync<TaskCanceledException>(() => camera.Capture.WaitUntilReady(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentStartsCannotBothEnterNativeStart() {
        var camera = new Camera();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondAttempted = new ManualResetEventSlim();
        camera.BeforeStart = () => {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var first = Task.Factory.StartNew(() => camera.Capture.Start(1), TaskCreationOptions.LongRunning);
        Task<Exception> second = null;
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            second = Task.Factory.StartNew(() => {
                secondAttempted.Set();
                return Record.Exception(() => camera.Capture.Start(1));
            }, TaskCreationOptions.LongRunning);
            Assert.True(secondAttempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(100)));
        } finally {
            release.Set();
        }
        await first;
        Assert.IsType<InvalidOperationException>(await second);
        Assert.Equal(1, camera.Starts);
    }

    [Fact]
    public async Task DelayedAbortForPreviousExposureCannotCancelNextExposure() {
        var camera = new Camera { NativeCancelEnabled = true };
        camera.Capture.Start(1);
        camera.Status = 4;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        camera.BeforeReadNativeCancel = () => {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var abort = Task.Factory.StartNew(() => camera.Capture.Abort(), TaskCreationOptions.LongRunning);
        try {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            camera.Capture.Start(1);
        } finally {
            release.Set();
        }
        Assert.Equal(CaptureController.AbortResult.AlreadyRequested, await abort);
        Assert.Equal(0, camera.Cancels);
        camera.Status = 4;
        await camera.Capture.WaitUntilReady(CancellationToken.None);
    }
}
