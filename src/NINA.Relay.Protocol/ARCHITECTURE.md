# NINA.Relay.Protocol — Architecture

Tiny shared library holding the **wire types** that flow between the
Polaris instance (running on the Pi / mini-PC at the telescope) and
the relay server (running on a VPS with a public hostname).

Both ends reference this project so they agree on framing without
duplicating record definitions.

## Layout

```
src/NINA.Relay.Protocol/
  NINA.Relay.Protocol.csproj
  Frames.cs                       # all the message records
```

## What's in `Frames.cs`

- **Tunnel control**: `HelloFrame` (Pi → server, opens the WS tunnel
  with tenant ID + token), `HelloAckFrame` (server → Pi, accept /
  reject)
- **HTTP forwarding**: `HttpRequestFrame` (server → Pi when a browser
  hits `https://relay.example.com/t/{tenant}/`, carries method, path,
  headers, body), `HttpResponseFrame` (Pi → server, the response)
- **WebSocket bridging**: `WsOpenFrame` / `WsMessageFrame` / `WsCloseFrame`
  for forwarding browser WS connections through the tunnel to Polaris's
  `/ws/status` and `/ws/image-stream`
- **Heartbeat**: `PingFrame` / `PongFrame` for keepalive

All records are immutable, serialized over the WS tunnel as MessagePack
(compact + fast — important for the image-stream path).

## Why a separate project

Sharing the records avoids the "we changed the protocol on one side
and forgot the other" class of bug. Both `NINA.Polaris`
(`Services/RelayClient.cs`) and `NINA.Relay.Server` reference this
project.

If you change a frame shape, update **both ends** and bump the
`HelloFrame.ProtocolVersion` so a stale client connecting to a fresh
server (or vice versa) fails cleanly with a readable error instead
of silently corrupting bytes.

## See also

- [NINA.Relay.Server/ARCHITECTURE.md](../NINA.Relay.Server/ARCHITECTURE.md)
- [docs/user-guide/relay.md](../../docs/user-guide/relay.md) — end-user
  perspective
- [Root ARCHITECTURE.md](../../ARCHITECTURE.md)
