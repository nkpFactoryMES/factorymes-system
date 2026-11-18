# MES API 계약 파일 v1

이 폴더는 **API를 설명하는 로컬 문서·JSON 파일**이다. API 키, 실행 서버, 모델, 유료 서비스 구독이 아니다. OpenAPI는 OpenAI와 다른 공개 명세다.

기준: 사용자 확정 v4 + `outputs/ddl`의31개 테이블. API 구현·DB 마이그레이션은 하지 않았다.

- `public.openapi.json`: 관리자/작업자 공개 API.
- `backend-internal.openapi.json`: 게이트웨이·커넥터·Worker가 호출하는 업무 백엔드 API.
- `gateway.openapi.json`: 연동 게이트웨이가 받는 API.
- `equipment.openapi.json`: 설비 커넥터가 받는 API.
- `ai.openapi.json`: AI 커넥터가 받는 API.
- `mes.schemas.json`: 공통 요청/응답 DTO 정의.
- `mes.events.schema.json`: 수집·설정·경보·알림·분석 이벤트10개 정의.
- `contract-examples.json`: 정상26/거절17개 로컬 예제. 예제 토큰은 가짜이고 로그인/수집에 사용할 수 없다.
- `validation-report.json`: 네트워크 차단 상태의 로컬 문법/스키마 검증 결과.

각 OpenAPI가 공통 스키마를 상대 경로로 참조하므로 폴더 전체를 함께 보관한다. 공개 API만 배포할 때 내부 문서를 외부에 함께 노출하지 않는다.

설명 문서는 상위 폴더의 [계약 기준](../../docs/MES-API-이벤트-계약기준-v1.md)과 [전체 경로 목록](../../docs/MES-API-엔드포인트목록-v1.md)을 참조한다.

현재 기본 정책은 Mock AI, 클라우드 AI 실행 금지, 외부 이메일/채팅 실발송 금지다. 검증 도구는 오픈소스이며 키 없이 로컬 파일만 검사했다. 실제 유료 제공자를 켜는 작업은 별도 사용자 지시 전까지 하지 않는다.
