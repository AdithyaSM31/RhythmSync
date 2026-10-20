# Interop fixtures

Golden files that pin the RhythmSync wire format (protocol v2). The Windows tests and the Android tests read the same files.

| File | Produced by | Contents |
| --- | --- | --- |
| `control-messages.jsonl` | Windows `ControlCodec` | One JSON message body (no length prefix) per line: every message and every `Reject` reason, heartbeats with null, finite, `"NaN"` and `"Infinity"` stats, `SetDeviceSettings` with and without EQ. |
| `control-messages.android.jsonl` | Android `ControlCodec` | What the Android app sends (Hello, Ready, Heartbeat, Bye). The Windows codec must accept every line. |
| `udp-packets.hex.txt` | Windows `PacketSerializer` | `name hex` per line (lowercase hex): one audio packet, one ping, one pong, one beacon. |

NaN and the infinities travel as the quoted strings `"NaN"`, `"Infinity"` and `"-Infinity"`.

## Verifying

- Windows: `dotnet test -c Release --filter "FullyQualifiedName~InteropFixtureTests"` (serializes and compares byte for byte, then deserializes).
- Android: in `android/`, `gradlew.bat testDebugUnitTest --tests "*InteropFixtureTest"`.

A missing or different fixture fails the test with the differing line. Nothing is regenerated silently.

## Regenerating

After an intentional Windows protocol change, run the Windows test once with `RHYTHMSYNC_UPDATE_FIXTURES=1`
(for example `RHYTHMSYNC_UPDATE_FIXTURES=1 dotnet test -c Release --filter "FullyQualifiedName~InteropFixtureTests"`),
review the diff, update the Android codec until its tests pass, and commit both. `control-messages.android.jsonl` is
edited by hand; the Kotlin test checks that the Android codec writes exactly those lines.
