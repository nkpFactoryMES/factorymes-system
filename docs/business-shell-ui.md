# 공통 업무 레이아웃·메뉴·팝업 재구성 (2026-11-16)

## 사용자 문제와 참고
사용자는 경보 목록 아래의 경보 규칙, 기준정보 목록 아래의 등록 폼을 찾으려면 긴 스크롤이
필요하고 메뉴를 누를 때 문서가 다시 열리는 점을 지적했다. 회사 프로젝트
<LOCAL_PATH> MainLayout·Sidebar·GenericFormModal·MainTable 패턴을
읽기만 하며 참고했다. 회사의 코드·API·비밀을 복제하지 않고 업무 구조를 Blazor로 구현했다.
목표는 한 화면에서 한 가지 업무를 찾기 쉽게 하고 공통 레이아웃 안의 본문만 교체하는 것이다.

## 기존 구조와 비교
기존에는 각 페이지가 자체 InteractiveWebAssembly 루트와 헤더·ConsoleNav를 가졌고,
메뉴 링크는 data-enhance-nav=false로 전체 탐색을 강제했다. 단순 링크 제거만으로는
라우터와 shell이 하나의 WASM 구성 요소 트리에 유지되는 것을 보장하지 않는다.
공식 Blazor 전역 InteractiveWebAssembly Routes + 정적 SSR 제외 패턴을 선택했다.
React로 다시 작성하거나 새 UI 프레임워크를 추가하지 않았다.

## 라우팅·인증
Client Routes가 Router와 ConsoleLayout을 소유한다. App은 HeadOutlet·Routes에
AcceptsInteractiveRouting()에 따라 WASM(prerender:false) 또는 정적 SSR을 지정한다.
로그인 '/'은 ExcludeFromInteractiveRouting + PortalLayout으로 유지한다.
session.js와 기존 로그인 DOM·Cookie+CSRF는 유지하며 같은 출처 /api를 MesApi로 호출한다.
로그인 화면에서 업무에 들어가는 경계와 로그아웃은 문서 탐색이 필요하다.
업무 안에서는 /dashboard도 Client 페이지라 메뉴·본문 링크·뒤로/앞으로가 같은 문서다.
직접 주소와 브라우저 새로고침은 새 문서를 열면서도 같은 업무 레이아웃으로 진입한다.
전체 화면 권한을 메뉴 숨김에 의존하지 않고 기존 Backend 권한 검사를 유지한다.

## 공통 구조와 시각 규칙
고정 상단은 서비스명·사용자/역할·로그아웃, 좌측은 역할별 업무 그룹이다.
현황(대시보드), 생산(작업지시/통계), 설비(경보/연결/분석), 알림(알림함/발송), 기준(관리자)으로 나눈다.
넓은 본문에는 제목·설명·도구, 선택 탭 하나와 그 목록을 표시한다.
고정 푸터와 모바일 접는 메뉴를 포함해 본문만 스크롤한다.
기존 남색·청록 업무 색상과 시스템 한글 글꼴을 유지하고 새 외부 폰트/아이콘 통신을 추가하지 않는다.
페이지별 중복 헤더 대신 공통 shell을 재사용하고 활성 메뉴·상태·오류는 실제 동작과 연결한다.

## 화면별 구성
- 대시보드: 실제 경보/알림/작업 API 요약, 조회 실패는 각 영역에 별도 표시, 본문 링크도 shell 탐색.
- 경보: 경보 목록/관리자 경보 규칙 탭. 규칙 등록 버튼과 행 수정은 팝업으로 처리.
- 기준정보: 품목/라인/설비/사용자 탭. 선택 탭의 등록 버튼은 상단, 등록/수정은 팝업.
  사용자 비밀번호는 닫기·저장 시 비우는 기존 보안 동작을 보존한다.
- 작업지시: 상단 작성 팝업. 목록과 상세를 넓은 화면에서 두 열로 표시하고 내부 업무는 탭으로 구분.
- 설비 연결: 상단 새 연결과 행 조회/수정 팝업.
- 발송 이력: 이력/Outbox 인계 탭, 시연·재시도·수신 내용·재처리 기존 기능 보존.
- 생산 통계: 집계 결과/작업 이력 탭과 재집계 팝업.
- 설비 분석: 분석 이력/대상 탭, 상단 새 요청·대상 등록 팝업, Mock 의미·고정 입력 설명 유지.
- 알림함: 목록·미읽음 필터·읽음 처리와 기존 링크 유지.

## 분담과 검증
Claude가 web/web-client 전체와 브라우저 시험의 실제 구현을 맡고 GPT는 Backend/Jobs/Tooling,
실제 빌드·Compose 배포·API/브라우저 실행과 통합 기록을 맡았다. 공유 분담 문서와 수락·완료에
담당 파일을 남겼다. 소유 파일 밖의 발견 사항은 공유 요청으로 전달했다.
문서 identity(window 표식)·공통 shell 한 벌·활성 메뉴·역할·뒤로/앞으로·직접 URL·새로고침,
탭·상단 등록·모달·모바일을 새 시험으로 확인하고 기존 업무 시험도 구조에 맞게 갱신한다.
전체 업무 시나리오를 지우고 외형만 검사하지 않는다. 최종 실제 결과는 완료 뒤 추가한다.

공식 참고: http<LOCAL_PATH>
및 http<LOCAL_PATH>

## 통합 검토에서 보완한 실제 문제
- Orders 자식 전송의 Busy가 부모에 전달되지 않아 전송 중 다른 작업을 열 수 있었다.
  OrderOperations/PreSubmitReversal/OrderDraftEditor의 BusyChanged를 부모의 구성 요소별 HashSet에
  반영한다. 전송·재조회가 끝날 때까지 상세 탭과 다른 작업 선택을 막는다.
  탭을 hidden으로 가려 기존 Pending UUID·입력을 보존한다. 상태가 바뀌어 탭이 없어지면 기본 탭으로 돌아간다.
  실제 POST를 지연시켜 전환 차단과 같은 작업에 결과 반영을 확인하는 회귀를 추가했다.
- 공통 modal.js는 실제 표시 중인 dialog만 관리한다. 첫 입력 포커스, Tab/ShiftTab 순환,
  Esc(각 페이지), 닫은 뒤 열기 버튼 복귀를 재사용한다. :disabled로 상위 fieldset 비활성도 반영한다.
  기존 페이지별 폼·버전·충돌·멱등 처리는 재작성하지 않았다.
- 고정 푸터는 본문 스크롤 영역 밖의 별도 grid 행이다. 모바일도 헤더/본문/푸터가 분리된다.
- 기준정보 등록 후 첫50건을 재조회하던 기존 방식은 뒤쪽 새 행을 보이지 않게 하고 읽어 둔
  다음 페이지도 잃게 했다. 저장 응답으로 현재 목록에 삽입/교체하고 커서를 보존한다.
  신규는 맨 위, 수정은 기존 자리이며 더 보기는 ID로 중복을 제거한다. 네 기준정보에 공통 적용했다.
- 접힌 모바일 메뉴는 innerText가 빈 문자열이고 getByRole 기본은 숨긴 링크를 찾지 못한다.
  시험은 활성 href/aria-current와 textContent를 확인하고, 화면 이동은 실제 메뉴 열기→클릭으로 수정했다.
  사용자 동작을 우회하거나 대기 시간을 늘려 성공 처리하지 않았다.

## Claude 중단 뒤 인계와 미확인 요청 표시 보완
Claude가 UI 대부분과 공통 레이아웃·기준정보 목록 보완을 실제 구현했고, 사용량 한도에 도달해
마지막 경계 2건을 미완료로 남겼다. GPT는 공유 파일 소유권 규칙에 따라 사용자에게 인계를
확인받은 뒤 남은 UI/시험 소유권을 넘겨받았다. 대기 중에는 독립 API/기존 화면 검증·기록을 계속했다.

실제 결함은 PreSubmitReversal의 미확인 요청이 실적·제출 탭의 hidden 패널 안에 있던 점이다.
다른 작업으로 이동했을 때 그 작업이 RELEASED/CLOSED이면 실적 탭 자체가 없어 같은 UUID로
확인할 버튼이 숨었다. 단순히 시험이 탭을 누르게 바꾸는 것으로는 해결할 수 없었다.
PendingChanged를 부모에 알리고 구성 요소는 탭 패널 밖의 고정된 렌더 위치에 유지했다.
평소에는 실적·제출 탭에서만 보이며 Pending이 있으면 현재 탭·작업 상태에 관계없이 안내가 보인다.
기존 PendingOrderId/본문/UUID는 그대로라 다른 작업을 보고 있어도 원래 요청만 재전송한다.
확정4xx 또는 성공 응답을 확인하면 알림을 해제한다. 5xx/통신 오류는 계속 같은 요청을 유지한다.

추가 경계 시험은 공용 DEMO-MANUAL 대신 고유 설비를 만들어 독립화했고, fixture HTTP 실패에는
경로와 Problem 본문을 출력하게 했다. 기존 데모 작업을 지우거나 업무 제약을 완화하지 않았다.
원래 작업이 이미 마감되고 화면은 실적 탭 없는 다른 작업을 보는 상태에서도 Pending 안내가
실제로 보이고 원래 UUID로 재전송되며 중복 역분개/마감 상태 변경이 없는지 검증한다.

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

## 최종 화면 증빙
읽기 전용 capture-console로 관리자 작업지시·경보 목록·경보 규칙·390px 작업자 화면을 저장했다. 가로 넘침0px, runtime 오류0. 실제 외부 호출/업무 데이터 생성 없이 로그인·조회·캡처만 수행했다.
- FactoryMES-console-admin-orders.png
- FactoryMES-console-admin-alarms.png
- FactoryMES-console-alarm-rules.png
- FactoryMES-console-phone-orders.png
