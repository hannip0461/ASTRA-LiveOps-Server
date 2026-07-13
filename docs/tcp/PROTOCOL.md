# ASTRA TCP + Protobuf 프로토콜 v1

## 전송 형식

- TCP port: `5300`
- Frame header: network byte order(big-endian)의 signed length 4 byte
- Payload: serialized Protobuf `RequestEnvelope` 또는 `ResponseEnvelope` 1개
- 기본 최대 payload: 65,536 byte
- Connection별 request를 순서대로 처리해 response 순서를 유지
- Schema: `src/Astra.Contracts/Protos/astra_tcp.proto`

`protocol_version`은 `1`이어야 한다. Protobuf evolution을 위해 unknown field는 허용하고 호환되지 않는 version은 `protocol_version_unsupported`로 거부한다.

## 세션 생명주기

1. Client가 TCP connection을 연다.
2. `player_id`와 HMAC-signed access token을 포함한 `bind_session`을 전송한다.
3. Gateway가 player binding과 token expiry를 검증한 뒤 random `session_id`를 반환한다.
4. 해당 connection의 모든 game command는 반환된 `session_id`를 포함해야 한다.
5. Rebinding과 다른 connection의 session 사용을 거부한다.
6. 모든 game command에서 token expiry를 다시 검사한다.

개발 signing key는 `appsettings.Development.json`에서만 사용한다. 배포 환경은 secret configuration으로 `Astra:TcpSessionToken:SigningKey`를 전달해야 한다.

## 명령

### `get_wallet`

- Read-only request이므로 `idempotency_key`는 비워야 한다.
- Gateway의 Orleans Client가 `IPlayerAccountGrain.GetSnapshotAsync()`를 호출한다.
- Typed Protobuf `WalletSnapshot`을 반환한다.

### `draw_gacha`

- `banner_id`, `draw_count`, `idempotency_key`가 필요하다.
- Gateway는 player, banner, draw count로 canonical request hash를 생성하며 client hash를 신뢰하지 않는다. ASP.NET Core도 같은 `PlayerRequestHash` 규칙을 사용해 HTTP 요청을 TCP로 재시도할 수 있다.
- `IPlayerAccountGrain.DrawGachaAsync()`를 직접 호출한다. ASP.NET Core API를 경유하지 않는다.
- Typed `GachaDrawResponse`를 반환하고 completed result를 재사용하면 `replayed=true`를 설정한다.

## 재시도 정책

- `request_id`는 transport request와 response를 연결하며 idempotency key가 아니다.
- 재연결 시 새 `session_id`를 받지만 같은 mutation `idempotency_key`를 다시 전송할 수 있다.
- 같은 key와 같은 command body는 draw와 debit을 반복하지 않고 저장된 결과를 반환한다.
- 같은 key에 다른 command body를 사용하면 `idempotency_conflict`를 반환한다.
- Gateway timeout 시 Grain이 commit했을 수 있으므로 client는 같은 idempotency key로 재시도해야 한다.

## 제한과 보안

- 식별자 field는 ASCII letter, digit, `.`, `_`, `-`, `:`만 허용한다.
- 기본 제한: connection 1,024개, connection별 request 1,000개, idle timeout 2분, command timeout 10초, response write timeout 10초
- 과도하거나 잘못된 frame은 connection을 종료한다. 가능한 경우 과도한 response에 `response_too_large`를 반환한다.
- Shared pool에 반환하기 전에 rented buffer를 초기화한다.
- 로컬 개발은 plaintext TCP를 사용한다. 운영 환경은 신뢰된 ingress에서 TLS를 종료하거나 certificate validation이 포함된 `SslStream`을 적용한다.
