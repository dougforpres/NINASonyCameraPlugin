using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using FTD2XX_NET;
using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Equipment.SDK.CameraSDKs.ASTPANSDK;
using NINA.Equipment.Utility;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.RetroKiwi.Plugin.SonyCamera;
using Sony;

namespace NINA.RetroKiwi.Plugin.SonyCamera.Drivers {
    public class CameraDriver : BaseINPC, ICamera {
        // Some camera settings we are interested in
        private const uint PROPID_BATTERY = 53784;
        private const uint PROPID_ISO = 0xD21E; // Actual ISO currently set
        private const uint PROPID_ISOS = 0xFFFE; // Registry-backed list of learnt ISOs (may be empty until learnt)

        // Capture Status
        private const uint CAPTURE_CREATED    = 0x0000;
        private const uint CAPTURE_CAPTURING  = 0x0001;
        private const uint CAPTURE_FAILED     = 0x0002;
        private const uint CAPTURE_CANCELLED  = 0x0003;
        private const uint CAPTURE_COMPLETE   = 0x0004;
        private const uint CAPTURE_STARTING   = 0x8001;
        private const uint CAPTURE_READING    = 0x8002;
        private const uint CAPTURE_PROCESSING = 0x8003;
        private const uint CAPTURE_STATUS_UNKNOWN = 0xFFFFFFFF;
        private static readonly uint[] IDLE_STATES = { CAPTURE_CREATED, CAPTURE_CANCELLED, CAPTURE_COMPLETE, CAPTURE_FAILED };
        private static readonly uint[] BUSY_STATES = { CAPTURE_CAPTURING, CAPTURE_PROCESSING, CAPTURE_STARTING, CAPTURE_READING };
        private static readonly uint[] COMPLETION_STATES = { CAPTURE_CANCELLED, CAPTURE_COMPLETE, CAPTURE_FAILED };

        // How long a poll-driven property read (battery, gain) will wait for the
        // driver before giving up and reporting the last known value. NINA polls
        // these on a 2s timer; an image download can hold the driver for longer
        // than that, and stalling the timer behind it just makes NINA complain.
        private const int POLL_READ_TIMEOUT_MS = 500;

        private readonly PluginOptionsAccessor _pluginSettings;
        private readonly bool _enableNativeCancelDefault;
        private volatile bool _softCancelRequested;

        // Cached because the ISO option list comes from the property descriptors
        // read once at open time, and CanGetGain/Gains/GainMin/GainMax are all on
        // NINA's poll path.
        private IReadOnlyList<PropertyValueOption> _isoOptions = Array.Empty<PropertyValueOption>();

        // Last values successfully read from the camera, reported when a poll
        // read is skipped or fails so the UI does not flap.
        private int _lastKnownGain = -1;
        private int _lastKnownBatteryLevel = 0;

        private SonyCameraInfo _camera = null;
        private SonyDevice _device = null;
        private IProfileService _profileService;
        private readonly IExposureDataFactory _exposureDataFactory;
        private bool _liveViewEnabled;
        private short _readoutModeForSnapImages;
        private short _readoutModeForNormalImages;
        private AsyncObservableCollection<BinningMode> _binningModes;
        private readonly object _captureLock = new object();

        public CameraDriver(IProfileService profileService, IExposureDataFactory exposureDataFactory, SonyDevice device, PluginOptionsAccessor pluginSettings, bool enableNativeCancel) {
            _profileService = profileService;
            _exposureDataFactory = exposureDataFactory;
            _device = device;
            _pluginSettings = pluginSettings;
            _enableNativeCancelDefault = enableNativeCancel;
            _softCancelRequested = false;
        }

        #region Internal Helpers

        private PropertyValue GetPropertyValue(uint id) {
            return SonyDriver.GetInstance().GetProperty(_camera.Handle, id);
        }

        /// <summary>
        /// Reads a property that NINA polls on a timer, without waiting on the
        /// driver. Returns the supplied fallback when the driver is busy or the
        /// read fails, so a poll can never stall or throw into NINA's timer.
        /// </summary>
        private bool TryReadPolledProperty(uint propertyId, out uint value) {
            value = 0;

            var camera = _camera;
            if (camera == null) {
                return false;
            }

            try {
                if (SonyDriver.GetInstance().TryGetProperty(camera.Handle, propertyId, POLL_READ_TIMEOUT_MS, out PropertyValue property)) {
                    value = property.Value;
                    return true;
                }
            } catch (Exception ex) {
                Logger.Debug($"Polled read of property 0x{propertyId:X} failed: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Works out the ISO list once per connection. The options come from the
        /// property descriptors the driver hands over at open time, so they do
        /// not change while connected - and re-deriving them on every poll of
        /// CanGetGain used to re-log the same warning several times a second.
        /// </summary>
        private IReadOnlyList<PropertyValueOption> ReadIsoOptions() {
            if (_camera == null) {
                return Array.Empty<PropertyValueOption>();
            }

            uint[] propertyCandidates = { PROPID_ISOS, PROPID_ISO };

            foreach (var propertyId in propertyCandidates) {
                try {
                    var options = _camera.GetPropertyInfo(propertyId)?.Options()?.Where(o => o.Value <= 0x00FFFFFF).ToList();
                    if (options != null && options.Count > 0) {
                        return options;
                    }
                } catch (Exception ex) {
                    Logger.Warning($"Unable to enumerate ISO options for property 0x{propertyId:X}: {ex.Message}.");
                }
            }

            Logger.Warning("Camera did not report any ISO options via known properties (registry ISO list may be empty until the camera learns it).");
            return Array.Empty<PropertyValueOption>();
        }

        private IReadOnlyList<PropertyValueOption> GetAvailableIsoOptions() {
            return _isoOptions;
        }

        private void NotifyGainPropertiesChanged() {
            RaisePropertyChanged(nameof(CanGetGain));
            RaisePropertyChanged(nameof(CanSetGain));
            RaisePropertyChanged(nameof(GainMin));
            RaisePropertyChanged(nameof(GainMax));
            RaisePropertyChanged(nameof(Gain));
            RaisePropertyChanged(nameof(Gains));
        }

        private bool NativeCancelEnabled {
            get {
                if (_pluginSettings != null) {
                    try {
                        var raw = _pluginSettings.GetValueString(nameof(SonyCamera.EnableNativeCancel), _enableNativeCancelDefault.ToString());
                        if (bool.TryParse(raw, out var enabled)) {
                            return enabled;
                        }
                    } catch (Exception ex) {
                        Logger.Warning($"EnableNativeCancel read failed; defaulting to {_enableNativeCancelDefault}. {ex.Message}.");
                    }
                }

                return _enableNativeCancelDefault;
            }
        }

        private bool TryCancelCaptureIfEnabled(string reason, int lockTimeoutMs = 1000) {
            var camera = _camera;

            if (camera == null) {
                return false;
            }

            if (!NativeCancelEnabled) {
                Logger.Info($"Native cancel disabled; skipping cancel ({reason}).");
                return false;
            }

            bool lockTaken = false;
            try {
                if (!Monitor.TryEnter(_captureLock, lockTimeoutMs)) {
                    Logger.Warning($"Skipping cancel ({reason}); capture lock unavailable after {lockTimeoutMs} ms.");
                    return false;
                }
                lockTaken = true;

                SonyDriver driver = SonyDriver.GetInstance();
                if (!TryGetCaptureStatus(driver, out var status, reason)) {
                    return false;
                }

                if (status == CAPTURE_STATUS_UNKNOWN) {
                    Logger.Warning($"Skipping cancel ({reason}); capture status is unknown (camera did not respond).");
                    return false;
                }

                if (!BUSY_STATES.Contains(status)) {
                    Logger.Debug($"[thread {Environment.CurrentManagedThreadId}] Skipping cancel ({reason}); capture status is {DescribeCaptureStatus(status)}.");
                    return false;
                }

                if (status == CAPTURE_READING || status == CAPTURE_PROCESSING) {
                    // Cancelling mid-download is the specific interleaving that
                    // was blamed for the access violation in #4: it releases the
                    // buffer the driver is still filling. The image is seconds
                    // from being ready anyway, so let it finish and treat this
                    // as a soft cancel.
                    Logger.Info($"Skipping cancel ({reason}); the image is already being read from the camera (status {status}). Letting it finish.");
                    return false;
                }

                Logger.Info($"[thread {Environment.CurrentManagedThreadId}] Issuing cancel ({reason}); capture status is {DescribeCaptureStatus(status)}.");
                driver.CancelCapture(camera.Handle);
                return true;
            } catch (Exception ex) {
                Logger.Error($"CancelCapture failed ({reason}).", ex);
                return false;
            } finally {
                if (lockTaken) {
                    Monitor.Exit(_captureLock);
                }
            }
        }

        private static string DescribeCaptureStatus(uint status) {
            switch (status) {
                case CAPTURE_CREATED: return "created (0)";
                case CAPTURE_CAPTURING: return "capturing (1)";
                case CAPTURE_FAILED: return "failed (2)";
                case CAPTURE_CANCELLED: return "cancelled (3)";
                case CAPTURE_COMPLETE: return "complete (4)";
                case CAPTURE_STARTING: return "starting (0x8001)";
                case CAPTURE_READING: return "reading from camera (0x8002)";
                case CAPTURE_PROCESSING: return "processing (0x8003)";
                case CAPTURE_STATUS_UNKNOWN: return "unknown";
                default: return $"0x{status:x}";
            }
        }

        private bool TryGetCaptureStatus(SonyDriver driver, out uint status, string reason) {
            status = CAPTURE_STATUS_UNKNOWN;

            var camera = _camera;

            if (camera == null) {
                Logger.Warning($"Unable to get capture status ({reason}); the camera is disconnected.");
                return false;
            }

            try {
                status = driver.GetCaptureStatus(camera.Handle);
                return true;
            } catch (Exception ex) {
                Logger.Warning($"Unable to get capture status ({reason}); camera may not have responded: {ex.Message}.");
                status = CAPTURE_STATUS_UNKNOWN;
                return false;
            }
        }

        #endregion

        #region Supported Properties

        public bool HasShutter => true;

        // Although the driver supports camera temperature, it gets it from the ARW's
        // metadata after a photo is taken, because this code doesn't request processed
        // ARW, the temp cannot be determined.
        public double Temperature {
            get => double.NaN;
            /*{

                if (_camera != null) {
                    PropertyValue value = GetPropertyValue(PROPID_TEMPERATURE);

                    return (value.Value) / 10.0;
                } else {
                    return double.NaN;
                }
            }*/
        }

        public short BinX { get => 1; set => throw new NotImplementedException(); }
        public short BinY { get => 1; set => throw new NotImplementedException(); }

        public string SensorName {
            get {
                if (_camera != null) {
                    return _camera.SensorName;
                } else {
                    return string.Empty;
                }
            }
        }

        public SensorType SensorType { get => SensorType.RGGB; set => throw new NotImplementedException(); }

        public short BayerOffsetX { get => 1; set => throw new NotImplementedException(); }

        public short BayerOffsetY { get => 1; set => throw new NotImplementedException(); }

        public int CameraXSize {
            get {
                if (_camera != null) {
                    return _camera.ImageSize.Width;
                }
                else {
                    return 0;
                }
            }
        }

        public int CameraYSize {
            get {
                if (_camera != null) {
                    return _camera.ImageSize.Height;
                } else {
                    return 0;
                }
            }
        }

        public double ExposureMin {
            get {
                if (_camera != null) {
                    return _camera.ExposureMin;
                } else {
                    return double.NaN;
                }
            }
        }

        public double ExposureMax {
            get {
                if (_camera != null) {
                    return _camera.ExposureMax;
                } else {
                    return double.NaN;
                }
            }
        }

        public short MaxBinX { get => 1; set => throw new NotImplementedException(); }

        public short MaxBinY { get => 1; set => throw new NotImplementedException(); }

        public double PixelSizeX {
            get {
                if (_camera != null) {
                    return _camera.PixelWidth;
                } else {
                    return double.NaN;
                }
            }
        }

        public double PixelSizeY {
            get {
                if (_camera != null) {
                    return _camera.PixelHeight;
                } else {
                    return double.NaN;
                }
            }
        }

        public bool CanSetTemperature => false;

        public CameraStates CameraState => CameraStates.NoState; // TODO

        public bool CanShowLiveView {
            get {
                if (_camera != null) {
                    return _camera.SupportsPreview();
                } else {
                    return false;
                }
            }
        }

        public bool LiveViewEnabled {
            get => _liveViewEnabled;
            set {
                _liveViewEnabled = value;
                RaisePropertyChanged();
            }
        }

        public bool HasBattery => true;

        public int BatteryLevel {
            get {
                if (TryReadPolledProperty(PROPID_BATTERY, out uint level)) {
                    _lastKnownBatteryLevel = (int)level;
                }

                return _camera != null ? _lastKnownBatteryLevel : 0;
            }
        }

        public int BitDepth {
            get {
                if (_camera != null) {
                    return _camera.BitsPerPixel;
                } else {
                    return 0;
                }
            }
        }

        public bool CanGetGain => GetAvailableIsoOptions().Any();

        public bool CanSetGain => CanGetGain;

        public int GainMax {
            get {
                var isoOptions = GetAvailableIsoOptions();
                if (!isoOptions.Any()) {
                    if (_camera != null) {
                        Logger.Error("Problem getting gain max: camera did not report ISO options.");
                    }
                    return -1;
                }

                return (int)isoOptions.Last().Value;
            }
        }

        public int GainMin {
            get {
                var isoOptions = GetAvailableIsoOptions();
                if (!isoOptions.Any()) {
                    if (_camera != null) {
                        Logger.Error("Problem getting gain min: camera did not report ISO options.");
                    }
                    return -1;
                }

                return (int)isoOptions.Min(o => o.Value);
            }
        }

        public int Gain {
            get {
                if (_camera == null) {
                    return -1;
                }

                if (TryReadPolledProperty(PROPID_ISO, out uint iso)) {
                    _lastKnownGain = (int)(iso == 0xffffff ? 0 : iso);
                }

                return _lastKnownGain;
            }

            set {
                var camera = _camera;

                if (camera != null) {
                    try {
                        SonyDriver.GetInstance().SetProperty(camera.Handle, PROPID_ISO, (uint)value);
                        _lastKnownGain = value;
                        RaisePropertyChanged(nameof(Gain));
                    } catch (Exception ex) {
                        Logger.Error($"Problem setting gain to {value}.", ex);
                    }
                }
            }
        }

        public IList<int> Gains {
            get {
                List<int> gains = new List<int>();

                foreach (var iso in GetAvailableIsoOptions()) {
                    if (iso.Value == 0xffffff) {
                        gains.Add(0); // AUTO
                    } else {
                        gains.Add((int)iso.Value);
                    }
                }

                return gains;
            }
        }

        public string Id => "Sony";

        public string Name {
            get => _device.Model;
            set => throw new NotImplementedException();
        }

        public string DisplayName {
            get => _device.Model;
            set => throw new NotImplementedException();
        }

        public string Category { get => "Sony"; }

        public bool Connected {
            get {
                var camera = _camera;

                if (camera == null) {
                    return false;
                }

                // The driver closes the handle itself if the camera stops
                // responding (unplugged, powered off, USB dropped). Report that
                // as disconnected so NINA stops driving a dead handle rather
                // than polling it until something faults inside the native DLL.
                if (!SonyDriver.GetInstance().IsOpen(camera.Handle)) {
                    Logger.Warning("Sony camera is no longer open; reporting it as disconnected.");
                    _camera = null;
                    NotifyGainPropertiesChanged();
                    RaisePropertyChanged(nameof(Connected));
                    return false;
                }

                return true;
            }
        }

        public string Description {
            get {
                if (_camera != null) {
                    return _camera.GetDescription();
                } else {
                    return _device.GetDescription();
                }
            }
        }

        public string DriverInfo => "https://retro.kiwi";

        public string DriverVersion => string.Empty;

        public double TemperatureSetPoint {
            get => double.NaN;

            set {
            }
        }

        public bool CanSubSample => false;

        public bool EnableSubSample { get; set; }

        public int SubSampleX { get; set; }

        public int SubSampleY { get; set; }

        public int SubSampleWidth { get; set; }

        public int SubSampleHeight { get; set; }

        public bool CoolerOn {
            get => false;
            set {
            }
        }

        public double CoolerPower => double.NaN;

        public bool HasDewHeater => false;

        public bool DewHeaterOn {
            get => false;
            set {
            }
        }

        public bool CanSetOffset => false;

        public int Offset { get => -1; set => throw new NotImplementedException(); }

        public int OffsetMin => 0;

        public int OffsetMax => 0;

        public bool CanSetUSBLimit => false;

        public int USBLimit { get => -1; set => throw new NotImplementedException(); }

        public int USBLimitMin => -1;

        public int USBLimitMax => -1;

        public int USBLimitStep => -1;

        public double ElectronsPerADU => double.NaN;

        public IList<string> ReadoutModes => new List<string> { "Default" };

        public short ReadoutMode {
            get => 0;
            set { }
        }

        public short ReadoutModeForSnapImages {
            get => _readoutModeForSnapImages;
            set {
                _readoutModeForSnapImages = value;
                RaisePropertyChanged();
            }
        }

        public short ReadoutModeForNormalImages {
            get => _readoutModeForNormalImages;
            set {
                _readoutModeForNormalImages = value;
                RaisePropertyChanged();
            }
        }

        public AsyncObservableCollection<BinningMode> BinningModes {
            get {
                if (_binningModes == null) {
                    _binningModes = new AsyncObservableCollection<BinningMode>();
                    _binningModes.Add(new BinningMode(1, 1));
                }

                return _binningModes;
            }
        }

        #endregion

        #region Supported Methods

        public void StartLiveView(CaptureSequence sequence) {
            LiveViewEnabled = true;
        }

        public void StopLiveView() {
            LiveViewEnabled = false;
        }

        public Task<bool> Connect(CancellationToken token) {
            return Task.Run<bool>(() => {
                try {
                    _camera = SonyDriver.GetInstance().OpenCamera(_device.Id);
                    _isoOptions = ReadIsoOptions();
                    _lastKnownGain = -1;
                    _lastKnownBatteryLevel = 0;
                    _softCancelRequested = false;
                } catch (Exception ex) {
                    Logger.Error(ex);
                    _camera = null;
                    _isoOptions = Array.Empty<PropertyValueOption>();
                }

                NotifyGainPropertiesChanged();
                RaisePropertyChanged(nameof(Connected));
                return _camera != null;
            });
        }

        public void Disconnect() {
            var camera = _camera;

            // Clear our handle first. Anything still polling (NINA's device
            // update timer, a capture wait loop) then stops calling the driver
            // instead of racing CloseDevice, which is the case that frees memory
            // out from under an in-flight command.
            _camera = null;
            _isoOptions = Array.Empty<PropertyValueOption>();
            _softCancelRequested = false;

            if (camera != null) {
                try {
                    SonyDriver.GetInstance().CloseCamera(camera.Handle);
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }

            NotifyGainPropertiesChanged();
            RaisePropertyChanged(nameof(Connected));
        }

        public Task<IExposureData> DownloadLiveView(CancellationToken token) {
            return Task.Run<IExposureData>(() => {
                var camera = _camera;

                if (camera == null) {
                    throw new SonyException("Cannot download a live view frame: the camera is disconnected.");
                }

                using (var memStream = new MemoryStream(SonyDriver.GetInstance().GetLiveView(camera.Handle))) {
                    memStream.Position = 0;

                    JpegBitmapDecoder decoder =
                        new JpegBitmapDecoder(memStream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);

                    FormatConvertedBitmap bitmap = new FormatConvertedBitmap();
                    bitmap.BeginInit();
                    bitmap.Source = decoder.Frames[0];
                    bitmap.DestinationFormat = System.Windows.Media.PixelFormats.Gray16;
                    bitmap.EndInit();

                    ushort[] outArray = new ushort[bitmap.PixelWidth * bitmap.PixelHeight];
                    bitmap.CopyPixels(outArray, 2 * bitmap.PixelWidth, 0);

                    var metaData = new ImageMetaData();

                    return _exposureDataFactory.CreateImageArrayExposureData(
                        input: outArray,
                        width: bitmap.PixelWidth,
                        height: bitmap.PixelHeight,
                        bitDepth: 16,
                        isBayered: false,
                        metaData: metaData);
                }
            });
        }

        public void SetupDialog() {
            throw new NotImplementedException();
        }

        public void StartExposure(CaptureSequence sequence) {
            var camera = _camera;

            if (camera != null) {
                SonyDriver driver = SonyDriver.GetInstance();

                double exposureTime;
                lock (_captureLock) {
                    _softCancelRequested = false;

                    if (!TryGetCaptureStatus(driver, out var captureStatus, "start exposure preflight") ||
                        captureStatus == CAPTURE_STATUS_UNKNOWN) {
                        Logger.Warning("Cannot start exposure: capture status unavailable.");
                        throw new TaskCanceledException("Cannot start exposure: capture status unavailable.");
                    }

                    TryCancelCaptureIfEnabled("start exposure reset");
                    if (NativeCancelEnabled && (!TryGetCaptureStatus(driver, out captureStatus, "start exposure post-cancel") || captureStatus == CAPTURE_STATUS_UNKNOWN)) {
                        Logger.Warning("Cannot start exposure: capture status unavailable after cancel.");
                        throw new TaskCanceledException("Cannot start exposure: capture status unavailable after cancel.");
                    }

                    if (BUSY_STATES.Contains(captureStatus)) {
                        Notification.ShowWarning("Camera is still busy with a previous exposure; skipping new start.");
                        throw new TaskCanceledException("Cannot start exposure: camera is still busy with a previous exposure.");
                    }

                    if (!IDLE_STATES.Contains(captureStatus)) {
                        Logger.Warning($"Cannot start exposure: camera in unexpected capture status ({captureStatus}).");
                        throw new TaskCanceledException($"Cannot start exposure: camera in unexpected capture status ({captureStatus}).");
                    }

                    exposureTime = sequence.ExposureTime;

                    // Drop anything left over from a previous capture, so a
                    // failed exposure cannot be handed the old frame. This has
                    // to come after the status reads above: a read that sees a
                    // stale COMPLETE takes that frame as a side effect.
                    driver.DiscardLastImage();
                }

                // Start capture outside the capture lock to avoid blocking abort/cancel paths if the call hangs.
                driver.StartCapture(camera.Handle, (float)exposureTime);
            }
        }

        public void StopExposure() {
            AbortExposure();
        }

        public void AbortExposure() {
            if (TryCancelCaptureIfEnabled("abort request")) {
                return;
            }

            _softCancelRequested = true;
            Notification.ShowWarning("Abort requested; native cancel is disabled or the camera did not accept it. Exposure will continue until it finishes.");
            Logger.Info("AbortExposure requested; native cancel unavailable; letting capture finish.");
        }

        public async Task WaitUntilExposureIsReady(CancellationToken token) {
            using (token.Register(AbortExposure)) {

                SonyDriver driver = SonyDriver.GetInstance();

                try {
                    uint captureStatus;
                    lock (_captureLock) {
                        if (!TryGetCaptureStatus(driver, out captureStatus, "wait begin") || captureStatus == CAPTURE_STATUS_UNKNOWN) {
                            throw new SonyException("Problem while waiting for image to be ready (status unavailable)");
                        }
                    }
                    Logger.Info($"[thread {Environment.CurrentManagedThreadId}] Waiting for image to be ready; current state is {DescribeCaptureStatus(captureStatus)}; completion states are {String.Join(", ", COMPLETION_STATES.Select(DescribeCaptureStatus))}.");

                    uint previousStatus = captureStatus;

                    while (!COMPLETION_STATES.Contains(captureStatus)) {
                        await CoreUtil.Wait(TimeSpan.FromMilliseconds(100), token);

                        lock (_captureLock) {
                            if (!TryGetCaptureStatus(driver, out captureStatus, "wait poll") || captureStatus == CAPTURE_STATUS_UNKNOWN) {
                                throw new SonyException("Problem while waiting for image to be ready (status unavailable)");
                            }
                        }

                        if (captureStatus != previousStatus) {
                            // Logged per transition rather than per poll: this is
                            // the trace needed to line a NINA log up against the
                            // driver's own log when diagnosing a capture that
                            // wedged or crashed.
                            Logger.Info($"[thread {Environment.CurrentManagedThreadId}] Capture status {DescribeCaptureStatus(previousStatus)} -> {DescribeCaptureStatus(captureStatus)}.");
                            previousStatus = captureStatus;
                        }
                    }

                    Logger.Info($"[thread {Environment.CurrentManagedThreadId}] Wait for image ready complete; completion state is {DescribeCaptureStatus(captureStatus)}.");

                    if (_softCancelRequested || token.IsCancellationRequested) {
                        _softCancelRequested = false;
                        throw new TaskCanceledException("Exposure cancelled by user (soft cancel).");
                    }
                } catch (TaskCanceledException) {
                    Logger.Info("WaitUntilExposureIsReady cancelled by token; exiting without native cancel.");
                    throw;
                } catch (Exception ex) {
                    Logger.Error("WaitUntilExposureIsReady got exception.", ex);
                    throw new SonyException("Problem while waiting for image to be ready (see log)");
                }
            }
        }

        public Task<IExposureData> DownloadExposure(CancellationToken token) {
            return Task.Run<IExposureData>(() => {
                // Whether there is a frame to hand over is the driver layer's
                // knowledge, not something to infer from the status this class
                // happened to observe: the frame is copied out by whichever call
                // saw the capture complete, and taking it clears it, so a
                // cancelled or failed exposure cannot be served the last one.
                byte[] rawImageData = SonyDriver.GetInstance().TakeLastImage();

                if (rawImageData == null || rawImageData.Length == 0) {
                    throw new SonyException("The last exposure did not produce an image to download.");
                }

                var metaData = new ImageMetaData();

                return _exposureDataFactory.CreateRAWExposureData(
                    converter: _profileService.ActiveProfile.CameraSettings.RawConverter,
                    rawBytes: rawImageData,
                    rawType: "arw",
                    bitDepth: this.BitDepth,
                    metaData: metaData);
            });
        }
        #endregion

        #region Unsupported Methods

        public string Action(string actionName, string actionParameters) {
            throw new NotImplementedException();
        }


        public void SendCommandBlind(string command, bool raw = true) {
            throw new NotImplementedException();
        }

        public bool SendCommandBool(string command, bool raw = true) {
            throw new NotImplementedException();
        }

        public string SendCommandString(string command, bool raw = true) {
            throw new NotImplementedException();
        }

        public void SetBinning(short x, short y) {
            // Ignore
        }
        
        public void UpdateSubSampleArea() {
            if (_camera == null) {
                EnableSubSample = false;
                SubSampleX = 0;
                SubSampleY = 0;
                SubSampleWidth = 0;
                SubSampleHeight = 0;
                return;
            }

            if (EnableSubSample && !CanSubSample) {
                Logger.Warning("Sub-sampling requested but not supported for Sony cameras. Falling back to full frame.");
                EnableSubSample = false;
            }

            // Sony cameras currently expose the entire frame, so always reset to the sensor dimensions.
            SubSampleX = 0;
            SubSampleY = 0;
            SubSampleWidth = _camera.ImageSize.Width;
            SubSampleHeight = _camera.ImageSize.Height;
        }

        #endregion


        // TODO!!! WE NEED ONE
        public bool HasSetupDialog => false;

        public IList<string> SupportedActions => new List<string>();
    }
}
