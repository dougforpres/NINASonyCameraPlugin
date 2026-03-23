using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.RetroKiwi.Plugin.SonyCamera {
    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "Sony Video Record")]
    [ExportMetadata("Description", "Starts video recording on a Sony camera for a set duration, then stops it. Blocks the sequencer during recording.")]
    [ExportMetadata("Icon", "VideoRecordSVG")] // optional – you can add an SVG later
    [ExportMetadata("Category", "Sony Camera")]
    public class SonyVideoRecordInstruction : SequenceItem {
        private readonly ICameraMediator cameraMediator;
        private readonly IProfileService profileService;

        [ImportingConstructor]
        public SonyVideoRecordInstruction(ICameraMediator cameraMediator, IProfileService profileService) {
            this.cameraMediator = cameraMediator;
            this.profileService = profileService;

            Duration = TimeSpan.FromSeconds(30); // default
        }

        // UI-bindable property (shown in sequencer editor)
        public TimeSpan Duration { get; set; }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Safety check – make sure a Sony camera is connected
            var camInfo = cameraMediator.GetInfo();
            if (!camInfo.IsConnected || !camInfo.Name.ToLower().Contains("sony")) {
                throw new InvalidOperationException("Sony camera must be connected and selected.");
            }

            var status = new ApplicationStatus { Source = "Sony Video Record", Status = "Starting video recording..." };
            progress.Report(status);

            // TODO: These two calls will be added to the Sony camera wrapper (see step 3 below)
            GetSonyCamera().StartVideoRecording();
            status.Status = $"Recording for {Duration.TotalSeconds}s...";
            progress.Report(status);

            // Block the sequencer exactly for the requested duration
            // (this is what lets you put dithering/meridian-flip steps after this one)
            token.WaitHandle.WaitOne(Duration);

            status.Status = "Stopping video recording...";
            progress.Report(status);

            GetSonyCamera().StopVideoRecording();

            return Task.CompletedTask;
        }

        // Helper – you will expose this once we extend the core
        private ISonyCamera GetSonyCamera() {
            // For the prototype we cast through the mediator.
            // In the real implementation you'll expose a strongly-typed Sony camera interface.
            // (Replace with your actual wrapper once added)
            return (ISonyCamera)cameraMediator.GetCamera(); // placeholder – adjust to your real type
        }
    }
}