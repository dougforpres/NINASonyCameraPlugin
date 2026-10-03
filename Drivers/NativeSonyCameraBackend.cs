using System.Collections.Generic;
using Sony;

namespace NINA.RetroKiwi.Plugin.SonyCamera.Drivers {
    internal sealed class NativeSonyCameraBackend : ISonyCameraBackend {
        private readonly SonyDriver driver = SonyDriver.GetInstance();

        public IEnumerable<SonyDevice> Cameras() => driver.Cameras();
        public SonyCameraInfo OpenCamera(string id) => driver.OpenCamera(id);
        public void CloseCamera(uint handle) => driver.CloseCamera(handle);
        public PropertyValue GetProperty(uint handle, uint propertyId) => driver.GetProperty(handle, propertyId);
        public void SetProperty(uint handle, uint propertyId, uint value) => driver.SetProperty(handle, propertyId, value);
        public byte[] GetLiveView(uint handle) => driver.GetLiveView(handle);
        public uint GetCaptureStatus(uint handle) => driver.GetCaptureStatus(handle);
        public void StartCapture(uint handle, float exposureTime) => driver.StartCapture(handle, exposureTime);
        public void CancelCapture(uint handle) => driver.CancelCapture(handle);
        public SonyExposureFrame GetLastImage() => new SonyExposureFrame { RawBytes = driver.GetLastImage() };
    }
}
