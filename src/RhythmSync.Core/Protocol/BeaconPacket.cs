namespace RhythmSync.Core.Protocol;

public sealed record BeaconPacket(uint SessionId, ushort ControlPort, byte ProtocolVersion, string Name);
