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

        private readonly PluginOptionsAccessor _pluginSettings;
        private readonly bool _enableNativeCancelDefault;
        private bool _softCancelRequested;

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

        private IReadOnlyList<PropertyValueOption> GetAvailableIsoOptions() {
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
            if (_camera == null) {
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
                    Logger.Debug($"Skipping cancel ({reason}); capture status is {status}.");
                    return false;
                }

                Logger.Info($"Issuing cancel ({reason}); capture status is {status}.");
                driver.CancelCapture(_camera.Handle);
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

        private bool TryGetCaptureStatus(SonyDriver driver, out uint status, string reason) {
            try {
                status = driver.GetCaptureStatus(_camera.Handle);
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
                if (_camera != null) {
                    return (int)GetPropertyValue(PROPID_BATTERY).Value;
                } else {
                    return 0;
                }
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
                if (_camera != null) {
                    try {
                        PropertyValue value = GetPropertyValue(PROPID_ISO);

                        return (int)(value.Value == 0xffffff ? 0 : value.Value);
                    } catch (Exception ex) {
                        Logger.Error("Problem getting gain.", ex);
                        return -1;
                    }
                } else {
                    return -1;
                }
            }

            set {
                if (_camera != null) {
                    try {
                        SonyDriver.GetInstance().SetProperty(_camera.Handle, PROPID_ISO, (uint)value);
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
                return _camera != null;
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
                } catch (Exception ex) {
                    Logger.Error(ex);
                    _camera = null;
                }

                NotifyGainPropertiesChanged();
                return _camera != null;
            });
        }

        public void Disconnect() {
            if (_camera != null) {
                try {
                    SonyDriver.GetInstance().CloseCamera(_camera.Handle);
                } catch (Exception ex) {
                    Logger.Error(ex);
                }

                _camera = null;
                NotifyGainPropertiesChanged();
            }
        }

        public Task<IExposureData> DownloadLiveView(CancellationToken token) {
            return Task.Run<IExposureData>(() => {
                using (var memStream = new MemoryStream(SonyDriver.GetInstance().GetLiveView(_camera.Handle))) {
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
            if (_camera != null) {
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
                }

                // Start capture outside the capture lock to avoid blocking abort/cancel paths if the call hangs.
                driver.StartCapture(_camera.Handle, (float)exposureTime);
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
                    Logger.Info($"Waiting for image to be ready; current state is {captureStatus}; completion states are {String.Join(", ", COMPLETION_STATES)}.");

                    while (!COMPLETION_STATES.Contains(captureStatus)) {
                        await CoreUtil.Wait(TimeSpan.FromMilliseconds(100), token);

                        lock (_captureLock) {
                            if (!TryGetCaptureStatus(driver, out captureStatus, "wait poll") || captureStatus == CAPTURE_STATUS_UNKNOWN) {
                                throw new SonyException("Problem while waiting for image to be ready (status unavailable)");
                            }
                        }
                    }

                    Logger.Info($"Wait for image ready complete; completion state is {captureStatus}.");
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
                byte[] rawImageData = SonyDriver.GetInstance().GetLastImage();

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
