using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sony
{
    #region SonyBase
    public class SonyDriver {
        private static SonyDriver _instance = null;

        private SonyMTPCameraDll _sonydll;
        private SonyCameraInfo _camera = null;

        public static SonyDriver GetInstance() {
            if (_instance == null) {
                _instance = new SonyDriver();
            }

            return _instance;
        }

        public SonyDriver() {
            _sonydll = new SonyMTPCameraDll();
        }

        public IEnumerable<SonyDevice> Cameras() {
            uint count = _sonydll.GetSupportedDeviceCount();

            for (uint i = 0; i < count; i++) {
                yield return new SonyDevice(_sonydll.GetSupportedDeviceInfo(i));
            }
        }

        public SonyCameraInfo CameraInfo { get { return _camera; } }

        public SonyCameraInfo OpenCamera(string path) {
            uint hCamera = _sonydll.OpenDevice(path);
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

            return _camera;
        }

        public void CloseCamera(uint handle) {
            _sonydll.CloseDevice(handle);

            _camera = null;
        }

        public PropertyValue GetProperty(uint handle, uint propertyId) {
            return _sonydll.GetSinglePropertyValue(handle, propertyId);
        }

        public void SetProperty(uint handle, uint propertyId, uint value) {
            _sonydll.SetPropertyValue(handle, propertyId, value);
        }

        public byte[] GetLiveView(uint handle) {
            return _sonydll.GetPreviewImage(handle);
        }

        public uint GetCaptureStatus(uint handle) {
            return _sonydll.GetCaptureStatus(handle);
        }

        public void StartCapture(uint handle, float exposureTime) {
            _sonydll.StartCapture(handle, exposureTime);
        }

        public void CancelCapture(uint handle) {
            _sonydll.CancelCapture(handle);
        }

        public byte[] GetLastImage() {
            return _sonydll.LastImage;
        }
        public IEnumerable<SonyLens> Lenses() {
            int count = _sonydll.GetLensCount();

            for (uint i = 0; i < count; i++) {
                yield return new SonyLens(_sonydll.GetLensInfo(i));
            }
        }

        public void SetAttachedLens(uint handle, string path) {
            _sonydll.SetAttachedLens(handle, path);
        }

        public void SetFocusPosition(uint handle, uint position) {
            _sonydll.SetFocusPosition(handle, position);
        }

        public uint GetFocusPosition(uint handle) {
            return _sonydll.GetFocusPosition(handle);
        }

        public uint GetFocusLimit(uint handle) {
            return _sonydll.GetFocusLimit(handle);
        }
    }
    #endregion
}
