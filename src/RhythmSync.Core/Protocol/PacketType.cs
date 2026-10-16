namespace RhythmSync.Core.Protocol;

public enum PacketType : byte
{
    Audio = 1,
    Ping = 2,
    Pong = 3,
    Beacon = 4,
}
