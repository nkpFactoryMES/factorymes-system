# 스키마 병합 검토 — 검증된 31개를 기준으로

상태: **사용자가 #3/#5/#6/#8/#9/#10 병합을 승인했으며 31테이블 DDL에 적용 완료. Oracle 재검증은 Claude 대기.**

사용자 확정 기준은 `01_tables.sql` + `02_indexes.sql`의 31개 테이블이다. 별도 `../MES-Oracle-schema-v1.sql`의 34개 안과 설계기준·데이터사전을 읽고 비교했다. 참고안을 기준으로 대체하거나 기존 파일을 수정하지 않는다. WORK_ORDER 통합, TOTAL_ADJUST_DOWN, 영구 실행 허가 테이블 미사용, MVP 축소는 유지한다.

병합 전 Claude 전달 검증: Oracle Free 23 (`gvenzl/oracle-free:23-slim`, FREEPDB1), 01·02 및 99→01→02 오류0, 테이블31/FK54/CHECK379/UQ19/조건부 UNIQUE5/INVALID0, 90_behavior_test.sql 11건(T10 DATE 저장 성공·T11 시간 포함 날짜 거부 포함) 기대 결과. 본 검토 중 DB를 다시 실행하지 않았다. 이 결과는 모든 동시성·서비스 권한·애플리케이션 규칙의 검증 완료를 뜻하지 않는다.

## 비교 결과

**승인된 병합6개 적용 완료**, 보류: 8개, 불필요: 4개, **적용 완료 버그 수정: 1개(#19)**. 승인된6개는 현재 DDL·ERD·데이터사전에 반영했다. 나머지 보류/불필요 항목은 적용하지 않았다. 테이블 수31을 유지하는 제약/컬럼 보강 후보와 테이블 추가가 필요한 확장 후보를 구분한다.

|#|34개 안의 설계 요소·실제 근거|현재 31개와의 차이|분류|근거·병합 시 주의점|
|---|---|---|---|---|
|1|CONFIG_REVISION 불변 이력, (TARGET_ID,CONFIG_VERSION) PK, 버전별 DEVICE_TAG|31은 현재 설정과 config_version만 유지. 구버전 스냅샷 검증 불가 시 지연 수집 거부|**보류**|감사·재현성에는 유용하나 테이블/원천 정리/과거 매핑 정책이 늘어난다. MVP 축소와 31개 기준을 지키고 실제 설정 변경·지연 이벤트 필요성 확인 후 도입. 허가 이력 테이블로 재활용하지 않음|
|2|INTEGRATION_TARGET.current_version 및 AI_JOB.current/accepted_attempt의 DEFERRABLE INITIALLY DEFERRED FK|31에는 이 역방향 포인터·참조 대상 이력 구조가 없음|**보류**|revision/attempt를 도입할 때만 의미 있다. 신규 대상/버전 원자 생성과 순환 FK·커밋 실패·drop 순서 변경이 필요하므로 지연 FK만 먼저 추가하지 않음|
|3|DEVICE_CONNECTION/AI_PROVIDER의 (TARGET_ID,TARGET_KIND) 복합 FK 및 subtype CHECK|31 FK는 존재하는 target만 검사. EQUIPMENT/AI 종류 일치는 API 책임|**적용 완료(사용자 승인)**|테이블 추가 없이 잘못된 subtype 연결을 DB에서 거절 가능. 부모 (target_id,kind) UQ 및 자식 고정 종류 컬럼/복합 FK 필요. 이름·종류코드는 현재 스키마 유지|
|4|TELEMETRY_VALUE의 (EVENT_ID,TARGET_ID,CONFIG_VERSION), (TAG_ID,TARGET_ID,CONFIG_VERSION) FK|31 TELEMETRY_METRIC은 event/map 각각 FK만 있어 같은 대상·버전 강제 불가|**보류**|올바른 개선이지만 현재 매핑에 불변 버전 행이 없어 단순 복사 불가. #1 채택 시 envelope·매핑 버전 모델과 함께 재설계. 단독 컬럼 추가만으로 무결성이 완성된다고 보지 않음|
|5|PRODUCTION_ENTRY의 원본 (ENTRY_ID,WORK_ID,QUANTITY_KIND)·정정 (CORRECTION_ID,WORK_ID) FK|31 reversal_of/correction_id는 다른 지시의 행도 단순 FK로 참조 가능|**적용 완료(사용자 승인)**|동일 지시 정정/역분개를 복합 FK로 보강. WORK_ORDER 통합과 승인된 kind는 유지한다. 현재 kind=REVERSAL을 원본 kind에 그대로 묶으면 정상 역분개도 막히므로 동일 order FK만 우선 적용하고 수량 종류/동일 qty는 앱 검증|
|6|WORK_PAUSE와 ALARM/WORK_TASK의 equipment_id 복합 FK|31 pause는 order/alarm 각각만 참조, 같은 설비 검증은 API|**적용 완료(사용자 승인)**|pause에 equipment_id를 넣고 WORK_ORDER(order_id,equipment_id), ALARM(alarm_id,equipment_id)에 복합 FK로 연결하면 다른 설비 경보 오연결을 차단. WORK_TASK 복원 불필요|
|7|AI_INPUT·AI_JOB·AI_ATTEMPT·AI_RESULT 분리, 시도별 provider_request_id/heartbeat/error|31 ANALYSIS_SNAPSHOT은 이미 입력 분리, INTEGRATION_JOB은 현재 attempt, RESULT는 시도별 결과만 보존|**보류**|새 요소는 입력 분리가 아니라 AI_ATTEMPT 실행 이력이다. 실패/무응답 시도까지 재현할 필요가 생길 때 추가. Mock 중심 MVP에서 새 테이블을 선행 도입하지 않음|
|8|UX_AI_ACCEPTED: IS_ACCEPTED=1일 때 JOB_ID UNIQUE|31 UQ(job_id,attempt)만 있어 같은 job의 여러 attempt가 accepted=1일 수 있음|**적용 완료(사용자 승인)**|현재 AI_ANALYSIS_RESULT(job_id CASE accepted=1)에 조건부 UNIQUE를 추가하면 테이블 증가 없이 채택 결과 최대1건을 DB 보장. 현재 시도·상태 일치는 별도 트랜잭션 검증 유지|
|9|DELIVERY의 (NOTIFICATION_ID,USER_ID)→NOTIFY_RECIPIENT FK|31 notification FK와 recipient 계정 FK가 별개여서 알림 수신자가 아닌 계정에도 발송 행 생성 가능|**적용 완료(사용자 승인)**|현재 NOTIFICATION_RECIPIENT(notification_id,user_id) PK에 DELIVERY(notification_id,recipient_id) 복합 FK 추가. 기존 수신자별 모델 그대로 유지 가능|
|10|PRODUCTION_ENTRY.REQUEST_HASH 및 AI_RESULT.PAYLOAD_HASH|31 실적 client_entry_id UNIQUE는 있지만 실적 요청 해시는 없음. 수집/입력 해시는 이미 존재|**적용 완료(사용자 승인)**|실적 원장 request_hash 저장을 우선 추천. 같은 UUID의 재전송 본문 충돌 판별을 안정화하고 범용 COMMAND_DEDUP은 추가하지 않음. AI 결과 해시는 실제 콜백 재전송 요구에 맞춰 선택적으로 추가|
|11|DELIVERY 목적지 단위 UQ + nullable USER_ID, 채팅방 사용자독립 발송|31 수신자 필수·(알림,수신자,채널) UQ|**보류**|공용방 중복 발송을 줄이지만 미확정 채팅 제공자/그룹 수신 정책에 해당. #9 개인 수신자 FK 보강과 그룹방 모델을 혼동하지 않음|
|12|TELEMETRY_EVENT JSON 반복저장 제거, 정규화 값+hash 위주|31 payload CLOB와 metrics를 모두 저장, 당시 매핑 스냅샷도 payload에 의존|**보류**|용량 이점은 있지만 현재 구버전 검증/재처리 근거를 잃을 수 있음. #1·#4 및 보관정책/실측 용량과 함께 판단|
|13|BAD/UNCERTAIN 측정은 numeric/text 둘 다 NULL 허용|31 TELEMETRY_METRIC CHECK는 품질과 무관하게 값 정확히1개 필수|**보류**|센서 읽기 실패를 이벤트로 표현할 때 유용. 현재는 연결/런타임 오류로 기록할 수 있으므로 첫 어댑터의 품질 계약 확정 후 변경. GOOD값 필수·둘다유값 금지는 유지|
|14|카운터 NUMBER(19,0), 측정 NUMBER(28,8), 긴 본문1000자 제한|31 카운터/측정 NUMBER(18,6), 본문 최대2000 CHAR|**보류**|카운터 정수부12자리 및 멀티바이트 바이트상한을 실장비/입력 정책에서 확인해야 함. 타입 변경은 마이그레이션/ODP.NET 범위에 영향, 시연용으로 임의 확장/축소하지 않음|
|15|WORK_ORDER:WORK_TASK 1:0..1·지시/작업 상태 이중 관리|사용자가 승인한 WORK_ORDER 통합과 상충|**불필요**|독립 지시당1작업의 현재 요구에서 분리 테이블 복원하지 않는다. 배치 파이프라인2차는 별도 설계|
|16|POSTING/REVERSAL + QUANTITY_KIND + SIGNED_QTY 원장 체계|31 양수 qty+TOTAL/REJECT/REVERSAL/TOTAL_ADJUST_DOWN 승인|**불필요**|승인된 원장을 다른 부호 체계로 치환할 필요 없음. #5의 동일 지시 제약만 추출 적용 후보로 둠|
|17|USER_ROLE PK(USER_ID)로 단일 역할만 허용|31 PK(user_id,role)은 두 역할 겸임 행도 허용, 시연 계정은 역할 분리|**불필요**|관리자/작업자2역할이라는 확정만으로 계정당 단일 역할 DB 정책까지 임의 강화하지 않음. 현재 자기마감/대리입력은 API 정책으로 검사|
|18|GENERATED ALWAYS IDENTITY·이름/로그 상태(SENDING) 등 참고안의 일괄 명명/기본값|31 BY DEFAULT IDENTITY와 이름·상태는 실DB 검증됨|**불필요**|기능상 필수 병합이 아닌 스타일 차이. 현재 스키마를 대규모 변경하지 않고 필요 시 개별 운영 정책으로 검토|

### 추가 대조에서 발견한 타입 오류 — 적용 완료

|#|34개 안의 설계 요소·실제 근거|현재 31개와의 차이|분류|근거·병합 시 주의점|
|---|---|---|---|---|
|19|DAILY_PRODUCTION.BUSINESS_DATE DATE 및 날짜 자정 CHECK|최초 DDL NUMBER(18,6) 오류를 Claude가 DATE로 수정, CK_DAY_DATE 유지|**적용 완료(Claude, 버그 수정)**|승인된 v4 DATE 설계와 DDL을 일치시킨 수정이며 새 사용자 설계 결정이 아니다. Oracle Free 23에서 99→01→02 오류0, 테이블31·INVALID0·DATE 확인, T10 DATE 저장/T11 시간 포함 날짜 거부를 포함한 11건 기대 결과(Claude 전달).|

이 항목은 참고안의 기능 확장이 아니라 승인된 설계와 DDL의 불일치 버그 수정이다. Claude가 SQL을 수정·실행 검증했고, 이번 데이터사전·Mermaid·경보/운영 이미지도 DATE로 동기화했다. 나머지 추천6개도 이후 사용자 승인을 받아 DDL에 적용했다. 이 버전의 실제 Oracle 재검증은 아직 대기 중이다.

## 승인된 병합 적용 결과

기존 비교표의 "현재31개와의 차이"는 병합 전 비교 이력이다. 실제 현행 정의는 아래와 01/02 DDL·데이터사전이 기준이다.

|항목|적용한 이름·내용|
|---|---|
|#3|UQ_TGT_ID_KIND; FK_CONN_TARGET_KIND / FK_AIP_TARGET_KIND; 자식 kind NOT NULL·고정값 CHECK. 기존 단일 target FK 제거|
|#5|UQ_ENTRY_ID_ORDER / UQ_CORR_ID_ORDER; FK_ENTRY_REV_ORDER / FK_ENTRY_CORR_ORDER. reversal_of·correction_id는 여전히 nullable, 독립 order FK 유지|
|#6|WORK_PAUSE.equipment_id NOT NULL; UQ_WO_ORDER_EQ / UQ_ALRM_ID_EQ; FK_PAUSE_ORDER_EQ / FK_PAUSE_ALARM_EQ|
|#8|UQ_AIRES_ACCEPTED: accepted=1일 때 job_id 유일|
|#9|FK_DLV_RECIPIENT → NOTIFICATION_RECIPIENT(notification_id,user_id), 중복 단일 notification/user FK2개 제거|
|#10|PRODUCTION_ENTRY.request_hash VARCHAR2(64 CHAR) NOT NULL, CK_ENTRY_HASH_NN|

테이블31개 유지. FK54→53, 일반 UNIQUE19→24, 조건부 UNIQUE 인덱스5→6, 컬럼351→355. 모든 복합 FK의 부모 PK/UQ·컬럼 타입·FK 전체 선두 인덱스·NULL 허용·자식 우선 삭제 순서는 정적 검사했다. NULL을 가진 복합 FK는 Oracle에서 검사 생략되므로 원본이 없는 일반 실적이나 alarm_id 없는 중지는 허용되고, 필수 부모 order FK/NOT NULL로 다른 무결성을 보장한다. 실제 정상 INSERT/다른 지시 거절 테스트는 Claude가 작성·실행한다.

90_behavior_test.sql은 수정하지 않았다. T8은 equipment_id 추가 전용 테스트라 현재 그대로 실행하면 NOT NULL에서 먼저 실패한다. 새 PRODUCTION_ENTRY INSERT에는 request_hash 필수. 기존11건은 병합 전 검증 이력이며 병합 후 통과로 표시하지 않는다. 전체 Oracle 재검증 상태는 README 참조.

CONFIG_REVISION·AI_ATTEMPT·지연 FK 등 보류8개 및 불필요4개는 계속 미적용. 영구 실행 허가 테이블을 복원하지 않았고 WORK_ORDER 통합·승인된 원장 종류를 유지했다.

## 생성 산출물과 정확성 범위

- [ERD-31.mmd](ERD-31.mmd): 31개 테이블·355개 컬럼·실제53 FK. 관계의 NULL/유일성은 현재 병합 DDL 기준(Oracle 재검증 대기). 부모행의 자식 최소1건 존재는 FK만으로 보장되지 않음.
- [데이터사전-31.md](데이터사전-31.md): 실제 컬럼·타입·NULL·PK/UQ/FK·기본값·CHECK·명시 인덱스. 참고안 설명은 업무 의미 확인용으로만 사용, 그 타입/키는 복사하지 않음.
- 영역별 [생산](ERD-31-production.svg) / [연동·AI](ERD-31-integration-ai.svg) / [경보·운영](ERD-31-alerts-ops.svg): 각각 SVG/PNG. 기존 밝은 배경·카드형 스타일. 그림은 주요 키와 인접 실제 FK 요약이며 전체53 FK는 Mermaid/데이터사전에 표시한다.
- Mermaid 원본은 정적 대조만 수행했고 Mermaid 엔진으로 렌더링하지 않았다. SVG/PNG는 동일 DDL 메타데이터로 직접 렌더링한 영역도이며 원본 Mermaid의 렌더 결과라고 표시하지 않는다.


