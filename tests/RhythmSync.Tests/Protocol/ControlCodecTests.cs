using System.Buffers.Binary;
using System.Text;
using RhythmSync.Core.Clock;
using RhythmSync.Core.Protocol;

namespace RhythmSync.Tests.Protocol;

public class ControlCodecTests
{
    public static TheoryData<ControlMessage> All => new()
    {
        new Hello(Guid.NewGuid(), "Desk", "0420", 1, 47802),
        new Welcome(7, 48000, 2, 240, 200, 47802, 47803),
        new Reject(RejectReason.LockedOut),
        new Ready(),
        new Heartbeat(new HeartbeatStats(0.1, 0, 42.5, 0.2, -0.3, SyncState.InSync)),
        new Heartbeat(null),
        new SetDeviceSettings(0.5f, -12.5),
        new SetLatency(300),
        new Bye(),
    };

    private static byte[] Frame(int prefix, byte[] body)
    {
        var buf = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)prefix);
        body.CopyTo(buf, 4);
        return buf;
    }

    [Theory, MemberData(nameof(All))]
    public async Task Round_trips(ControlMessage m)
    {
        var ms = new MemoryStream();
        await ControlCodec.WriteAsync(ms, m, CancellationToken.None);
        ms.Position = 0;
        var read = await ControlCodec.ReadAsync(ms, CancellationToken.None);
        Assert.Equal(m, read);
    }

    [Fact]
    public async Task SetDeviceSettings_round_trips_its_eq()
    {
        float[] gains = [3f, -2.5f, 0, 0, 1f, 0, 0, 0, 12f, -12f];
        var ms = new MemoryStream();
        await ControlCodec.WriteAsync(ms, new SetDeviceSettings(0.5f, -12.5, gains), CancellationToken.None);
        ms.Position = 0;

        var read = Assert.IsType<SetDeviceSettings>(await ControlCodec.ReadAsync(ms, CancellationToken.None));

        Assert.Equal(0.5f, read.Volume);
        Assert.Equal(-12.5, read.OffsetMs);
        Assert.Equal(gains, read.EqGainsDb);
    }

    [Fact]
    public async Task SetDeviceSettings_without_eq_reads_as_null()
    {
        var body = Encoding.UTF8.GetBytes("{\"type\":\"SetDeviceSettings\",\"Volume\":0.5,\"OffsetMs\":0}");

        var read = Assert.IsType<SetDeviceSettings>(
            await ControlCodec.ReadAsync(new MemoryStream(Frame(body.Length, body)), CancellationToken.None));

        Assert.Null(read.EqGainsDb);
    }

    [Fact]
    public async Task SetDeviceSettings_with_a_wrong_length_eq_still_parses()
    {
        // Validation is the client's job; a bad array must not drop the connection.
        var body = Encoding.UTF8.GetBytes("{\"type\":\"SetDeviceSettings\",\"Volume\":0.5,\"OffsetMs\":0,\"EqGainsDb\":[1,2,3,\"NaN\"]}");

        var read = Assert.IsType<SetDeviceSettings>(
            await ControlCodec.ReadAsync(new MemoryStream(Frame(body.Length, body)), CancellationToken.None));

        Assert.NotNull(read.EqGainsDb);
        Assert.Equal(4, read.EqGainsDb!.Length);
        Assert.True(float.IsNaN(read.EqGainsDb[3]));
    }

    [Fact]
    public async Task Json_uses_type_discriminator_and_string_enums()
    {
        var ms = new MemoryStream();
        await ControlCodec.WriteAsync(ms, new Reject(RejectReason.LockedOut), CancellationToken.None);
        var bytes = ms.ToArray();
        Assert.Equal((uint)(bytes.Length - 4), BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        var json = Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4);
        Assert.Contains("\"type\":\"Reject\"", json);
        Assert.Contains("\"LockedOut\"", json);
    }

    [Fact]
    public async Task Empty_stream_returns_null()
    {
        Assert.Null(await ControlCodec.ReadAsync(new MemoryStream(), CancellationToken.None));
    }

    [Fact]
    public async Task Oversize_length_throws_InvalidData()
    {
        var ms = new MemoryStream(Frame(70_000, []));
        await Assert.ThrowsAsync<InvalidDataException>(() => ControlCodec.ReadAsync(ms, CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_body_throws_InvalidData()
    {
        var ms = new MemoryStream(Frame(50, new byte[10]));
        await Assert.ThrowsAsync<InvalidDataException>(() => ControlCodec.ReadAsync(ms, CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_type_throws_InvalidData()
    {
        var body = Encoding.UTF8.GetBytes("{\"type\":\"Nope\"}");
        var ms = new MemoryStream(Frame(body.Length, body));
        await Assert.ThrowsAsync<InvalidDataException>(() => ControlCodec.ReadAsync(ms, CancellationToken.None));
    }

    [Fact]
    public async Task Non_finite_heartbeat_stats_round_trip()
    {
        var m = new Heartbeat(new HeartbeatStats(
            double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.NaN, double.PositiveInfinity, SyncState.Syncing));
        var ms = new MemoryStream();
        await ControlCodec.WriteAsync(ms, m, CancellationToken.None);
        ms.Position = 0;
        var read = Assert.IsType<Heartbeat>(await ControlCodec.ReadAsync(ms, CancellationToken.None));
        Assert.Equal(m, read);
    }
}
