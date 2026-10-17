using RhythmSync.Platform.Windows;

namespace RhythmSync.Platform.Windows.Tests;

public class HostAudioCheckTests
{
    [Theory] // (default, vbCable, local) -> status
    [InlineData(null, null, "spk", HostAudioStatus.VbCableMissing)]
    [InlineData("spk", "cab", "spk", HostAudioStatus.VbCableNotDefault)]
    [InlineData("cab", "cab", "cab", HostAudioStatus.LocalSpeakerIsCaptured)]
    [InlineData("cab", "cab", "spk", HostAudioStatus.Ok)]
    public void Host_audio_status(string? def, string? cab, string? local, HostAudioStatus expected) =>
        Assert.Equal(expected, HostAudioCheck.Evaluate(def, cab, local));

    [Theory]
    [InlineData(null, "", null)]            // no VB-CABLE wins over everything else
    [InlineData("spk", null, "spk")]
    [InlineData("spk", "cab", "cab")]       // local speaker is the cable: reported before "cable is not the default"
    public void Precedence_is_missing_then_captured_then_not_default(string? def, string? cab, string? local)
    {
        var expected = string.IsNullOrEmpty(cab) ? HostAudioStatus.VbCableMissing : HostAudioStatus.LocalSpeakerIsCaptured;
        Assert.Equal(expected, HostAudioCheck.Evaluate(def, cab, local));
    }

    [Fact]
    public void Endpoint_ids_compare_case_insensitively_and_unknown_defaults_are_not_default()
    {
        Assert.Equal(HostAudioStatus.Ok, HostAudioCheck.Evaluate("{ABC}", "{abc}", "{def}"));
        Assert.Equal(HostAudioStatus.VbCableNotDefault, HostAudioCheck.Evaluate(null, "cab", "spk"));
        Assert.Equal(HostAudioStatus.Ok, HostAudioCheck.Evaluate("cab", "cab", null));
    }
}
