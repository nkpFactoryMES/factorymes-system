# 기준정보 관리·작업지시 초안 수정 및 취소 구현·검증

2026-11-13 / 실제 프로젝트 `<LOCAL_PATH>`

## 완료한 기능

- 관리자 /master-data: 품목(EA)·라인·설비·사용자 조회, 커서 더보기, 등록, 수정, 비활성화. 사용자 초기 암호는 일반 정책12~256자, 응답/감사에 암호·해시·stamp 없음. 기존 개발계정 admin/portal은 변경하지 않았다.
- 관리자 /orders 상세: DRAFT의 품목·설비·순수WORKER 담당자·작업명·목표·KST 계획·체크리스트 정의 수정. 같은 순번을 삭제·재생성해도 단일 트랜잭션, 부모 버전 한 번 증가. RELEASED 이후 초안 변경 불가.
- DRAFT/RELEASED 미착수 지시만 사유와 버전으로 CANCELLED 처리. 착수/제출/마감된 작업의 취소 불가. 취소 시 전이·감사·원본 작업을 보존한다.
- 기준정보 이력 후 코드/설비 라인 변경, 미완료 작업의 참조 비활성화/담당자 역할 변경, 마지막 활성 ADMIN 제거를 서버에서 차단한다. 역할·활성 변경은 세션 stamp를 바꿔 기존 Cookie를 무효화한다.
- Cookie+CSRF와 서버 ADMIN 정책 유지. WORKER 기준정보 목록은 자기 작업 참조만, 상세 관리와 변경 API는 거절한다. 기준정보 물리 DELETE 없음.
- 버전 충돌 때 입력 보존, 최신 서버 필드·점검표 전체 비교 후 명시적 재저장. 일부 참조 조회 실패 후 다음 열기에서 세 목록 재조회. 취소 응답 유실 시 자동 재송신하지 않고 새로고침으로 결과 확인.

## GPT·Claude 분담

공유 FactoryMES-기준정보초안-동시구현분담-v1.md에 경로·DTO·권한·상태·소유권을 먼저 정했다. Claude21:46 수락,21:51 UI/테스트 완료,22:01 검토2건 보완 확인. GPT는 Backend/DTO/부모 연결/API71개·추가UI10개·빌드·Compose, Claude는 새 MasterData/OrderDraftEditor 화면과 브라우저12개 검증을 담당했다. Claude 소유 파일은 GPT가 수정하지 않았다.

기준정보 변경과 작업 생성/발행/시작의 참조검사는 PRODUCT 테이블 SHARE ROW EXCLUSIVE 잠금을 같은 트랜잭션 끝까지 사용한다. 마스터 변경에서는 작업 행을 잠그지 않아 주문행→공유잠금 순서와 역순 경합을 피한다. 소규모 개인 포폴에서 단순한 정합성을 우선한 설계이며 고처리량 운영에서는 잠금 세분화가 필요하다.

## 검증 결과

| 검증 | 결과 |
|---|---:|
| 전체 Release 빌드 | 경고0·오류0 |
| 새 실제 Oracle API | 71/71 |
| Claude 작성 기준정보·초안 브라우저 | 12/12 |
| GPT 추가 조회실패·충돌·응답유실 브라우저 | 10/10 |
| 기존 중지·정정 API | 70/70 |
| 기존 MANUAL 작업 API | 44/44 |
| 기존 인증·모델·내부토큰 | 35/35 |
| 기존 기본 업무 브라우저 | 12/12 |
| 기존 중지·정정 브라우저 | 11/11 |
| 검증 항목 합계 | 265 |

컨테이너9개 healthy. canonical DDL2개·API/스키마 JSON7개 바이트 동일, session.js SHA256 유지. 유료AI 호출·외부 이메일/채팅 발송0. 시험이 만든 고유 기준정보/취소·마감 이력은 보존하며 기존 업무 데이터는 삭제하지 않았다. 중단된 GPT 초안 검증76/81/84번은 정상 취소로 정리했다.

## 실행

관리자 로그인 후 작업지시 상단의 **기준정보 관리**로 이동한다. 작업지시 상세의 **초안 수정 열기**, **취소 사유 → 작업지시 취소**를 사용한다. 주소 http<LOCAL_PATH> .

```powershell
Set-Location <LOCAL_PATH>
node verify-master-api.cjs
node verify-master-draft.cjs
node verify-master-review.cjs
```

별도 Client·페이지별 InteractiveWebAssembly(prerender:false)와 동일출처 /api를 유지한다. 공통 DTO MasterData.cs, Backend MasterDataService.cs·OrderService.Draft.cs·OrderEndpoints.MasterData.cs·OrderEndpoints.Draft.cs, UI Pages/MasterData·Components/OrderDraftEditor가 주 구현 파일이다. Backend 참조검사/기존 선택목록과 부모 Orders 페이지도 필요한 범위만 수정했다.

## 후속

Claude22:32 요청에 따른 새 Backend 독립 검토 결과를 확인해 추가 기록한다. 제출 전 RUNNING/PAUSED 담당 작업자 역분개는 Claude21:44 사용자 확정 요청으로 전달됐고, 이번 기준정보 변경과 분리한 다음 작업이다. AUTO 카운터·마감 후 정정·설비수집·Redis 설정 동기화·알림Outbox·집계/Mock AI는 아직 미구현 범위다.
