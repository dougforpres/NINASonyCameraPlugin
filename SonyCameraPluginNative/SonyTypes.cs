using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Sony {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PortableDeviceInfo {
        public string id;
        public string manufacturer;
        public string model;
        public string devicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DeviceInfo {
        public UInt32 Version;
        public UInt32 ImageWidthPixels;
        public UInt32 ImageHeightPixels;
        public UInt32 ImageWidthCroppedPixels;
        public UInt32 ImageHeightCroppedPixels;
        public UInt32 BayerXOffset;
        public UInt32 BayerYOffset;
        public UInt32 CropMode;
        public Double ExposureTimeMin;
        public Double ExposureTimeMax;
        public Double ExposureTimeStep;
        public Double PixelWidth;
        public Double PixelHeight;
        public UInt32 BitsPerPixel;

        public string Manufacturer;
        public string Model;
        public string SerialNumber;
        public string DeviceName;
        public string SensorName;
        public string DeviceVersion;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ImageInfo {
        public UInt32 ImageSize;
        public IntPtr ImageData;
        public UInt32 Status;
        public UInt32 ImageMode;
        public UInt32 Width;
        public UInt32 Height;
        public UInt32 Flags;
        public Double ExposureTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CameraInfo {
        public UInt32 CameraFlags;
        public UInt32 ImageWidthPixels;
        public UInt32 ImageHeightPixels;
        public UInt32 ImageWidthCroppedPixels;
        public UInt32 ImageHeightCroppedPixels;
        public UInt32 PreviewWidthPixels;
        public UInt32 PreviewHeightPixels;
        public UInt32 BayerXOffset;
        public UInt32 BayerYOffset;
        public Double PixelWidth;
        public Double PixelHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PropertyValueOption {
        public UInt32 Value;
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PropertyValue {
        public UInt32 Id;
        public UInt32 Value;
        public string Text;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PropertyDescriptor {
        public UInt32 Id;
        public UInt16 Type;
        public UInt16 Flags;
        public string Name;
        public UInt32 ValueCount;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LensInfo {
        public string Id;
        public string Manufacturer;
        public string Model;
        public string LensPath;
    }
}
