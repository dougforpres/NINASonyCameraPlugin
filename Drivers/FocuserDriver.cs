using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
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
        private bool disposedValue;
        internal SonyLens _info;
        internal IProfileService _profileService;
        internal bool _moving = false;
        internal bool _connected = false;

        public FocuserDriver(IProfileService profileService, SonyLens info) {
            _profileService = profileService;
            _info = info;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public bool IsMoving { get => _moving; }

        private uint GetCameraHandle() {
            SonyCameraInfo camera = SonyDriver.GetInstance().CameraInfo;

            if (camera != null) {
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
                    return (int)SonyDriver.GetInstance().GetFocusLimit(hCamera);
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
                    return (int)SonyDriver.GetInstance().GetFocusPosition(hCamera);
                } else {
                    return 0;
                }
            }

            set {
                uint hCamera = GetCameraHandle();

                if (hCamera != 0) {
                    _moving = true;
                    SonyDriver.GetInstance().SetFocusPosition(hCamera, (uint)value);
                    _moving = false;
                }
            }
        }

        public double StepSize { get => 1.0; }

        public bool TempCompAvailable { get => false; }

        public bool TempComp { get => false; set => throw new NotImplementedException(); }

        public double Temperature { get => double.NaN; }

        public bool HasSetupDialog => throw new NotImplementedException();

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
                        _connected = false;
                        _moving = false;
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
            _moving = false;

            return Task<bool>.Run(() => {
                UInt32 hCamera = GetCameraHandle();

                if (hCamera != 0) {
                    _connected = true;

                    _moving = true;
                    SonyDriver.GetInstance().SetAttachedLens(hCamera, Id);
                    _moving = false;
                } else {
                    Notification.ShowWarning(Loc.Instance["LblNoCameraConnected"]);
                }

                return _connected;
            });
        }

        public void Disconnect() {
            _moving = false;
            _connected = false;
        }

        public void Halt() {
            throw new NotImplementedException();
        }

        public Task Move(int position, CancellationToken ct, int waitInMs) {
            UInt32 hCamera = GetCameraHandle();

            if (hCamera != 0) {
                _moving = true;

                return Task.Run(() => {
                    try {
                        SonyDriver.GetInstance().SetFocusPosition(hCamera, (uint)position);
                    } catch (Exception ex) {
                        Logger.Error(ex);
                    }

                    _moving = false;
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
