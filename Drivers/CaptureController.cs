using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.RetroKiwi.Plugin.SonyCamera.Drivers {
    // Keep capture coordination independent of NINA and the native DLL so races can be tested without a camera.
    internal sealed class CaptureController {
        internal const uint StatusUnknown = 0xFFFFFFFF;
        private const uint Created = 0x0000;
        private const uint Capturing = 0x0001;
        private const uint Failed = 0x0002;
        private const uint Cancelled = 0x0003;
        private const uint Complete = 0x0004;
        private const uint Starting = 0x8001;
        private const uint Reading = 0x8002;
        private const uint Processing = 0x8003;

        internal enum AbortResult { AlreadyRequested, NativeCancelRequested, SoftCancelRequested }

        private readonly object captureLock = new object();
        private readonly Func<uint> getStatus;
        private readonly Action<float> startCapture;
        private readonly Func<bool> cancelCapture;
        private readonly Func<bool> nativeCancelEnabled;
        private readonly int cancelLockTimeoutMs;
        private sealed class CaptureRequest {
            internal int CancelRequested;
        }
        private CaptureRequest currentRequest = new CaptureRequest();

        internal CaptureController(Func<uint> getStatus, Action<float> startCapture,
            Func<bool> cancelCapture, Func<bool> nativeCancelEnabled, int cancelLockTimeoutMs = 1000) {
            this.getStatus = getStatus;
            this.startCapture = startCapture;
            this.cancelCapture = cancelCapture;
            this.nativeCancelEnabled = nativeCancelEnabled;
            this.cancelLockTimeoutMs = cancelLockTimeoutMs;
        }

        internal void Start(float exposureTime) {
            lock (captureLock) {
                uint status = getStatus();
                if (IsBusy(status)) {
                    throw new InvalidOperationException("Cannot start exposure: camera is still busy with a previous exposure.");
                }
                if (status != Created && status != Cancelled && status != Complete && status != Failed) {
                    throw new InvalidOperationException($"Cannot start exposure: capture status unavailable or unexpected ({status}).");
                }

                // A rejected start must not clear cancellation of the preceding exposure.
                Volatile.Write(ref currentRequest, new CaptureRequest());
                // Serialize the native start itself: it can replace the native capture object.
                startCapture(exposureTime);
            }
        }

        internal AbortResult Abort() {
            // Record cancellation before waiting for the lock, including while StartCapture is blocked.
            var request = Volatile.Read(ref currentRequest);
            if (Interlocked.Exchange(ref request.CancelRequested, 1) != 0) {
                return AbortResult.AlreadyRequested;
            }
            if (!nativeCancelEnabled() || !Monitor.TryEnter(captureLock, cancelLockTimeoutMs)) {
                return AbortResult.SoftCancelRequested;
            }

            try {
                // An abort queued for the previous exposure must never cancel a new one.
                if (!ReferenceEquals(request, currentRequest)) {
                    return AbortResult.AlreadyRequested;
                }
                uint status = getStatus();
                if (status == StatusUnknown || (!IsBusy(status) && status != Created &&
                    status != Cancelled && status != Complete && status != Failed)) {
                    return AbortResult.SoftCancelRequested;
                }
                if (!IsBusy(status)) {
                    return AbortResult.AlreadyRequested;
                }
                return cancelCapture() ? AbortResult.NativeCancelRequested : AbortResult.SoftCancelRequested;
            } finally {
                Monitor.Exit(captureLock);
            }
        }

        internal async Task WaitUntilReady(CancellationToken token) {
            var request = Volatile.Read(ref currentRequest);
            while (true) {
                token.ThrowIfCancellationRequested();
                ThrowIfCancelled(request);
                uint status;
                lock (captureLock) {
                    if (!ReferenceEquals(request, currentRequest)) {
                        throw new TaskCanceledException("Exposure superseded by a new capture.");
                    }
                    status = getStatus();
                }
                token.ThrowIfCancellationRequested();
                ThrowIfCancelled(request);

                switch (status) {
                    case Complete:
                        return;
                    case Cancelled:
                        throw new TaskCanceledException("Exposure cancelled by camera.");
                    case Failed:
                        throw new InvalidOperationException("Camera capture failed.");
                    case Created:
                    case Capturing:
                    case Starting:
                    case Reading:
                    case Processing:
                        await Task.Delay(100, token);
                        break;
                    default:
                        throw new InvalidOperationException($"Capture status unavailable or unexpected ({status}).");
                }
            }
        }

        private static void ThrowIfCancelled(CaptureRequest request) {
            if (Volatile.Read(ref request.CancelRequested) != 0) {
                throw new TaskCanceledException("Exposure cancelled by user; the camera may still be finishing it.");
            }
        }

        private static bool IsBusy(uint status) {
            return status == Capturing || status == Starting || status == Reading || status == Processing;
        }
    }
}
