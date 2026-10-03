using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.RetroKiwi.Plugin.SonyCamera.Drivers;
using Xunit;

[CollectionDefinition("Simulator environment", DisableParallelization = true)]
public class SimulatorEnvironmentCollection { }

[Collection("Simulator environment")]
public class SimulatorDiscoveryTests {
    [Fact]
    public async Task SimulatorAppearsInActualNinaProviderWithoutNativeDll() {
        string previous = Environment.GetEnvironmentVariable("NINA_SONY_SIMULATOR");
        string scenario = Environment.GetEnvironmentVariable("NINA_SONY_SIMULATOR_SCENARIO");
        Environment.SetEnvironmentVariable("NINA_SONY_SIMULATOR", "1");
        Environment.SetEnvironmentVariable("NINA_SONY_SIMULATOR_SCENARIO", null);
        try {
            var profile = new Mock<IProfileService>();
            profile.SetupGet(x => x.ActiveProfile.PluginSettings).Returns(new PluginSettings());
            var factory = new Mock<IExposureDataFactory>();
            var provider = new CameraProvider(profile.Object, factory.Object);
            var camera = Assert.Single(provider.GetEquipment());
            try {
                Assert.Equal("Sony Camera Simulator", camera.DisplayName);
                Assert.True(await camera.Connect(CancellationToken.None));
                camera.StartExposure(new NINA.Equipment.Model.CaptureSequence { ExposureTime = 0.01 });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await camera.WaitUntilExposureIsReady(timeout.Token);
                Assert.Empty(new FocuserProvider(profile.Object).GetEquipment());
                Assert.False(camera.CanShowLiveView);
            } finally {
                camera.Disconnect();
            }
        } finally {
            Environment.SetEnvironmentVariable("NINA_SONY_SIMULATOR", previous);
            Environment.SetEnvironmentVariable("NINA_SONY_SIMULATOR_SCENARIO", scenario);
        }
    }
}
