using System.Text.Json.Serialization;
using RhythmSync.Core.Clock;

namespace RhythmSync.Core.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "Hello")]
[JsonDerivedType(typeof(Welcome), "Welcome")]
[JsonDerivedType(typeof(Reject), "Reject")]
[JsonDerivedType(typeof(Ready), "Ready")]
[JsonDerivedType(typeof(Heartbeat), "Heartbeat")]
[JsonDerivedType(typeof(SetDeviceSettings), "SetDeviceSettings")]
[JsonDerivedType(typeof(SetLatency), "SetLatency")]
[JsonDerivedType(typeof(Bye), "Bye")]
public abstract record ControlMessage;

public sealed record Hello(Guid DeviceId, string DeviceName, string PairingCode, int ProtocolVersion, int AudioPort) : ControlMessage;

public sealed record Welcome(uint SessionId, int SampleRate, int Channels, int FrameSamples, int TargetLatencyMs, int AudioPort, int ClockPort) : ControlMessage;

public enum RejectReason { BadCode, LockedOut, VersionMismatch, SessionFull }

public sealed record Reject(RejectReason Reason) : ControlMessage;

public sealed record Ready : ControlMessage;

public sealed record HeartbeatStats(double LossPct, double LatePct, double DriftPpm, double FitResidualMs, double SyncErrorMs, SyncState State);

public sealed record Heartbeat(HeartbeatStats? Stats) : ControlMessage;

public sealed record SetDeviceSettings(float Volume, double OffsetMs) : ControlMessage;

public sealed record SetLatency(int TargetLatencyMs) : ControlMessage;

public sealed record Bye : ControlMessage;
