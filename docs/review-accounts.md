# Claude 검증 후속 수정 및 개발 계정 설정

2026-11-13 / GPT. 실제 프로젝트: `<LOCAL_PATH>`.

## 최신 Claude 기록 확인

20:17 독립 검증과 20:23 결함 수정 기록을 읽었다. Claude가 잘못된 JSON 본문의 BadHttpRequestException이500으로 바뀌는 원인을 고쳤고, 회귀 테스트를 추가해 업무 검증은43→44개가 됐다. 이 수정과 Claude의 가독성 정리를 보존했다.

작업 초반 현재 배포에서 잘못된 JSON이400으로 반환되는 것을 확인했지만, 응답에 계약에 없는 traceId 필드가 남아 추가 필드 금지 규칙을 위반했다. Foundation의 ProblemDetails 맞춤 처리에서 traceId를 제거하고 기존 correlationId를 유지했다. 잘못된 요청의 원래 상태(400·415)를 유지하며, 일반 서버 예외는500으로 구분하는 Claude의 StatusCodeSelector를 유지했다. 로그인 JS·DB DDL·API v1.1 원본은 변경하지 않았다.

## 요청한 개발 계정

| 로그인 ID | 권한 | 결과 |
|---|---|---|
| admin | ADMIN | 요청 비밀번호로 갱신·로그인 확인 |
| portal | WORKER | 요청 비밀번호로 설정·로그인 확인 |

비밀번호 원문은 이 문서·공유 기록·소스 코드에 남기지 않았다. 값은 ignored `.secrets/admin_password`, `.secrets/portal_password`에 저장하며 Git 제외를 확인했다. Oracle에는 PasswordHasher 해시를 저장한다.

새 `DevelopmentAccounts`와 `init-dev-accounts` 명령은 Development 환경에서 명시적으로 실행할 때만 적용한다. 두 계정의 역할을 정확히 설정하고 활성화·잠금 해제·실패 횟수 초기화·security_stamp 갱신을 수행한다. 이전 로그인 세션은 무효화된다. 계정 ID·작업 이력은 보존하고 이전 시연 작업자 worker.demo도 삭제하지 않았다. 앱 재시작 시 비밀번호를 자동 초기화하지 않는다.

사용자가 개발용으로 요청한 짧은 암호는 로컬 도구에서 설정했다. 일반 비밀번호 변경 API의12자 정책과 공개 사용자 생성 계약을 완화하지 않았다. 검증에서 짧은 새 암호의 공개 변경 요청이400으로 거절되고 현재 세션이 유지되는 것을 확인했다.

## 수정 파일

- `shared/FactoryMES.Foundation/ServiceFoundation.cs`: Problem 응답 추가 traceId 제거. 기존 Claude 상태 코드 수정 보존.
- `tools/FactoryMES.Tooling/DevelopmentAccounts.cs`: 개발 계정 전용 명시적 설정·감사 기록.
- `tools/FactoryMES.Tooling/Program.cs`: portal 비밀 생성과 init-dev-accounts 진입점.
- `deploy/compose.yaml`: portal_password를 tooling에만 마운트.
- `tests/browser/verify-input-errors.cjs`: 요청 오류8종의 상태·Content-Type·Problem 필수/추가 필드 검사.
- `tests/browser/verify-dev-accounts.cjs`: admin/portal 로그인·정확한 역할·관리자 경계·비밀번호 정책12개 검사.
- `tests/browser/verify-workflow.cjs`: 기존 기본값을 유지하면서 환경변수로 portal을 선택해 브라우저 업무 검증 가능.
- `README.md`: 현재 계정과 재현 명령. 비밀번호 원문 없음.

## 최종 검증

| 항목 | 결과 |
|---|---|
| Release solution build | 경고0·오류0 |
| 기존 Oracle·인증·JWT |35/35 PASS |
| 업무 흐름 및 Claude JSON 회귀 |44/44 PASS |
| 실제 Edge 브라우저 admin→portal→admin |12/12 PASS, runtime error0 |
| 요청 오류 상태·Problem 형식 |8/8 PASS |
| 요청 계정 로그인·권한·기존 암호 정책 |12/12 PASS |
| Compose 전체 서비스 재빌드 후 |9개 healthy |
| 유료 API/외부 발송 |0/0 |

오류8종: 알 수 없는 submit/create/login/password-change 필드, 잘못된 숫자 타입, 손상된 JSON, 빈 본문, 미지원 Content-Type. 결과는400 또는415이며 application/problem+json이다. 일반 서버 예외는 분류 코드상500을 유지하며, 의도적 서버 장애를 만드는 공개 테스트 엔드포인트는 추가하지 않았다.

브라우저에서는 요청한 admin 계정으로 작성·발행하고 portal에게 배정했다. portal 로그인·점검·시작·실적 거절 후 수정·총10/불량2 저장·제출 후 admin이 마감했다. 시연 마감 건은 이력으로 남겼고 테스트용 임시 DB fixture는 검증기가 자기 데이터만 정리했다.

검증 결과: [오류8개](../verification-results/input-errors-verification.json), [계정12개](../verification-results/dev-accounts-verification.json), [기존35개](../verification-results/scaffold-verification.json), [업무44개](../verification-results/orders-verification.json), [portal 브라우저12개](../verification-results/FactoryMES-browser-workflow-verification.json).

[실제 마감 화면](../verification-results/FactoryMES-manual-closed.png)

## 재현

```powershell
Set-Location <LOCAL_PATH>
dotnet build FactoryMES.sln -c Release --no-restore
docker compose -f deploy/compose.yaml --profile tools run --build --rm tooling init-dev-accounts
docker compose -f deploy/compose.yaml --profile tools run --rm tooling verify
docker compose -f deploy/compose.yaml --profile tools run --rm tooling verify-orders
Set-Location tests/browser
$env:MES_BROWSER_WORKER_LOGIN = 'portal'
$env:MES_BROWSER_WORKER_SECRET = 'portal_password'
node verify-input-errors.cjs
node verify-dev-accounts.cjs
node verify-workflow.cjs
```

init-dev-accounts는 파일의 값으로 비밀번호와 세션 stamp를 다시 설정하므로 계정 재설정이 필요할 때만 실행한다. 검증 스크립트는 암호·쿠키를 보고서에 쓰지 않는다. /auth 관련 요청을 너무 빠르게 반복하면 기존 분당 제한이 적용되므로429는 제한 창 경과 후 재확인한다.

접속: http<LOCAL_PATH> → 기존 로그인 폼 → 업무 화면. 인증서 경고는 로컬 자체서명 개발 인증서 때문이다.

## 인계·다음 작업

Claude의20:23 수정은 확인 완료이고, 이번 GPT 추가 보완·개발 계정 설정 및 재검증 결과를 종합.md와 오늘 기록에 남겼다. 새로운 기능은 추가하지 않았다. 이번 추가 보완을 Claude가 다시 독립 검증했다고 표현하지 않는다.

다음은 기존 계획의 정정/중지·재개/기준정보 UI 또는 Redis·설비 수집·경보/Outbox 단계다. 서비스 토큰 발급 경로의 OpenAPI 누락은 이전 후속 사항으로 남아 있다. 원격 push·DB 초기화·유료 AI·실발송은 하지 않았다.
