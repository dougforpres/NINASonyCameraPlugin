using NINA.Core.Utility;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.Equipment.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel.Composition;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Mediator;
using Sony;

namespace NINA.RetroKiwi.Plugin.SonyCamera.Drivers {
    /// <summary>
    /// This Class shows the basic principle on how to add a new Device driver to N.I.N.A. via the plugin interface
    /// When the application scans for equipment the "GetEquipment" method of a device provider is called.
    /// This method should then return the specific List of Devices that you can connect to
    /// </summary>
    [Export(typeof(IEquipmentProvider))]
    public class CameraProvider : IEquipmentProvider<ICamera> {
        private IProfileService profileService;
        private IExposureDataFactory exposureDataFactory;
        SonyDriver driver;

        [ImportingConstructor]
        public CameraProvider(IProfileService profileService, IExposureDataFactory exposureDataFactory) {
            this.profileService = profileService;
            this.exposureDataFactory = exposureDataFactory;

            if (!DllLoader.IsX86()) {
                try {
                    this.driver = SonyDriver.GetInstance();
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }
        }

        public string Name => "SonyCameraPlugin";

        public IList<ICamera> GetEquipment() {
            var devices = new List<ICamera>();

            if (this.driver != null) {
                try {
                    int count = 0;

                    foreach (var sonyDevice in driver.Cameras()) {
                        count++;
                        devices.Add(new CameraDriver(profileService, exposureDataFactory, sonyDevice));
                    }

                    Logger.Info($"Found {count} Sony Cameras");
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }

            return devices;
        }
    }
}
