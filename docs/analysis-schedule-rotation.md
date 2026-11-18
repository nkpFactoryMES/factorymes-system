# 예약 AI 분석의 20개 초과 순환 처리 (2026-11-16)

## 문제와 완료 조건
기존 예약은 그날 분석 구간의 수집 설비를 ID 순으로 20개만 조회했다.
같은 요청을 재실행해도 앞의 20개를 재조회하므로 21번째 이후 설비는 접수되지 않았다.
한 번의 부하 상한 20을 유지하면서 같은 고정 분석 창의 미접수 설비를 다음 배치에서 처리해야 한다.
실패/무자료 접수도 진척으로 인정하고, 재시작·중복 호출·늦은 낮은 ID 입력에 안전해야 한다.

## 대안과 선택
1. Take(20)만 제거하면 한 요청이 무제한으로 길어지고 잠금/기한/내부 HTTP 제한에 영향을 준다.
2. 메모리 마지막 ID 커서는 재시작 시 사라지고, 늦게 들어온 더 작은 ID를 놓친다.
3. Redis 커서는 휘발성/복구와 Oracle 작업 저장의 이중 기록 문제가 생긴다.
4. 새 Oracle 커서 테이블은 원자성 확보에 쓸 수 있지만 고정 ID 순회에서 지연 입력과 실패 건을
   관리하기 위한 별도 규격이 필요하다.
5. 이미 Oracle에 원자적으로 저장되는 IntegrationJob + analysis.requested 감사 intent를
   처리 원장으로 삼아 미접수 설비만 선택한다. 이번에는 이 방법을 선택했다. 새 DDL 없음.

## 실행 방식
AnalysisService.Scheduling.cs를 Execution에서 분리해 예약 관련 코드만 모았다.
매 호출마다 가장 최근 완료된 02:00 KST 경계의 이전 24시간 [start,end)를 고정한다.
기존 첫 활성 Mock AI 대상 선택을 유지하고, 해당 기간에 자료가 있는 설비 중 아직 같은
대상·예약 서비스·종류·기간의 요청 이력이 없는 ID를 Oracle NOT EXISTS로 최대20개 가져온다.
AUDIT_EVENT의 JSON request.equipmentId/periodEnd를 조회하므로 snapshot이 없는 FAILED도 제외한다.
서버가 생성한 UTC end를 같은 JsonSerializer 옵션으로 직렬화해 비교한다. 사용자 SQL은 받지 않는다.
원시 측정 행이나 전체 대상 ID 목록을 애플리케이션에 읽지 않는다.

QueueCore의 설비+대상+기간 결정적 UUID는 유지한다. 성공·실패·INSUFFICIENT_DATA 모두 접수 이력을
남기므로 특정 불량 설비 때문에 뒤의 설비가 막히지 않는다. 기존 불변 snapshot/설정/결과는 수정하지 않는다.
동일 프로세스의 동시 예약 호출은 ScheduleGate로 직렬화한다. QueueCore 업무 Gate와 분리해
중첩 교착을 피했다. 다른 인스턴스의 중복 선택도 기존 대상 Oracle 행 잠금+UUID 재검사로 같은 작업을 돌려준다.
영속 이력은 프로세스가 바뀌어도 남으므로 별도 커서 저장/복원은 없다.

Quartz 기존02:00 일일 트리거에 같은 JobKey의 시작 즉시·60초 복구 트리거를 추가했다.
DisallowConcurrentExecution으로 같은 Job의 두 트리거가 겹치지 않는다. 20개 처리 후 다음 주기에
남은 설비를 처리하고 배출이 끝나면 0건이다. 서버 재시작 때도 최근 구간의 남은 설비를 조회한다.
이전에 수집이 전혀 없던 낮은 ID 설비의 늦은 데이터도 원장에 없으면 다음 주기에 포함한다.

## 보존하는 의미와 한계
이미 접수된 설비에 뒤늦게 측정이 추가되어도 같은 UUID를 재분석하지 않는다. 고정본 불변성이다.
실패 이력도 자동 재시도하지 않으며 새 분석 요청/후속 재처리 정책은 별개다.
진정한 시간 분산 round-robin 커서보다는 '같은 창의 미접수 대상을 작은 묶음으로 끝까지 배출'이다.
최근 완료 창만 처리한다. 하루 넘는 중단의 과거 창 전체 backfill이나 무한 대상 처리 보장은 추가하지 않았다.
한 창의 대상이 매우 많으면 60초·20건 상한과 다음 날짜 경계의 처리 용량을 별도 운영 설계해야 한다.
첫 활성 대상 선택 정책도 그대로다. 감사 JSON anti-join은 정확성 우선이며 이력 증가 시 실행계획과
JSON 인덱스/전용 배치 원장을 측정·검토해야 한다. 같은 요청/집계 잠금 경계도 전 단계와 같다.

## 검증 설계
고유 격리 라인에 먼저 낮은 ID 무자료 설비1개, 이후27개를 만든다. 뒤의27개 중 첫 설비는
비활성으로 만들어 snapshot 없는 실패를 유도하고 나머지26개는 GOOD 숫자145분 관측으로 예약 실행 가능하다.
모든 fixture는 실제 Oracle에 직접 넣는 집계/예약 시험이며 실장비 처리량 시험이 아니다.
각 배치<=20, 27개 모두 한 번 접수, 실패도 진척, 26개 Quartz→Gateway→Mock 성공,
동시 재호출의 중복0/원본불변, 낮은 ID 지연 자료의 실제60초 Quartz 자동 복구를 확인한다.
마지막에는 Backend 프로세스를 재생성해28개 최종 이력이 다시 접수되지 않는지도 확인한다.
무료 로컬 Mock만 사용하며 유지보전 경보나 실제 모델/알림 실발송을 만들지 않는다.

## 실제 검증·독립 검토 결과
전용 순환 검증27항목 PASS. 27개 초기 fixture가20건+7건 배치로 모두 접수되었고,
비활성 설비1개의 snapshot 없는 FAILED가 뒤 설비를 막지 않았다. 정상26개는 실제 Mock 성공.
동시 재호출 두 번은0건이며 JobId/State/SnapshotId/Attempt가 보존되었다.
먼저 생성해 둔 더 낮은 ID 설비에 나중에145분 관측을 넣었을 때 실제 Quartz60초 복구가
자동 접수했고 Mock 분석까지 성공했다. 합계28설비=27성공+1명시적실패다.
Backend 컨테이너를 같은 이미지로 재생성한 뒤 별도 재생 검증1항목도 PASS했다.
프로세스 재시작으로 대상이 다시 접수되지 않고 같은28개 최종 상태·attempt1이 보존되었다.

첫 시험에서는 Tooling fixture에 EQUIPMENT_CONNECTION 부모 행을 누락해 FK_MAP_02 오류가 났다.
실제 제약을 완화하지 않고 fixture 연결 부모를 추가했다. 실패한 초기 fixture transaction은 rollback되었고
이미 생성한 격리 라인/무자료 설비만 남겼다. 통과 결과만 최종 검증 합계로 센다.

Claude 읽기 검토에서도 고정 창·결정적 UUID·JSON 이력 비교·실패 제외·낮은 ID 지연 자료·
Gate 분리의 정확성 결함은 발견하지 않았다. 다음 운영 한계를 문서에 명시한다.
- 기존 catch(Exception) 정책 때문에 일시 오류도 FAILED로 기록되면 그 창에서 자동 재시도하지 않는다.
  이 단계는 대상 순환이며 일시 장애 재시도/실패 분류 정책은 별도다.
- 최근 창만 다루므로 날짜 경계 뒤 과거 잔여분을 자동 backfill하지 않는다.
- JSON anti-join/측정 DISTINCT는 이력 증가 시 실행계획과 인덱스 측정이 필요하다.
검증 원본: verification-results/analysis-rotation-verification.json,
analysis-rotation-restart-verification.json. 실제 PLC 처리량/다중 노드 스트레스 시험은 아니다.

## 최종 통합 완료 (22:43)
이번 단계 총 279개 실제 검증 항목 PASS. 아래 원본 JSON의 checks 길이를 합산했다.
실패/중단한 시도와 이전93·362 결과를 더하지 않았다. 독립 단위 테스트 수라는 뜻은 아니다.
- analysis-rotation-verification.json: 27
- analysis-rotation-restart-verification.json: 1
- analyses-verification.json: 58
- analysis-aggregation-verification.json: 25
- FactoryMES-browser-shell-verification.json: 12
- FactoryMES-browser-workflow-verification.json: 12
- FactoryMES-browser-master-draft-verification.json: 12
- FactoryMES-browser-lifecycle-verification.json: 12
- FactoryMES-browser-pre-submit-reversal-verification.json: 7
- FactoryMES-browser-alarms-inbox-verification.json: 13
- FactoryMES-browser-high-alarms-verification.json: 11
- FactoryMES-browser-alarm-actions-verification.json: 13
- collection-browser.json: 12
- FactoryMES-browser-deliveries-verification.json: 9
- FactoryMES-browser-outbox-retry-verification.json: 7
- FactoryMES-browser-reporting-verification.json: 8
- FactoryMES-browser-analyses-verification.json: 11
- lifecycle-review-verification.json: 8
- master-review.json: 10
- pre-submit-review.json: 11

전체11프로젝트 Release 경고0·오류0, 최종9서비스 healthy.
Backend/Jobs와 최종 Web 이미지 반영 완료. 기존 계정·Oracle31테이블/53FK·볼륨·업무 이력 보존.
예약 fixture28개는27 Mock 성공+1의도된 비활성 설비 거절이며 원본·감사 이력을 유지한다.
검증 Simulator 연결은 각 시험 cleanup에서 중지했고 고유 시험 데이터/완료 이력은 보존했다.
실제 모델·GPU 추론·클라우드 AI·SMTP/채팅 실발송·DB초기화는0이다.

최종 잔여 경계 결과: lifecycle-review8 PASS, master-review10 PASS, pre-submit-review11 PASS.
미확인 취소가 있는 채 다른 RELEASED 작업을 열어 실적 탭이 없어도 안내가 보였다.
동일 원본 UUID/본문을 재전송해 마감된 원래 작업에 중복 역분개나 버전 변경이 생기지 않았다.
초기 추가 assertion은 다른 작업 조회가 끝나기 전에 이전 탭을 읽어 실패했으므로,
정확한 새 작업 상세 제목과 조회 완료를 기다려 확인하도록 시험을 수정했다(고정 sleep 없음).
정정 review는 격리된 설비 fixture에서 조회 실패 재시도·최신 원장 재검토·종결까지 통과했다.

증빙 요약: rotation-business-shell-summary-2026-11-16.json, 실제 JSON20개와 최종 health.
재개: 규칙.md → 종합.md → 오늘 마지막 GPT 완료 기록. 업무 진입은 http<LOCAL_PATH>
로그인 '/'과 session.js는 유지한다. 초기 로그인/로그아웃 경계를 제외한 업무 탐색은 같은 WASM 문서다.
현재 사용자 요청 범위 미완료 없음. 실제 모델/과거 분석 창 전체 backfill/영속 rollup/고부하·다중노드 시험은 후속이다.
