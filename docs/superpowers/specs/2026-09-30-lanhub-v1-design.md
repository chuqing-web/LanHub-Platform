# LanHub V1 Design

## Goal

Windows LAN party client with host-fixed rooms, UDP discovery, room-code auth, encrypted session signaling, and a C# SDK so only integrated games can multiplayer through the platform. No store download in V1. No shipped demo game in the library; `LanHub.Sdk.Probe` verifies the SDK path.

## Stack

- VS2022 / .NET 8 / WPF
- Single solution: `LanHub`, `LanHub.Core`, `LanHub.Sdk`, `LanHub.Sdk.Probe`

## Roles

- **Host mode:** create room, 6-digit code, UDP beacon, TCP room server, relay SDK messages, launch registered game with session token
- **Client mode:** discover hosts, join with room code, ready up, receive Start, launch local registered exe with token

## Channels

1. **Launcher:** UDP discovery (no plaintext room code) + TCP room signaling (AES-GCM after room-code proof)
2. **SDK:** localhost-only port; one-time launch token; messages relayed via host encrypted with session key

## Security (V1)

- Room code not in UDP beacon
- Join: salt+nonce challenge, HMAC proof of room code, HKDF session key, AES-GCM frames
- Join rate limit per IP
- SDK bind `127.0.0.1`; require session token from launcher
- No public accounts / CA / anti-cheat

## Ports (defaults)

- UDP discovery: 37810
- TCP room: 37811
- Local SDK: 37812

## UI (must ship)

- Mode select (Host / Client)
- Host lobby: room code, members, kick, select game, start, dissolve
- Client: host list, join dialog, ready, leave
- Library: add/edit/delete game entries (`gameId`, path, args) — may be empty
- Settings: display name, ports, max players (2–8)

## Out of V1

Third-party store install APIs, virtual LAN for non-SDK games, voice, overlay, cloud saves.

## Phase 2 (landed)

- Unreliable channel over encrypted **UDP P2P** between LanHub instances
- Host UDP relay fallback when peer endpoint unknown
- Peer announce / peer table sync
- SDK session exposes `Transport` + `Peers`
- Settings: Game UDP port (default 37813)

