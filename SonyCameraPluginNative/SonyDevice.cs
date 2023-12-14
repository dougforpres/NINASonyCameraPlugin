using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Sony {
    public class SonyDevice {
        private PortableDeviceInfo _deviceInfo;
        public SonyDevice(PortableDeviceInfo info) {
            _deviceInfo = info;
        }

        public string Id {
            get {
                return _deviceInfo.id;
            }
        }

        public string Manufacturer {
            get {
                return _deviceInfo.manufacturer;
            }
        }

        public string Model {
            get {
                return _deviceInfo.model;
            }
        }

        public string Path {
            get {
                return _deviceInfo.devicePath;
            }
        }
    }
}
