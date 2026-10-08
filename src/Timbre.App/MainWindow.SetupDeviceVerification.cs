using System.IO;
using System.Windows;
using Timbre.Core;

namespace Timbre.App;

public partial class MainWindow
{
    private void VerifySetupDeviceGroupsDemoUi()
    {
        var originalChoices = setupChoices.ToDictionary(c => c.Key, c => c.IsIncluded);
        var selectedEndpoint = Selected;
        var savedBytes = File.ReadAllBytes(setups.FilePath);
        var settings = setupControls.Capture("Before grouping checks", endpoints);
        try {
            var gsx = setupDeviceChoices.Single(g => g.Pages.Count == 2);
            var sound = gsx.Pages.Single(p => p.Endpoint?.Direction == AudioDirection.Playback);
            var mic = gsx.Pages.Single(p => p.Endpoint?.Direction == AudioDirection.Microphone);
            DeviceList.SelectedItem = sound.Endpoint;
            SelectedSetupDeviceIncludeCheck.IsChecked = true;
            if (gsx.IsIncluded != true || gsx.Pages.Any(p => !p.IsIncluded) || SelectedSetupIncludeCheck.IsChecked != true)
                throw new InvalidOperationException("Whole-device inclusion did not select both GSX pages.");
            SelectedSetupDeviceIncludeCheck.IsChecked = false;
            if (gsx.IsIncluded != false || gsx.Pages.Any(p => p.IsIncluded))
                throw new InvalidOperationException("Whole-device exclusion left a page selected.");
            SelectedSetupIncludeCheck.IsChecked = true;
            if (gsx.IsIncluded is not null || SelectedSetupDeviceIncludeCheck.IsChecked is not null || mic.IsIncluded || gsx.SelectionInfo != "Selected pages only")
                throw new InvalidOperationException("A sound-only selection did not show partial whole-device inclusion.");
            DeviceList.SelectedItem = mic.Endpoint; SelectedSetupIncludeCheck.IsChecked = true;
            if (gsx.IsIncluded != true || SelectedSetupDeviceIncludeCheck.IsChecked != true)
                throw new InvalidOperationException("Page selections did not synchronize whole-device inclusion.");

            var b20 = endpoints.First(IsB20);
            var speaker = b20 with { Id = "test-b20-speaker", Name = "Speakers (EPOS B20)", Direction = AudioDirection.Playback };
            var choices = new SetupEndpointChoice[] { new(b20, null, true), new(speaker, null, false), new(speaker with {
                Id = "test-other-b20-speaker", Usb = b20.Usb! with { InstanceId = "OTHER-PHYSICAL-B20" } }, null, false) };
            var grouped = GroupSetupChoices(choices);
            if (grouped.Count != 2 || grouped.Single(g => g.Pages.Count == 2).IsIncluded is not null || choices[1].IsIncluded)
                throw new InvalidOperationException("Physical grouping selected an excluded B20 speaker or merged a different USB instance.");
            var captured = setupControls.Capture("Disconnected grouping", [b20]).Devices.Single();
            var ghost = new SetupEndpointChoice(null, captured, true);
            var mixed = GroupSetupChoices([ghost, choices[1]]).Single();
            if (mixed.Pages.Count != 2 || mixed.IsIncluded is not null || ghost.PageLabel != "Microphone (disconnected)")
                throw new InvalidOperationException("A disconnected canonical page did not group with its connected physical sibling.");
            mixed.IsIncluded = false;
            if (ghost.IsIncluded || choices[1].IsIncluded) throw new InvalidOperationException("Whole-device exclusion did not clear disconnected membership.");

            var previouslyExcluded = gsx.Pages.Single(p => p.Endpoint?.Direction == AudioDirection.Playback);
            gsx.IsIncluded = false; mic.IsIncluded = true; RefreshSetupChoices();
            if (setupDeviceChoices.Single(g => g.Pages.Count == 2).IsIncluded is not null || setupChoices.Single(c => c.Key == previouslyExcluded.Key).IsIncluded)
                throw new InvalidOperationException("Refreshing physical groups re-included an excluded sound page.");
            if (!File.ReadAllBytes(setups.FilePath).SequenceEqual(savedBytes) ||
                !setupControls.Capture("Before grouping checks", endpoints).Devices.SequenceEqual(settings.Devices))
                throw new InvalidOperationException("Device/page inclusion changed saved setups or hardware settings.");
        }
        finally {
            foreach (var choice in setupChoices) choice.IsIncluded = originalChoices[choice.Key];
            DeviceList.SelectedItem = selectedEndpoint; SetupInclusionChanged();
        }
    }
}
