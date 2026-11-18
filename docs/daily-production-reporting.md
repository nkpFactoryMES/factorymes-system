# KST 마감 실적 집계·관리자 통계 (2026-11-16)

## 요구와 결과
관리자 /reports에서 영업일을 선택해 마감된 작업의 설비·품목별 총량/불량/양품/불량률과 막대를
확인한다. 사유를 입력한 재집계 요청은 Oracle에 먼저 저장하고 Quartz가 처리한다.
미집계, 집계 성공이나 실적 없음, 진행 중, 실패 후 이전 성공 데이터 존재를 구분한다.
이번 단계의 실제 Oracle/API42항목·브라우저8항목이 통과했다. 전체11프로젝트Release빌드
경고0·오류0, Backend/Jobs/Web 이미지를 기존 데이터 보존 상태로 로컬 적용했다.

## 비교와 선택
증분 가산은 빠르지만 정정·역분개·작업 마감 재실행에서 중복과 누락을 별도로 관리해야 한다.
현재 로컬 규모에서는 원장 재계산이 단순하고 검증하기 쉬워 선택했다. 집계 테이블은 조회를
빠르게 하고 원장과 역할을 분리한다. 화면 조회 때마다 원장을 계산하거나 브라우저에서
수량을 합산하지 않는다. Kafka/CDC/추가 DB·UI 패키지도 도입하지 않는다.

Quartz가 Oracle을 직접 갱신하면 서비스별 DB 소유권이 흐려지므로 내부 Service JWT API를
호출한다. Backend가 집계와 업무 제약을 관리한다. 프로세스 메모리 큐 대신 JOB_RUN에
RUNNING 요청을 먼저 커밋하므로 재시작으로 요청 자체가 사라지지 않는다.

## 집계 기준
- WORK_ORDER.status=CLOSED인 MANUAL 작업만 대상이다.
- ClosedAtUtc가 KST 영업일 D의 [00:00, 다음날00:00)에 포함되는 작업 전체 수량을 D에 귀속한다.
  생산 실적 발생 시각으로 일별 분할하지 않는다. 날짜별 생산량과 마감 집계는 다른 지표다.
- KST 날짜키는 DAILY_PRODUCTION의 DATE 자정이다. UTC 순간으로 바꿔 다른 날짜키로 저장하지 않는다.
- 총량 = TOTAL − TOTAL 원본의 REVERSAL − TOTAL_ADJUST_DOWN.
- 불량 = REJECT − REJECT 원본의 REVERSAL. 수량 원본은 양수이고 부호는 종류에서 결정한다.
- 원장 없이 마감된 작업은 0/0 행이다. 마감 작업이 존재했다는 뜻이며 미집계와 다르다.
- AUTO 마감이 있으면 현재 구현이 이를 조용히 빼지 않고 REPORTING_AUTO_NOT_SUPPORTED로 실패한다.
- 미마감/취소 제외. 현재 업무 API는 마감 이후 신규 정정을 허용하지 않는다.

## 트랜잭션·실패·동시 실행
1. 요청 UUID·사용자·정규화 날짜·trim 사유를 감사 Detail에 저장하고 Job을 RUNNING으로 먼저 커밋한다.
2. 실행은 Backend Gate → Oracle JOB_RUN EXCLUSIVE → DAILY_PRODUCTION EXCLUSIVE 순서다.
3. 해당 날짜 집계 DELETE + INSERT SELECT(설비·품목 그룹) + Job SUCCEEDED/종료/처리그룹수를 한 TX에 커밋한다.
4. 실패하면 삭제/부분 합계를 rollback한다. 별도 TX에서 FAILED·종료시각·고정 오류 코드를 보존한다.
5. 읽기는 Oracle MVCC로 커밋 전 이전 성공 데이터를 본다. 상태와 표는 별도 시점 조회이므로 화면에 안내한다.
6. 다음 Pending이 영속 RUNNING을 읽고 잠금 뒤 상태를 재검사한다. 이미 끝난 Job이면 건너뛴다.
   이는 프로세스를 강제 종료한 부하 실험을 했다는 뜻은 아니다. 시험은 영속 미완료 Job fixture로 확인했다.

이 잠금은 로컬 구현의 명확한 직렬화 선택이다. 전체 JOB_RUN 쓰기가 잠기므로 다른 종류의 Job을
해당 테이블로 확장하거나 큰 집계를 만들 때는 전용 실행 키/행 잠금·lease로 범위를 줄여야 한다.
HTTP 호출이 이 DB 잠금을 가로지르지 않는다.

## 스케줄·API
- Quartz Pending 5초: 수동 요청 최대10개. Today/Yesterday 재계산60초. RAMJobStore는 시간표,
  업무 원장·실행 상태는 Oracle이다. 영속 QRTZ 스키마를 추가한 것은 아니다.
- GET /api/v1/reports/daily-production: businessDate 필수, limit1..100, 복합 커서 equipmentId:productId.
- GET /api/v1/reports/daily-production/status: 최신 Job 또는 null.
- POST /api/v1/reports/daily-production/rebuild: businessDate/clientRequestId UUID/reason1..1000 →202 JobRunView.
- GET /api/v1/job-runs 및 /{id}: 실행/실패/처리 행 수 조회.
- 내부 POST /internal/v1/reporting/rebuild, /pending: jobs-worker+jobs:reporting:run만.
- 공개 API는 ADMIN Cookie+CSRF. 작업자/익명/다른서비스 및 Web 내부 경로는 차단한다.
- 동일 UUID/actor/정규화 의도는 같은 Job이다. 내용 변경409. 자동 재집계는 새 실행 이력/attempt를 남긴다.

## 화면·협업
GPT: Reporting DTO/Backend/Oracle 집계/Jobs/API검증/공통배포/문서.
Claude: Reports/CSS·공통 메뉴·브라우저 시험, Backend 읽기 전용 독립 검토.
양품과 불량률은 조회된 집계 행에서 계산하며 총량0은 비율 '-'다. 막대는 표의 합계를 그대로
보여 준다. 기준정보 이름을 붙이고 찾지 못하면 ID를 표시한다. 변경 실패/409/통신 미확인 때
같은 UUID 재전송을 제공한다. 표 갱신시각과 최신 Job 시각은 별도로 표시한다.

## 실제 검증·발견 수정
- Oracle/API42: 권한/CSRF/서비스격리·날짜/커서·역분개/하향정정·KST 경계·미마감 제외,
  반복/동시 재계산·멱등 요청, 수량 제약 실패 rollback·이전 값 보존, AUTO 명시거절,
  영속 RUNNING 회복·실행 이력까지 확인했다.
- API fixture는 고유 설비/품목과 직접 Oracle 원장으로 경계를 만들었다. 실제 작업 흐름 검증과 구분한다.
  TOTAL_ADJUST_DOWN은 실제 DDL의 정정 FK가 필요해 격리 RESOLVED 정정 fixture를 함께 넣었다.
- 브라우저8: 작업자 차단, 실제 작업 A 시작/실적/역분개/제출/관리자 마감, 미마감 B 제외,
  재집계10/3/7/30%, 반복 무가산·빈상태·날짜·이력·390px 넘침0을 확인했다.
- 첫 두 브라우저 시도는 시험이 호스트 Date.now로 실적 시각을 만들며 컨테이너와 차이가 나422였다.
  sleep 추측 보완은 철회했다. 서버 X-Mes-Time을 사용해 수정한 뒤8항목이 통과했다.
  실제 서버의 시작 이후/현재 이전 경계는 완화하지 않았다.
- 결과: reporting-verification.json, FactoryMES-browser-reporting-verification.json,
  FactoryMES-reports-screen.png. 실제 보고서 파일 이름은 verification-results에서 확인한다.

## 한계·시연
자동 이력은 하루 약2880행이라 보존/구분 필터가 필요하다. 자동 집계는 오늘·어제만이며 이전
날짜는 수동 요청한다. 그룹 수·DB 용량에 대한 생산 부하 시험은 수행하지 않았다. 대형 합계가
NUMBER(12,0)을 넘으면 해당 집계는 실패하고 이전 결과를 보존한다. 기존 데이터/31테이블/53FK 유지.

http<LOCAL_PATH> 에서 admin 로그인 → 통계 → 오늘 → 사유 입력 재집계 → 집계 조회.
검증: tooling verify-reporting(Compose deliveries-verification override의 jobs secret 사용),
tests/browser의 node verify-reporting.cjs. 유료 API·실제 메일/채팅·DB 초기화 없이 실행한다.


## 최종 통합 검증 갱신
최종 Oracle/API는 **45항목**, 통계 화면8항목 PASS다. 기존42항목에 시간 역행 격리fixture3항목을 더했다.
로그에서 CK_JRUN_TIME 위반을 확인해, 종료가 시작보다 빠르면 성공 집계를 rollback하고
REPORTING_CLOCK_REGRESSION으로 종료한다. 실패 종료시각은 최소 시작시각으로 제약을 보존한다.
미래 StartedAt을 가진 직접 fixture로 terminal FAILED·끝없는 재시도 없음·이전 집계 보존을 확인했다.
실제 시계 조정 원인/이동량을 확정한 것은 아니다. DB 제약을 느슨하게 바꾸지 않았다.
