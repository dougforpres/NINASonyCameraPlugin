﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
//using System.Reflection.Metadata.Ecma335;
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
using Sony;

namespace NINA.RetroKiwi.Plugin.SonyCameraPlugin.SonyCameraPluginDrivers {
    public class CameraDriver : BaseINPC, ICamera {
        // Some camera settings we are interested in
        private const uint PROPID_BATTERY     = 53784;
        private const uint PROPID_ISO         = 53790;  // Actual ISO currently set
        private const uint PROPID_ISOS        = 65534; // List of learnt ISOs this camera supports

        // Capture Status
        private const uint CAPTURE_CREATED    = 0x0000;
        private const uint CAPTURE_CAPTURING  = 0x0001;
        private const uint CAPTURE_FAILED     = 0x0002;
        private const uint CAPTURE_CANCELLED  = 0x0003;
        private const uint CAPTURE_COMPLETE   = 0x0004;
        private const uint CAPTURE_STARTING   = 0x8001;
        private const uint CAPTURE_READING    = 0x8002;
        private const uint CAPTURE_PROCESSING = 0x8003;

        private SonyCameraInfo _camera = null;
        private SonyDevice _device = null;
        private IProfileService _profileService;
        private readonly IExposureDataFactory _exposureDataFactory;
        private bool _liveViewEnabled;
        private short _readoutModeForSnapImages;
        private short _readoutModeForNormalImages;
        private AsyncObservableCollection<BinningMode> _binningModes;

        public CameraDriver(IProfileService profileService, IExposureDataFactory exposureDataFactory, SonyDevice device) {
            _profileService = profileService;
            _exposureDataFactory = exposureDataFactory;
            _device = device;
        }

        #region Internal Helpers

        private PropertyValue GetPropertyValue(uint id) {
            return SonyDriver.GetInstance().GetProperty(_camera.Handle, id);
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

        public bool CanGetGain {
            get {
                if (_camera != null) {
                    PropertyInfo gainInfo = _camera.GetPropertyInfo(PROPID_ISOS);

                    if (gainInfo != null && gainInfo.Options().Any()) {
                        return true;
                    } else {
                        return false;
                    }
                } else {
                    return false;
                }
            }
        }

        public bool CanSetGain => CanGetGain;

        public int GainMax {
            get {
                if (_camera != null) {
                    PropertyInfo gainInfo = _camera.GetPropertyInfo(PROPID_ISOS);

                    return (int)gainInfo.Options().Last().Value;
                } else {
                    return 0;
                }
            }
        }

        public int GainMin {
            get {
                if (_camera != null) {
                    PropertyInfo gainInfo = _camera.GetPropertyInfo(PROPID_ISOS);

                    return (int)gainInfo.Options().Min(o => o.Value);
                } else {
                    return -1;
                }
            }
        }

        public int Gain {
            get {
                if (_camera != null) {
                    try {
                        PropertyValue value = GetPropertyValue(PROPID_ISO);
                        PropertyInfo gainInfo = _camera.GetPropertyInfo(PROPID_ISOS);

                        return (int)(value.Value == 0xffffff ? 0 : value.Value);
                    } catch (Exception ex) {
                        Logger.Error("Problem getting gain", ex);
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
                    } catch (Exception ex) {
                        Logger.Error($"Problem setting gain to {value}", ex);
                    }
                }
            }
        }

        public IList<int> Gains {
            get {
                List<int> gains = new List<int>();

                if (_camera != null) {
                    PropertyInfo gainInfo = _camera.GetPropertyInfo(PROPID_ISOS);

                    foreach (var iso in gainInfo.Options()) {
                        if (iso.Value == 0xffffff) {
                            // AUTO
                            gains.Add(0);
                        }
                        else {
                            gains.Add((int)iso.Value);
                        }
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
                    _camera = SonyDriver.GetInstance().OpenDevice(_device.Id);
                }
                catch (Exception ex) {
                    Logger.Error(ex);
                    _camera = null;
                }

                return _camera != null;
            });
        }

        public void Disconnect() {
            if (_camera != null) {
                try {
                    SonyDriver.GetInstance().CloseDevice(_camera.Handle);
                } catch (Exception ex) {
                    Logger.Error(ex);
                }

                _camera = null;
            }
        }

        public Task<IExposureData> DownloadLiveView(CancellationToken token) {
            return Task.Run<IExposureData>(() => {
                using (var memStream = new MemoryStream(SonyDriver.GetInstance().GetLiveView(_camera.Handle))) {
                    memStream.Position = 0;

                    JpegBitmapDecoder decoder = new JpegBitmapDecoder(memStream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);

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
                uint captureStatus = driver.GetCaptureStatus(_camera.Handle);

                if (captureStatus == CAPTURE_CAPTURING || captureStatus == CAPTURE_PROCESSING || captureStatus == CAPTURE_STARTING || captureStatus == CAPTURE_READING || captureStatus == CAPTURE_PROCESSING) {
                    Notification.ShowWarning("Another exposure still in progress. Cancelling it to start another.");
                }

                // Tell the camera to cancel capture, we do this every time regardless - this will reset the status to be non-complete
                driver.CancelCapture(_camera.Handle);

                double exposureTime = sequence.ExposureTime;
                driver.StartCapture(_camera.Handle, (float)exposureTime);//);
            }
        }

        public void StopExposure() {
            AbortExposure();
        }

        public void AbortExposure() {
            if (_camera != null) {
                SonyDriver.GetInstance().CancelCapture(_camera.Handle);
            }
        }

        public async Task WaitUntilExposureIsReady(CancellationToken token) {
            using (token.Register(AbortExposure)) {
                uint[] completionStates = { CAPTURE_CANCELLED, CAPTURE_COMPLETE, CAPTURE_FAILED };

                SonyDriver driver = SonyDriver.GetInstance();

                try {
                    uint captureStatus = driver.GetCaptureStatus(_camera.Handle);
                    Logger.Info($"Waiting for image to be ready, current state is {captureStatus}, completion states are {String.Join(", ", completionStates)}");

                    while (!completionStates.Contains(captureStatus)) {
                        await CoreUtil.Wait(TimeSpan.FromMilliseconds(100), token);
                        captureStatus = driver.GetCaptureStatus(_camera.Handle);
                    }

                    Logger.Info($"Wait for image ready complete, completion state is {captureStatus}");
                } catch (Exception ex) {
                    Logger.Error("WaitUntilExposureIsReady got exception", ex);
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

        #endregion


        // TODO!!! WE NEED ONE
        public bool HasSetupDialog => false;

        public IList<string> SupportedActions => new List<string>();
    }
}
