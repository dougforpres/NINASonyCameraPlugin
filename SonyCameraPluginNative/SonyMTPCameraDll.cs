using NINA.Core.Utility;
using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Sony {
    #region Kernel32 Imports

    internal class Kernel32 {
        private const string _DLL = "Kernel32.dll";

        [DllImport(_DLL, CharSet = CharSet.Ansi)]
        internal static extern IntPtr LoadLibrary([MarshalAs(UnmanagedType.LPStr)] string lpFileName);

        [DllImport(_DLL, CharSet = CharSet.Ansi)]
        internal static extern Int32 FreeLibrary(IntPtr hLibModule);

        [DllImport(_DLL, CharSet = CharSet.Ansi)]
        internal static extern IntPtr GetProcAddress(IntPtr hModule, [MarshalAs(UnmanagedType.LPStr)] string lpProcName);

        [DllImport(_DLL, CharSet = CharSet.Ansi)]
        internal static extern Int32 GetModuleFileName(IntPtr hModule, [Out] char[] lpFileName, Int32 size);
    }

    #endregion

    internal class SonyException : Exception {
        internal SonyException(string message) : base(message) { }
    }

    #region DLL Class
    internal class SonyMTPCameraDll {
        internal const string MINIMUM_DLL_VERSION_STRING = "1.0.1.17";
        internal readonly int[] MINIMUM_DLL_VERSION = { 1, 0, 1, 17 };

        internal const UInt32 ERROR_SUCCESS = 0;
        internal const UInt32 ERROR_RETRY = 1237;
        internal const UInt32 INVALID_HANDLE_VALUE = 0xffffffff;

        internal const UInt32 IMAGEMODE_RAW = 1;
        internal const UInt32 IMAGEMODE_RGB = 2;
        internal const UInt32 IMAGEMODE_JPEG = 3;
        internal const UInt32 IMAGEMODE_PASSTHRU = 256;

        internal const UInt32 STATUS_EXPOSING = 0x01;
        internal const UInt32 STATUS_FAILED = 0x02;
        internal const UInt32 STATUS_CANCELLED = 0x03;
        internal const UInt32 STATUS_COMPLETE = 0x04;
        internal const UInt32 STATUS_STARTING = 0x8001;
        internal const UInt32 STATUS_READING = 0x8002;

        private IntPtr _dllHandle;
        private string _dllName = "SonyMTPCamera.dll";
        private byte[] _lastImage = null;

#pragma warning disable 0649
        private dGetSupportedDeviceCount fGetSupportedDeviceCount;
        private dGetSupportedDeviceInfo fGetSupportedDeviceInfo;
        private dGetDeviceCount fGetDeviceCount;
        private dGetDeviceInfo fGetDeviceInfo;
        private dOpenDevice fOpenDevice;
        private dCloseDevice fCloseDevice;
        private dGetPropertyList fGetPropertyList;
        private dGetPropertyDescriptor fGetPropertyDescriptor;
        private dGetPropertyValueOption fGetPropertyValueOption;
        private dGetSinglePropertyValue fGetSinglePropertyValue;
        private dSetPropertyValue fSetPropertyValue;
        private dGetCameraInfo fGetCameraInfo;
        private dGetPreviewImage fGetPreviewImage;
        private dStartCapture fStartCapture;
        private dGetCaptureStatus fGetCaptureStatus;
        private dCancelCapture fCancelCapture;
        private dSetExposureTime fSetExposureTime;

        // Focus
        private dGetLensCount fGetLensCount;
        private dGetLensInfo fGetLensInfo;
        private dSetAttachedLens fSetAttachedLens;
        private dSetFocusPosition fSetFocusPosition;
        private dGetFocusPosition fGetFocusPosition;
        private dGetFocusLimit fGetFocusLimit;
#pragma warning restore 0649

        private string[] DllFunctions = {
            "GetSupportedDeviceCount", "GetSupportedDeviceInfo",
            "GetDeviceCount", "GetDeviceInfo",
            "OpenDevice", "CloseDevice",
            "GetPropertyList", "GetPropertyDescriptor", "GetPropertyValueOption",
            "GetSinglePropertyValue", "SetPropertyValue",
            "GetCameraInfo",
            "GetPreviewImage",
            "StartCapture", "GetCaptureStatus", "CancelCapture",
            "SetExposureTime",
            "GetLensCount", "GetLensInfo", "SetAttachedLens",
            "SetFocusPosition", "GetFocusPosition", "GetFocusLimit",
        };

        internal SonyMTPCameraDll() {
            _dllHandle = Kernel32.LoadLibrary(_dllName);

            if (_dllHandle == IntPtr.Zero) {
                throw new SonyException("Failed to load Sony Driver DLL '" + _dllName + "'");
            }

            char[] buffer = ArrayPool<char>.Shared.Rent(256 + 1);
            int l = Kernel32.GetModuleFileName(_dllHandle, buffer, 256);
            string filename = new string(buffer, 0, l);

            // Confirm the driver version meets the minimum required 1.0.1.16
            FileVersionInfo dllVersion = FileVersionInfo.GetVersionInfo(filename);

            int[] v = { dllVersion.ProductMajorPart, dllVersion.ProductMinorPart, dllVersion.ProductPrivatePart, dllVersion.ProductBuildPart };

            if (dllVersion == null) {
                throw new SonyException("The loaded Sony dll does not have version info - this is invalid");
            }

            Logger.Info($"Using Sony driver dll @ {filename} with version {dllVersion.ProductVersion}");

            if (CompareVersions(v, MINIMUM_DLL_VERSION) < 0) {
                throw new SonyException($"The installed driver is version {dllVersion.ProductVersion}, version {MINIMUM_DLL_VERSION_STRING} or later is required");
            }


            // Populate function pointers
            foreach (var functionName in DllFunctions) {
                SetupDelegate(functionName);
            }
        }

        internal int CompareVersions(int[] lhs, int[] rhs) {
            if (lhs[0] == rhs[0] && lhs[1] == rhs[1] && lhs[2] == rhs[2] && lhs[3] == rhs[3]) {
                return 0;
            }

            if (lhs[0] < rhs[0]) {
                return -1;
            }

            if (lhs[0] == rhs[0] && lhs[1] < rhs[1]) {
                return -1;
            }

            if (lhs[0] == rhs[0] && lhs[1] == rhs[1] && lhs[2] < rhs[2]) {
                return -1;
            }

            if (lhs[0] == rhs[0] && lhs[1] == rhs[1] && lhs[2] == rhs[2] && lhs[3] < rhs[3]) {
                return -1;
            }

            return 1;
        }

        internal void SetupDelegate(string functionName) {
            string delegateName = "d" + functionName;
            string propertyName = "f" + functionName;
            Type delegateType = GetType().GetNestedType(delegateName, BindingFlags.NonPublic);
            FieldInfo field = GetType().GetField(propertyName, BindingFlags.NonPublic | BindingFlags.Instance);
            Delegate d = Marshal.GetDelegateForFunctionPointer(GetExportedFunction(_dllHandle, functionName), delegateType);

            field.SetValue(this, d);
        }

        internal byte[] LastImage {
            get {
                return _lastImage;
            }
        }

        internal IntPtr GetExportedFunction(IntPtr hDll, string name) {
            IntPtr methodPointer = Kernel32.GetProcAddress(_dllHandle, name);

            if (methodPointer == IntPtr.Zero) {
                throw new SonyException("Unable to locate method '" + name + "'");
            }

            return methodPointer;
        }

        #region DLL Functions

        public UInt32 GetSupportedDeviceCount() {
            return fGetSupportedDeviceCount();
        }

        public PortableDeviceInfo GetSupportedDeviceInfo(UInt32 id) {
            PortableDeviceInfo info = new PortableDeviceInfo();

            uint hr = fGetSupportedDeviceInfo(id, ref info);

            if (hr != ERROR_SUCCESS) {
                throw new SonyException($"Unable to get portable device info for id {id}, error {hr}");
            }

            return info;
        }

        public UInt32 GetDeviceCount() {
            return fGetDeviceCount();
        }

        public DeviceInfo GetDeviceInfo(uint hDevice) {
            DeviceInfo info = new DeviceInfo {
                Version = 1
            };

            uint hr = fGetDeviceInfo(hDevice, ref info);

            if (hr != ERROR_SUCCESS) {
                throw new SonyException($"Unable to get device info for handle {hDevice}, error {hr}");
            }

            return info;
        }

        public UInt32 OpenDevice(string DeviceName) {
            return fOpenDevice(DeviceName);
        }

        public void CloseDevice(UInt32 hDevice) {
            fCloseDevice(hDevice);
        }

        public byte[] GetPreviewImage(UInt32 hDevice) {
            ImageInfo info = new ImageInfo {
                ImageMode = IMAGEMODE_JPEG
            };
            uint hr = fGetPreviewImage(hDevice, ref info);

            if (hr != ERROR_SUCCESS) {
                throw new SonyException($"Unable to get preview image, error {hr}");
            }

            byte[] returndata = new byte[info.ImageSize];

            if (returndata == null) {
                throw new SonyException("Unable to allocate memory for image");
            }

            Marshal.Copy(info.ImageData, returndata, 0, (Int32)info.ImageSize);

            return returndata;
        }

        public UInt32 StartCapture(UInt32 hDevice, float exposureTime) {
            PropertyValue value = new PropertyValue();

            fSetExposureTime(hDevice, exposureTime, ref value);

            ImageInfo info = new ImageInfo {
                ImageMode = IMAGEMODE_PASSTHRU,
                ExposureTime = (double)exposureTime,
            };

            if (fStartCapture(hDevice, ref info) != ERROR_SUCCESS) {
                throw new SonyException("Problem starting capture");
            }

            return info.Status;
        }

        public void CancelCapture(UInt32 hDevice) {
            ImageInfo Info = new ImageInfo();

            fCancelCapture(hDevice, ref Info);
        }

        public UInt32 GetCaptureStatus(UInt32 hDevice) {
            ImageInfo info = new ImageInfo();

            if (fGetCaptureStatus(hDevice, ref info) != ERROR_SUCCESS) {
                throw new SonyException("Unable to get image status");
            }

            // If the capture is complete, copy the image
            if (info.Status == STATUS_COMPLETE) {
                _lastImage = new byte[info.ImageSize];

                if (_lastImage == null) {
                    throw new SonyException("Unable to allocate memory for image");
                }

                Marshal.Copy(info.ImageData, _lastImage, 0, (Int32)info.ImageSize);
            }

            return info.Status;
        }

        public CameraInfo GetCameraInfo(UInt32 hDevice, UInt32 Flags) {
            CameraInfo info = new CameraInfo();

            if (fGetCameraInfo(hDevice, ref info, Flags) != ERROR_SUCCESS) {
                throw new SonyException("Error retrieving camera info");
            }

            return info;
        }

        public int[] GetPropertyList(UInt32 hCamera) {
            UInt32 listSize = 0;
            IntPtr listPtr = IntPtr.Zero;

            if (fGetPropertyList(hCamera, listPtr, ref listSize) != ERROR_RETRY) {
                throw new SonyException("Problem getting size of property list");
            }

            int[] ids = new int[listSize];
            IntPtr pIds = Marshal.AllocCoTaskMem((int)listSize * sizeof(UInt32));

            if (fGetPropertyList(hCamera, pIds, ref listSize) != ERROR_SUCCESS) {
                throw new SonyException("Problem retrieving list of property ids");
            }

            Marshal.Copy(pIds, ids, 0, (int)listSize);
            Marshal.FreeCoTaskMem(pIds);

            return ids;
        }

        public PropertyDescriptor GetPropertyDescriptor(UInt32 hCamera, UInt32 propertyId) {
            PropertyDescriptor descriptor = new PropertyDescriptor();

            if (fGetPropertyDescriptor(hCamera, propertyId, ref descriptor) != ERROR_SUCCESS) {
                throw new SonyException("Problem getting property descriptor for property id " + propertyId.ToString());
            }

            return descriptor;
        }

        public List<PropertyDescriptor> GetPropertyDescriptors(uint hCamera) {
            int[] ids = GetPropertyList(hCamera);
            List<PropertyDescriptor> descriptors = new List<PropertyDescriptor>();

            foreach (int id in ids) {
                descriptors.Add(GetPropertyDescriptor(hCamera, (uint)id));
            }

            return descriptors;
        }

        public PropertyValueOption GetPropertyValueOption(UInt32 hCamera, UInt32 propertyId, UInt32 index) {
            PropertyValueOption option = new PropertyValueOption();

            if (fGetPropertyValueOption(hCamera, propertyId, ref option, index) != ERROR_SUCCESS) {
                throw new SonyException("Problem getting option " + index.ToString() + " for property " + propertyId.ToString());
            }

            return option;
        }

        public List<PropertyValueOption> GetPropertyValueOptions(UInt32 hCamera, PropertyDescriptor descriptor) {
            List<PropertyValueOption> options = new List<PropertyValueOption>();

            for (int i = 0; i < descriptor.ValueCount; i++) {
                options.Add(GetPropertyValueOption(hCamera, descriptor.Id, (uint)i));
            }

            return options;
        }

        public PropertyValue GetSinglePropertyValue(UInt32 hCamera, UInt32 propertyId) {
            PropertyValue option = new PropertyValue();

            if (fGetSinglePropertyValue(hCamera, propertyId, ref option) != ERROR_SUCCESS) {
                throw new SonyException("Problem getting property value for " + propertyId.ToString());
            }

            return option;
        }

        public PropertyValue SetExposureTime(UInt32 hCamera, float exposureTime) {
            PropertyValue valueOut = new PropertyValue();

            if (fSetExposureTime(hCamera, exposureTime, ref valueOut) != ERROR_SUCCESS) {
                throw new SonyException("Error setting exposure time");
            }

            return valueOut;
        }

        public void SetPropertyValue(UInt32 hCamera, UInt32 propertyId, UInt32 value) {
            if (fSetPropertyValue(hCamera, propertyId, value) != ERROR_SUCCESS) {
                throw new SonyException("Problem setting property " + propertyId.ToString() + " to " + value.ToString());
            }
        }

        public int GetLensCount() {
            return (int)fGetLensCount();
        }

        public LensInfo GetLensInfo(uint index) {
            LensInfo info = new LensInfo();

            uint hr = fGetLensInfo(index, ref info);

            if (hr != ERROR_SUCCESS) {
                throw new SonyException($"Unable to get lens info for id {index}, error {hr}");
            }

            return info;
        }

        public void SetAttachedLens(UInt32 hCamera, string lensId) {
            if (fSetAttachedLens(hCamera, lensId) != ERROR_SUCCESS) {
                throw new SonyException($"Problem setting attached lens '{lensId}'.");
            }
        }

        public UInt32 GetFocusPosition(UInt32 hCamera) {
            return fGetFocusPosition(hCamera);
        }

        public void SetFocusPosition(UInt32 hCamera, UInt32 focusPosition) {
//            Logger.Info($"Request to set focus position to {focusPosition} for hCamera {hCamera:x}");
            UInt32 hr = fSetFocusPosition(hCamera, ref focusPosition);

            if (hr != ERROR_SUCCESS) {
                throw new SonyException($"Problem setting focus position to {focusPosition}, error {hr}");
            }
        }

        public UInt32 GetFocusLimit(UInt32 hCamera) {
            return fGetFocusLimit(hCamera);
        }
        #endregion

        #region Delegate definitions
        internal delegate UInt32 dGetSupportedDeviceCount();
        internal delegate UInt32 dGetSupportedDeviceInfo(UInt32 id, ref PortableDeviceInfo info);

        internal delegate UInt32 dGetDeviceCount();
        internal delegate UInt32 dGetDeviceInfo(UInt32 id, ref DeviceInfo info);

        internal delegate UInt32 dOpenDevice([MarshalAs(UnmanagedType.LPWStr)] string deviceName);
        internal delegate UInt32 dCloseDevice(UInt32 id);

        internal delegate UInt32 dGetPropertyList(UInt32 hCamera, IntPtr list, ref UInt32 listSize);
        internal delegate UInt32 dGetPropertyDescriptor(UInt32 hCamera, UInt32 propertyId, ref PropertyDescriptor descriptor);
        internal delegate UInt32 dGetPropertyValueOption(UInt32 hCamera, UInt32 propertyId, ref PropertyValueOption option, UInt32 index);
        internal delegate UInt32 dGetSinglePropertyValue(UInt32 hCamera, UInt32 propertyId, ref PropertyValue value);
        internal delegate UInt32 dGetAllPropertyValues(UInt32 hCamera, ref PropertyValue[] values, ref UInt32 count);
        internal delegate UInt32 dSetExposureTime(UInt32 hCamera, float exposureTime, ref PropertyValue valueOut);
        internal delegate UInt32 dSetPropertyValue(UInt32 hCamera, UInt32 propertyId, UInt32 value);
        internal delegate UInt32 dGetPreviewImage(UInt32 hCamera, ref ImageInfo Data);
        internal delegate UInt32 dStartCapture(UInt32 hCamera, ref ImageInfo Data);
        internal delegate UInt32 dCancelCapture(UInt32 hCamera, ref ImageInfo Data);
        internal delegate UInt32 dGetCaptureStatus(UInt32 hCamera, ref ImageInfo Data);
        internal delegate UInt32 dGetCameraInfo(UInt32 hCamera, ref CameraInfo Data, UInt32 Flags);

        // Focus
        internal delegate UInt32 dSetFocusPosition(UInt32 hCamera, ref UInt32 position);
        internal delegate UInt32 dGetFocusPosition(UInt32 hCamera);
        internal delegate UInt32 dGetFocusLimit(UInt32 hCamera);

        internal delegate UInt32 dGetLensCount();
        internal delegate UInt32 dGetLensInfo(UInt32 offset, ref LensInfo info);
        internal delegate UInt32 dSetAttachedLens(UInt32 hCamera, [MarshalAs(UnmanagedType.LPWStr)] string lensId);
        #endregion
}
#endregion
}