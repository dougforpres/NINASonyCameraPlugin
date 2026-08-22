using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Castle.Components.DictionaryAdapter.Xml;
using FTD2XX_NET;
using Google.Protobuf.WellKnownTypes;
using Newtonsoft.Json.Linq;
using NINA.Core.Enum;
using NINA.Core.Locale;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Equipment.MyGuider.PHD2;
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

namespace NINA.RetroKiwi.Plugin.SonyCamera.Drivers {
    public class FocuserDriver : IFocuser, IDisposable {
        // NINA polls Position/MaxIncrement on the focuser's device-update timer,
        // and the focuser shares its device handle with the camera. Rather than
        // let a poll queue up behind a 25MB image download, give up quickly and
        // report the last known value.
        private const int POLL_READ_TIMEOUT_MS = 500;

        private bool disposedValue;
        internal SonyLens _info;
        internal IProfileService _profileService;
        internal bool _moving = false;
        internal bool _connected = false;
        private int _lastKnownPosition = 0;
        private int _lastKnownMaxIncrement = 0;

        public FocuserDriver(IProfileService profileService, SonyLens info) {
            _profileService = profileService;
            _info = info;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool Moving {
            get => _moving;
            set {
                if (_moving != value) {
                    _moving = value;
                    RaisePropertyChanged(nameof(IsMoving));
                }
            }
        }

        private bool IsConnected {
            get => _connected;
            set {
                if (_connected != value) {
                    _connected = value;
                    RaisePropertyChanged(nameof(Connected));
                }
            }
        }

        public bool IsMoving { get => _moving; }

        private uint GetCameraHandle() {
            SonyDriver driver = SonyDriver.GetInstance();
            SonyCameraInfo camera = driver.CameraInfo;

            // Also confirm the handle is still open: the driver drops it when the
            // camera stops responding, and the focuser must not keep issuing
            // focus commands against a handle the camera side has abandoned.
            if (camera != null && driver.IsOpen(camera.Handle)) {
                return camera.Handle;
            }
            else
            {
                return 0;
            }
        }

        public int MaxIncrement {
            get {
                uint hCamera = GetCameraHandle();

                if (hCamera != 0) {
                    try {
                        if (SonyDriver.GetInstance().TryGetFocusLimit(hCamera, POLL_READ_TIMEOUT_MS, out uint limit)) {
                            _lastKnownMaxIncrement = (int)limit;
                        }
                    } catch (Exception ex) {
                        Logger.Debug($"Unable to read focus limit: {ex.Message}");
                    }

                    return _lastKnownMaxIncrement;
                }
                else {
                    return 0;
                }
            }
        }

        public int MaxStep { get => MaxIncrement; }

        public int Position {
            get {
                uint hCamera = GetCameraHandle();

                if (hCamera != 0) {
                    try {
                        if (SonyDriver.GetInstance().TryGetFocusPosition(hCamera, POLL_READ_TIMEOUT_MS, out uint position)) {
                            _lastKnownPosition = (int)position;
                        }
                    } catch (Exception ex) {
                        Logger.Debug($"Unable to read focus position: {ex.Message}");
                    }

                    return _lastKnownPosition;
                } else {
                    return 0;
                }
            }

            set {
                uint hCamera = GetCameraHandle();

                if (hCamera != 0) {
                    Moving = true;

                    try {
                        SonyDriver.GetInstance().SetFocusPosition(hCamera, (uint)value);
                        _lastKnownPosition = value;
                    } catch (Exception ex) {
                        Logger.Error($"Problem moving focus to {value}", ex);
                    } finally {
                        Moving = false;
                    }
                }
            }
        }

        public double StepSize { get => 1.0; }

        public bool TempCompAvailable { get => false; }

        public bool TempComp { get => false; set { } }

        public double Temperature { get => double.NaN; }

        public bool HasSetupDialog => false;

        public string Id { get => _info.Id; }

        public string Name { get => $"{_info.Manufacturer} {_info.Model}"; }

        public string DisplayName { get => $"{_info.Manufacturer} {_info.Model}"; }

        public string Category { get => "Sony"; }

        public bool Connected {
            get {
                // Check that the camera is still open, if it is closed, then we are also closed
                if (_connected) {
                    UInt32 hCamera = GetCameraHandle();

                    if (hCamera == 0) {
                        Logger.Debug("Sony camera was disconnected without closing focuser first, cleaning up");
                        IsConnected = false;
                        Moving = false;
                    }
                }

                return _connected;
            }
        }

        public string Description { get => _info.Model; }

        public string DriverInfo => "https://retro.kiwi";

        public string DriverVersion => string.Empty;

        public IList<string> SupportedActions => new List<string>();

        public string Action(string actionName, string actionParameters) {
            throw new NotImplementedException();
        }

        public Task<bool> Connect(CancellationToken token) {
            Moving = false;

            return Task<bool>.Run(() => {
                UInt32 hCamera = GetCameraHandle();

                if (hCamera != 0) {
                    Moving = true;

                    try {
                        SonyDriver.GetInstance().SetAttachedLens(hCamera, Id);
                        IsConnected = true;
                    } catch (Exception ex) {
                        Logger.Error($"Unable to attach lens '{Id}'", ex);
                        Notification.ShowError($"Unable to attach lens '{Id}': {ex.Message}");
                    } finally {
                        Moving = false;
                    }
                } else {
                    Notification.ShowWarning(Loc.Instance["LblNoCameraConnected"]);
                }

                return _connected;
            });
        }

        public void Disconnect() {
            Moving = false;
            IsConnected = false;
        }

        public void Halt() {
            throw new NotImplementedException();
        }

        public Task Move(int position, CancellationToken ct, int waitInMs) {
            UInt32 hCamera = GetCameraHandle();

            if (hCamera != 0) {
                Moving = true;

                return Task.Run(() => {
                    try {
                        SonyDriver.GetInstance().SetFocusPosition(hCamera, (uint)position);
                    } catch (Exception ex) {
                        Logger.Error(ex);
                    }

                    Moving = false;
                });
            }
            else
            {
                return Task.CompletedTask;
            }
        }

        public void SendCommandBlind(string command, bool raw) {
            throw new NotImplementedException();
        }

        public bool SendCommandBool(string command, bool raw) {
            throw new NotImplementedException();
        }

        public string SendCommandString(string command, bool raw) {
            throw new NotImplementedException();
        }

        public void SetupDialog() {
            throw new NotImplementedException();
        }

        protected virtual void Dispose(bool disposing) {
            if (!disposedValue) {
                if (disposing) {
                    // TODO: dispose managed state (managed objects)
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        public void Dispose() {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
