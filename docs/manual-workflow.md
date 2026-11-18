# FactoryMES MANUAL 업무 구현 · Claude 검증 인계

2026-11-13 / GPT 구현 완료, Claude 독립 검증 대기. 실제 코드: `<LOCAL_PATH>`.

## 완료 범위

로그인 → 관리자 DRAFT 작성·발행 → 담당 작업자 필수 점검·시작 → MANUAL 총수량/불량 입력 → 제출 → 관리자 검토·마감(CLOSED) → 로그아웃을 실제 Oracle와 Edge 브라우저에서 확인했다.

로그인 `services/web/wwwroot/session.js`는 수정하지 않았다. SHA256은 `D0148270CD0107270701837B4C902683D5EB3C75182ECA932E74C522378804AD`다. 기존 로그인 HTML에 업무 링크를 연결했고, Web App의 모듈 로더는 로그인 폼이 있을 때만 기존 JS를 로드한다.

업무 화면은 사용자 확정대로 별도 `services/web-client/FactoryMES.Web.Client.csproj`의 `/orders` 페이지다. 페이지별 `InteractiveWebAssembly(prerender: false)`를 사용하며, 브라우저 HttpClient가 같은 출처 `/api`를 호출한다. 변경 요청마다 CSRF 토큰을 얻고 SameOrigin 쿠키를 사용한다. 사용자 토큰·CookieContainer를 Web 서버에서 공유하는 구조로 바꾸지 않았다. README의 기존 Interactive Server 표기도 실제 구성에 맞게 수정했다.

공통 요청/응답 타입은 `shared/FactoryMES.Contracts`에 두며 Client는 Backend/Oracle/Foundation을 참조하지 않는다. 전체 C# 프로젝트는 11개가 됐지만 서버 호스트 7개·상시 컨테이너9개 구성은 그대로다.

## 업무 화면

- 관리자: 활성 품목·설비·담당 작업자 선택, 목표 EA·계획 KST·필수 점검표 입력, 초안 저장, 발행, 제출 결과/원장 조회, 마감 사유 입력·마감.
- 작업자: 자신의 발행된 작업 목록/상세, 점검 체크, 시작, 총수량·불량 증가 입력, 제출 사유·제출, 실적 원장 조회.
- 목록/원장은 페이지 조회, 관리자는 전체 MANUAL·작업자는 담당 MANUAL 범위. 관리자 UI를 숨기는 것과 별개로 Backend가 역할·행 범위를 검사한다.
- 실적 요청의 응답이 불명확하면 같은 UUID/본문으로 재전송한다. 확정4xx 거절 후에는 입력 수정이 가능하다. 페이지 종료 시 미확인 요청 메모리는 사라지므로 원장 조회로 먼저 확인한다.

## Backend 구현

`Features/Orders/OrderEndpoints.cs`, `OrderService.cs`와 공통 DTO를 추가했다. 기존 Backend Program은 서비스 등록/엔드포인트 연결 및 CSRF 응답의 서버 시각 헤더만 보강했다.

| 경로 | 구현 |
|---|---|
| GET /api/v1/orders | 역할·담당 범위, 상태/설비/담당자 필터, keyset 페이지 |
| POST /api/v1/orders | MANUAL DRAFT 생성, 서버 orderNo/createdBy |
| GET /api/v1/orders/{id} | 관리자 또는 담당 작업자 상세, 작업자 DRAFT 접근 차단 |
| POST .../release | 관리자 발행, 활성 기준정보·배정 재검사 |
| PUT .../checklist | 담당 작업자 RELEASED 점검, 부모 version 증가 |
| POST .../start | 담당 작업자 시작, 필수 점검·설비 점유 검사 |
| POST .../entries | 일반 TOTAL/REJECT 배치 저장, 원장 불변·멱등성 |
| GET .../entries | 권한 범위 안의 실적 원장 페이지 |
| POST .../submit | 작업자 제출, 합계·목표 차이 사유, 열린 중지 종료 |
| POST .../close | 관리자 마감, 자기 마감·열린 정정·상태·합계 검사 |
| GET /products, /equipment, /users | 관리자 선택 목록, 페이지·활성/역할/배정 가능 필터 |

요약13 operation 구현이며 canonical98개 전체 구현이 아니다. `/products`·`/equipment`의 active 필터는 UI 선택용 조회 확장이고 v1.1 원본 계약 파일은 수정하지 않았다.

변경 명령은 Cookie+CSRF·HTTPS·Origin을 확인한다. 작업 행을 잠근 상태에서 version·상태·합계·전이·감사 저장을 같은 트랜잭션으로 처리하고 응답 스냅샷을 읽는다. 서로 다른 작업의 동시 시작은 설비 행 잠금과 기존 조건부 UQ로 막는다. 실적은 order/actor/요청 본문 해시와 clientEntryId를 비교해 전체 동일 재전송을 version 검사보다 먼저 처리하며, 일부 중복·다른 본문은409다. 수량은 양의 EA, 불량 합계가 총수량을 넘으면 거절한다.

DB는 기존31테이블·53FK, API 기준은 v1.1 그대로다. WORK_TASK 등 과거 모델이나 신규 영구 테이블을 추가하지 않았다.

## 실제 검증에서 수정한 문제

1. Oracle EF의 NUMBER(1) 조건 상수 변환에서 InvalidCastException 발생: 새 업무의 불리언 조건을 `EF.Parameter`로 전달하도록 수정. DDL/기존 bool 매핑은 변경하지 않음.
2. WebAssembly scoped CSS 링크 누락: Web App에 FactoryMES.Web.styles.css를 연결하고 실제 브라우저 computed style로 로딩 확인.
3. 수량 거절 후 Pending 요청이 남아 입력을 못 고치는 문제: 확정4xx와 네트워크/5xx를 구분해 처리.
4. Docker 재시작 후 시계 보정으로 마감<제출 제약 오류 발생: 상태 전이 시각이 저장된 선행 시각보다 앞서지 않도록 유지하고 마이크로초 정밀도로 처리.
5. Windows 브라우저와 Docker 시계 차이로 실적이 미래로 판정됨: 기존 CSRF 응답에 X-Mes-Time UTC 헤더를 추가하고 수동 입력 시각은 서버에서 받아 사용. 기존 CSRF JSON 형식은 유지. 미래 실적 거절을 해제하지 않음.

중간 실패한 브라우저 테스트 작업은 삭제하지 않고 작업자 제출 사유·관리자 검토 사유를 남겨 정상 마감했다. 테스트용 시연 작업은 이력으로 남아 있다.

## GPT 검증 결과

| 검증 | 최종 결과 |
|---|---|
| Release solution build | 경고0·오류0 |
| verify-orders | 실제 Oracle + HTTPS 업무43개 PASS |
| 기존 tooling verify | 인증·매핑·내부 JWT 등35개 PASS |
| 실제 Edge headless | 브라우저12개 PASS, runtime error0 |
| Compose |9개 healthy 확인 |
| 유료 호출/외부 발송 |0/0 |

업무 검증은 CSRF 누락, 타 역할 명령, 타인/DRAFT 조회, 필수 점검 누락, 낡은 version, 설비 중복/동시 점유, 실적 동일·상이·부분 재전송, 불량 초과, 목표 차이 사유, 제출·마감·마감 후 변경 거절, 원장·전이·감사를 포함한다. 테스트 fixture는 고유 코드로 만들고 자기 데이터만 정리한다.

브라우저 검증은 admin/worker.demo 각각의 로그인·비밀번호 필드 지우기·WASM 및 CSS 로딩, 초안/발행, 담당자 점검/시작, 수량 거절 후 수정, 총10/불량2/양품8 저장, 제출, 관리자 마감(CLOSED), 로그아웃까지 확인했다. 테스트 실행마다 마감 시연 지시1건을 남긴다. 실제 외부 PLC·AI·이메일/채팅 검증은 아니다.

결과: [업무43개](../verification-results/orders-verification.json), [기존35개](../verification-results/scaffold-verification.json), [브라우저12개](../verification-results/FactoryMES-browser-workflow-verification.json).

![실제 MANUAL 마감 화면](../verification-results/FactoryMES-manual-closed.png)

## 실행·검증 재현

관리자 admin 암호는 `.secrets/admin_password`, 시연 작업자 worker.demo 암호는 `.secrets/worker_password`에서 로컬로 확인한다. 문서·로그에 복사하지 않는다.

```powershell
Set-Location <LOCAL_PATH>
dotnet build FactoryMES.sln -c Release --no-restore
docker compose -f deploy/compose.yaml ps
docker compose -f deploy/compose.yaml --profile tools run --build --rm tooling init-demo
docker compose -f deploy/compose.yaml --profile tools run --build --rm tooling verify-orders
docker compose -f deploy/compose.yaml --profile tools run --rm tooling verify
Set-Location tests/browser
npm ci
node verify-workflow.cjs
```

init-demo는 사용자 요청 범위의 명시적 로컬 시연 준비 도구다. 활성 기준정보와 작업자를 처음 만들고 기존 값·암호를 덮어쓰지 않는다. 새 설치는 먼저 init-secrets로 worker_password를 생성한다. 기존 DB 볼륨을 초기화하지 않는다. Playwright는 테스트용 무료 패키지이며 Edge 설치 경로는 Windows 기준이다. 자체서명 인증서 허용은 테스트 context에만 적용하고 OS 신뢰 루트는 바꾸지 않았다. 테스트 브라우저 외부 출처 요청은 차단한다.

실제 화면: http<LOCAL_PATH> → 로그인 → 작업지시 업무 화면 링크(/orders). 자체서명 개발 인증서 경고는 예상된다. 상세 구현 파일과 lockfiles는 실제 프로젝트에 있다.

## Claude 독립 검증 요청

구현은 GPT가 끝냈고, 아직 Claude가 이번 변경을 검증했다고 표시하지 않는다. 사용자 확정 역할 분담대로 다음 단계를 Claude가 수행한다.

1. 종합.md와 오늘 기록, 이 문서 및 실제 코드의 변경 범위 대조. 이전 줄바꿈 정리를 되돌리지 않았는지 확인.
2. 로그인 session.js 해시 유지, 페이지별 WASM/별도 Client, 브라우저 same-origin Cookie+CSRF, Client에 비밀/DB 참조가 없는지 확인.
3. 빌드·기존35개·업무43개 재실행 및 실제 브라우저 흐름 확인. 새 서버 시각 헤더와 Pending 재시도 분기를 우선 검토.
4. 담당/역할·version·상태·설비 잠금·실적 hash·동일 재전송·감사 원자성을 독립적으로 검토.
5. 전역 AGENTS 6번 형식 재확인. session.js는 사용자 지시로 유지했고, 신규 코드/설정/테스트는 읽기 쉬운 줄바꿈을 적용했다.
6. 발견 사항과 검증 결과를 오늘 기록·종합.md에 남긴다. API v1.1에 서비스 토큰 발급 경로가 빠졌다는 이전 Claude 발견은 이번 범위에서 계약 수정하지 않고 남긴다.

## 남은 기능

AUTO·실설비 연동, 초안 수정/취소, 중지/재개, 정정/역분개, 기준정보 편집 UI, 경보·알림·Outbox 발행/전달·집계·예약 Mock AI는 다음 단계다. 이번 전이·실적에는 감사 로그를 저장했으며, 알림 Outbox 파이프라인 완료로 표현하지 않는다. 최종 포트폴리오용 대시보드 디자인·성능/운영 환경 검증도 별도다.

컨테이너는 실행 상태로 인계했다. 중간에 Docker Desktop 종료를 관측해 다시 시작했으며, 기존 가족 미디어/DDL 검증 프로젝트를 삭제하거나 설정을 변경하지 않았다. Git 커밋·원격 push·유료 API 호출은 하지 않았다.
