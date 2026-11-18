# MES API · 이벤트 계약 기준 v1.0

작성: 2026-11-12 / GPT. **설계·파일 검증 단계이며 서버 구현이나 외부 API 호출은 하지 않았다.**

## 1. 기준과 비용 정책

최신 사용자 결정은 **제조MES-화면권한-상태전이-ERD-설계초안-v4.md + outputs/ddl의 31테이블(사용자 승인 병합6개 반영, 공유 사본 ddl-31)**이다. WORK_ORDER에 작업을 통합하고, 실적은 양수 qty와 TOTAL/REJECT/REVERSAL/TOTAL_ADJUST_DOWN으로 표현한다. 앞선 34테이블 참고본의 WORK_TASK, CONFIG_REVISION, AI_JOB/AI_ATTEMPT, signedQty를 새 API에 가져오지 않는다. 현재 문서는 DB 변경 요청이 아니다.

OpenAPI는 HTTP API를 설명하는 무료 공개 명세다. **OpenAI API나 유료 AI 서비스가 아니며 API 키가 필요 없다.** JSON Schema 역시 데이터 형식 검증 규격이다. 계약 검증 도구는 작업 폴더에만 설치한 무료 오픈소스 도구이며 로컬 파일만 읽는다.

- 기본 실행 정책: `cloudAiEnabled=false`, `externalDeliveryEnabled=false`, 기본 제공자 MOCK.
- 이번 단계에는 클라우드 AI·유료 DB·이메일·채팅 서비스 신청, 키 발급, 모델 추론, 실발송, 외부 API 호출을 하지 않는다.
- CLOUD 설정을 문서에 정의하는 것과 실제 실행은 다르다. 기본 정책에서 CLOUD 활성화/실행은 `403 CloudExecutionDisabled`로 거절한다. 비용이 생길 수 있는 실행을 켜는 API는 만들지 않는다.
- LOCAL은 명시적으로 설정한 로컬 서버만 허용한다. 최초 시연은 MOCK이다. MOCK 연결 테스트는 로컬 설정만 검사하며 클라우드 상태 확인이나 추론을 호출하지 않는다.
- 외부 발송도 기본 정책에서 실행하지 않는다. 실패/대기 상태를 조회하는 것과 실제 전달을 구분한다. 나중에 실제 연동할 때 제공자 약관·무료 한도·비용을 확인하고 사용자가 별도로 지시해야 한다.

이 문서의 무료 정책은 새 외부 API 호출 비용을 방지하기 위한 프로젝트 정책이다. 사용 중인 개발 도구 구독 자체의 요금제를 변경하는 작업은 아니다.

## 2. 계약 파일

| 파일 | 대상 |
|---|---|
| [공개 OpenAPI](../contracts/v1/public.openapi.json) | 관리자·작업자 → 업무 백엔드 |
| [업무 백엔드 내부 OpenAPI](../contracts/v1/backend-internal.openapi.json) | 게이트웨이·커넥터·Quartz Worker → 백엔드 |
| [게이트웨이 내부 OpenAPI](../contracts/v1/gateway.openapi.json) | 백엔드·커넥터 → 게이트웨이 |
| [설비 커넥터 OpenAPI](../contracts/v1/equipment.openapi.json) | 게이트웨이 → 설비 커넥터 |
| [AI 커넥터 OpenAPI](../contracts/v1/ai.openapi.json) | 게이트웨이 → AI 커넥터 |
| [공통 DTO JSON Schema](../contracts/v1/mes.schemas.json) | 요청·응답·공통 오류·설정·수집·분석 데이터 |
| [이벤트 JSON Schema](../contracts/v1/mes.events.schema.json) | 이벤트 envelope와 10개 종류 |
| [정상·거절 예제](../contracts/v1/contract-examples.json) | 로컬 데이터 검증용. 토큰은 가짜 예제이며 실제 인증 불가 |
| [전체 경로·권한 목록](MES-API-엔드포인트목록-v1.md) | 경로별 역할/호출자/scope·요청·응답·DB 매핑 |

OpenAPI는 3.1.1, 공통 스키마는 JSON Schema 2020-12로 고정한다. 스키마 파일은 상대 참조하므로 **api-contracts-v1 폴더를 통째로 유지**한다. URL은 배포 예시일 뿐 연결하거나 신청한 주소가 아니다.

## 3. 공통 HTTP·데이터 형식

### 경로와 소유권

- 사용자 API는 `/api/v1/...`, 내부 API는 `/internal/v1/...`로 구분한다.
- 공개 API는 업무 백엔드만 제공한다. 브라우저는 Redis·Oracle·내부 커넥터 API에 직접 접근하지 않는다.
- 서비스별 OpenAPI 문서는 각 수신 서비스 기준이다. 동일 경로가 다른 호스트에 있어도 같은 API가 아니다.
- API 응답은 DB 행 전체를 직렬화하지 않고 DTO로 제한한다. 암호 해시·security_stamp·API 키·웹훅 토큰·수집 허가 원문은 화면 응답·감사 로그·이벤트에 넣지 않는다.

### JSON 규칙

| 항목 | 규칙 |
|---|---|
| 필드명 | camelCase |
| DB 숫자 ID | `"101"`처럼 십진 문자열. 서버는 Int64 범위도 검사 |
| 공개 연동 대상 ID | `targetId="FILLER-01"`는 INTEGRATION_TARGET.target_code. 숫자 target_id와 구분 |
| jobId / requestId / clientEntryId / eventId | UUID 문자열. DB 이벤트 컬럼은 64자여도 v1 생성기는 UUID로 통일 |
| version / configVersion / attempt | JSON 정수. `expectedVersion`은 변경 대상 루트의 version |
| EA 수량 | JSON 정수 0..999999999999, 입력 실적 qty는 1 이상 |
| 카운터·일반 소수 | 문자열. NUMBER(18,6)에 맞춰 정수부12자리/소수부6자리 이내. COUNTER는 비음수 정수 문자열 |
| sequenceNo | 19자리 이내 비음수 십진 문자열. 스트림 재시작 시 streamId 교체 |
| 시각 | UTC RFC3339 `...Z`, 소수 초 최대6자리. 화면 KST 입력은 전송 전 UTC로 변환 |
| 영업일 | `YYYY-MM-DD`, KST 날짜. 시각 필드와 혼용하지 않음 |
| 목록 | `{ "items": [...], "nextCursor": null }`, limit 기본50/최대100, 고정 정렬+서버 발급 cursor |
| 미지원 필드 | 요청 additionalProperties=false. accepted/actorId/createdBy/receivedAt 같은 서버 소유 필드 위조는 거절 |

HTTP 헤더는 `Content-Type: application/json`, 추적에는 표준 traceparent와 선택적 X-Correlation-ID를 쓴다. 서버가 길이/형식을 검증하고 누락 시 correlationId를 만든다. 추적 ID는 인증이나 멱등 키가 아니다. `202`는 접수이며 성공적인 실행 완료가 아니다. 생성/분석 응답의 Location으로 상세 조회한다.

### 오류

오류는 `application/problem+json`을 사용한다.

```json
{
  "type": "urn:mes:problem:version-conflict",
  "title": "다른 요청이 먼저 변경했습니다.",
  "status": 409,
  "code": "VersionConflict",
  "correlationId": "00000000-0000-0000-0000-0000000000ca",
  "detail": "재조회 후 현재 상태를 확인하세요."
}
```

| HTTP | 주요 code | 처리 |
|---|---|---|
| 400 | InvalidJson / SchemaValidationFailed | 타입·필드·형식 오류 수정 |
| 401 | Unauthenticated / InvalidServiceToken | 다시 로그인 또는 서비스 자격 확인 |
| 403 | Forbidden / CsrfFailed / CloudExecutionDisabled / ExternalDeliveryDisabled | 권한·정책 거절. 재시도로 해결하지 않음 |
| 404 | NotFound | 존재하지 않거나 조회 범위 밖. 다른 사용자의 존재를 드러내지 않음 |
| 409 | VersionConflict / InvalidState / EquipmentOccupied / DuplicatePayloadConflict / ConfigVersionMismatch / ExternalOutcomeUnknown | 재조회·원인 확인. 무조건 재시도하지 않음 |
| 413 | PayloadTooLarge | 내부 수집 본문/배치 한도 초과 |
| 422 | CounterInvalid / SampleStale / QuantityInvalid / CollectionAuthorizationInvalid / CollectionGraceExpired / ConfigVersionUnavailable | 업무·허가 조건 위반. 데이터/시각·설정 확인 |
| 429 | RateLimited | Retry-After에 따른 제한 재시도 |
| 503 | DependencyUnavailable | 짧은 타임아웃/백오프. 사용자 입력 저장과 외부 발송 실패를 분리 |

오류 detail에 내부 SQL, 토큰, 비밀번호, 키, 원문 제공자 응답을 포함하지 않는다. OpenAPI의 공통 응답은 가능한 오류 형식을 정의하고, 경로별 실제 오류 code는 업무 규칙과 아래 사례를 따른다.

## 4. 인증·권한

### 사용자

- 로그인은 보안 세션 쿠키, 변경 요청은 CSRF 헤더를 사용한다. 로그인도 CSRF/Origin 검증 대상이다. `/auth/csrf`가 요청 토큰과 안티포저리 쿠키를 발급한다.
- 역할 코드는 ADMIN/WORKER 두 가지다. 현재 USER_ROLE은 두 역할의 조합을 허용하며 API도 roles 배열로 표현한다. 최소1개 역할과 중복 없음을 검사한다. 시연 계정은 관리자/작업자를 분리하고, ADMIN이 포함된 계정에는 WORKER 역할이 있어도 생산 입력 명령을 허용하지 않는다. 별도의 계정당 단일 역할 DB 정책을 임의 추가하지 않는다.
- 관리자도 작업자 대리 생산입력, 자기 지시 마감, 타인의 알림 읽음을 하지 못한다.
- WORKER의 작업 조회는 SQL에서 assignee_id로 필터한다. 대시보드·경보는 현재 담당 설비 범위, 과거 연결 경보는 자신의 중지 이력에 명시적으로 연결된 건만 허용한다. 임의 equipmentId로 다른 설비 이력을 조회하지 못한다.
- ADMIN 전용: 계정·설정·지시 발행/취소/마감·정정 요청 확인·경보 종결·AI 요청/조회·운영 이력. WORKER 전용: 본인 점검·시작/중지/재개·실적·제출.
- 세션 쿠키는 Secure/HttpOnly와 적절한 SameSite, 서버 측 만료·security_stamp 검증을 적용한다. 로그인 실패는 일반화하고 제한한다. 별도 유료 인증 서비스는 필수 아니다.
- Blazor와 API가 분리 배포되면 같은 사이트의 고정 API 주소 또는 명시적 허용 origin만 사용한다. 세션을 전달하는 HTTP 클라이언트의 CookieContainer를 사용자 간 공유하지 않는다. 구현 전 .NET 인증 저장소·배포 호스트 구성을 검증한다.

### 내부 서비스

| 호출 서비스 | 접근 범위 |
|---|---|
| integration-gateway | 원본 설정 조회, 원본 기반 수집 허가 발급 요청 |
| equipment-connector | 자신에게 배정된 설정/허가, 측정 전송, 상태 보고 |
| ai-connector | 배정된 작업의 스냅샷 조회, 상태·결과 회신 |
| jobs-worker | 허용된 집계·정리·Outbox 실행·예약 분석 생성 |
| mes-backend | 게이트웨이 설정 통지·연결 테스트·분석 dispatch/cancel |

내부 호출은 서비스용 Bearer JWT의 서명·iss·aud·exp·scope와 대상/instance 바인딩을 검증한다. OpenAPI의 x-allowed-callers/x-required-scopes는 구현해야 할 인가 계약이며 프레임워크가 자동 집행하는 코드는 아니다. ADMIN 쿠키로 내부 API를 호출할 수 없고 actorId 헤더를 임의 신뢰하지 않는다.

개별 서비스 자격과 측정 당시 수집 허가는 별개다. 서비스 JWT는 **요청 수신 현재시각에도 유효**해야 한다. 초 단기 수집 토큰을 로그인 토큰처럼 재사용하지 않는다. 내부 포트는 외부에 공개하지 않는다.

## 5. 관리자·작업자 업무 API

주요 그룹은 로그인/계정, 기준정보, 대시보드, 작업지시, 점검·실적·정정, 설정, 경보·조치, 알림함·발송 상태, AI, 보고·운영이다. 전 경로와 DTO는 [전체 경로 목록](MES-API-엔드포인트목록-v1.md)을 참조한다.

### 작업 상태와 명령

```text
ADMIN: DRAFT → RELEASED
WORKER: RELEASED → RUNNING ↔ PAUSED → SUBMITTED
ADMIN: SUBMITTED → CLOSED
ADMIN: DRAFT / RELEASED → CANCELLED (시작 이력 없음)
```

상태를 바꾸는 범용 `PUT status`는 제공하지 않는다. `/orders/{orderId}/start`, `/pause`, `/resume`, `/submit`, `/close`가 각각 허용 조건을 검사한다. RELEASED의 필수 점검 완료 후 시작하며 PAUSED도 설비 점유를 유지한다. 제출에서 열린 중지를 닫고 점유를 해제한다.

```json
{
  "expectedVersion": 4,
  "reasonCode": "EQUIPMENT_CHECK",
  "note": "시뮬레이터 설비 점검",
  "alarmId": "701"
}
```

이는 `POST /api/v1/orders/{orderId}/pause` 요청이다. 서버는 본인 담당·RUNNING·버전·경보 설비 일치를 확인하고 WORK_PAUSE/WORK_ORDER/ORDER_TRANSITION/Outbox를 원자적으로 반영한다. 성공은 최신 Order DTO와 증가한 version을 반환한다.

점검 행에는 version이 없으므로 점검 갱신은 부모 WORK_ORDER.expectedVersion을 확인하고 부모 version을 증가시킨다. 실제 생산수량을 요청의 totalQty로 덮어쓰는 제출 API는 만들지 않는다. AUTO 제출의 기준 카운터·샘플 시각은 백엔드가 유효 수집 데이터에서 고정한다.

### 실적·정정

```json
{
  "expectedVersion": 5,
  "entries": [
    {
      "clientEntryId": "00000000-0000-0000-0000-000000000190",
      "kind": "REJECT",
      "qty": 3,
      "reason": "CAP_DAMAGE",
      "occurredAt": "2026-11-12T14:00:05Z"
    }
  ]
}
```

`POST /api/v1/orders/{orderId}/entries`는 최대50개 행을 한 트랜잭션으로 처리한다.

- qty는 항상 양수다. MANUAL 총량은 TOTAL 순합, AUTO는 종료−시작 스냅샷 + 정정 TOTAL − TOTAL_ADJUST_DOWN 순합이다. REVERSAL은 원본 효과를 반대로 적용한다. REJECT 원본의 역분개는 불량 감소, TOTAL_ADJUST_DOWN 역분개는 감소량 복원이다.
- AUTO의 일반 TOTAL 입력은 거절하며 SUBMITTED에서 OPEN 정정 요청에 연결된 TOTAL/TOTAL_ADJUST_DOWN만 허용한다. 시작·종료 스냅샷은 바꾸지 않는다.
- REVERSAL은 같은 지시·동일 qty의 원본1건을1회 취소하고, 역분개의 역분개는 금지한다. SUBMITTED의 모든 수정은 같은 지시 OPEN correctionId에 연결해야 한다.
- 원본 역분개와 대체 실적은 같은 배치로 제출할 수 있어 중간 합계 오류를 피한다. 최종 합계는 총량>=불량>=0, EA 정수를 만족해야 한다.
- CLIENT_ENTRY_ID가 모두 존재하고 서버가 계산·저장한 PRODUCTION_ENTRY.request_hash와 요청 의미가 같으면 현재 권한을 확인한 뒤 기존 행을 반환한다. expectedVersion 검사보다 이 비교를 먼저 해 응답 유실 후 재전송을 처리한다. 일부 UUID만 존재하거나 같은 UUID의 qty/kind/지시/정정/사유/발생시각이 다르면409다.
- 마감 후 신규 입력은 거절한다. 마감 전에 커밋된 동일 실적의 재전송은 새 쓰기 없이 기존 결과를 반환할 수 있다.

정정 OPEN/RESOLVED는 관리자가 제어하고 실제 추가/역분개는 담당 작업자가 수행한다. 정정 중지 대기 시간을 WORK_PAUSE에 넣지 않는다.

### 멱등성 한계

31테이블에는 범용 요청 결과 저장소가 없다. 모든 POST에 Idempotency-Key를 붙여 영구 재생을 보장한다고 약속하지 않는다.

- 분석은 `(principal, request_id, operation)`과 고정 입력/감사 스냅샷으로 비교한다. 요청 JSON의 canonical intent hash를 감사 detail에 보관하고 다른 본문은409로 거절한다. 새 분석 입력을 만들기 전에 기존 requestId를 조회한다.
- 실적은 위 CLIENT_ENTRY_ID 비교를 사용한다. 권한 검증과 동일성 비교는 재전송에서도 생략하지 않는다.
- 상태·설정 명령은 expectedVersion을 사용한다. 응답을 잃으면 GET 후 상태를 확인한다. 자동으로 version을 최신으로 바꿔 재전송하지 않는다.
- 초안·마스터·계정 생성은 일반적인 네트워크 자동 재시도 대상이 아니다. 불확실한 생성은 목록/고유 코드/감사 기록으로 확인한다.

## 6. Oracle → Redis → 커넥터 설정 흐름

1. 관리자가 TargetCreate 또는 TargetConfigUpdate를 보낸다. 초기 HTTP/MOCK만 지원하며 비밀 원문·임의 스크립트는 받지 않는다.
2. 백엔드가 대상 subtype·설비·mapping·주소 allowlist·역할을 검사하고 부모 target을 잠근다. expectedVersion/expectedConfigVersion을 확인한다.
3. 부모 config_version을 한 번 증가시키고 EQUIPMENT_CONNECTION/태그 또는 AI_PROVIDER_CONFIG를 갱신한다. 설정 스냅샷·감사를 남기고 ConfigChanged Outbox를 같은 트랜잭션으로 저장한다.
4. 화면 응답은 저장 버전과 적용 버전을 구분한다. CONFIG_REVISION 테이블을 새로 만들지 않는다. HTTP 캐시는 no-store, 게이트웨이 Redis는 초기 TTL60초다.
5. Quartz Worker가 백엔드의 dispatch-batch를 호출한다. **백엔드가 자신의 Outbox를 선택하고 게이트웨이에 전송**한다. Worker는 업무 DB를 직접 읽거나 쓰지 않는다. 원래의 Jobs→Gateway 구상에서 전달 소유권을 백엔드에 모은 상세 선택이며 별도 분산 claim 테이블은 도입하지 않는다.
6. 게이트웨이는 원본 설정을 확인하고 캐시를 무효화·재구성한다. 설비는 desired-state로 커넥터에 적용한 뒤 ACK한다. AI 설정은 캐시에 반영하고 실행 시 해당 버전을 dispatch에 실어 보낸다. 연결 성공과 설정 수락은 다른 상태다.
7. 같은 버전/같은 해시는 재적용 성공, 같은 버전/다른 해시는409, 오래된 변경 이벤트는 SUPERSEDED로 처리한다. 장애로 적용하지 못하면503으로 Outbox 재시도를 유지한다.

설정 변경이 RUNNING/PAUSED AUTO 작업의 의미를 바꾸면 거절한다. 사용중지는 허용하고 수집 허가 갱신을 중단한다. 과거 매핑 행을 물리 삭제하지 않고 이벤트 payload의 승인 당시 스냅샷으로 의미를 보존한다. 최신값·heartbeat 갱신은 config_version을 올리지 않는다.

## 7. 서명 수집 허가와 설정 해시

초 단기 허가는 DB 테이블을 만들지 않고 백엔드가 서명한다. 게이트웨이는 원본의 enabled/deleted/configVersion·허용 인스턴스를 확인받고 토큰을 중계한다.

필수 클레임은 iss, aud=mes-equipment-collection, target_id, config_version, instance_id, scope=telemetry:collect, iat/nbf/exp다. **이번 구체화에서 config_hash를 추가**해 해당 허가가 승인한 매핑 스냅샷을 검증할 수 있게 한다. 이는 서명 계약의 추가 필드이며 DB 스키마 변경이 아니다.

- configHash = SHA-256(RFC8785로 canonicalize한 ResolvedConfig.content)의 소문자 hex. 최신값·런타임 시각·토큰은 content에 포함하지 않는다. decimal과 큰 ID는 문자열로 고정해 숫자 표현 차이를 피한다.
- Backend 원본 조회와 토큰 발급은 같은 대상 잠금/일관된 버전 아래에서 content hash를 계산한다. 게이트웨이가 임의 hash를 제공했다고 그대로 서명하지 않는다.
- 키는 비밀 파일로 주입하는 비대칭 서명 키를 사용하고 허용 알고리즘/kid/issuer/audience를 고정한다. Connector·수집 API는 검증용 공개키를 사용한다. 실제 키 생성·암호 구현은 이번 작업에 포함하지 않는다.
- 갱신5초/유효15초는 초기값이다. 만료 후 신규 수집을 중단하고 재발급 실패로 만료가 늘어나지 않는다. 동일 허가를 다른 인스턴스·대상·버전으로 전용하지 못한다.
- 각 이벤트의 occurredAt은 설비 내부 시계가 아니라 커넥터가 허가 아래 읽은 시각이다. `nbf <= occurredAt < exp`, 수신시각은 `receivedAt <= exp+60초`여야 한다. 서비스 인증은 현재 유효해야 한다.
- exp가 지났다는 이유로 JWT 검증을 전체 비활성화하지 않는다. 서비스 인증과 분리된 collection proof 검증기로 서명·클레임·허가 수명·측정시각·전송 유예를 각각 검사한다.
- 커넥터 시계 오차는 2초 이내를 초기 운용 목표로 두고 미래 occurredAt은 서버 현재시각+2초를 넘으면 거절한다. 토큰 유효 측정 구간 자체를 확장하지 않는다. 하드 실시간·안전 정지는 보장하지 않는다.
- 정상 운영 중 이전 설정이 되더라도 당시 서명 hash와 batch의 content가 일치하면 승인된 스냅샷임을 확인할 수 있다. mappingId/설비/target의 소유권과 계약 버전도 검사한다. 스냅샷이 없거나 검증할 수 없으면 ConfigVersionUnavailable이다. 최신 매핑으로 과거 데이터를 재해석하지 않는다.
- 오래된 증빙과 바뀐 설정을 임의로 섞거나 새 토큰을 과거 이벤트에 붙이는 것은 거절한다. 토큰 원문은 HTTP 전송/제한된 메모리 버퍼에만 두고 DB payload·Outbox·로그에는 저장하지 않는다.

## 8. 수집 API와 ACK

`POST Backend /internal/v1/telemetry/batches`

```text
ServiceBearer (현재 서비스 인증)
body.collectionAuthorization (측정 당시 서명 토큰)
body.configuration (서명 configHash에 묶인 승인 설정 스냅샷)
body.events[] (mes.telemetry.collected.v1)
```

배치당 이벤트100개, 이벤트당 metric50개, 본문 최대1MiB를 초기 상한으로 제안한다. 하나의 배치는 같은 target/instance/config/hash/collectionAuthorization을 사용한다. 서로 다른 토큰의 이벤트는 별도 배치다.

1. 서비스 인증·바인딩, 토큰·시각·hash, 이벤트 스키마·대상/버전, mappingId/metricCode/단위/타입·값 범위를 검사한다. 같은 metricCode는 이벤트 내1개만 허용한다.
2. TELEMETRY_METRIC의 현 DDL은 숫자/문자 중 정확히 하나를 요구한다. 값 자체가 없는 읽기 실패에 0을 만들어 넣지 않는다. 유효한 원천값과 BAD/UNCERTAIN을 전달하거나, 값이 전혀 없으면 해당 샘플을 보내지 않고 상태 오류를 보고해 COMM_LOST가 판정되게 한다.
3. 배치는 전부 유효할 때만 원자적으로 저장한다. 일부만 저장하는 207 응답은 사용하지 않는다. 오류 details에 실패 eventId/metricCode를 주되 비밀은 제거한다. 한 이벤트가 영구 오류면 클라이언트는 그 이벤트를 격리하고 나머지를 새 배치로 보낼 수 있다. 서버가 임의로 누락시키지 않는다.
4. eventId와 `(equipment_id, stream_id, sequence_no)`를 모두 검사한다. 동일 식별자·다른 정규화 본문은409다. SHA-256 검증은 서버가 canonical event에 대해 계산하며 클라이언트가 제공한 hash를 그대로 신뢰하지 않는다.
5. 새 이벤트·metric·TelemetryAccepted Outbox를 한 트랜잭션으로 저장한 뒤 ACK한다. receivedAt/telemetryId는 서버가 부여한다. 중복은 원래 receivedAt/telemetryId를 반환한다.
6. 지연·역순 이벤트는 이력으로 보존하되 latest 포인터를 과거로 되돌리지 않는다. AUTO 실적에 원천 delta를 더해 스냅샷 차이와 이중 합산하지 않는다.

```json
{
  "items": [
    {
      "eventId": "00000000-0000-0000-0000-000000000064",
      "telemetryId": "601",
      "outcome": "STORED",
      "receivedAt": "2026-11-12T14:00:06Z"
    }
  ],
  "correlationId": "00000000-0000-0000-0000-0000000000ca"
}
```

유예60초가 끝난 이벤트는 재전송해도 허용 시간이 늘어나지 않는다. 원천 보관7일은 이미 저장된 데이터 정책이며, 새 전송을7일까지 허용한다는 뜻이 아니다. 커넥터 메모리 버퍼1만 이벤트 초기안은 종료 시 손실될 수 있고 무손실을 보장하지 않는다.

## 9. AI 분석 작업과 결과

1. ADMIN 또는 예약 Worker가 requestId·설비·제공자 targetId·기간·분석종류를 전송한다. 현재 정책은 MOCK만 기본 사용한다. 기간은 최대7일이며 데이터 부족·정책·권한을 확인한다.
2. Backend는 ANALYSIS_SNAPSHOT과 INTEGRATION_JOB, AnalysisRequested Outbox를 저장하고202 + jobId를 반환한다. 분석 input은 period/coverage/inputHash로 고정한다.
3. dispatch-batch가 게이트웨이→AI 커넥터로 AiDispatch를 전달한다. 전달에는 임의 URL이 아니라 등록된 구성 스냅샷·고정 입력 descriptor가 들어간다. 게이트웨이는 실제 모델을 실행하지 않는다.
4. 커넥터는 백엔드에서 고정 스냅샷을 받고 hash를 확인한다. 실행 전에 RUNNING 상태를 보고하고 현재 job/attempt·취소 여부를 확인받는다.
5. 202 JobReceipt는 커넥터 메모리에 실행이 접수됐다는 뜻이며 durableCompletion=false다. 영구 책임은 백엔드의 작업에 남는다. 프로세스 종료 시 작업 감시가 기한/현재 attempt를 확인하고 실패/불확실 상태로 전환한다. 일반 API 재전송으로 외부 추론을 무조건 재실행하지 않는다.
6. 성공 콜백은 AnalysisCompleted 이벤트를 `/internal/v1/analyses/{jobId}/results`로 보낸다. callback URL을 클라이언트가 임의 지정하지 않고 내부 고정 주소를 쓴다.
7. Backend는 경로 jobId·현재 attempt·상태·기한·inputHash·configHash·provider/model·분석 기간·Mock 표시를 검증하고 AI_ANALYSIS_RESULT와 작업 상태를 원자적으로 반영한다. accepted와 resultId는 서버만 정한다.
8. `(jobId,attempt)` 동일 결과 재전송은 기존 결과 반환, 다른 내용은409다. 과거 attempt/취소/기한 초과의 결과는 accepted=false로 보관하고 현재 성공으로 바꾸지 않는다. 미래 attempt는 거절한다.
9. 실패 콜백은 status API로 FAILED/TIMED_OUT/NOT_CONFIGURED/INSUFFICIENT_DATA를 보고한다. SUCCEEDED는 검증된 결과 API만 설정한다. 과거 시도의 상태 보고는 감사 후 무시한다.
10. 현재 attempt 상태는 INTEGRATION_JOB 한 행에 있다. 별도 AI_ATTEMPT 테이블이나 영구 시도 전체 상세 조회를 약속하지 않는다. 이전 결과는 AI_ANALYSIS_RESULT, 상태 변화 근거는 감사·Outbox에서 추적한다.

재시도는 ADMIN이 FAILED/TIMED_OUT에 명시 요청하며 attempt를 증가시킨다. 스냅샷·제공자 설정 버전은 유지하고 설정이 달라졌으면 새 작업을 만든다. 실제 외부 실행의 완료 여부가 불명확하면 allowDuplicateExecution 없이는409다. 이 필드가 있더라도 기본 CLOUD 금지 정책을 우회하지 못한다. accepted=true인 Mock 결과도 실제 예측 검증 완료로 표시하지 않는다.

## 10. 이벤트 스키마와 전달

공통 envelope는 eventId, type, schemaVersion, source, occurredAt, correlationId, data이며 causationId는 선택이다. 수신자가 type/schemaVersion을 검증하고 모르는 종류·버전은 명시적으로 거절/격리한다. 이벤트 발생 사실의 occurredAt과 DB receivedAt을 구분한다.

| type | 생산자 → 소비자 | 핵심 data |
|---|---|---|
| mes.telemetry.collected.v1 | 설비 커넥터 → 수집 API | 설비/대상/인스턴스·stream/sequence·configVersion/hash·metrics |
| mes.telemetry.accepted.v1 | Backend Outbox → 경보·읽기 모델 | 원천eventId·telemetryId·발생/수신 시각·필요한 metric 사본 |
| mes.integration.config.changed.v1 | Backend Outbox → Gateway | targetId·configVersion·승인된 비밀 없는 구성 스냅샷 |
| mes.alarm.changed.v1 | Backend Outbox → 알림 처리 | 경보/설비/규칙·상태·변경종류·version·중요도 |
| mes.notification.created.v1 | Backend Outbox → 수신자/전달 처리 | notificationId·제목/본문·recipientIds·원인 참조 |
| mes.notification.read.v1 | Backend → 본인 UI 동기화 | 알림/사용자·readAt. DB 읽음 저장이 원본이며 선택적 실시간 이벤트 |
| mes.ai.analysis.requested.v1 | Backend Outbox → Gateway → AI | jobId/attempt·입력descriptor·제공자 구성·deadline |
| mes.ai.analysis.completed.v1 | AI 커넥터 → Backend | 제안 결과·근거·input/config hash·Mock 표시. accepted 없음 |
| mes.ai.analysis.recorded.v1 | Backend Outbox → 알림/조회 | resultId·jobId/attempt·accepted·mockFlag·risk |
| mes.integration.job.state.changed.v1 | Backend Outbox → 운영 조회 | jobId/attempt·현재 state/version·오류 code |

Kafka/브로커를 설치했다는 뜻은 아니다. 동일 스키마를 초기 내부 HTTP/Outbox 처리에 사용하고 후속 브로커에서도 재사용할 수 있다. topic/partition 수·분산 소비자는 아직 선정하지 않는다.

- 이벤트 하나는 최소 한 번 전달될 수 있다. 수집·분석·알림 각 도메인의 UQ와 version으로 중복/역순을 처리한다. 모든 소비자를 위한 범용 Inbox 테이블은 현재 기준에 없다.
- NotificationCreated 소비자는 현재 recipient·content 행과 event ID를 확인하고 NOTIFICATION_RECIPIENT를 중복 삽입하지 않는다. 여러 수신자의 원본 이벤트를 각 브라우저에 그대로 전송하지 않는다.
- DELIVERY는 `(notification_id,recipient_id,channel)` 유일성이다. 개인 수신자만 기본 단위로 삼고 수신자 쌍 존재를 API에서 확인한다. 공용 채팅방 fan-out은 현재 DDL로 목적지당1회 보장하지 못하므로 후속 설계로 남긴다. 앞선 34테이블의 destination 기반 UQ를 적용했다고 주장하지 않는다.
- 알림 읽음은 최초 readAt을 보존하는 멱등 PUT이며 경보 ack와 분리한다. 외부 발송 성공/실패는 읽음 상태를 바꾸지 않는다.
- Outbox 실제 status는 PENDING/DELIVERED/FAILED, Delivery는 PENDING/SENT/FAILED다. 현재 DDL에 SENDING/claim/lease 컬럼은 없다. 단일 Worker와 비중복 Job 실행, 짧은 DB 트랜잭션·외부 호출 분리로 시작한다.
- 단일 소비자 계약이므로 중복 프로세스가 생기면 운영 오류로 보고 중단한다. 다중 Worker가 필요하면 새로운 claim/lease/소비자 상태 설계가 선행돼야 한다.
- Outbox 재처리는 업무 생산을 다시 수행하는 것이 아니다. 게이트웨이 적용·알림 생성·전달 상태를 재확인한다. 외부 이메일/채팅이 실제 전달된 직후 장애가 나면 제공자 멱등 지원 없이는 중복 발송 가능성이 있다.

## 11. 스키마와 실제 DDL의 경계

계약 JSON은 31테이블의 컬럼/상태에 연결했다. 물리 DB가 다음 규칙까지 자동 보장하는 것은 아니다.

- 담당자 역할, mapping 대상·버전, TOTAL_ADJUST_DOWN의 상태·수량·담당 범위는 서비스 트랜잭션 검증 대상이다. 최신 병합 DDL은 같은 지시의 정정/역분개, 중지/경보의 같은 설비, 알림 수신자 쌍을 복합 FK로 보강했고 작업당 AI 채택 결과 최대1건을 조건부 UQ로 보장한다. API도 이 관계와 현재 상태를 다시 확인한다.
- 이전 34테이블의 immutable config revision/복합 FK/채팅 목적지 UQ/AI 시도 테이블은 병합 완료가 아니다. 승인 당시 설정은 서명 configHash·Outbox/수집 payload·감사 스냅샷으로 보존한다.
- **날짜 매핑:** 최신 ddl/01_tables.sql과 공유 ddl-31의 DAILY_PRODUCTION.business_date는 DATE로 정정돼 있다. API의 YYYY-MM-DD를 KST 영업일 자정의 DATE 키로 바인딩한다. 날짜 키를 UTC 순간으로 변환해 하루를 바꾸거나 숫자 YYYYMMDD로 저장하지 않는다. DDL 자체는 이번 작업에서 변경하지 않았다.
- 실적 재전송은 승인된 PRODUCTION_ENTRY.request_hash를 사용한다. 서버가 orderId·인증된 recordedBy·kind·qty·정정/역분개 참조·사유·UTC 발생시각의 canonical intent를 해시해 저장하고 비교하며, expectedVersion과 추적 ID는 의도 해시에서 제외한다. 분석 요청은 별도 request_hash 컬럼이 없으므로 고정 입력·감사 detail·Outbox payload의 원래 의도를 비교한다. 이 복구·보존과 조회 성능은 구현 시 검증해야 한다.
- VARCHAR2(2000 CHAR)는 실제 바이트 상한 때문에 모든 한글2000자를 저장한다고 가정할 수 없다. API의 일반 본문/사유는 최대1000자로 제한하고 인코딩 바이트 길이도 검사한다. 큰 근거는 기존 CLOB JSON으로 보낸다.
- 서명 configHash는 기본 클레임에 더한 구체화다. RFC8785 canonicalization·키 교체·서명 검증·시계 오차는 실제 .NET 구성에서 통합 검증한다.

## 12. 검증 범위와 구현 순서

계약 파일의 OpenAPI 문법·참조, JSON Schema 자체, 정상/거절 예제, 경로별 권한·scope·DB 테이블 매핑을 로컬 검사했다. **OpenAPI 5개·88개 operation, DTO 스키마114개·이벤트10개, 정상 예제26개 통과·거절 예제17개 거절**을 확인했다. 33개 컬럼 주석 매핑과 모든 operation의 DB 테이블 참조도 현31테이블 DDL과 대조했다. [검증 보고서](../contracts/v1/validation-report.json)를 함께 보관한다.

검증 중 네트워크 연결을 차단했고 API/유료 제공자 호출은0건이다. 이는 실행 중인 서버에 HTTP 요청을 보내거나 실제 서명을 발급해 검증한 결과가 아니다. 역할/행 범위/토큰 서명·시계·멱등 상태 머신은 실제 백엔드 구현 후 통합 테스트해야 한다.

실제 서버 구현 후 필요한 검증:

1. WORKER가 다른 지시·설비·알림을 조회/변경하지 못하고 ADMIN도 생산입력에 접근하지 못함.
2. 동시 version 충돌·설비 점유·실적 같은 UUID 재전송·다른 본문 충돌·원장 배치 원자성.
3. CSRF와 사용자별 세션 전달, 마지막 관리자 보호·비활성화 세션 취소.
4. 토큰 변조·다른 target/instance/hash·만료 후 신규 측정·exp+60초 초과·새 토큰 재부착 거절.
5. 동일 버전/다른 config hash 거절, 게이트웨이/Redis 재시작·원본 장애·사용중지 수집 중단.
6. 수집 ACK 유실·부분 불량 배치·지연 이벤트·COMM_LOST·AUTO 제출 검증.
7. Mock/미설정/실패/취소·늦은 결과와 비용 금지 정책. 어떠한 경로도 CLOUD 실행이나 외부 전달을 자동 활성화하지 않음.
8. 알림 읽음/경보 확인 구분, 대상별 발송 중복·개인정보 노출 방지, Outbox 재처리.

권장 첫 수직 구현: 로그인 → 관리자 초안·발행 → 작업자 점검·MANUAL 실적·제출 → 관리자 마감. 이후 시뮬레이터/수집 허가/경보/Mock AI 순서로 연결한다. 실제 외부 AI·메일·채팅은 별도 지시 전까지 비활성으로 유지한다.

## 공식 참고

- [OpenAPI 3.1.1](http<LOCAL_PATH>): HTTP 계약과 security/schemas 표현.
- [RFC9457](http<LOCAL_PATH>): HTTP Problem Details 오류 형식.
- [RFC8785](http<LOCAL_PATH>): 서명·hash용 JSON canonicalization.

인증·권한·기간·비용 금지·멱등성의 세부 규칙은 이 프로젝트의 설계 선택이며 OpenAPI 자체가 실행해주는 기능은 아니다.
