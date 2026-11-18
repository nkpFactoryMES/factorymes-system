# 제출 전 작업자 실적 취소(역분개) 구현·검증

2026-11-13 / 실제 프로젝트 `<LOCAL_PATH>`

## 동작

RUNNING/PAUSED 담당 WORKER가 본인이 입력한 MANUAL TOTAL/REJECT 원본을 선택하고 사유를 입력해 취소한다. 원본은 수정·삭제하지 않고 같은 수량의 REVERSAL 기록을 추가해 합계에서 뺀다. 취소한 원본은 후보에서 사라지며 원본당 한 번만 가능하다.

여러 원본을 선택해 한 배치(최대50)로 취소할 수 있다. 예를 들어 총10/불량2에서 총10만 취소하면 불량이 총수량보다 커져 거절된다. 총10과 불량2를 함께 취소하면 최종0/0으로 한 번에 저장된다. PAUSED 취소가 작업을 재개하거나 설비 점유를 풀지 않는다. 잘못 입력한 수량을 바꾸려면 취소 후 기존 실적 추가 기능으로 다시 입력한다.

SUBMITTED 이후에는 기존처럼 관리자의 OPEN 정정 요청과 correctionId가 필요하다. 관리자 정정 요청→작업자 역분개·대체실적→관리자 확인→마감 흐름은 유지했다. CLOSED에는 신규 실적/취소를 허용하지 않지만, 이미 성공한 동일 요청의 결과 확인은 읽기만 수행한다.

## API와 정합성

기존 POST /api/v1/orders/{orderId}/entries 및 EntryBatch/EntryInput/EntryView를 그대로 사용한다. 새 테이블·엔드포인트·DTO·마이그레이션은 없다.

| 항목 | 처리 |
|---|---|
| 권한 | ADMIN 생산명령403, 다른담당404, 담당WORKER만 |
| 제출 전 원본 | 같은작업 TOTAL/REJECT,RecordedBy=요청자,CorrectionId=null |
| 수량·사유 | 원본과 같은 양의 qty,사유1~1000자 |
| 원장 보호 | 다른작업/다른작성자/역분개의역분개/수량불일치 거절,원본당1회 |
| 합계 | 배치 최종 총수량≥불량≥0,서버 상한 유지 |
| 동시성 | WORK_ORDER 행잠금·expectedVersion·원장/UQ·감사 한 트랜잭션 |
| 재전송 | 동일UUID/본문/요청자 배치는 버전·상태검사 전 기존결과 반환,부분중복/본문변경409 |

네트워크·5xx 결과 미확인 시 최초 UUID·본문·원래 작업ID를 보존한다. 다른 작업을 열어도 취소 요청은 원래 작업으로만 재전송한다. 4xx 확정 거절 후에는 요청을 비우고 최신 원장을 확인하여 다시 선택한다. 조회·서버시각 응답 중 작업을 바꿀 때 이전 결과가 섞이지 않게 조회 키와 전송할 원본/작업/버전/사유를 고정했다.

## 분담과 변경 파일

공유 FactoryMES-제출전역분개-동시구현분담-v1.md에 규격·소유권을 먼저 합의했다. Claude22:48 수락,22:50 화면/테스트 완료,22:53 작업전환·전송문맥 보완 완료를 기록했다.

- GPT: OrderService.Entries.cs의 제출전 REVERSAL 허용·원본작성자 검사, 새 Tooling/PreSubmitReversalVerification.cs와 Program.cs의 verify-pre-submit-reversal 모드, 부모 Orders.razor 연결, 추가 verify-pre-submit-review.cjs, 공통빌드·Compose·문서.
- Claude: 새 PreSubmitReversal.razor/.css, verify-pre-submit-reversal.cjs. 기존 OrderOperations.razor와 관리자 정정 API/DTO는 변경하지 않았다. Claude 소유 파일을 GPT가 직접 수정하지 않았다.

## 실제 검증

| 검증 | 결과 |
|---|---:|
| 전체 Release 빌드 | 경고0·오류0 |
| 새 실제 Oracle/HTTPS API·DB 원장 검증 | 59/59 |
| Claude 작성 신규 브라우저(GPT 실행) | 7/7 |
| GPT 추가 응답유실·4xx·다른작업·CLOSED재전송 브라우저 | 10/10 |
| 기존 관리자 정정/중지 API | 70/70 |
| 기존 일반 작업지시/실적 API | 44/44 |
| 기존 발행·실적·마감 브라우저 | 12/12 |
| 기존 중지·정정·마감 브라우저 | 11/11 |
| 기존 기준정보·초안수정/취소 브라우저 | 12/12 |
| 합계(회귀 포함) | 225 |

원본 불변, 사유/동일수량, 본인원본 제한, 타담당/다른작업 차단, 합계위반의 전량롤백, 동시취소 한 요청만 성공, 감사 이벤트가 성공배치에만 추가됨, 제출·마감 후 동일UUID 읽기재전송을 확인했다. 별도 DB 원본작성자 fixture도 거절을 확인하고 자체 데이터만 정리했다. 첫 검증의 정정 resolve 경로 오기는 GPT 검증 코드만 실제 /api/v1/corrections/{id}/resolve에 맞춰 수정한 뒤 전 시나리오를 다시 통과했다.

컨테이너9개 healthy. canonical DDL2개·API/스키마 JSON7개 바이트 동일, 로그인 session.js 해시 유지. 유료AI·실제외부발송0. 사용자 계정/암호 초기화 없음. 브라우저 시연은 고유 작업의 CLOSED/CANCELLED 이력을 보존하고 API 검증은 고유 fixture만 정리한다.

## 실행·확인

```powershell
Set-Location <LOCAL_PATH>
docker compose -f deploy/compose.yaml --profile tools run --rm tooling verify-pre-submit-reversal
Set-Location tests/browser
node verify-pre-submit-reversal.cjs
node verify-pre-submit-review.cjs
```

http<LOCAL_PATH> 에서 portal로 로그인한 뒤 시작한 작업 상세의 **제출 전 실적 취소**에서 원본을 선택한다. 관리자 화면에는 취소 폼이 없다. 실제 사용자당 원장과 상태·권한은 서버가 최종 검사한다.

## Claude 독립 재검증 상태

사용자 안내로 Claude가 사용 한도에 도달하여 2026-11-13 23:50(KST)까지 새 작업이 어렵다. 23:00 [GPT→Claude 요청]은 한도 회복 후 진행할 후속이며, 현재 새 Backend를 Claude가 독립 재검증 완료했다고 주장하지 않는다. 위225개는 GPT가 실제 통합 실행한 결과다. Claude 임시 사본 빌드·화면 구현 완료와 통합 독립 재검증은 구분한다. 자동 호출·예약 작업은 만들지 않았다.

다음 요청은 새 API59/브라우저7 재실행과 Backend 소규모 변경·컴포넌트 검토이며 코드는 수정하지 않고 발견 사항을 공유 기록에 남기는 것이다. 이후 기능은 Redis 설정/설비 수집→경보/Outbox→집계/Mock AI, AUTO·마감후수정 등 기존 계획을 따른다.


## 2026-11-14 독립 검증 상태 갱신

사용 한도로 보류했던 Claude 검증은00:16 완료됐다. API59/59·UI7/7·독립HTTP15/15 PASS,결함 없음. 이전 보류 기록은 과거 시점의 상태다.
