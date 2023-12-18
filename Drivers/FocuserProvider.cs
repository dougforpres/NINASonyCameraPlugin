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
    public class FocuserProvider : IEquipmentProvider<IFocuser> {
        private IProfileService profileService;
        SonyDriver driver;

        [ImportingConstructor]
        public FocuserProvider(IProfileService profileService) {
            this.profileService = profileService;

            if (!DllLoader.IsX86()) {
                try {
                    this.driver = SonyDriver.GetInstance();
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }
        }

        public string Name => "SonyCameraPlugin";

        public IList<IFocuser> GetEquipment() {
            Logger.Info("Asked for a list of focusers");
            var devices = new List<IFocuser>();

            if (this.driver != null) {
                try {
                    int count = 0;

                    foreach (var lens in driver.Lenses()) {
                        count++;
                        devices.Add(new FocuserDriver(profileService, lens));
                    }

                    Logger.Info($"Found {count} Sony Lenses");
                } catch (Exception ex) {
                    Logger.Error(ex);
                }
            }

            return devices;
        }
    }
}
