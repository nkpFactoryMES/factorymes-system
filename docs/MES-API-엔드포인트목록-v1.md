# MES API 전체 경로·권한 목록 v1.0

기준: 사용자 확정 v4 + outputs/ddl 31테이블. API 설계 계약이며 구현/호출/배포는 하지 않았다. 비용 정책은 CLOUD/외부 발송 비활성, MOCK 기본이다.

[계약 기준·공통 규칙](MES-API-이벤트-계약기준-v1.md) · [공통 DTO 정의](../contracts/v1/mes.schemas.json) · [이벤트 스키마](../contracts/v1/mes.events.schema.json)

DB 숫자 ID는 문자열, 연동 targetId는 target_code다. 요청·응답 이름은 JSON Schema $defs를 뜻한다. 목록 응답은 items/nextCursor wrapper를 사용한다. 추가 검증 조건은 아래 각 경로에 명시했다.

## 관리자·작업자 → 업무 백엔드 — 65개

[OpenAPI 원본](../contracts/v1/public.openapi.json)

| Method / 경로 | 권한 / 호출자 | 요청 → 성공 응답 | DB 소유 테이블 |
|---|---|---|---|
| `GET /api/v1/auth/csrf` | 로그인 전 허용; 로그인도 CSRF 필요 | `본문 없음` → 200 `CsrfToken` | 없음 |
| `POST /api/v1/auth/login` | 로그인 전 허용; 로그인도 CSRF 필요 | `LoginRequest` → 200 `Session` | USER_ACCOUNT, USER_ROLE |
| `POST /api/v1/auth/logout` | ADMIN, WORKER | `본문 없음` → 204 `본문 없음` | USER_ACCOUNT |
| `GET /api/v1/auth/me` | ADMIN, WORKER | `본문 없음` → 200 `Session` | USER_ACCOUNT, USER_ROLE |
| `POST /api/v1/auth/password` | ADMIN, WORKER | `PasswordChange` → 204 `본문 없음` | USER_ACCOUNT |
| `GET /api/v1/users` | ADMIN | `본문 없음` → 200 `User` | USER_ACCOUNT, USER_ROLE |
| `POST /api/v1/users` | ADMIN | `UserCreate` → 201 `User` | USER_ACCOUNT, USER_ROLE |
| `PUT /api/v1/users/{userId}` | ADMIN | `UserUpdate` → 200 `User` | USER_ACCOUNT, USER_ROLE |
| `GET /api/v1/products` | ADMIN, WORKER | `본문 없음` → 200 `Product` | PRODUCT |
| `POST /api/v1/products` | ADMIN | `ProductCreate` → 201 `Product` | PRODUCT |
| `PUT /api/v1/products/{productId}` | ADMIN | `ProductUpdate` → 200 `Product` | PRODUCT |
| `GET /api/v1/lines` | ADMIN, WORKER | `본문 없음` → 200 `Line` | LINE |
| `POST /api/v1/lines` | ADMIN | `LineCreate` → 201 `Line` | LINE |
| `PUT /api/v1/lines/{lineId}` | ADMIN | `LineUpdate` → 200 `Line` | LINE |
| `GET /api/v1/equipment` | ADMIN, WORKER | `본문 없음` → 200 `Equipment` | EQUIPMENT |
| `POST /api/v1/equipment` | ADMIN | `EquipmentCreate` → 201 `Equipment` | EQUIPMENT |
| `PUT /api/v1/equipment/{equipmentId}` | ADMIN | `EquipmentUpdate` → 200 `Equipment` | EQUIPMENT |
| `GET /api/v1/dashboard` | ADMIN, WORKER | `본문 없음` → 200 `Dashboard` | WORK_ORDER, CONNECTOR_RUNTIME, ALARM, DAILY_PRODUCTION |
| `GET /api/v1/orders` | ADMIN, WORKER | `본문 없음` → 200 `Order` | WORK_ORDER |
| `POST /api/v1/orders` | ADMIN | `OrderCreate` → 201 `Order` | WORK_ORDER, WORK_CHECK_ITEM |
| `GET /api/v1/orders/{orderId}` | ADMIN, WORKER | `본문 없음` → 200 `Order` | WORK_ORDER, WORK_CHECK_ITEM, PRODUCTION_ENTRY |
| `PUT /api/v1/orders/{orderId}/draft` | ADMIN | `OrderDraftUpdate` → 200 `Order` | WORK_ORDER, WORK_CHECK_ITEM |
| `POST /api/v1/orders/{orderId}/release` | ADMIN | `Command` → 200 `Order` | WORK_ORDER, ORDER_TRANSITION, WORK_PAUSE, OUTBOX_EVENT |
| `POST /api/v1/orders/{orderId}/cancel` | ADMIN | `ReasonCommand` → 200 `Order` | WORK_ORDER, ORDER_TRANSITION, WORK_PAUSE, OUTBOX_EVENT |
| `POST /api/v1/orders/{orderId}/close` | ADMIN | `ReasonCommand` → 200 `Order` | WORK_ORDER, ORDER_TRANSITION, WORK_PAUSE, OUTBOX_EVENT |
| `POST /api/v1/orders/{orderId}/start` | WORKER | `Command` → 200 `Order` | WORK_ORDER, ORDER_TRANSITION, WORK_PAUSE, OUTBOX_EVENT |
| `POST /api/v1/orders/{orderId}/pause` | WORKER | `PauseRequest` → 200 `Order` | WORK_ORDER, ORDER_TRANSITION, WORK_PAUSE, OUTBOX_EVENT |
| `POST /api/v1/orders/{orderId}/resume` | WORKER | `Command` → 200 `Order` | WORK_ORDER, ORDER_TRANSITION, WORK_PAUSE, OUTBOX_EVENT |
| `POST /api/v1/orders/{orderId}/submit` | WORKER | `SubmitRequest` → 200 `Order` | WORK_ORDER, ORDER_TRANSITION, WORK_PAUSE, OUTBOX_EVENT |
| `PUT /api/v1/orders/{orderId}/checklist` | WORKER | `ChecklistUpdate` → 200 `Order` | WORK_ORDER, WORK_CHECK_ITEM |
| `GET /api/v1/orders/{orderId}/entries` | ADMIN, WORKER | `본문 없음` → 200 `Entry` | PRODUCTION_ENTRY, WORK_ORDER |
| `POST /api/v1/orders/{orderId}/entries` | WORKER | `EntryBatch` → 200 `EntryBatchResult` | PRODUCTION_ENTRY, WORK_ORDER, CORRECTION_REQUEST |
| `GET /api/v1/orders/{orderId}/corrections` | ADMIN, WORKER | `본문 없음` → 200 `Correction` | CORRECTION_REQUEST, WORK_ORDER |
| `POST /api/v1/orders/{orderId}/corrections` | ADMIN | `CorrectionRequest` → 201 `Correction` | CORRECTION_REQUEST, WORK_ORDER |
| `POST /api/v1/corrections/{correctionId}/resolve` | ADMIN | `CorrectionResolve` → 200 `Correction` | CORRECTION_REQUEST, WORK_ORDER |
| `GET /api/v1/integration-targets` | ADMIN | `본문 없음` → 200 `Target` | INTEGRATION_TARGET, CONNECTOR_RUNTIME |
| `POST /api/v1/integration-targets` | ADMIN | `TargetCreate` → 201 `Target` | INTEGRATION_TARGET, EQUIPMENT_CONNECTION, EQUIPMENT_TAG_MAPPING, AI_PROVIDER_CONFIG, OUTBOX_EVENT |
| `GET /api/v1/integration-targets/{targetId}` | ADMIN | `본문 없음` → 200 `Target` | INTEGRATION_TARGET, EQUIPMENT_CONNECTION, AI_PROVIDER_CONFIG |
| `PUT /api/v1/integration-targets/{targetId}/configuration` | ADMIN | `TargetConfigUpdate` → 200 `Target` | INTEGRATION_TARGET, EQUIPMENT_CONNECTION, EQUIPMENT_TAG_MAPPING, AI_PROVIDER_CONFIG, OUTBOX_EVENT |
| `PUT /api/v1/integration-targets/{targetId}/state` | ADMIN | `TargetStateUpdate` → 200 `Target` | INTEGRATION_TARGET, OUTBOX_EVENT |
| `POST /api/v1/integration-targets/{targetId}/connection-test` | ADMIN | `ConnectionTestRequest` → 200 `ConnectionTestResult` | INTEGRATION_TARGET, AUDIT_EVENT |
| `GET /api/v1/alarm-rules` | ADMIN, WORKER | `본문 없음` → 200 `Rule` | ALARM_RULE, WORK_ORDER |
| `POST /api/v1/alarm-rules` | ADMIN | `RuleWrite` → 201 `Rule` | ALARM_RULE |
| `PUT /api/v1/alarm-rules/{ruleId}` | ADMIN | `RuleUpdate` → 200 `Rule` | ALARM_RULE |
| `GET /api/v1/alarms` | ADMIN, WORKER | `본문 없음` → 200 `Alarm` | ALARM, WORK_ORDER |
| `GET /api/v1/alarms/{alarmId}` | ADMIN, WORKER | `본문 없음` → 200 `Alarm` | ALARM |
| `POST /api/v1/alarms/{alarmId}/ack` | ADMIN, WORKER | `Command` → 200 `Alarm` | ALARM |
| `POST /api/v1/alarms/{alarmId}/actions` | ADMIN, WORKER | `ActionCreate` → 201 `AlarmAction` | ALARM, ALARM_ACTION |
| `POST /api/v1/alarm-actions/{actionId}/complete` | ADMIN, WORKER | `ActionComplete` → 200 `AlarmAction` | ALARM, ALARM_ACTION |
| `POST /api/v1/alarms/{alarmId}/close` | ADMIN | `ReasonCommand` → 200 `Alarm` | ALARM_RULE, ALARM, ALARM_ACTION, OUTBOX_EVENT |
| `POST /api/v1/alarms/{alarmId}/force-close` | ADMIN | `ReasonCommand` → 200 `Alarm` | ALARM_RULE, ALARM, ALARM_ACTION, OUTBOX_EVENT |
| `GET /api/v1/inbox` | ADMIN, WORKER | `본문 없음` → 200 `Notification` | NOTIFICATION, NOTIFICATION_RECIPIENT |
| `PUT /api/v1/inbox/{notificationId}/read` | ADMIN, WORKER | `본문 없음` → 200 `ReadAck` | NOTIFICATION_RECIPIENT |
| `GET /api/v1/deliveries` | ADMIN | `본문 없음` → 200 `Delivery` | DELIVERY |
| `POST /api/v1/deliveries/{deliveryId}/retry` | ADMIN | `ReasonCommand` → 200 `Delivery` | DELIVERY |
| `POST /api/v1/analyses` | ADMIN | `AnalysisRequest` → 202 `IntegrationJob` | INTEGRATION_JOB, ANALYSIS_SNAPSHOT, OUTBOX_EVENT |
| `GET /api/v1/analyses/{jobId}` | ADMIN | `본문 없음` → 200 `IntegrationJob` | INTEGRATION_JOB, AI_ANALYSIS_RESULT |
| `POST /api/v1/analyses/{jobId}/cancel` | ADMIN | `ReasonCommand` → 200 `IntegrationJob` | INTEGRATION_JOB, OUTBOX_EVENT |
| `POST /api/v1/analyses/{jobId}/retry` | ADMIN | `AnalysisRetry` → 202 `IntegrationJob` | INTEGRATION_JOB, OUTBOX_EVENT |
| `GET /api/v1/reports/daily-production` | ADMIN | `본문 없음` → 200 `DailyProduction` | DAILY_PRODUCTION |
| `GET /api/v1/job-runs` | ADMIN | `본문 없음` → 200 `JobRun` | JOB_RUN |
| `GET /api/v1/job-runs/{jobRunId}` | ADMIN | `본문 없음` → 200 `JobRun` | JOB_RUN |
| `POST /api/v1/jobs/{jobKey}/runs` | ADMIN | `BatchRunRequest` → 202 `JobRun` | JOB_RUN |
| `GET /api/v1/audit-events` | ADMIN | `본문 없음` → 200 `AuditEvent` | AUDIT_EVENT |
| `GET /api/v1/execution-policy` | ADMIN | `본문 없음` → 200 `ExecutionPolicy` | 없음 |

경로별 검증·처리 조건:

- **getCsrf**: Sets antiforgery cookie. Login also requires CSRF and Origin validation. Not a cloud API key.
- **login**: Uniform invalid credentials response; lockout/rate limit. Set Secure HttpOnly session cookie. No password/token echoes.
- **logout**: Current session only; expire cookie. No collection shutdown.
- **getSession**: Authenticated user only; require at least one valid role. DB permits ADMIN/WORKER combinations. ADMIN presence denies worker production commands even if WORKER is also present.
- **changePassword**: Verify current password; update security_stamp; revoke current session; login again.
- **listUsers**: 기본 역할/서비스 인증, 공통 입력 검증·행 범위·삭제 제한을 적용한다.
- **createUser**: Initial role created atomically; library password hash only. No public signup. Demo accounts use separate roles; DB-level multi-role support is not removed.
- **updateUser**: Last active ADMIN protection; no role/assignment mismatch for open orders; update security_stamp on role/active change. ADMIN-containing accounts cannot execute worker production commands.
- **listProduct**: ADMIN all. WORKER only reference rows associated with own orders; no cross-equipment telemetry access.
- **createProduct**: Code unique. No automatic retry guarantee for creation; query by code after uncertain response.
- **updateProduct**: expectedVersion required. Reject identity/unit/line reassignment after history. No physical DELETE.
- **listLine**: ADMIN all. WORKER only reference rows associated with own orders; no cross-equipment telemetry access.
- **createLine**: Code unique. No automatic retry guarantee for creation; query by code after uncertain response.
- **updateLine**: expectedVersion required. Reject identity/unit/line reassignment after history. No physical DELETE.
- **listEquipment**: ADMIN all. WORKER only reference rows associated with own orders; no cross-equipment telemetry access.
- **createEquipment**: Code unique. No automatic retry guarantee for creation; query by code after uncertain response.
- **updateEquipment**: expectedVersion required. Reject identity/unit/line reassignment after history. No physical DELETE.
- **getDashboard**: ADMIN whole factory; WORKER assigned active equipment and own work quantities. Never infer STOPPED from a stale connection.
- **listOrders**: ADMIN all; WORKER authenticated user equals assignee_id. Filter SQL before pagination; no trusting supplied user ID.
- **createOrder**: Server generates orderNo/createdBy/status DRAFT. No cross-order predecessor or batch pipeline. Create is not generically idempotent.
- **getOrder**: ADMIN all; WORKER own assignment only. No WORK_TASK entity.
- **updateOrderDraft**: DRAFT only. Validate active product/equipment/WORKER; MANUAL counterMappingId null; AUTO COUNTER mapping belongs to same equipment.
- **releaseOrder**: DRAFT -> RELEASED; freeze assignment/count mode/checklist. expectedVersion required; unknown-response retry uses GET and reconciliation, not blind replay.
- **cancelOrder**: DRAFT/RELEASED -> CANCELLED; never started; reason required. expectedVersion required; unknown-response retry uses GET and reconciliation, not blind replay.
- **closeOrder**: SUBMITTED -> CLOSED; no OPEN correction; valid totals; actor must not be assignee. expectedVersion required; unknown-response retry uses GET and reconciliation, not blind replay.
- **startOrder**: Own RELEASED -> RUNNING; required checklist complete; equipment free; AUTO GOOD sample <=20 seconds. expectedVersion required; unknown-response retry uses GET and reconciliation, not blind replay.
- **pauseOrder**: Own RUNNING -> PAUSED; keep equipment occupancy; OTHER requires note; related alarm same equipment. expectedVersion required; unknown-response retry uses GET and reconciliation, not blind replay.
- **resumeOrder**: Own PAUSED -> RUNNING; close exactly one open pause; do not reset counter baseline. expectedVersion required; unknown-response retry uses GET and reconciliation, not blind replay.
- **submitOrder**: Own RUNNING/PAUSED -> SUBMITTED; close pause/release occupancy; AUTO validity and totals; target variance needs completionReason. expectedVersion required; unknown-response retry uses GET and reconciliation, not blind replay.
- **checkOrderChecklist**: Own RELEASED only; lock and increment parent WORK_ORDER.version because checklist has no version column.
- **listEntries**: ADMIN or assigned WORKER. Immutable entries.
- **appendEntries**: Own RUNNING/PAUSED or SUBMITTED with OPEN correction. Batch atomic. All duplicate clientEntryIds with same intent returns200 before expectedVersion check; partial duplicates/conflicting intent409. Validate AUTO/MANUAL source and nonnegative totals.
- **listCorrections**: ADMIN or assigned WORKER.
- **requestCorrection**: SUBMITTED only; max1 OPEN; not actual downtime; increment parent order version.
- **resolveCorrection**: OPEN -> RESOLVED; check correction and parent expected versions; no post-close changes.
- **listTargets**: 기본 역할/서비스 인증, 공통 입력 검증·행 범위·삭제 제한을 적용한다.
- **createTarget**: Atomic subtype creation. HTTP simulator / MOCK first. CLOUD enabled=true rejected by local-only execution policy. IDs, configHash/configVersion assigned server-side.
- **getTarget**: 기본 역할/서비스 인증, 공통 입력 검증·행 범위·삭제 제한을 적용한다.
- **updateTargetConfig**: Lock target and child config; compare expectedVersion/configVersion; increment parent config_version once; snapshots in Outbox/audit, no CONFIG_REVISION table. Reject config edits affecting RUNNING/PAUSED AUTO orders. No arbitrary scripts/URLs.
- **setTargetState**: Update desired state and config_version; repeated unknown response needs GET. Disabled config still propagates; no new lease when disabled/deleted. CLOUD disabled by default policy.
- **testTargetConnection**: Short synchronous call <=5 seconds. No persistent job or automatic retry guarantee. External CLOUD probing denied under local-only policy; no AI inference during a connectivity test.
- **listAlarmRules**: ADMIN full; WORKER own current equipment only.
- **createAlarmRule**: HIGH requires mapping/threshold/hold, clearBelow<triggerHigh; COMM_LOST has those fields null; same target/equipment; COMM max1.
- **updateAlarmRule**: Same validation as create; prior alarm snapshots remain unchanged.
- **listAlarms**: ADMIN all; WORKER assigned current equipment alarms. Retain access to alarms explicitly linked to own historical pause; not all equipment history.
- **getAlarm**: Same row-scope as list; out-of-scope returns404.
- **acknowledgeAlarm**: ADMIN or assigned-equipment WORKER. First ack preserved; CLOSED immutable; not inbox read.
- **createAlarmAction**: ADMIN may assign eligible user; WORKER only self within own equipment. Parent alarm expectedVersion; increment parent version.
- **completeAlarmAction**: ADMIN or action owner in allowed equipment; verify action+alarm versions; preserve started/completed times.
- **closeAlarm**: RECOVERED, ack present, >=1 DONE action and no incomplete actions.
- **forcecloseAlarm**: ACTIVE; no incomplete actions, mandatory reason, set rule suppressUntil=serverNow+30min atomically; do not mark recovered.
- **listInbox**: Both roles: authenticated user only; never accept userId override.
- **readNotification**: Idempotent: set timestamp only once. No expectedVersion because row has none. Other recipient404; does not ACK alarm.
- **listDeliveries**: No credential/webhook URL in response.
- **retryDelivery**: FAILED -> PENDING; expectedVersion, attempts retained; external delivery disabled by local policy, no actual external sends in this phase.
- **requestAnalysis**: Operation AI_ANALYZE. Dedup principal+requestId+operation before creating snapshot. Period max7 days; equipment data scope; MOCK default, CLOUD execution blocked.
- **getAnalysis**: Current attempt stored in INTEGRATION_JOB. No AI_JOB or AI_ATTEMPT tables in the baseline.
- **cancelAnalysis**: QUEUED/DISPATCHED/RUNNING -> CANCELLED first; connector cancellation best effort; late result accepted=false.
- **retryAnalysis**: FAILED/TIMED_OUT only; lock/increment attempt; fixed snapshot and target version. Version changed ->409 create new job. Uncertain external completion requires allowDuplicateExecution=true; CLOUD still blocked.
- **getDailyProduction**: KST business day YYYY-MM-DD maps to approved DATE business_date at midnight, without converting this date-only key as a UTC instant.
- **listJobRuns**: 기본 역할/서비스 인증, 공통 입력 검증·행 범위·삭제 제한을 적용한다.
- **getJobRun**: 기본 역할/서비스 인증, 공통 입력 검증·행 범위·삭제 제한을 적용한다.
- **triggerJob**: Allowlisted jobs only; Quartz scheduling integration, no arbitrary SQL/job name. Unknown-response reconciliation by jobKey/scheduledFor; not generic request-key replay.
- **listAuditEvents**: Mask secrets and personal data; do not expose raw tokens.
- **getExecutionPolicy**: Read-only local-only baseline. No API to enable billable CLOUD or external sends. Future activation requires separate user instruction/configuration.

## 내부 서비스 → 업무 백엔드 — 12개

[OpenAPI 원본](../contracts/v1/backend-internal.openapi.json)

| Method / 경로 | 권한 / 호출자 | 요청 → 성공 응답 | DB 소유 테이블 |
|---|---|---|---|
| `GET /internal/v1/integration/targets` | integration-gateway / integration:config:read | `본문 없음` → 200 `ResolvedConfig` | INTEGRATION_TARGET |
| `GET /internal/v1/integration/targets/{targetId}/configuration` | integration-gateway / integration:config:read | `본문 없음` → 200 `ResolvedConfig` | INTEGRATION_TARGET, EQUIPMENT_CONNECTION, EQUIPMENT_TAG_MAPPING, AI_PROVIDER_CONFIG |
| `POST /internal/v1/integration/leases/renew` | integration-gateway / telemetry:lease:issue | `LeaseRequest` → 200 `LeaseResponse` | INTEGRATION_TARGET, CONNECTOR_RUNTIME |
| `POST /internal/v1/telemetry/batches` | equipment-connector / telemetry:write | `TelemetryBatch` → 200 `TelemetryBatchAck` | TELEMETRY_EVENT, TELEMETRY_METRIC, OUTBOX_EVENT, EQUIPMENT_TAG_MAPPING |
| `POST /internal/v1/connector-status` | equipment-connector, ai-connector / integration:runtime:write | `RuntimeReport` → 200 `Ack` | CONNECTOR_RUNTIME |
| `GET /internal/v1/analyses/{jobId}/snapshot` | ai-connector / analysis:input:read | `본문 없음` → 200 `Snapshot` | INTEGRATION_JOB, ANALYSIS_SNAPSHOT |
| `POST /internal/v1/analyses/{jobId}/status` | ai-connector / analysis:result:write | `JobStatusReport` → 200 `JobStatusAck` | INTEGRATION_JOB, OUTBOX_EVENT |
| `POST /internal/v1/analyses/{jobId}/results` | ai-connector / analysis:result:write | `AnalysisCompletedEnvelope` → 200 `ResultAck` | INTEGRATION_JOB, AI_ANALYSIS_RESULT, OUTBOX_EVENT |
| `POST /internal/v1/jobs/outbox/dispatch-batch` | jobs-worker / jobs:outbox:dispatch | `DispatchBatchRequest` → 200 `DispatchBatchResult` | OUTBOX_EVENT, INTEGRATION_JOB, DELIVERY |
| `POST /internal/v1/reporting/rebuild` | jobs-worker / jobs:reporting:run | `RebuildRequest` → 200 `JobRun` | DAILY_PRODUCTION, WORK_ORDER, JOB_RUN |
| `POST /internal/v1/retention/run` | jobs-worker / jobs:retention:run | `RetentionRequest` → 200 `RetentionResult` | TELEMETRY_EVENT, TELEMETRY_METRIC, WORK_ORDER, OUTBOX_EVENT, JOB_RUN |
| `POST /internal/v1/analyses` | jobs-worker / analysis:request | `AnalysisRequest` → 202 `IntegrationJob` | INTEGRATION_JOB, ANALYSIS_SNAPSHOT, OUTBOX_EVENT |

경로별 검증·처리 조건:

- **listInternalTargets**: Includes disabled/deleted tombstones for reconciliation; service target allowlist applied. Config-bearing response private/no-store.
- **getSourceConfiguration**: Source of truth, compiled configuration and hash; no CONFIG_REVISION or secret material. no-store on HTTP caches; Redis owned by gateway.
- **issueCollectionAuthorization**: Check original enabled/deleted/version, configHash, bound connector identity. Asymmetric signed token; 15s lifetime / 5s renew target, receive grace60s. Never persist token or lease rows.
- **ingestTelemetry**: Separate valid service token + measurement-time signed collectionAuthorization. Batch<=100 events,50 metrics/event,1MiB body. All-or-nothing schema/auth/semantic validation; commit before ACK; duplicate detection before insert; other payload409. Backend assigns receivedAt.
- **reportConnectorStatus**: Bind target/instance to authenticated service. reportedAt older than last heartbeat ignored. Heartbeat must not update last_valid_received_at; no caller server-time overrides.
- **getAnalysisSnapshot**: Only assigned job/target; job must not be cancelled or expired. No arbitrary period/SQL query; return fixed snapshot hash.
- **reportAnalysisStatus**: Current attempt and target only; terminal state not reopened. Old attempt status reports audited/ignored. SUCCEEDED only via validated result callback.
- **recordAnalysisResult**: Path jobId==event.data.jobId; validate snapshot/provider/config hash, attempt and period. Lock current job; unique(jobId,attempt); identical duplicate200, changed result409; late result accepted=false. Do not trust incoming accepted flag (not permitted).
- **dispatchOutboxBatch**: Single worker, no overlapping dispatch. Backend selects pending, calls allowlisted gateway/notification handlers, marks DELIVERED on durable responsibility transfer; failures backoff. Gateway config ACK only after apply; AI 202 not completion. Reconcile RUNNING jobs after process loss; no SENDING DB enum.
- **rebuildDailyProduction**: Serialize date scope; replace same day transactionally, closed-only; normalized date conversion documented.
- **cleanupTelemetry**: Cutoff <= serverNow-7days, protect open orders and unprocessed dependent Outbox; metrics first then events; maxEvents bounded.
- **requestScheduledAnalysis**: Principal derived from service, actor null; requestId derived per schedule slot; same dedup rules as public request; MOCK default.

## 백엔드·커넥터 → 연동 게이트웨이 — 6개

[OpenAPI 원본](../contracts/v1/gateway.openapi.json)

| Method / 경로 | 권한 / 호출자 | 요청 → 성공 응답 | DB 소유 테이블 |
|---|---|---|---|
| `GET /internal/v1/configurations/{targetId}` | equipment-connector, ai-connector / integration:config:resolve | `본문 없음` → 200 `ResolvedConfig` | 없음 |
| `POST /internal/v1/integration/config-changes` | mes-backend / integration:config:apply | `ConfigChanged` → 200 `ConfigEventAck` | 없음 |
| `POST /internal/v1/integration/leases/renew` | equipment-connector / telemetry:lease:request | `LeaseRequest` → 200 `LeaseResponse` | 없음 |
| `POST /internal/v1/integration/connection-tests` | mes-backend / integration:connection:test | `ConnectionTestDispatch` → 200 `ConnectionTestResult` | 없음 |
| `POST /internal/v1/ai/jobs` | mes-backend / analysis:dispatch | `AiDispatch` → 202 `JobReceipt` | 없음 |
| `POST /internal/v1/ai/jobs/{jobId}/cancel` | mes-backend / analysis:dispatch | `AiCancel` → 200 `Ack` | 없음 |

경로별 검증·처리 조건:

- **resolveCachedConfiguration**: Bound targets only. Redis TTL60s; source API on miss, limit stampede. Never use stale enabled value as a new collection authorization.
- **applyConfigChange**: Only backend source. Compare original config/version; invalidate and apply newest config; old event SUPERSEDED. 200 only after connector accepts configuration; no promise connection is live. Failure503 leaves Outbox pending.
- **renewConnectorLease**: Validate service instance/target before backend issue; preserve original exp and configHash; no local unsigned renewal.
- **routeConnectionTest**: Verify backend-approved current config; <=5s total deadline. Route to correct registered connector, no caller-controlled arbitrary connector URL. CLOUD and billable probes disabled.
- **routeAiJob**: Fixed job/attempt/config. Target allowlist. No DB access; no model inference. 202 is volatile execution admission, not durable completion; backend job reconciler retains ownership.
- **routeAiCancel**: Backend must persist CANCELLED first; connector call best effort and may not stop provider computation.

## 게이트웨이 → 설비 커넥터 — 2개

[OpenAPI 원본](../contracts/v1/equipment.openapi.json)

| Method / 경로 | 권한 / 호출자 | 요청 → 성공 응답 | DB 소유 테이블 |
|---|---|---|---|
| `PUT /internal/v1/equipment/{targetId}/desired-state` | integration-gateway / equipment:configure | `ConfigApply` → 200 `ConfigAck` | 없음 |
| `POST /internal/v1/equipment/connection-tests` | integration-gateway / equipment:connection:test | `ConnectionTestDispatch` → 200 `ConnectionTestResult` | 없음 |

경로별 검증·처리 조건:

- **applyEquipmentConfiguration**: Path target==configuration content target; same version same hash idempotent; same version different hash409; lower version ignored409. enabled=false stops new collection. Config admission != connected. Lease requested independently before any read.
- **testEquipmentConnection**: Equipment subtype only; approved HTTP endpoint; no PLC writes or AI inference; timeout respects total deadline.

## 게이트웨이 → AI 커넥터 — 3개

[OpenAPI 원본](../contracts/v1/ai.openapi.json)

| Method / 경로 | 권한 / 호출자 | 요청 → 성공 응답 | DB 소유 테이블 |
|---|---|---|---|
| `POST /internal/v1/ai/jobs` | integration-gateway / analysis:execute | `AiDispatch` → 202 `JobReceipt` | 없음 |
| `POST /internal/v1/ai/jobs/{jobId}/cancel` | integration-gateway / analysis:execute | `AiCancel` → 200 `Ack` | 없음 |
| `POST /internal/v1/ai/connection-tests` | integration-gateway / analysis:connection:test | `ConnectionTestDispatch` → 200 `ConnectionTestResult` | 없음 |

경로별 검증·처리 조건:

- **acceptAiJob**: Default MOCK only; CLOUD execution disabled; LOCAL requires explicit configured local runtime. Same job/attempt shares in-memory execution only. Report RUNNING to backend before starting; backend verifies cancelled/current attempt. Fetch fixed snapshot and validate hash.
- **cancelAiExecution**: Best effort cancellation; no terminal state authority; late callback still stored as nonaccepted by backend.
- **testAiConnection**: MOCK local validation. CLOUD disabled; LOCAL health endpoint allowlist only; never trigger inference or external billing to test connectivity.
