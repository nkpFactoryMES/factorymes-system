# 예약 AI 과거 구간 복구·일시 오류 자동 재시도 (2026-11-16)

## 문제와 선택
이전 순환은 최근02:00 KST 창 하나의20개 초과 설비를 이어 처리했다. 서버가 날짜 경계를
넘겨 꺼져 있으면 과거 창을 다시 조회하지 않았고, 일시 통신 실패도 terminal FAILED로 끝났다.
이번에는 데이터가 보관된 과거 창을 복구하고 지연 재시도 시각을 Oracle에 영속화한다.

메모리 날짜 커서는 재시작/늦은 데이터에 취약하다. Redis 커서는 Oracle 작업 저장과 이중 기록
일관성을 추가해야 한다. 별도 날짜별 완료 테이블은 무자료 날짜/부분 처리/늦은 입력을 관리해야 한다.
기존 원장 기반 순환을 확장해 보관된 TELEMETRY_EVENT의 (설비,분석 창) 쌍 중 기존 요청 이력이
없는 쌍을 오래된 순으로20개씩 읽는다. 새 커서 테이블 없이 중단 위치를 재구성한다.
단순히 최근 N일로 제한하면 긴 중단분이 다시 누락될 수 있어 임의 날짜 상한은 두지 않았다.
DB가 보관한 자료만 복구할 수 있으며 삭제됐거나 서버 중단 중 수집되지 않은 관측을 만들어 내지 않는다.

## 날짜와 중복 방지
UTC 관측 t의 종료 경계는 TRUNC(t -17/24)+1+17/24다. 17:00 UTC는 다음날02:00 KST다.
경계 시각의 자료는 다음 창에 속한다. 아직 끝나지 않은 최신 창은 제외한다.
각 창은 [end-24h,end)이며 오래된 종료일,설비ID 순이다. 같은 날짜 자료가 없는 날은 작업을 만들지 않는다.
요청ID는 기존 SHA256(대상코드:설비ID:end.Ticks)의 UUID를 유지한다. JSON 감사 intent와 작업의
principal/operation/target/equipment/periodEnd를 비교하므로 기존 성공·부족자료·영구거절은 재등록하지 않는다.
새 창 복구와 기존 실패 작업 재시도는 구분한다. 기존 작업을 삭제하거나 새 UUID로 우회하지 않는다.

## 오류 분류와 재시도
통신 오류/timeout, HTTP408·429·5xx, 명시한 Oracle 일시 연결/잠금/timeout 오류만 재시도한다.
검증 OrderFault, 권한/기타4xx, 입력/설정 해시 불일치, 잘못된 결과는 terminal 판정한다.
원인 불명 내부 예외를 무조건 일시 오류로 보지 않는다.
초기시도 포함 최대3회다. 다음 시도까지5초,10초를 기다리고 각 시도의 기한은30초다.
QUEUED와 nullable RETRY_AT_UTC로 대기를 표현하며 다음시도 번호를 Attempt에 예약한다.
재시도 예정 시각과 이전/다음 Attempt·오류는 analysis.retry.scheduled 감사에 남긴다.

INTEGRATION_JOB에 RETRY_AT_UTC TIMESTAMP(6) nullable 열 하나만 추가했다. 테이블/기존 상태 제약은 유지한다.
초기 DDL과 반복 가능한 upgrade-ai-retry Tooling/03_ai_retry_upgrade.sql을 함께 제공한다.
기존 행은 NULL이고 데이터/암호/볼륨은 그대로다. 배포는 스키마 보완→새 Backend 순서다.

입력 캡처 단계가 일시 실패하면 요청·설정 intent와 snapshot 없는 지연 QUEUED 작업을 남긴다.
다음 시도에서 같은 요청 기간·동결 설정을 검증하고 입력 snapshot을 만든다.
전송 단계 재시도는 이미 저장된 snapshot/설정/UUID를 유지하고 재집계하지 않는다.
부족자료는 INSUFFICIENT_DATA로 끝내며 예측 성공으로 꾸미지 않는다.
기존 예약 작업의 알려진 일시 FAILED와 만료 DISPATCHED/TIMED_OUT도 남은3회 예산 안에서 복구한다.
과거의 원인 불명 ANALYSIS_SCHEDULE_REJECTED나 영구 검증 실패를 임의로 다시 실행하지 않는다.

## 동시 실행과 보장의 한계
행 잠금/SKIP LOCKED로 claim하고 커밋한 뒤 네트워크를 호출한다. 네트워크 중 Oracle 잠금은 잡지 않는다.
완료 저장은 JobId뿐 아니라 예상 Attempt가 현재와 같은지 확인한다. 이전 시도의 늦은 응답이
다음 시도 상태를 실패로 덮거나 결과를 추가하지 못한다. 입력 조회도 활성 Attempt·deadline을 확인한다.
SUCCEEDED/최종FAILED/INSUFFICIENT_DATA는 새 요청 재생으로 다시 수행하지 않는다.
한 작업의 최종 결과/완료 알림은 한 번 저장한다. 네트워크 호출 자체의 exactly-once를 주장하지 않는다.
외부 모델이 결과를 반환한 뒤 연결이 끊기면 재호출될 수 있으므로 실제 모델 어댑터에는 같은
멱등키를 받는 제공자/영속 실행 영수증 규격이 추가로 필요하다. 현재 실제 모델은 호출하지 않는다.

## 검증 설계와 운영 범위
기존 backlog를 Gateway가 정상일 때 배출한 뒤 격리된2설비×3과거 날짜에145분 GOOD 자료를 넣는다.
실제 Gateway 중단으로 지연 재시도를 만들고 Backend 컨테이너를 재생성한 뒤 서비스 복구로
같은 작업/입력의 성공·단일결과를 확인한다. 별도 지속 장애는3회까지만 실행되는지 확인한다.
수동 완료 요청의 동일UUID 재생과 정보성 알림1개, 설정 불일치의 무재시도도 검증한다.
시험 제어기는 단계 파일/확인 응답으로 장애 순서를 조율하고 finally에서 Gateway/Jobs를 복구한다.
대량 과거 telemetry DISTINCT와 감사 JSON anti-join의 DB비용은 남아 있다. 보관/색인·실행계획은
실측 후 별도 최적화해야 한다. 분당20건과 다음 전송 배치/기한은 무한 처리량 보장이 아니다.
유료API·실제모델·GPU·실제메일/채팅을 쓰지 않는다.

## 실제 장애 시험 결과
전용33항목 PASS. 고유2설비×3과거 종료일(오늘 창보다2~4일 전)의6개 누락 작업을 생성했다.
실제 Gateway를 중단한 뒤 Attempt2·RETRY_AT_UTC가 Oracle에 남는 것을 확인하고,
Gateway를 되살리면서 Backend 컨테이너를 재생성했다. 같은 JobId·SnapshotId로6개 모두
SUCCEEDED가 됐고 결과는 작업마다1개였다. 같은 예약 복구를 반복해도6개가 유지됐다.
아직 자료가 없는 날짜를 생성하거나 과거 기간을 오늘로 바꾸지 않았다.
수동 작업도 복구 후 성공했고 같은 UUID 재생은 같은 작업/결과1개, 완료 알림도1개였다.

두 번째 실제 Gateway 중단은 계속 유지해 총3회에서 terminal FAILED로 끝나는지 확인했다.
재생해도 Attempt3 예산을 초기화하지 않고 결과를 꾸며 만들지 않았다. 재시도 감사는2개였다.
Jobs를 잠깐 중지한 상태에서 동결 설정과 현재 설정이 달라진 fixture를 만들고 직접 내부
배치를 실행해, 영구 불일치가 Attempt1·재시도시각NULL·재시도감사0으로 종료됨을 확인했다.
시험 제어기의 finally에서 Gateway와 Jobs를 다시 실행했다. 서비스 상태는 마지막에 별도 확인한다.

실제 서버 중단 동안 센서 자료를 만들었다는 뜻은 아니다. 과거 날짜의 관측을 격리된 Oracle
fixture에 직접 넣고, 실제 예약 API·Quartz·Gateway 장애·Backend 프로세스 재시작을 결합한 시험이다.
DB가 아예 내려간 동안에는 진행을 기록할 수 없으므로 미접수 원본을 다음 주기에 다시 발견한다.
3회 제한은 영속화한 작업/다음시도 예약 예산에 적용하며, 외부 모델 호출의 exactly-once 보장이 아니다.

실행(로컬 전용 제어): pwsh -NoProfile -File tools/verify-analysis-recovery.ps1.
내부 Tooling 모드: verify-analysis-recovery. 네트워크 장애 제어와 복구는 해당 스크립트의 책임이다.
독립 실행 시에는 제어기 단계 확인 파일이 없으므로 이 모드를 단독 실행하지 않는다.

## 최종 완료·검증 (23:44)
이번 단계154항목 PASS. 성공한 최신 원본 JSON5개만 합산했으며, 실패/중단된 준비 시도나
이전279·93·362 결과를 더하지 않았다. 독립 단위 테스트 수가 아니라 실제 검증 assertion 수다.
- analysis-recovery-verification.json: 33
- analysis-input-retry-verification.json: 11
- analyses-verification.json: 58
- analysis-aggregation-verification.json: 25
- analysis-rotation-verification.json: 27

전체11프로젝트 Release 경고0·오류0, 최종9서비스 healthy. Windows 호스트의 Oracle 앱계정
로그인도 다시 확인해 USER_TABLES31개를 읽었다. 기본 schema31테이블/53FK는 유지하며
INTEGRATION_JOB의 nullable RETRY_AT_UTC 열 하나만 추가했다. 기존 행·암호·볼륨은 유지한다.

추가 입력 경계11항목: Oracle에 입력 캡처 전 실패한 snapshot 없는 Attempt2·미래RetryAt 상태를
준비해, 예정 전에는 직접 dispatch해도 처리하지 않고 실제 Quartz가 예정 후 입력을 재캡처해
Mock 성공하는 것을 확인했다. 원래 기간·canonical InputHash·시도예산·결과1개를 확인했고,
동결 설정을 바꾼 두 번째 fixture는 SnapshotNULL·Attempt2·RetryAtNULL의 영구실패로 끝났다.
이는 최초 DB 접속 끊김을 주입한 시험은 아니다. 준비된 영속 상태에서 재개하는 경계 시험이며,
실제 Gateway 중단/Backend 재생성33항목과 구분한다.

수동 조회/재생, 역할·CSRF·Service JWT·입력/설정해시·Mock만허용·최종기한 등의 기존58항목,
24시간86400표본/172800숫자 행25항목,20개초과28설비/지연낮은ID 순환27항목도 재통과했다.
이번에는 화면 구현을 바꾸지 않아 전체UI279항목을 반복한 것으로 주장하지 않는다.
새 코드/시험은 줄바꿈·인자/객체 들여쓰기를 정리했고 새 파일의 불필요한 import/변수만 제거했다.

재현: 기존DB는 새Backend 전에 Tooling upgrade-ai-retry(반복가능)로 열을 추가한다.
실제 장애 제어기는 프로젝트 tools/verify-analysis-recovery.ps1에 보관했다.
`pwsh -NoProfile -File tools/verify-analysis-recovery.ps1`은 Gateway/Jobs를 잠시 중지하고
Backend를 재생성한다. 본체는 실제 검증한 제어기에서 옮겼고 저장한 파일은 PowerShell구문 검사도 통과했다.
단독 Tooling verify-analysis-recovery는 제어기와 단계 확인 파일이 필요하므로 독립 실행하지 않는다.
입력 경계는 main 회복 시험 뒤 `docker compose -f deploy/compose.yaml -f deploy/compose.analysis-verification.yaml run --rm tooling verify-analysis-input-retry`로 실행한다.

재시도 이력은 DataGrip에서 다음처럼 확인할 수 있다.
```sql
SELECT job_id, state, attempt, retry_at_utc, deadline_utc, error
FROM integration_job
WHERE operation = 'AI_ANALYZE'
ORDER BY created_at_utc DESC
FETCH FIRST 20 ROWS ONLY;
```
접속은127.0.0.1:1527/FREEPDB1, factorymes, 비밀번호는.secrets/oracle_app_password다.
비밀 값은 문서/로그에 남기지 않았다. docs/local-oracle-connection.md와 공유 접속안내에
실제HostBindings/NetworkSettings 차이·internal망·전용로컬브리지·TCP/로그인 결과를 정리했다.

상태: 요청 범위 미완료 없음. Backend 최종 이미지·재시도 열·Oracle로컬접속망 반영 완료.
검증 과정의 Gateway/Jobs 중지는 복구했고 실제모델/GPU/유료API/메일·채팅실발송/DB초기화는0이다.
검증용 격리 데이터·성공/의도된실패·시도 감사는 보존했다. 실제 모델 호출은 적어도한번 전송의
중복 실행 가능성을 별도 제공자 멱등키/실행영수증으로 해결해야 하며 여기서는 모델정확히1회를 보장하지 않는다.
다음 재개는 규칙.md→종합.md→오늘마지막완료→이 문서 순서다.
