using System;
using System.Collections.Generic;
using Sony;

namespace NINA.RetroKiwi.Plugin.SonyCamera.Drivers {
    // A managed replacement for the native camera calls, shared by NINA and automated tests.
    public sealed class SimulatedSonyCameraBackend : ISonyCameraBackend {
        public const string DeviceId = "sony-camera-simulator";
        public static bool Enabled => Environment.GetEnvironmentVariable("NINA_SONY_SIMULATOR") == "1";
        private const uint IsoProperty = 0xD21E;
        private const uint IsoOptionsProperty = 0xFFFE;
        private const uint BatteryProperty = 53784;
        private const int Width = 320;
        private const int Height = 240;
        private const int BitDepth = 14;
        private readonly object cameraLock = new object();
        private readonly TimeProvider clock;
        private uint handle;
        private bool connected;
        private bool captureActive;
        private uint captureStatus = 2;
        private uint iso = 400;
        private long startedAt;
        private TimeSpan exposureDuration;
        private int frameNumber;
        private SonyExposureFrame lastImage;

        public TimeSpan ReadoutDelay { get; set; } = TimeSpan.FromMilliseconds(150);
        public bool FailCapture { get; set; }
        public bool IgnoreNativeCancel { get; set; }
        public int StatusReadFailures { get; set; }
        public int NativeCancelCalls { get; private set; }
        public int CaptureStarts { get; private set; }

        public SimulatedSonyCameraBackend(TimeProvider clock = null) {
            this.clock = clock ?? TimeProvider.System;
        }

        internal static SimulatedSonyCameraBackend FromEnvironment() {
            var camera = new SimulatedSonyCameraBackend();
            switch (Environment.GetEnvironmentVariable("NINA_SONY_SIMULATOR_SCENARIO")) {
                case "slow-readout": camera.ReadoutDelay = TimeSpan.FromSeconds(10); break;
                case "ignored-cancel": camera.IgnoreNativeCancel = true; break;
                case "status-failure": camera.StatusReadFailures = 1; break;
                case "failed-capture": camera.FailCapture = true; break;
            }
            return camera;
        }

        public IEnumerable<SonyDevice> Cameras() {
            yield return new SonyDevice(new PortableDeviceInfo {
                id = DeviceId, manufacturer = "Sony", model = "Sony Camera Simulator", devicePath = DeviceId
            });
        }

        public SonyCameraInfo OpenCamera(string id) {
            lock (cameraLock) {
                if (id != DeviceId) {
                    throw new InvalidOperationException("Unknown simulated camera.");
                }
                if (connected) {
                    throw new InvalidOperationException("Simulated camera is already connected.");
                }
                connected = true;
                handle++;
                captureActive = false;
                captureStatus = 2;
                lastImage = null;
                var isos = new List<PropertyValueOption>();
                foreach (uint value in new uint[] { 100, 200, 400, 800, 1600, 3200 }) {
                    isos.Add(new PropertyValueOption { Value = value, Name = value.ToString() });
                }
                var properties = new List<Sony.PropertyInfo> {
                    new Sony.PropertyInfo(new PropertyDescriptor { Id = IsoProperty, Name = "ISO", ValueCount = (uint)isos.Count }, isos),
                    new Sony.PropertyInfo(new PropertyDescriptor { Id = IsoOptionsProperty, Name = "ISO options", ValueCount = (uint)isos.Count }, isos)
                };
                return new SonyCameraInfo(handle, new DeviceInfo {
                    Manufacturer = "Sony", Model = "Sony Camera Simulator", DeviceName = "Sony Camera Simulator",
                    SensorName = "Simulated RGGB sensor", ImageWidthPixels = Width, ImageHeightPixels = Height,
                    ExposureTimeMin = 0.01, ExposureTimeMax = 3600, PixelWidth = 4.5, PixelHeight = 4.5,
                    BitsPerPixel = BitDepth
                }, new CameraInfo(), properties);
            }
        }

        public void CloseCamera(uint cameraHandle) {
            lock (cameraLock) {
                RequireConnection(cameraHandle);
                connected = false;
                captureActive = false;
                lastImage = null;
            }
        }

        public PropertyValue GetProperty(uint cameraHandle, uint propertyId) {
            lock (cameraLock) {
                RequireConnection(cameraHandle);
                return propertyId switch {
                    IsoProperty => new PropertyValue { Id = propertyId, Value = iso },
                    BatteryProperty => new PropertyValue { Id = propertyId, Value = 100 },
                    _ => throw new InvalidOperationException("Unsupported simulated camera property.")
                };
            }
        }

        public void SetProperty(uint cameraHandle, uint propertyId, uint value) {
            lock (cameraLock) {
                RequireConnection(cameraHandle);
                if (propertyId != IsoProperty || (value != 100 && value != 200 && value != 400 &&
                    value != 800 && value != 1600 && value != 3200)) {
                    throw new InvalidOperationException("Unsupported simulated ISO value.");
                }
                iso = value;
            }
        }

        public byte[] GetLiveView(uint cameraHandle) => throw new NotSupportedException("The Sony simulator does not provide LiveView.");

        public void StartCapture(uint cameraHandle, float exposureTime) {
            lock (cameraLock) {
                RequireConnection(cameraHandle);
                UpdateCapture();
                if (captureActive && captureStatus != 2 && captureStatus != 3 && captureStatus != 4) {
                    throw new InvalidOperationException("Simulated camera is busy.");
                }
                if (float.IsNaN(exposureTime) || float.IsInfinity(exposureTime) || exposureTime < 0.01f || exposureTime > 3600) {
                    throw new ArgumentOutOfRangeException(nameof(exposureTime));
                }
                lastImage = null;
                captureActive = true;
                captureStatus = 0x8001;
                startedAt = clock.GetTimestamp();
                exposureDuration = TimeSpan.FromSeconds(exposureTime);
                frameNumber++;
                CaptureStarts++;
            }
        }

        public uint GetCaptureStatus(uint cameraHandle) {
            lock (cameraLock) {
                RequireConnection(cameraHandle);
                if (StatusReadFailures > 0) {
                    StatusReadFailures--;
                    throw new InvalidOperationException("Simulated camera status read failure.");
                }
                UpdateCapture();
                uint result = captureStatus;
                if (result == 4) {
                    // Match the native API: reading Complete consumes the capture and saves its image.
                    lastImage = CreateFrame();
                    captureActive = false;
                    captureStatus = 2;
                }
                return result;
            }
        }

        public void CancelCapture(uint cameraHandle) {
            lock (cameraLock) {
                RequireConnection(cameraHandle);
                NativeCancelCalls++;
                UpdateCapture();
                if (captureActive && captureStatus != 4 && !IgnoreNativeCancel) {
                    captureStatus = 3;
                    captureActive = false;
                    lastImage = null;
                }
            }
        }

        public SonyExposureFrame GetLastImage() {
            lock (cameraLock) {
                if (!connected || lastImage == null) {
                    throw new InvalidOperationException("No completed simulated exposure is available.");
                }
                return lastImage;
            }
        }

        private void UpdateCapture() {
            // Complete remains latched until GetCaptureStatus collects the image.
            if (!captureActive || captureStatus == 4) return;
            var elapsed = clock.GetElapsedTime(startedAt);
            if (elapsed < TimeSpan.FromMilliseconds(20)) captureStatus = 0x8001;
            else if (elapsed < exposureDuration) captureStatus = 1;
            else if (elapsed < exposureDuration + ReadoutDelay) captureStatus = 0x8002;
            else if (elapsed < exposureDuration + ReadoutDelay + TimeSpan.FromMilliseconds(20)) captureStatus = 0x8003;
            else {
                captureStatus = FailCapture ? 2u : 4u;
                if (FailCapture) captureActive = false;
            }
        }

        private SonyExposureFrame CreateFrame() {
            var pixels = new ushort[Width * Height];
            for (int y = 0; y < Height; y++) {
                for (int x = 0; x < Width; x++) {
                    double distance = Math.Pow(x - Width / 2.0, 2) + Math.Pow(y - Height / 2.0, 2);
                    pixels[y * Width + x] = (ushort)Math.Min(16383, 200 + frameNumber + (x + y) % 31 + 12000 * Math.Exp(-distance / 50));
                }
            }
            pixels[0] = (ushort)(frameNumber % 16384);
            return new SonyExposureFrame { Pixels = pixels, Width = Width, Height = Height, BitDepth = BitDepth };
        }

        private void RequireConnection(uint cameraHandle) {
            if (!connected || cameraHandle != handle) {
                throw new InvalidOperationException("Simulated camera is disconnected or the handle is invalid.");
            }
        }
    }
}
