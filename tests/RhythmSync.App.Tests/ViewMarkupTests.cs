using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace RhythmSync.App.Tests;

/// <summary>Checks on the views' XAML that matter for behaviour (the views themselves are not instantiated in tests).</summary>
public class ViewMarkupTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument LoadView(string file, [CallerFilePath] string here = "")
    {
        string repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
        return XDocument.Load(Path.Combine(repo, "src", "RhythmSync.App", "Views", file));
    }

    private static XElement ByAutomationName(XDocument view, string element, string name) =>
        Assert.Single(view.Descendants(Wpf + element), e => (string?)e.Attribute("AutomationProperties.Name") == name);

    [Fact]
    public void The_latency_slider_commits_once_after_dragging_stops_not_every_step()
    {
        var slider = ByAutomationName(LoadView("HostView.xaml"), "Slider", "Latency in milliseconds");

        string binding = (string)slider.Attribute("Value")!;
        Assert.Contains("Binding LatencyMs", binding);
        Assert.Contains("Delay=300", binding);   // keyboard steps still commit, 300 ms after the last one
    }

    [Fact]
    public void The_running_host_screen_offers_the_local_speaker_picker()
    {
        var view = LoadView("HostTabView.xaml");
        var hostingPanel = Assert.Single(view.Descendants(Wpf + "DockPanel"),
            p => ((string?)p.Attribute("Visibility"))?.Contains("Binding IsHosting") == true);

        var picker = Assert.Single(hostingPanel.Descendants(Wpf + "ComboBox"));
        Assert.Contains("Binding Speakers", (string)picker.Attribute("ItemsSource")!);
        Assert.Contains("Binding SelectedSpeaker", (string)picker.Attribute("SelectedItem")!);
    }

    [Fact]
    public void The_client_tab_shows_volume_and_offset_without_sliders()
    {
        var view = LoadView("ClientView.xaml");

        Assert.Empty(view.Descendants(Wpf + "Slider"));
        Assert.Contains(view.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding VolumeText}");
        Assert.Contains(view.Descendants(Wpf + "TextBlock"), t => (string?)t.Attribute("Text") == "{Binding OffsetText}");
        Assert.Contains(view.Descendants(Wpf + "TextBlock"), t => ((string?)t.Attribute("Text"))?.Contains("SetOnHostText") == true);
    }
}
