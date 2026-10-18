using RhythmSync.Platform.Windows;

namespace RhythmSync.Platform.Windows.Tests;

public class HostAudioCheckTests
{
    [Theory] // (default, vbCable, captured, local) -> status
    [InlineData(null, null, null, "spk", HostAudioStatus.VbCableMissing)]
    [InlineData("spk", "cab", "cab", "spk", HostAudioStatus.VbCableNotDefault)]
    [InlineData("cab", "cab", "cab", "cab", HostAudioStatus.LocalSpeakerIsCaptured)]
    [InlineData("cab", "cab", "cab", "spk", HostAudioStatus.Ok)]
    public void Host_audio_status(string? def, string? cab, string? captured, string? local, HostAudioStatus expected) =>
        Assert.Equal(expected, HostAudioCheck.Evaluate(def, cab, captured, local));

    [Theory]
    [InlineData(null, "", null, null)]             // no VB-CABLE wins over everything else
    [InlineData("spk", null, null, "spk")]
    [InlineData("spk", "cab", "cab", "cab")]       // local speaker is the captured cable: reported before "cable is not the default"
    public void Precedence_is_missing_then_captured_then_not_default(string? def, string? cab, string? captured, string? local)
    {
        var expected = string.IsNullOrEmpty(cab) ? HostAudioStatus.VbCableMissing : HostAudioStatus.LocalSpeakerIsCaptured;
        Assert.Equal(expected, HostAudioCheck.Evaluate(def, cab, captured, local));
    }

    [Fact]
    public void The_local_speaker_is_compared_with_whatever_device_is_captured()
    {
        // Capturing some other endpoint by id: playing on that endpoint would feed back into the capture.
        Assert.Equal(HostAudioStatus.LocalSpeakerIsCaptured, HostAudioCheck.Evaluate("cab", "cab", "spk", "spk"));
        // Capturing "the default" (no id): the default device is the captured one.
        Assert.Equal(HostAudioStatus.LocalSpeakerIsCaptured, HostAudioCheck.Evaluate("spk", "cab", null, "spk"));
        Assert.Equal(HostAudioStatus.LocalSpeakerIsCaptured, HostAudioCheck.Evaluate("cab", "cab", null, "cab"));
    }

    [Fact]
    public void Endpoint_ids_compare_case_insensitively_and_unknown_defaults_are_not_default()
    {
        Assert.Equal(HostAudioStatus.Ok, HostAudioCheck.Evaluate("{ABC}", "{abc}", "{aBc}", "{def}"));
        Assert.Equal(HostAudioStatus.LocalSpeakerIsCaptured, HostAudioCheck.Evaluate("{ABC}", "{abc}", "{ABC}", "{abc}"));
        Assert.Equal(HostAudioStatus.VbCableNotDefault, HostAudioCheck.Evaluate(null, "cab", "cab", "spk"));
        Assert.Equal(HostAudioStatus.Ok, HostAudioCheck.Evaluate("cab", "cab", "cab", null));
        Assert.Equal(HostAudioStatus.VbCableNotDefault, HostAudioCheck.Evaluate(null, "cab", null, null));   // nothing known is not "captured"
    }
}
