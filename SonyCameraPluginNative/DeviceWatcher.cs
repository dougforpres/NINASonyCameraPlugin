using NINA.Core.Utility;
using System;
using System.Runtime.InteropServices;

namespace Sony {
    /// <summary>
    /// Tells us the moment the camera's USB device disappears from Windows, and
    /// again when it comes back.
    /// <para>
    /// Without this the only way to notice a disconnect is to make a call into
    /// SonyMTPCamera.dll and have it fail - and while the camera sits idle the
    /// next call may be up to two seconds away. That is two seconds in which
    /// the driver can be handed a device that no longer exists, which is how
    /// the access violation that takes NINA down gets hit. Windows knows within
    /// milliseconds; this just asks it.
    /// </para>
    /// <para>
    /// Uses CM_Register_Notification (Windows 8 and later), which delivers to a
    /// callback on a system thread and needs no window or message pump.
    /// </para>
    /// </summary>
    internal sealed class DeviceWatcher : IDisposable {
        /// <summary>GUID_DEVINTERFACE_WPD - the interface class Sony cameras appear under.</summary>
        private static readonly Guid WPD_DEVICE_INTERFACE = new Guid("6AC27878-A6FA-4155-BA85-F98F491D4F33");

        private const uint CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE = 0;
        private const int CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL = 0;
        private const int CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL = 1;
        private const int CR_SUCCESS = 0;

        /// <summary>Offset of SymbolicLink within CM_NOTIFY_EVENT_DATA for an interface event: FilterType (4) + Reserved (4) + ClassGuid (16).</summary>
        private const int SYMBOLIC_LINK_OFFSET = 24;

        private readonly string _deviceId;
        private readonly Action _onRemoved;
        private readonly Action _onReturned;

        // Held in a field so the GC cannot collect the delegate while native
        // code still holds a pointer to it.
        private readonly CmNotifyCallback _callback;

        private IntPtr _registration = IntPtr.Zero;
        private bool _disposed;

        private DeviceWatcher(string deviceId, Action onRemoved, Action onReturned) {
            _deviceId = deviceId;
            _onRemoved = onRemoved;
            _onReturned = onReturned;
            _callback = OnDeviceNotification;
        }

        /// <summary>
        /// Starts watching, or returns null if Windows would not register the
        /// notification. A null return is not fatal - it just means we are back
        /// to noticing a disconnect when a call fails.
        /// </summary>
        internal static DeviceWatcher TryStart(string deviceId, Action onRemoved, Action onReturned) {
            if (string.IsNullOrEmpty(deviceId)) {
                return null;
            }

            var watcher = new DeviceWatcher(deviceId, onRemoved, onReturned);

            try {
                var filter = new CM_NOTIFY_FILTER {
                    cbSize = (uint)Marshal.SizeOf<CM_NOTIFY_FILTER>(),
                    Flags = 0,
                    FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINTERFACE,
                    Reserved = 0,
                    Union = new byte[UNION_SIZE]
                };

                // The DeviceInterface arm of the union is just the class GUID,
                // at the start of the union.
                WPD_DEVICE_INTERFACE.ToByteArray().CopyTo(filter.Union, 0);

                int result = CM_Register_Notification(ref filter, IntPtr.Zero, watcher._callback, out IntPtr registration);

                if (result != CR_SUCCESS) {
                    Logger.Warning($"Sony: could not subscribe to USB device notifications (CM_Register_Notification returned {result}). A disconnect will only be noticed when a call to the camera fails.");
                    return null;
                }

                watcher._registration = registration;
                Logger.Info($"Sony: watching for removal of '{deviceId}'.");

                return watcher;
            } catch (Exception ex) {
                Logger.Warning($"Sony: could not subscribe to USB device notifications: {ex.Message}. A disconnect will only be noticed when a call to the camera fails.");
                return null;
            }
        }

        private int OnDeviceNotification(IntPtr notification, IntPtr context, int action, IntPtr eventData, int eventDataSize) {
            // Runs on a system thread. Do as little as possible here, never
            // throw, and never call into SonyMTPCamera.dll - the device this is
            // reporting on may be mid-removal.
            try {
                if (action != CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL && action != CM_NOTIFY_ACTION_DEVICEINTERFACEARRIVAL) {
                    return CR_SUCCESS;
                }

                if (eventData == IntPtr.Zero || eventDataSize <= SYMBOLIC_LINK_OFFSET) {
                    return CR_SUCCESS;
                }

                string symbolicLink = Marshal.PtrToStringUni(eventData + SYMBOLIC_LINK_OFFSET);

                if (!IsSameDevice(symbolicLink, _deviceId)) {
                    return CR_SUCCESS;
                }

                if (action == CM_NOTIFY_ACTION_DEVICEINTERFACEREMOVAL) {
                    Logger.Warning("Sony: Windows reports the camera has been removed from USB.");
                    _onRemoved?.Invoke();
                } else {
                    Logger.Info("Sony: Windows reports the camera is back on USB.");
                    _onReturned?.Invoke();
                }
            } catch (Exception ex) {
                // A throw here would cross back into native code, which is
                // undefined behaviour. Swallow it.
                try {
                    Logger.Error("Sony: device notification handler failed.", ex);
                } catch {
                    // Nothing sensible left to do.
                }
            }

            return CR_SUCCESS;
        }

        /// <summary>
        /// Compares a notification's symbolic link against the device id the
        /// driver gave us. They are normally identical, but the prefix and case
        /// are not guaranteed to match, and the trailing interface GUID is not
        /// needed to identify the device.
        /// </summary>
        internal static bool IsSameDevice(string symbolicLink, string deviceId) {
            string left = Normalise(symbolicLink);
            string right = Normalise(deviceId);

            if (left.Length == 0 || right.Length == 0) {
                return false;
            }

            return left == right || StripInterfaceGuid(left) == StripInterfaceGuid(right);
        }

        private static string Normalise(string value) {
            if (string.IsNullOrEmpty(value)) {
                return string.Empty;
            }

            string result = value.Trim().ToLowerInvariant();

            if (result.StartsWith(@"\\?\") || result.StartsWith(@"\\.\")) {
                result = result.Substring(4);
            }

            return result;
        }

        private static string StripInterfaceGuid(string value) {
            int guidStart = value.LastIndexOf("#{", StringComparison.Ordinal);

            return guidStart > 0 ? value.Substring(0, guidStart) : value;
        }

        public void Dispose() {
            if (_disposed) {
                return;
            }

            _disposed = true;

            if (_registration != IntPtr.Zero) {
                try {
                    CM_Unregister_Notification(_registration);
                } catch (Exception ex) {
                    Logger.Warning($"Sony: failed to unsubscribe from USB device notifications: {ex.Message}");
                }

                _registration = IntPtr.Zero;
            }
        }

        #region cfgmgr32

        /// <summary>Size of the CM_NOTIFY_FILTER union: the largest arm is DeviceInstance, WCHAR[MAX_DEVICE_ID_LEN].</summary>
        private const int UNION_SIZE = 200 * 2;

        private delegate int CmNotifyCallback(IntPtr notification, IntPtr context, int action, IntPtr eventData, int eventDataSize);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CM_NOTIFY_FILTER {
            public uint cbSize;
            public uint Flags;
            public uint FilterType;
            public uint Reserved;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = UNION_SIZE)]
            public byte[] Union;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Register_Notification(ref CM_NOTIFY_FILTER pFilter, IntPtr pContext, CmNotifyCallback pCallback, out IntPtr pNotifyContext);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Unregister_Notification(IntPtr NotifyContext);

        #endregion
    }
}
