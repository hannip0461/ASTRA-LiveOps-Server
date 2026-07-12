# ASTRA TCP + Protobuf Protocol v1

## Transport

- TCP port: `5300`
- Frame header: 4-byte signed length in network byte order (big-endian)
- Payload: one serialized Protobuf `RequestEnvelope` or `ResponseEnvelope`
- Default maximum payload: 65,536 bytes
- Requests are processed sequentially per connection, preserving response order.
- The schema source is `src/Astra.Contracts/Protos/astra_tcp.proto`.

`protocol_version` must be `1`. Unknown fields remain compatible with Protobuf evolution, while an incompatible protocol receives `protocol_version_unsupported`.

## Session Lifecycle

1. Client opens a TCP connection.
2. Client sends `bind_session` with `player_id` and an HMAC-signed access token.
3. Gateway validates player binding and token expiry, then returns a random `session_id`.
4. Every game command on that connection must include the returned `session_id`.
5. Rebinding or using a session from another connection is rejected.
6. Token expiry is checked again for every game command.

The development signing key lives only in `appsettings.Development.json`. A deployed environment must provide `Astra:TcpSessionToken:SigningKey` through secret configuration.

## Commands

### `get_wallet`

- Read-only request; `idempotency_key` must be empty.
- Calls `IPlayerAccountGrain.GetSnapshotAsync()` through the Gateway's embedded Orleans Client.
- Returns a typed Protobuf `WalletSnapshot`.

### `draw_gacha`

- Requires `banner_id`, `draw_count`, and `idempotency_key`.
- Gateway creates the canonical request hash from player, banner, and draw count. Client-provided hashes are not trusted. ASP.NET Core uses the same `PlayerRequestHash` rule, so an HTTP request can be retried through TCP without a false conflict.
- Calls `IPlayerAccountGrain.DrawGachaAsync()` directly; ASP.NET Core API is not in this path.
- Returns a typed `GachaDrawResponse` and `replayed=true` when the completed result is reused.

## Retry Semantics

- `request_id` correlates one transport request and response. It is not an idempotency key.
- A reconnect receives a new `session_id` but may resend the same mutation `idempotency_key`.
- The same key and same command body returns the stored result without another draw or debit.
- The same key with a different command body returns `idempotency_conflict`.
- A Gateway timeout is ambiguous: the Grain may still commit. The client must retry with the same idempotency key.

## Limits And Security

- Identifier fields allow ASCII letters, digits, `.`, `_`, `-`, and `:` only.
- Default limits: 1,024 connections, 1,000 requests per connection, 2-minute idle timeout, 10-second command timeout, and 10-second response write timeout.
- Oversized or malformed frames close the connection; oversized responses return `response_too_large` when possible.
- Rented buffers are cleared before returning to the shared pool.
- Local development is plaintext TCP. Production must terminate TLS at the trusted ingress or add `SslStream` with certificate validation.
