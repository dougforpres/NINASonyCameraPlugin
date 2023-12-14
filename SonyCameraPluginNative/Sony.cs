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

        public static SonyDriver GetInstance() {
            if (_instance == null) {
                _instance = new SonyDriver();
            }

            return _instance;
        }

        public SonyDriver() {
            _sonydll = new SonyMTPCameraDll();
        }

        public IEnumerable<SonyDevice> Devices() {
            uint count = _sonydll.GetSupportedDeviceCount();

            for (uint i = 0; i < count; i++) {
                yield return new SonyDevice(_sonydll.GetSupportedDeviceInfo(i));
            }
        }

        public SonyCameraInfo OpenDevice(string path) {
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

            return new SonyCameraInfo(hCamera, _sonydll.GetDeviceInfo(hCamera), _sonydll.GetCameraInfo(hCamera, 0), properties);
        }

        public void CloseDevice(uint handle) {
            _sonydll.CloseDevice(handle);
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
    }
    #endregion
}
