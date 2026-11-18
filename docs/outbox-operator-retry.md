# 실패 Outbox 운영 재처리 (2026-11-16)

## 목표와 범위
관리자 발송 이력 화면의 Outbox에서 자동 시도 종료 FAILED 이벤트를 재처리한다.
이는 로컬 소비자의 책임 인계를 다시 시도하는 것이고 이메일/채팅 실제 발송을 뜻하지 않는다.
현재 소비하는 alarm.changed/notification.created 타입만 지원하며 설정 전달기·미지원 이벤트를
강제로 성공 처리하지 않는다. 기존31테이블/53FK·개인 알림/발송 이력·계정은 유지한다.

## 비교·선택
OUTBOX_EVENT에는 rowVersion이 없고 AggregateVersion은 업무 엔티티의 이벤트 버전이다.
이를 재시도 때 올리거나 attempts를0으로 초기화하면 원본 의미와 누적 시도 이력을 훼손한다.
그래서 원본/상태의 SHA-256을 관측 토큰으로 사용하고, 재처리 회차 시작 횟수는 기존 감사에 저장한다.
새 컬럼·행 버전·임의 JSON 수정 API를 추가하지 않는다.

무조건 FAILED를 PENDING으로 바꾸면 아직 잘못된 원본을 계속 재전송할 수 있다. 기존 소비자의
구조/원본 정합성 검사를 validateOnly 모드로 먼저 실행한다. 원본이 여전히 잘못됐으면422로
차단하고, 실제 DB/원본 문제를 해결한 뒤 재검토하게 한다. 이벤트 payload를 UI에서 편집하는
방법은 선택하지 않았다. 소비와 재처리의 검증 규칙이 서로 달라지지 않도록 같은 함수를 쓴다.

## API·권한
- GET /api/v1/outbox-events/{id}/retry-state → OutboxRetryState(Event,StateHash,CanRetry,BlockReason).
- POST /api/v1/outbox-events/{id}/retry →202 OutboxRetryState.
- 본문: clientRequestId UUID, expectedStateHash 64자리, reason trim1..1000자.
- ADMIN Cookie+CSRF. 익명401, 작업자403. public 원본 payload/비밀 값을 내보내지 않는다.
- CanRetry는 지원 타입·terminal FAILED·nextAttemptAt=null·아직 인계되지 않음 기준이다.
  전송 때 원본 preflight를 다시 수행하므로 화면상 가능하더라도 정합성 오류422가 날 수 있다.
- BlockReason: ALREADY_HANDED_OFF, UNSUPPORTED_CONSUMER, NOT_FAILED, AUTOMATIC_RETRY_PENDING.

## 상태 토큰·멱등·트랜잭션
StateHash는 ID/EventID/type/aggregate ID·type·version/payload hash/상태/attempts/다음시각/오류/
인계시각을 포함한다. 원문은 노출하지 않는다. 전체 payload의 bytes hash를 사용해 원문 변경도
오래된 조회로 취급한다. AggregateVersion은 그대로 유지한다.

재처리는 Backend Gate → Oracle 해당 Outbox FOR UPDATE → 재전송 감사 확인 → 상태 토큰 비교 →
eligibility → 원본 preflight → PENDING/now/error clear + 감사 COMMIT 순서다. HTTP·외부 발송은 없다.
상태가 바뀌었으면409, 원본 정합성 오류422는 데이터/횟수를 바꾸지 않는다.

감사 outbox.retry.requested는 actor/clientRequestId/expectedStateHash/trim reason/attemptBase를
담는다. 같은 Outbox·UUID·actor·의도면 현재 상태를 반환하며 다시 큐에 넣지 않는다.
다른 내용이면409다. 인계가 끝난 뒤 동일 UUID 응답 미확인 재전송도 안전하다.
새 UUID라도 같은 옛 토큰으로 두 명이 동시에 요청하면 하나만 접수되고 나머지는409다.

attempts는 누적 유지한다. 새 회차에서 attempts-attemptBase를 계산해 최대3회,5초·10초 뒤
자동 재시도를 허용한다. 실패 부분 트랜잭션은 rollback하고 실패 횟수/오류는 별도 TX에 기록한다.
3→4 인계 성공 또는3→6 최종 실패를 실제로 확인한다. 최종 실패 뒤 자동 반복은 없다.

## Mock 시연
관리자 본인 Mock 알림의 기존4가지 발송 시나리오에 다음을 추가한다.
- OUTBOX_FAIL_ONCE: 첫3회 로컬 인계를 의도적으로 실패시킨다. 수동 재처리 뒤 인계 성공.
- OUTBOX_ALWAYS_FAIL: 매 회차3회에서 최종 실패한다. 수동 재처리 뒤 누적6회도 FAILED.
이것은 명시적 로컬 오류 주입이며 실제 외부 장애가 아니다. 원본을 조작하거나 외부 네트워크를
중단하지 않는다. 인계가 성공해 실제 Delivery를 만들 때는 SUCCESS로 정규화한다.
원래4개 SUCCESS/FAIL_ONCE/TRANSIENT_ONCE/ALWAYS_FAIL 동작은 유지한다.

같은 알림의 Delivery는 (notification,recipient,channel) UQ·notification 행 잠금으로 보호하고,
이미 있는 의도/Mock 수신 영수증을 삭제하지 않는다. Outbox 재처리가 수신자 재활성화나
이미 수신한 메시지 재발송을 의미하지 않는다. 실제 Delivery는 PENDING/sentAt=null이며
별도 Mock 영수증 ACCEPTED다. SENT로 가장하지 않는다.

## 화면·협업·검증
GPT: DTO/Backend/회차 예산·preflight/실제 Oracle 검증/공통 빌드·배포/문서.
Claude: Deliveries Outbox 재처리 모달·시나리오/브라우저 시험 및 독립 검토.
충돌이면 사유를 유지하고 최신 상태를 다시 확인한다. 통신 미확인은 같은 요청만 재전송한다.
상태 조회 실패면 변경을 막는다. 원본 편집 기능은 제공하지 않는다.

최종 검증 결과는 아래 완료 항목에 기록한다.

## 한계
현재 로컬 소비자 두 타입만 지원한다. Kafka/DLQ/범용 이벤트 재생 또는 실발송 재전송 기능이
아니다. 원본 구조가 틀린 이벤트는 안전한 원본 정정/재발행 절차가 별도로 필요하다.
DB 직접 수정 권한은 관리자 웹 권한과 다르며 이 API가 제공하지 않는다. 감사 보존/대량 재처리/
다중 노드 부하는 후속이다. 원본·이력·실패 사례를 삭제해 성공률을 꾸미지 않는다.


## 최종 검증·배포
Oracle/API **42항목**, 브라우저 **7항목** PASS. 최종 배포본에서 원본 불변/동시 하나 접수/
state hash·UUID/CSRF·권한/422 preflight/3→4 인계·3→6 실패/후속 정상 진행/중복 의도·수신0을 확인했다.
기존 전달 API66·화면9도 통과했다. 실제 상태 SENT로 바뀐 건은 없고 모든 외부 발송은0이다.
보고서 outbox-retry-verification.json / FactoryMES-browser-outbox-retry-verification.json,
화면 FactoryMES-outbox-retry-screen.png를 보존했다.
Claude 19:19 읽기 전용 독립 검토에서 차단 결함 없음을 확인했다.
규격 초안의 UI8이라는 예상 수 대신 실제 JSON의7항목을 최종 수로 쓴다.
UUID 범위는 Outbox 이벤트 단위이며, 대량 배치는 같은 Gate 때문에 운영자 요청을 잠시 지연시킬 수 있다.
