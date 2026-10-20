using System.Buffers.Binary;
using System.Text;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Protocol;

namespace RhythmSync.Tests.Interop;

/// <summary>
/// Golden-file tests that pin the Windows wire format for other clients (the Android app reads the same files).
/// <para>
/// <c>docs/interop/control-messages.jsonl</c> holds the exact JSON body (no length prefix) that <see cref="ControlCodec"/>
/// writes for each message, one per line. <c>docs/interop/control-messages.android.jsonl</c> holds what the Android
/// codec writes for the messages Android originates; those lines must be accepted by <see cref="ControlCodec"/>.
/// <c>docs/interop/udp-packets.hex.txt</c> holds lowercase hex of the UDP packets as written by
/// <see cref="PacketSerializer"/>, one <c>name hex</c> pair per line.
/// </para>
/// <para>
/// A missing or different fixture fails the test. To regenerate the Windows-produced files after an intentional
/// protocol change, run the tests once with the environment variable <c>RHYTHMSYNC_UPDATE_FIXTURES=1</c>, review the
/// diff, and commit it. The Android file is written by the Kotlin side and is never regenerated here.
/// </para>
/// </summary>
public class InteropFixtureTests
{
    private const string UpdateVariable = "RHYTHMSYNC_UPDATE_FIXTURES";
    private const string ControlFile = "control-messages.jsonl";
    private const string AndroidControlFile = "control-messages.android.jsonl";
    private const string PacketFile = "udp-packets.hex.txt";

    private static readonly Guid FixtureDeviceId = Guid.Parse("e0b5b111-6677-4c07-b353-2bf9b4862dc7");

    // ---- the messages, in fixture order (a name is documentation only; the line index is the key) ---------------------

    private static readonly (string Name, ControlMessage Message)[] WindowsMessages =
    {
        ("Hello", new Hello(FixtureDeviceId, "Living Room PC", "4821", 2, 47802)),
        ("Welcome", new Welcome(0x8899AABB, 48000, 2, 240, 250, 47802, 47803)),
        ("Reject BadCode", new Reject(RejectReason.BadCode)),
        ("Reject LockedOut", new Reject(RejectReason.LockedOut)),
        ("Reject VersionMismatch", new Reject(RejectReason.VersionMismatch)),
        ("Reject SessionFull", new Reject(RejectReason.SessionFull)),
        ("Ready", new Ready()),
        ("Heartbeat null stats", new Heartbeat(null)),
        ("Heartbeat finite stats", new Heartbeat(new HeartbeatStats(0.5, 0.25, -14.2, 0.18, 0.32, SyncState.InSync))),
        ("Heartbeat NaN stats", new Heartbeat(new HeartbeatStats(double.NaN, double.NaN, 0, 0.18, double.NaN, SyncState.Syncing))),
        ("Heartbeat Infinity stats", new Heartbeat(new HeartbeatStats(double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.18, double.NaN, SyncState.Degraded))),
        ("SetDeviceSettings without EQ", new SetDeviceSettings(0.75f, -12.5)),
        ("SetDeviceSettings with EQ", new SetDeviceSettings(0.5f, 3.25, new[] { -9f, -6f, -1f, 2f, 1f, 0f, 0f, 1f, 2f, 1.5f })),
        ("SetLatency", new SetLatency(300)),
        ("Bye", new Bye()),
    };

    /// <summary>What the Android codec writes for the messages Android originates (decoded by the Windows codec).</summary>
    private static readonly (string Name, ControlMessage Message)[] AndroidMessages =
    {
        ("Hello", new Hello(FixtureDeviceId, "Pixel 8", "4821", 2, 47802)),
        ("Ready", new Ready()),
        ("Heartbeat null stats", new Heartbeat(null)),
        ("Heartbeat finite stats", new Heartbeat(new HeartbeatStats(0.5, 0.25, -14.2, 0.18, 0.32, SyncState.InSync))),
        ("Heartbeat NaN stats", new Heartbeat(new HeartbeatStats(double.NaN, double.NaN, 0, 0.18, double.NaN, SyncState.Syncing))),
        ("Heartbeat Infinity stats", new Heartbeat(new HeartbeatStats(double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.18, double.NaN, SyncState.Degraded))),
        ("Bye", new Bye()),
    };

    private static byte[] AudioPacket()
    {
        var pcm = Enumerable.Range(0, 480).Select(i => (short)(i * 37 - 8000)).ToArray();
        var buf = new byte[PacketSerializer.AudioPacketBytes];
        PacketSerializer.WriteAudio(buf, 0xA1B2C3D4, 77, 123_456_789_012, pcm);
        return buf;
    }

    private static readonly (string Name, Func<byte[]> Build)[] Packets =
    {
        ("audio", AudioPacket),
        ("ping", () =>
        {
            var b = new byte[PacketSerializer.PingBytes];
            PacketSerializer.WritePing(b, new PingPacket(0xDEADBEEF, 42, 9_876_543_210_123));
            return b;
        }),
        ("pong", () =>
        {
            var b = new byte[PacketSerializer.PongBytes];
            PacketSerializer.WritePong(b, new PongPacket(0xCAFEF00D, 7, -5, 123_456_789_012_345, 987_654_321_098_765));
            return b;
        }),
        ("beacon", () =>
        {
            var b = new byte[PacketSerializer.HeaderBytes + 4 + PacketSerializer.MaxBeaconNameBytes];
            int n = PacketSerializer.WriteBeacon(b, new BeaconPacket(0x8899AABB, 47801, 2, "Living Room PC"));
            return b.AsSpan(0, n).ToArray();
        }),
    };

    // ---- control messages -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Control_codec_output_matches_the_golden_file()
    {
        var actual = new List<string>();
        foreach (var (name, message) in WindowsMessages)
            actual.Add(await EncodeBodyAsync(message, name));

        AssertFixture(ControlFile, actual);
    }

    [Fact]
    public async Task Control_codec_reads_every_golden_line()
    {
        var lines = ReadFixtureLines(ControlFile, WindowsMessages.Length);
        for (int i = 0; i < WindowsMessages.Length; i++)
        {
            var read = await DecodeBodyAsync(lines[i], $"{ControlFile} line {i + 1} ({WindowsMessages[i].Name})");
            await AssertSameAsync(WindowsMessages[i].Message, read, WindowsMessages[i].Name);
        }
    }

    [Fact]
    public async Task Control_codec_accepts_what_the_android_app_sends()
    {
        var lines = ReadFixtureLines(AndroidControlFile, AndroidMessages.Length);
        for (int i = 0; i < AndroidMessages.Length; i++)
        {
            var read = await DecodeBodyAsync(lines[i], $"{AndroidControlFile} line {i + 1} ({AndroidMessages[i].Name})");
            await AssertSameAsync(AndroidMessages[i].Message, read, AndroidMessages[i].Name);
        }
    }

    [Fact]
    public async Task Control_codec_writes_nan_and_infinity_as_quoted_literals()
    {
        // The Android codec must produce, and accept, exactly this spelling.
        var body = await EncodeBodyAsync(
            new Heartbeat(new HeartbeatStats(double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0, 0, SyncState.Syncing)),
            "quoted literals");
        Assert.Contains("\"LossPct\":\"NaN\"", body);
        Assert.Contains("\"LatePct\":\"Infinity\"", body);
        Assert.Contains("\"DriftPpm\":\"-Infinity\"", body);
    }

    // ---- UDP packets ------------------------------------------------------------------------------------------------

    [Fact]
    public void Packet_serializer_output_matches_the_golden_file()
    {
        var actual = Packets.Select(p => $"{p.Name} {Convert.ToHexString(p.Build()).ToLowerInvariant()}").ToList();
        AssertFixture(PacketFile, actual);
    }

    [Fact]
    public void Packet_serializer_reads_every_golden_packet()
    {
        var lines = ReadFixtureLines(PacketFile, Packets.Length);
        var byName = lines.Select(l => l.Split(' ', 2)).ToDictionary(p => p[0], p => Convert.FromHexString(p[1]));

        var audio = byName["audio"];
        Assert.Equal(PacketSerializer.AudioPacketBytes, audio.Length);
        var pcm = new short[480];
        Assert.True(PacketSerializer.TryReadAudio(audio, out var seq, out var playAt, pcm));
        Assert.Equal(77u, seq);
        Assert.Equal(123_456_789_012, playAt);
        Assert.Equal(Enumerable.Range(0, 480).Select(i => (short)(i * 37 - 8000)).ToArray(), pcm);

        Assert.True(PacketSerializer.TryReadPing(byName["ping"], out var ping));
        Assert.Equal(new PingPacket(0xDEADBEEF, 42, 9_876_543_210_123), ping);

        Assert.True(PacketSerializer.TryReadPong(byName["pong"], out var pong));
        Assert.Equal(new PongPacket(0xCAFEF00D, 7, -5, 123_456_789_012_345, 987_654_321_098_765), pong);

        Assert.True(PacketSerializer.TryReadBeacon(byName["beacon"], out var beacon));
        Assert.Equal(new BeaconPacket(0x8899AABB, 47801, 2, "Living Room PC"), beacon);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static async Task<string> EncodeBodyAsync(ControlMessage message, string name)
    {
        var ms = new MemoryStream();
        await ControlCodec.WriteAsync(ms, message, CancellationToken.None);
        var frame = ms.ToArray();
        Assert.Equal((uint)(frame.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(frame));
        var body = Encoding.UTF8.GetString(frame, 4, frame.Length - 4);
        Assert.False(body.Contains('\n'), $"'{name}' encodes with a newline; fixtures are one message per line.");
        return body;
    }

    /// <summary>Records holding arrays (SetDeviceSettings) compare by reference, so compare type and re-encoded body.</summary>
    private static async Task AssertSameAsync(ControlMessage expected, ControlMessage actual, string name)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(await EncodeBodyAsync(expected, name), await EncodeBodyAsync(actual, name));
    }

    private static async Task<ControlMessage> DecodeBodyAsync(string line, string where)
    {
        var body = Encoding.UTF8.GetBytes(line);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        try
        {
            var read = await ControlCodec.ReadAsync(new MemoryStream(frame), CancellationToken.None);
            Assert.NotNull(read);
            return read!;
        }
        catch (InvalidDataException e)
        {
            throw new Xunit.Sdk.XunitException($"The Windows codec rejected {where}: {line}{Environment.NewLine}{e.Message}");
        }
    }

    private static bool UpdateRequested =>
        Environment.GetEnvironmentVariable(UpdateVariable) == "1";

    private static string InteropDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "RhythmSync.sln")))
                return Path.Combine(dir.FullName, "docs", "interop");
        }
        throw new Xunit.Sdk.XunitException(
            $"Cannot find the repository root (RhythmSync.sln) above {AppContext.BaseDirectory}, so docs/interop is unreachable.");
    }

    private static List<string> ReadFixtureLines(string file, int expectedCount)
    {
        var path = Path.Combine(InteropDirectory(), file);
        if (!File.Exists(path))
            throw new Xunit.Sdk.XunitException(
                $"Interop fixture {path} is missing. Run the tests once with {UpdateVariable}=1 to generate the Windows-produced fixtures.");
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Assert.True(lines.Count == expectedCount,
            $"{path} has {lines.Count} entries but the test defines {expectedCount}. Regenerate with {UpdateVariable}=1 or fix the file.");
        return lines;
    }

    private static void AssertFixture(string file, IReadOnlyList<string> actual)
    {
        var path = Path.Combine(InteropDirectory(), file);
        var text = string.Join('\n', actual) + "\n";

        if (UpdateRequested)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return;
        }

        if (!File.Exists(path))
            throw new Xunit.Sdk.XunitException(
                $"Interop fixture {path} is missing. Run the tests once with {UpdateVariable}=1 to generate it, review it, and commit it.");

        var expected = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0).ToList();
        for (int i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            var e = i < expected.Count ? expected[i] : "<no line>";
            var a = i < actual.Count ? actual[i] : "<no line>";
            Assert.True(e == a,
                $"{file} line {i + 1} differs from what the Windows code now produces.{Environment.NewLine}  fixture: {e}{Environment.NewLine}  actual:  {a}{Environment.NewLine}" +
                $"If the change is intentional, regenerate with {UpdateVariable}=1 and commit the diff (Android must be updated too).");
        }
    }
}
