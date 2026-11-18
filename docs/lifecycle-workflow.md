# 중지·재개 및 마감 전 실적 정정 구현·검증

2026-11-13 / 실제 프로젝트 `<LOCAL_PATH>`. 사용자 요청에 따라 GPT·Claude가 분담 구현하고 GPT가 통합·실행 검증했다.

## 역할 분담과 규격

공유 `FactoryMES-중지정정-동시구현분담-v1.md`에 HTTP 경로·DTO·상태·권한·버전·파일 소유권을 먼저 정했다. Claude20:55 수락,20:58 화면/테스트 완료,21:19 및21:26 검토 보완을 공유 기록으로 확인했다. 규칙7번의 공유 요청/수락/완료 방식을 사용했고 수락한 파일을 서로 동시에 고치지 않았다. 공통 빌드·Compose는 GPT만 담당했다.

| 담당 | 구현 |
|---|---|
| GPT | 공통 DTO, Backend 중지/재개·정정·역분개, 부모 Orders 페이지 연결, 실제 Oracle70개 검증, 추가 UI 검토8개 검증, 통합 배포·회귀 검증 |
| Claude | 새 OrderOperations.razor/.css, 실제 브라우저 중지/정정11개 검증, 최신 실적 재검토·조회 재시도·비동기 화면 보완 |

새 규격을 별도로 만들지 않고 승인된 DDL31테이블/API v1.1을 사용했다. DDL01/02 및5개 OpenAPI·공통/이벤트 JSON을 공유 기준본과 바이트 비교하여 변경 없음을 확인했다. 로그인 session.js 해시도 유지했다. 기존 admin/portal 계정·암호와 Cookie+CSRF·페이지별 WebAssembly 구조를 유지한다.

## 사용할 수 있는 화면

작업자 portal:
1. 내 작업을 시작하고 중지 사유·메모·선택 경보 ID를 입력해 중지한다. OTHER는 메모 필수다.
2. 중지 중에는 설비 점유가 유지된다. 재개하면 원래 시작 시각·카운터 기준을 보존한다. 중지 상태에서 제출해도 열린 중지 구간은 끝난다.
3. 제출 후 관리자가 정정을 요청하면 정정 사유·요청 번호가 보인다.
4. 원본 TOTAL/REJECT를 선택해 같은 수량으로 역분개하고, 필요한 대체 총수량/불량을 같은 배치로 저장한다. 원본은 삭제하거나 수정하지 않는다.

관리자 admin:
1. 중지 상태를 확인하되 작업자 대신 중지·재개·실적 입력은 할 수 없다.
2. SUBMITTED 작업에 정정 사유를 넣어 요청한다. 열린 정정은 최대1개이며 해결 전에는 마감이 차단된다.
3. 작업자의 수정된 원장·합계를 확인하고 확인 메모를 입력해 정정을 RESOLVED로 처리한 뒤 마감한다.
4. 관리자가 보고 있던 실적보다 새로운 변경이 있으면 바로 확인하지 않고 먼저 화면·원장을 갱신한다. 재검토 후 다시 눌러야 한다.

공통으로 정정 요청 이력·상태·요청자·확인자·확인 시각을 표시한다. 마감 후에는 변경 버튼을 제공하지 않는다. 네트워크/5xx로 실적 저장 응답을 확인하지 못하면 동일 UUID·본문을 보존해 재전송한다. 확정4xx 거절은 입력 수정이 가능하다.

## 구현 API

| 경로 | 권한 | 동작 |
|---|---|---|
| POST /api/v1/orders/{id}/pause | 담당 WORKER | RUNNING→PAUSED, 사유/동일 설비 경보 확인 |
| POST /api/v1/orders/{id}/resume | 담당 WORKER | PAUSED→RUNNING, 열린 중지 정확히1건 종료 |
| GET /api/v1/orders/{id}/corrections | ADMIN/담당 WORKER | 정정 이력·현재 부모 버전, 커서 페이지 |
| POST /api/v1/orders/{id}/corrections | ADMIN | SUBMITTED에서 OPEN 생성,201 |
| POST /api/v1/corrections/{id}/resolve | ADMIN | 정정/부모 버전을 모두 검사해 RESOLVED |
| POST /api/v1/orders/{id}/entries | 담당 WORKER | 기존 배치에 SUBMITTED 정정·REVERSAL 지원 추가 |

요청/응답 DTO는 `shared/FactoryMES.Contracts/OrderLifecycle.cs`, Backend는 `OrderService.Lifecycle.cs`, `OrderService.Entries.cs`, `OrderEndpoints.Lifecycle.cs`와 기존 부모 파일에 있다. 입력 API는 기존 그룹의 HTTPS·Origin·CSRF·역할 정책을 그대로 쓴다.

모든 변경은 부모 WORK_ORDER를 먼저 잠근 뒤 상태·담당·expectedVersion을 검사한다. 정정 확인은 부모와 정정 버전을 모두 검사한다. 원장/중지/전이/감사/부모 버전을 한 트랜잭션으로 저장하고 잠금 안에서 응답을 읽는다. 정정 때문에 생산 상태를 RUNNING으로 되돌리거나 설비를 재점유하지 않는다.

SUBMITTED의 모든 정정 실적은 같은 지시의 OPEN correctionId와 사유가 필요하다. REVERSAL은 같은 지시·같은 qty의 비역분개 원본을1회 취소한다. 역분개의 역분개, 이미 취소된 원본, 다른 지시 원본/정정 참조를 거절한다. 최종 합계는 총량>=불량>=0이며 중간 합계가 아닌 배치 최종 결과를 검증한다. MANUAL에서 AUTO 전용 TOTAL_ADJUST_DOWN은 허용하지 않는다.

커밋된 전체 동일 실적 재전송은 현재 권한을 확인한 후 버전·상태 검사보다 먼저 조회 반환한다. 따라서 정정 확인/마감 후에도 기존 결과를 읽을 수 있으며 새 쓰기는 하지 않는다. 일부 중복·같은UUID 다른본문은409다. 미확인 요청을 가진 페이지를 닫으면 메모리가 사라지므로 원장 조회로 먼저 확인한다.

## 통합 검토에서 보완한 점

- 관리자 정정 확인에서 최신 버전을 자동으로 받아 미검토 실적까지 확인하지 않도록, 부모 화면의 버전이 다르면 갱신·재검토하도록 했다.
- 정정 조회가 실패했을 때 동일 버전 새로고침도 재시도하도록 LoadedKey를 성공 후에만 확정한다.
- 비동기 조회 중 목록을 비워 정정 요청 버튼이 일시적으로 보이던 동작을 줄였다. 새 조회 결과를 한 번에 교체하고 처리 시작 상태를 표시한다.
- 브라우저 테스트는 일시적 버튼이나 다른 작업의 CLOSED 문자열을 완료로 오인하지 않고 해당 정정 이력/해당 작업의 최종 상태를 기다린다.
- API 테스트의 경보 fixture는 실제 EquipmentConnection FK를 갖추도록 수정했다. 테스트 편의를 위해 DDL을 약화하지 않았다.
- 중단된 브라우저 검증 건은 삭제하지 않고 실제 저장 원장 확인→정정 확인→정상 마감으로 정리했다.

## 최종 검증

| 검증 | 결과 |
|---|---|
| Release solution build | 경고0·오류0 |
| 실제 Oracle·HTTPS 중지/재개·정정 |70/70 PASS |
| Claude 작성 브라우저 중지·정정·확인·마감 |11/11 PASS |
| GPT 추가 브라우저 조회 실패·동일버전 재시도·미검토 확인 차단 |8/8 PASS |
| 기존 MANUAL API 회귀 |44/44 PASS |
| 기존 인증·Oracle 매핑·내부 JWT 회귀 |35/35 PASS |
| 기존 admin/portal 브라우저 업무 회귀 |12/12 PASS |
| Compose |9개 healthy |
| 유료 호출/외부 발송 |0/0 |

총180개 확인 항목(중복되는 회귀 항목 포함)이 통과했다. API는 중지 점유·다른 설비 경보·필수 사유·다른 사용자·낡은 버전·제출 시 중지 종료·정정1건 제한·마감 차단·원본 불변·동일수량/1회 역분개·다른 지시 참조 거절·최종 합계·재전송·정정과 실적의 동시 경쟁을 검사한다. 고유 fixture는 자기 데이터만 정리한다.

브라우저에서는 총10/불량2 실적을 제출하고 관리자가 정정을 요청했다. 작업자가 원본 불량2를 역분개하고 불량1로 대체해 양품9가 되는 것을 확인한 뒤 관리자가 정정 확인·마감했다. 추가 검증은 정정 조회503을 한 번 주입한 뒤 같은 부모 버전의 재조회 성공, 최신 실적을 보지 않은 확인 차단, 갱신 후 명시적 재확인을 실제 화면에서 확인했다.

보고서: [API70](../verification-results/lifecycle-verification.json), [브라우저11](../verification-results/FactoryMES-browser-lifecycle-verification.json), [검토UI8](../verification-results/lifecycle-review-verification.json), [기존API44](../verification-results/orders-verification.json), [기존인증35](../verification-results/scaffold-verification.json), [기존브라우저12](../verification-results/FactoryMES-browser-workflow-verification.json).

[실제 정정·마감 화면](../verification-results/FactoryMES-lifecycle-closed.png)

## 재현

```powershell
Set-Location <LOCAL_PATH>
dotnet build FactoryMES.sln -c Release --no-restore
docker compose -f deploy/compose.yaml up -d --build
docker compose -f deploy/compose.yaml --profile tools run --build --rm tooling verify-lifecycle
docker compose -f deploy/compose.yaml --profile tools run --rm tooling verify-orders
docker compose -f deploy/compose.yaml --profile tools run --rm tooling verify
Set-Location tests/browser
node verify-lifecycle.cjs
node verify-lifecycle-review.cjs
$env:MES_BROWSER_WORKER_LOGIN = 'portal'
$env:MES_BROWSER_WORKER_SECRET = 'portal_password'
node verify-workflow.cjs
```

접속: http<LOCAL_PATH> → 로그인 → 작업지시 업무 화면. 자체서명 개발 인증서이며 테스트 허용은 브라우저 test context에 한정했다. 비밀번호·쿠키는 보고서에 저장하지 않는다. 브라우저 검증은 마감된 시연 지시를 남긴다. tests/browser의 npm 의존성은 기존 npm ci로 준비하며 외부 AI/채팅 API를 쓰지 않는다.

## 남은 범위

이번에는 MANUAL·마감 전 정정을 완성했다. AUTO 수집/카운터 정정, 마감 후 수정·재작업, 초안 편집/취소, 기준정보 편집 화면, 알림/Outbox 전달·설비 설정/수집·AI 파이프라인 등은 후속이다. 실제 PLC 안전 제어·상용 운영 적합성·대용량 성능을 검증한 것은 아니다.

Claude가 구현한 화면과 GPT Backend를 함께 실행해 검증했으며, 이번 Backend70개를 Claude가 별도로 재실행했다고 표현하지 않는다. 공유 기록에 소유권·검토 요청·완료·통합 결과를 남겼다. 원격 push·DB 초기화·개발 계정 암호 재설정은 하지 않았다.


## 후속 확장: 제출 전 역분개(2026-11-13)

RUNNING/PAUSED 담당 작업자의 본인 원본 취소도 지원한다. correctionId=null로 기존 entries API에 REVERSAL 배치를 전송하며 동일수량·1회·사유·원장 불변·배치 최종합계 조건을 유지한다. SUBMITTED 관리자 정정 경로는 그대로다. 세부 규격과 이번225개 검증은 pre-submit-reversal-workflow.md 참고.
