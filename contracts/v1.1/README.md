# MES API 계약 파일 v1.1

## v1 → v1.1 병합 표

사용자 선택: `api-contracts-v1`을 기준으로 유지하고 `outputs/api`에만 있는 MVP 기능을 대조·병합했다. 원본 v1 및 outputs/api는 수정하지 않았다. 아래 "동등 기능 있음"은 새 경로를 중복 추가하지 않았다는 뜻이며 필요한 설명/필터는 기존 operation에 보강했다.

|항목|추가 / 동등 기능 있음|근거|
|---|---|---|
|조치 시작 OPEN→IN_PROGRESS|추가|POST /api/v1/alarm-actions/{actionId}/start, ActionStart.expectedVersion+expectedAlarmVersion. 기존 complete도 IN_PROGRESS→DONE을 명시|
|경보별 조치 목록|추가|GET /api/v1/alarms/{alarmId}/actions, 기존 AlarmAction DTO의 페이지 응답|
|관리자 DRAFT 점검표 편집|동등 기능 있음|PUT /api/v1/orders/{orderId}/draft의 OrderDraftUpdate.checklist가 정의 편집 수행. 별도 관리자 checklist 경로 불필요|
|작업자 항목별 체크|동등 기능 있음|WORKER 전용 PUT /api/v1/orders/{orderId}/checklist의 items에 1개 항목 전달 가능. ADMIN 정의 수정과 권한/상태 분리|
|GET 점검표|동등 기능 있음|GET /api/v1/orders/{orderId}의 Order.checklist와 parent version에 포함. 중복 GET 경로 추가하지 않음|
|경보 규칙·품목·라인·설비·사용자 단건|추가5개|GET /api/v1/alarm-rules/{ruleId}, /products/{productId}, /lines/{lineId}, /equipment/{equipmentId}, /users/{userId}. 기존 읽기 DTO/ADMIN 권한 재사용|
|품목·라인·설비·사용자 비활성화|동등 기능 있음|기존 각 PUT의 active=false. 종속성·진행업무·참조이력 가드를 동일 적용하고 물리삭제 금지|
|경보 규칙 비활성화|동등 기능 있음|기존 PUT /api/v1/alarm-rules/{ruleId}의 enabled=false. ACTIVE 에피소드의 정상복귀로 위장하지 않음|
|사용자 역할 변경·마지막 관리자 보호|동등 기능 있음|기존 UserUpdate.roles+expectedVersion. 같은 트랜잭션으로 마지막 활성 ADMIN 보호, 409 Problem.code=LastAdministrator를 명시|
|관리자 배정용 작업자 목록|동등 기능 있음(필터 보강)|GET /api/v1/users?role=WORKER&active=true&assignable=true. 기존 User DTO·ADMIN 목록을 재사용, 필터는 페이지네이션 전에 서버 적용. ADMIN 포함 계정은 기준안 정책에 따라 배정 후보 제외|
|분석 작업 목록|추가|GET /api/v1/analyses. operation=AI_ANALYZE 필터와 현재 job/attempt 상태, 기존 IntegrationJob DTO 페이지|
|AI 제공자 설정|동등 기능 있음|기존 integration-targets 등록·PUT /api/v1/integration-targets/{targetId}/configuration의 AI 설정 분기, AiSettings/ResolvedConfig 재사용|
|외부 발송 Dispatch|추가|POST /internal/v1/jobs/deliveries/dispatch-batch. 기존 DispatchBatchRequest/Result 재사용, 단일 Quartz 폴러. externalDeliveryEnabled=false 기본 유지: 외부 호출0·SENT 처리 금지|
|통신 만료 감시 Job|추가|POST /internal/v1/jobs/connections/watch. COMM_LOST 평가·복귀·억제·Outbox 원자 저장, heartbeat를 측정 신선도로 오인하지 않음|
|Outbox pending/dispatch|동등 기능 있음|기존 POST /internal/v1/jobs/outbox/dispatch-batch가 Backend의 대기 조회·전달·상태 갱신 담당. 별도 pending/개별 dispatch 중복 추가 안 함|

### 최종 수와 예제

- OpenAPI5개·전체98 operation: public73, backend-internal14, gateway6, equipment2, ai3. v1의88개에서10개 추가.
- 공통 DTO118개(기존114+ActionStart/AlarmActionPage/AnalysisJobPage/ConnectionWatchResult), 이벤트10개는 변경 없음.
- 정상36개(기존26+새 operation별10), 스키마 거절22개(기존17+4+severity HIGH 거절1). 새 예제 operation 메타데이터에 문서/메서드/경로/요청·응답 종류를 명시해 해당 operation 스키마와 대조했다.
- 권한/상태/서명/마지막 관리자 보호는 스키마만으로 검사할 수 없어 runtimeReviewExamples에 미실행 검토 사례로 구분했다. 스키마 거절 예제가403/409 실제 응답 테스트를 뜻하지 않는다.

### 검증 결과 및 재실행

현재 `validation-report.json.status=partially_validated`. Claude 독립 검증에서 수정 전 v1.1의 OpenAPI5개/98 operation 및 두 공통 Schema의 Draft2020-12 검증이 통과했음을 전달받았다. 이번 severity 수정 뒤에는 아래 로컬 검증을 재실행했으나 이 프로세스의 전용 검증기 import 접근 제한은 여전하므로 수정 후 전용 검증 통과로 표시하지 않았다. JSON 파싱·상대참조1022개·모든 operationId/경로 파라미터·DB33개 컬럼 주석·권한/호출 scope·새 operation 예제 연결·보호된 원본 해시는 정적 검사 통과했다. 제한된 로컬 assertion runner에서도 정상36/거절22이 기대대로 판정됐다. 이 runner는 표준 Draft202012Validator/check_schema 또는 OpenAPI 검증기의 대체가 아니다.

원래 방식의 openapi-spec-validator+jsonschema 재검증을 시도했으나 work/contract-libs의 패키지 파일을 읽을 수 없고, 설치도 네트워크 소켓 정책(WinError10013)에 막혔다. 따라서 전용 검증기를 통과했다고 표시하지 않았다. v1의 기존88개 통과 결과를 변경된98개 검증 결과로 재사용하지 않는다.

원래 검증 방식에 새 operation/예제 커버리지를 더한 `verify_contracts.py`를 함께 제공한다. 의존성이 설치된 Python에서 아래 명령으로 실행한다. 필요하면 `MES_CONTRACT_LIBS`에 읽기 가능한 jsonschema/openapi-spec-validator 라이브러리 폴더를 지정한다. 스크립트는 네트워크를 차단하고 v1.1만 검증/보고서 갱신한다.

```powershell
python outputs/api-contracts-v1.1/verify_contracts.py
```

동일한 `01_tables.sql`에 대해 `ddlSha256=3490745d874651c420ecbbaf789845200bf67862ca41d12d76f9afa28582a625`는 UTF-8 BOM 제거·CRLF/CR→LF 정규화 후 UTF-8 SHA-256, `ddlRawSha256=c6f264c5edab63fb0fba4c6c8e32f8cc258f602d97163affa4b0ce14100c5830`는 원본 CRLF 바이트 SHA-256이다. 두 값은 같은 파일의 정규화 방식 차이다. API 호출·외부 제공자 호출·DB 변경·협업 기록 갱신은 하지 않았다.

### v1.1에서 수정 완료 — severity와 DDL 정렬

Rule/Alarm DTO·이벤트·정상 예제의 severity를 승인된 ALARM_RULE/ALARM DDL CHECK와 같은 INFO/WARN/CRITICAL로 수정했다. severity=HIGH 거절 예제1개를 추가했다. risk(LOW/MEDIUM/HIGH/UNKNOWN)와 alarm change(OPENED/ACKNOWLEDGED/…)는 별개 의미이며 DDL severity CHECK 대상이 아니므로 유지했다. 실행 기본 정책 cloudAiEnabled=false·externalDeliveryEnabled=false 및 Mock도 유지했다.



이 폴더는 **API를 설명하는 로컬 문서·JSON 파일**이다. API 키, 실행 서버, 모델, 유료 서비스 구독이 아니다. OpenAPI는 OpenAI와 다른 공개 명세다.

기준: 사용자 확정 v4 + `outputs/ddl`의31개 테이블. API 구현·DB 마이그레이션은 하지 않았다.

- `public.openapi.json`: 관리자/작업자 공개 API.
- `backend-internal.openapi.json`: 게이트웨이·커넥터·Worker가 호출하는 업무 백엔드 API.
- `gateway.openapi.json`: 연동 게이트웨이가 받는 API.
- `equipment.openapi.json`: 설비 커넥터가 받는 API.
- `ai.openapi.json`: AI 커넥터가 받는 API.
- `mes.schemas.json`: 공통 요청/응답 DTO 정의.
- `mes.events.schema.json`: 수집·설정·경보·알림·분석 이벤트10개 정의.
- `contract-examples.json`: 정상36/거절22개 로컬 예제. 예제 토큰은 가짜이고 로그인/수집에 사용할 수 없다.
- `validation-report.json`: 네트워크 차단 상태의 로컬 검증 결과. 현재 전용 검증기 재실행은 접근 제한으로 blocked이며 부분 검증 상태다.

각 OpenAPI가 공통 스키마를 상대 경로로 참조하므로 폴더 전체를 함께 보관한다. 공개 API만 배포할 때 내부 문서를 외부에 함께 노출하지 않는다.

설명 문서는 상위 폴더의 [계약 기준](../MES-API-이벤트-계약기준-v1.md)과 [전체 경로 목록](../MES-API-엔드포인트목록-v1.md)을 참조한다.

현재 기본 정책은 Mock AI, 클라우드 AI 실행 금지, 외부 이메일/채팅 실발송 금지다. 검증 도구는 오픈소스이며 키 없이 로컬 파일만 검사했다. 실제 유료 제공자를 켜는 작업은 별도 사용자 지시 전까지 하지 않는다.
