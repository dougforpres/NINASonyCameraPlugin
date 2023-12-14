using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Drawing;
using NINA.Core.Utility;

namespace Sony {
    public class Size {
        int _width, _height;

        public Size(int width, int height) {
            _width = width;
            _height = height;
        }

        public int Width { get { return _width; } }
        public int Height { get { return _height;} }

        public override string ToString() {
            return $"(width={_width}, height={_height})";
        }
    }

    public class PropertyInfo {
        PropertyDescriptor _descriptor;
        List<PropertyValueOption> _options;

        public PropertyInfo(PropertyDescriptor descriptor, List<PropertyValueOption> options) {
            _descriptor = descriptor;
            _options = options;
        }

        public string Name {
            get {
                return _descriptor.Name;
            }
        }

        public uint Id {
            get {
                return _descriptor.Id;
            }
        }

        public override string ToString() {
            return $"(Id={_descriptor.Id}, Name={_descriptor.Name}, Type={_descriptor.Type})";
        }

        public bool IsEnum() {
            return _descriptor.ValueCount > 0 && _options != null;
        }

        public IEnumerable<PropertyValueOption> Options() {
            if (_options == null) {
                yield break;
            }

            foreach (PropertyValueOption option in _options) {
                yield return option;
            }
        }
    }

    public class SonyCameraInfo {
        private DeviceInfo _info;
        private CameraInfo _cameraInfo;
        private List<PropertyInfo> _descriptors;
        private uint _handle;

        public SonyCameraInfo(uint handle, DeviceInfo info, CameraInfo cameraInfo, List<PropertyInfo> descriptors) {
            _handle = handle;
            _info = info;
            _cameraInfo = cameraInfo;
            _descriptors = descriptors;
            Logger.Trace($"BitsPerPixel for {info.Model} is {info.BitsPerPixel}");
        }

        public uint Handle {
            get {
                return _handle;
            }
        }
        public string Manufacturer {
            get {
                return _info.Manufacturer;
            }
        }

        public string Model {
            get {
                return _info.Model;
            }
        }

        public string Name {
            get {
                return _info.DeviceName;
            }
        }

        public string SensorName {
            get {
                return _info.SensorName;
            }
        }

        public double ExposureMin {
            get {
                return _info.ExposureTimeMin;
            }
        }

        public double ExposureMax {
            get {
                return _info.ExposureTimeMax;
            }
        }

        public int BitsPerPixel {
            get {
                return (int)_info.BitsPerPixel;
            }
        }

        public double PixelWidth {
            get {
                return _info.PixelWidth;
            }
        }

        public double PixelHeight {
            get {
                return _info.PixelHeight;
            }
        }

        public PropertyInfo GetPropertyInfo(uint propertyId) {
            return _descriptors.Find(info => info.Id == propertyId);
        }

        public bool SupportsPreview() {
            return _cameraInfo.PreviewWidthPixels != 0;
        }

        public Size ImageSize {
            get {
                if (_info.CropMode == 0) {
                    return new Size((int)_info.ImageWidthPixels, (int)_info.ImageHeightPixels);
                } else {
                    return new Size((int)_info.ImageWidthCroppedPixels, (int)_info.ImageHeightCroppedPixels);
                }
            }
        }

        public Size PreviewSize {
            get {
                return new Size((int)_cameraInfo.PreviewWidthPixels, (int)_cameraInfo.PreviewHeightPixels);
            }
        }

        public List<PropertyInfo> Descriptors {
            get { return _descriptors; }
        }
    }
}
