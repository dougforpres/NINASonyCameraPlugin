using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sony
{
    #region SonyBase
    /// <summary>
    /// The single point of entry into SonyMTPCamera.dll.
    /// <para>
    /// Every call into the native driver is serialized here. The driver keeps
    /// per-device state that is not safe to touch from more than one thread:
    /// closing a device (or cancelling a capture) while another thread is
    /// mid-command frees memory the other thread is still reading, which
    /// surfaces as an 0xc0000005 access violation inside SonyMTPCamera.dll or
    /// PortableDeviceApi.dll. That kills the whole NINA process - an access
    /// violation in native code cannot be caught from managed code.
    /// </para>
    /// <para>
    /// There is plenty of opportunity for that to happen: the camera and the
    /// focuser share one device handle, NINA polls each of them on its own
    /// device-update timer, the capture runs on a task thread, and gain is set
    /// from the UI thread. So the lock below is what keeps the capture thread,
    /// both poll timers and the UI apart.
    /// </para>
    /// </summary>
    public class SonyDriver {
        private static readonly Lazy<SonyDriver> _instance = new Lazy<SonyDriver>(() => new SonyDriver());

        /// <summary>
        /// Consecutive native failures on the open handle before we declare the
        /// camera lost and stop calling into the driver. One failure is not
        /// enough - some properties legitimately fail on some bodies - but a
        /// camera that has been unplugged or has powered itself off fails every
        /// call from then on, and continuing to poll a dead handle is exactly
        /// how the access violation gets hit.
        /// </summary>
        private const int LOST_CAMERA_FAILURE_THRESHOLD = 3;

        /// <summary>
        /// How long any single call into the driver may take before we stop
        /// waiting for it. Nothing here is a long operation - StartCapture and
        /// CancelCapture just queue work with the camera and return, and
        /// GetCaptureStatus copies an already-downloaded buffer - so anything
        /// past this is the driver's worker thread stuck on an MTP command that
        /// will never complete. Left unbounded, that call blocks NINA's imaging
        /// thread indefinitely (observed: a StartExposure that never returned
        /// and silently burned three hours of a session).
        /// </summary>
        private const int NATIVE_CALL_TIMEOUT_MS = 30000;

        /// <summary>Opening a camera enumerates every property, so it gets longer.</summary>
        private const int OPEN_CAMERA_TIMEOUT_MS = 60000;

        /// <summary>
        /// Starting a capture also applies the shutter speed, and that can take
        /// a while: the camera has to be stepped one click at a time and may
        /// need the change re-issued before it takes.
        /// </summary>
        private const int START_CAPTURE_TIMEOUT_MS = 60000;

        /// <summary>
        /// Closing must not join the queue behind a stuck call - disconnecting
        /// is the user's way out, so it gives up quickly and cleans up locally.
        /// </summary>
        private const int CLOSE_CAMERA_LOCK_TIMEOUT_MS = 5000;

        private readonly object _nativeLock = new object();

        private SonyMTPCameraDll _sonydll;

        // Written only under _nativeLock, but read without it: Connected and the
        // focuser's handle lookup are on NINA's UI and poll paths, and they must
        // not block behind a 25MB image download just to ask whether the camera
        // is still there. A stale read is harmless - EnsureUsable rechecks under
        // the lock before anything actually reaches the driver.
        private volatile SonyCameraInfo _camera = null;
        private volatile uint _openHandle = 0;
        private volatile bool _cameraOpen = false;
        private volatile bool _cameraLost = false;

        /// <summary>
        /// Set when a call into the driver failed to return within the watchdog
        /// timeout. That thread is still inside the native DLL and cannot be
        /// recovered, so from this point nothing else is allowed in - the only
        /// way back is restarting NINA.
        /// </summary>
        private volatile bool _driverWedged = false;

        private int _consecutiveFailures = 0;

        public static SonyDriver GetInstance() {
            return _instance.Value;
        }

        public SonyDriver() {
            _sonydll = new SonyMTPCameraDll();
        }

        #region Native call plumbing

        /// <summary>
        /// Runs <paramref name="action"/> with the native lock held, after
        /// confirming <paramref name="handle"/> is still the open one.
        /// </summary>
        /// <param name="countsTowardsLoss">
        /// Whether a failure here is evidence the camera itself has gone away.
        /// True for the calls that talk to the device on every invocation
        /// (capture, live view, focus). False for individual property reads and
        /// writes: plenty of bodies advertise a property and then refuse it, and
        /// that must not be mistaken for an unplugged camera.
        /// </param>
        private T Invoke<T>(uint handle, string what, Func<T> action, bool countsTowardsLoss = true, int timeoutMs = NATIVE_CALL_TIMEOUT_MS) {
            if (!Monitor.TryEnter(_nativeLock, timeoutMs)) {
                throw new SonyException($"Timed out after {timeoutMs / 1000}s waiting to {what}: another operation is still running inside the Sony driver.");
            }

            try {
                EnsureUsable(handle, what);

                try {
                    T result = RunWithWatchdog(what, action, timeoutMs);
                    _consecutiveFailures = 0;
                    return result;
                } catch (Exception ex) {
                    RecordFailure(what, ex, countsTowardsLoss);
                    throw;
                }
            } finally {
                Monitor.Exit(_nativeLock);
            }
        }

        private void Invoke(uint handle, string what, Action action, bool countsTowardsLoss = true, int timeoutMs = NATIVE_CALL_TIMEOUT_MS) {
            Invoke<bool>(handle, what, () => { action(); return true; }, countsTowardsLoss, timeoutMs);
        }

        /// <summary>
        /// Runs a native call on a worker thread and stops waiting for it after
        /// <paramref name="timeoutMs"/>.
        /// <para>
        /// A blocked P/Invoke cannot be aborted, so the worker is abandoned
        /// rather than killed and the driver is marked wedged so nothing else
        /// follows it in. The point is not to recover the driver - it is to give
        /// the caller back its thread. NINA can then fail the exposure, log it
        /// and tell the user, instead of an imaging thread disappearing into the
        /// DLL for the rest of the night.
        /// </para>
        /// <para>Caller must hold <see cref="_nativeLock"/>.</para>
        /// </summary>
        private T RunWithWatchdog<T>(string what, Func<T> action, int timeoutMs) {
            var call = Task.Run(action);

            if (!call.Wait(timeoutMs)) {
                _driverWedged = true;
                _cameraOpen = false;
                _camera = null;

                Logger.Error($"Sony: the driver did not return from '{what}' within {timeoutMs / 1000}s. That thread is stuck inside SonyMTPCamera.dll and cannot be recovered; no further calls will be made. Restart NINA to use the camera again.");

                throw new SonyException($"The Sony driver stopped responding while trying to {what}. Restart NINA to recover the camera.");
            }

            // Unwraps to the original exception rather than an AggregateException.
            return call.GetAwaiter().GetResult();
        }

        /// <summary>
        /// Same as <see cref="Invoke{T}"/>, but gives up rather than waiting if
        /// the driver is busy. Used for the properties NINA polls on a timer -
        /// a 25MB image download can hold the lock for seconds, and stalling
        /// the poll timer behind it is worse than reporting a stale value.
        /// Returns false only when the lock could not be taken in time; a
        /// failing call still throws.
        /// </summary>
        private bool TryInvoke<T>(uint handle, string what, int timeoutMs, Func<T> action, out T result, bool countsTowardsLoss = true) {
            result = default;

            if (!Monitor.TryEnter(_nativeLock, timeoutMs)) {
                Logger.Debug($"Sony: skipping {what}; driver busy after {timeoutMs} ms.");
                return false;
            }

            try {
                EnsureUsable(handle, what);

                try {
                    result = RunWithWatchdog(what, action, NATIVE_CALL_TIMEOUT_MS);
                    _consecutiveFailures = 0;
                    return true;
                } catch (Exception ex) {
                    RecordFailure(what, ex, countsTowardsLoss);
                    throw;
                }
            } finally {
                Monitor.Exit(_nativeLock);
            }
        }

        /// <summary>Caller must hold <see cref="_nativeLock"/>.</summary>
        private void EnsureUsable(uint handle, string what) {
            if (_driverWedged) {
                throw new SonyException($"Cannot {what}: the Sony driver stopped responding earlier in this session. Restart NINA to recover the camera.");
            }

            if (_cameraLost) {
                throw new SonyException($"Cannot {what}: the camera is no longer responding and has been disconnected.");
            }

            if (!_cameraOpen) {
                throw new SonyException($"Cannot {what}: no camera is open.");
            }

            if (handle != _openHandle) {
                throw new SonyException($"Cannot {what}: handle {handle:x} is stale (open handle is {_openHandle:x}).");
            }
        }

        /// <summary>Caller must hold <see cref="_nativeLock"/>.</summary>
        private void RecordFailure(string what, Exception ex, bool countsTowardsLoss) {
            if (!countsTowardsLoss) {
                Logger.Warning($"Sony: {what} failed: {ex.Message}");
                return;
            }

            _consecutiveFailures++;

            if (_consecutiveFailures < LOST_CAMERA_FAILURE_THRESHOLD) {
                Logger.Warning($"Sony: {what} failed ({_consecutiveFailures} of {LOST_CAMERA_FAILURE_THRESHOLD} before giving up): {ex.Message}");
                return;
            }

            Logger.Error($"Sony: {what} failed {_consecutiveFailures} times in a row - treating the camera as disconnected and closing it.", ex);
            _cameraLost = true;
            _camera = null;

            if (_cameraOpen) {
                _cameraOpen = false;

                try {
                    _sonydll.CloseDevice(_openHandle);
                } catch (Exception closeEx) {
                    Logger.Warning($"Sony: closing the lost camera also failed: {closeEx.Message}");
                }
            }

            _openHandle = 0;
        }

        #endregion

        /// <summary>
        /// True while <paramref name="handle"/> is the open device handle. Goes
        /// false as soon as the camera is closed or declared lost, so callers
        /// can report "disconnected" instead of calling into a dead handle.
        /// </summary>
        public bool IsOpen(uint handle) {
            return _cameraOpen && !_cameraLost && handle == _openHandle;
        }

        /// <summary>True if the camera stopped responding rather than being closed on purpose.</summary>
        public bool CameraLost {
            get { return _cameraLost; }
        }

        public IEnumerable<SonyDevice> Cameras() {
            // Materialized rather than yielded: the caller must not be able to
            // hold the enumerator open across the lock.
            return InvokeUnhandled("enumerate cameras", () => {
                List<SonyDevice> cameras = new List<SonyDevice>();
                uint count = _sonydll.GetSupportedDeviceCount();

                for (uint i = 0; i < count; i++) {
                    cameras.Add(new SonyDevice(_sonydll.GetSupportedDeviceInfo(i)));
                }

                return cameras;
            }, NATIVE_CALL_TIMEOUT_MS);
        }

        /// <summary>
        /// Lock + watchdog for the calls that are not tied to an open handle
        /// (device and lens enumeration, open).
        /// </summary>
        private T InvokeUnhandled<T>(string what, Func<T> action, int timeoutMs) {
            if (!Monitor.TryEnter(_nativeLock, timeoutMs)) {
                throw new SonyException($"Timed out after {timeoutMs / 1000}s waiting to {what}: another operation is still running inside the Sony driver.");
            }

            try {
                if (_driverWedged) {
                    throw new SonyException($"Cannot {what}: the Sony driver stopped responding earlier in this session. Restart NINA to recover the camera.");
                }

                return RunWithWatchdog(what, action, timeoutMs);
            } finally {
                Monitor.Exit(_nativeLock);
            }
        }

        public SonyCameraInfo CameraInfo {
            get { return _camera; }
        }

        public SonyCameraInfo OpenCamera(string path) {
            return InvokeUnhandled($"open camera '{path}'", () => {
                if (_cameraOpen) {
                    Logger.Warning($"Sony: a camera is already open (handle {_openHandle:x}); closing it before opening '{path}'.");
                    CloseCameraInternal(_openHandle);
                }

                uint hCamera = _sonydll.OpenDevice(path);

                if (hCamera == 0 || hCamera == SonyMTPCameraDll.INVALID_HANDLE_VALUE) {
                    throw new SonyException($"Unable to open camera '{path}'.");
                }

                List<PropertyDescriptor> descriptors = _sonydll.GetPropertyDescriptors(hCamera);
                List<PropertyInfo> properties = new List<PropertyInfo>();

                foreach (PropertyDescriptor descriptor in descriptors) {
                    List<PropertyValueOption> options = null;

                    if (descriptor.ValueCount > 0) {
                        options = _sonydll.GetPropertyValueOptions(hCamera, descriptor);
                    }

                    properties.Add(new PropertyInfo(descriptor, options));
                }

                _camera = new SonyCameraInfo(hCamera, _sonydll.GetDeviceInfo(hCamera), _sonydll.GetCameraInfo(hCamera, 0), properties);
                _openHandle = hCamera;
                _cameraOpen = true;
                _cameraLost = false;
                _consecutiveFailures = 0;

                return _camera;
            }, OPEN_CAMERA_TIMEOUT_MS);
        }

        public void CloseCamera(uint handle) {
            // Disconnecting is the user's escape hatch, so it must not wait on
            // whatever else is in the driver. If we cannot get in quickly we
            // forget the handle locally and leave the device open rather than
            // block the UI - calling CloseDevice while another thread is inside
            // the driver is exactly the double-free we are trying to avoid.
            if (!Monitor.TryEnter(_nativeLock, CLOSE_CAMERA_LOCK_TIMEOUT_MS)) {
                Logger.Error($"Sony: could not close handle {handle:x} within {CLOSE_CAMERA_LOCK_TIMEOUT_MS / 1000}s because the driver is busy. Marking the camera closed without calling the driver; restart NINA before reconnecting.");
                _driverWedged = true;
                _cameraOpen = false;
                _camera = null;
                return;
            }

            try {
                if (_driverWedged) {
                    Logger.Warning($"Sony: not calling the driver to close handle {handle:x}; it stopped responding earlier in this session.");
                    _cameraOpen = false;
                    _camera = null;
                    return;
                }

                CloseCameraInternal(handle);
            } finally {
                Monitor.Exit(_nativeLock);
            }
        }

        /// <summary>Caller must hold <see cref="_nativeLock"/>.</summary>
        private void CloseCameraInternal(uint handle) {
            if (!_cameraOpen || handle != _openHandle) {
                // Already closed, or closed and reopened under a new handle.
                // Closing twice is what frees memory another thread may still
                // be reading, so do nothing rather than call the driver again -
                // and in particular do not clear _camera, which may now belong
                // to a different, still-open device.
                Logger.Debug($"Sony: ignoring close of handle {handle:x}; it is not the open handle.");
                return;
            }

            // Drop our record of the handle first so that anything queued
            // behind us on the lock fails the EnsureUsable check instead of
            // calling into a device we are about to close.
            _cameraOpen = false;
            _camera = null;
            _openHandle = 0;
            _cameraLost = false;
            _consecutiveFailures = 0;

            _sonydll.CloseDevice(handle);
        }

        public PropertyValue GetProperty(uint handle, uint propertyId) {
            return Invoke(handle, $"read property 0x{propertyId:x}", () => _sonydll.GetSinglePropertyValue(handle, propertyId), countsTowardsLoss: false);
        }

        /// <summary>
        /// Non-blocking <see cref="GetProperty"/> for values NINA polls on a
        /// timer (battery, gain). Returns false if the driver is busy.
        /// </summary>
        public bool TryGetProperty(uint handle, uint propertyId, int timeoutMs, out PropertyValue value) {
            return TryInvoke(handle, $"read property 0x{propertyId:x}", timeoutMs, () => _sonydll.GetSinglePropertyValue(handle, propertyId), out value, countsTowardsLoss: false);
        }

        public void SetProperty(uint handle, uint propertyId, uint value) {
            Invoke(handle, $"set property 0x{propertyId:x} to {value}", () => _sonydll.SetPropertyValue(handle, propertyId, value), countsTowardsLoss: false);
        }

        public byte[] GetLiveView(uint handle) {
            return Invoke(handle, "get live view frame", () => _sonydll.GetPreviewImage(handle));
        }

        /// <summary>
        /// Reads the capture status. On the one call that observes a completed
        /// capture this also copies the frame out of the driver's buffer, which
        /// is why it has to hold the lock: that buffer is freed by CancelCapture
        /// and CloseDevice.
        /// </summary>
        public uint GetCaptureStatus(uint handle) {
            return Invoke(handle, "read capture status", () => _sonydll.GetCaptureStatus(handle));
        }

        /// <summary>
        /// Gets a longer watchdog than everything else: it may have to re-issue
        /// a shutter-speed change several times before the camera takes it, and
        /// the driver steps roughly one click per second.
        /// </summary>
        public void StartCapture(uint handle, float exposureTime) {
            Invoke(handle, $"start a {exposureTime}s capture", () => _sonydll.StartCapture(handle, exposureTime), timeoutMs: START_CAPTURE_TIMEOUT_MS);
        }

        public void CancelCapture(uint handle) {
            Invoke(handle, "cancel the capture", () => _sonydll.CancelCapture(handle));
        }

        /// <summary>
        /// Hands over the frame that <see cref="GetCaptureStatus"/> took when it
        /// saw the capture complete, and forgets it. Purely managed - the copy
        /// out of the driver already happened - so no watchdog is needed.
        /// </summary>
        /// <remarks>
        /// Deliberately does not take the native lock: it touches nothing but a
        /// managed field, and taking the lock would let a wedged driver call
        /// block the download of a frame that has already been copied out.
        /// </remarks>
        public byte[] TakeLastImage() {
            return _sonydll.TakeLastImage();
        }

        /// <summary>Drops any frame left over from a previous capture.</summary>
        public void DiscardLastImage() {
            _sonydll.DiscardLastImage();
        }

        public IEnumerable<SonyLens> Lenses() {
            return InvokeUnhandled("enumerate lenses", () => {
                List<SonyLens> lenses = new List<SonyLens>();
                int count = _sonydll.GetLensCount();

                for (uint i = 0; i < count; i++) {
                    lenses.Add(new SonyLens(_sonydll.GetLensInfo(i)));
                }

                return lenses;
            }, NATIVE_CALL_TIMEOUT_MS);
        }

        public void SetAttachedLens(uint handle, string path) {
            Invoke(handle, $"attach lens '{path}'", () => _sonydll.SetAttachedLens(handle, path));
        }

        public void SetFocusPosition(uint handle, uint position) {
            Invoke(handle, $"move focus to {position}", () => _sonydll.SetFocusPosition(handle, position));
        }

        public uint GetFocusPosition(uint handle) {
            return Invoke(handle, "read focus position", () => _sonydll.GetFocusPosition(handle));
        }

        /// <summary>Non-blocking <see cref="GetFocusPosition"/> for NINA's focuser poll timer.</summary>
        public bool TryGetFocusPosition(uint handle, int timeoutMs, out uint position) {
            return TryInvoke(handle, "read focus position", timeoutMs, () => _sonydll.GetFocusPosition(handle), out position);
        }

        public uint GetFocusLimit(uint handle) {
            return Invoke(handle, "read focus limit", () => _sonydll.GetFocusLimit(handle));
        }

        /// <summary>Non-blocking <see cref="GetFocusLimit"/> for NINA's focuser poll timer.</summary>
        public bool TryGetFocusLimit(uint handle, int timeoutMs, out uint limit) {
            return TryInvoke(handle, "read focus limit", timeoutMs, () => _sonydll.GetFocusLimit(handle), out limit);
        }
    }
    #endregion
}
