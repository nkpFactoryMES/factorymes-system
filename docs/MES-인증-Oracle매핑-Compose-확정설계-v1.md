# FactoryMES 인증 · Oracle 매핑 · Compose 구현 기준 v1

2026-11-12 / GPT. 사용자가 승인한 31테이블+병합6개, API 계약 v1을 구현 가능한 초기 프로젝트로 구체화한다. 실제 생산·설비·알림 업무 전체 구현과 프로젝트 골격 완성을 구분한다.

## 기술과 실행 범위

- 설치된 .NET SDK 10.0.404, net10.0 / ASP.NET Core 10 / Blazor Interactive Server를 기준으로 한다.
- Oracle.EntityFrameworkCore 10.23.26400 + EF Core 10.0.15. Oracle Free 23, 기존 승인 DDL을 원본으로 사용한다. EnsureCreated/Migrate/자동 스키마 삭제는 금지한다.
- Oracle 공급자의 NUMERIC Boolean 기본 추론에 맡기지 않고 기존 NUMBER(1,0)을 bool↔0/1 변환으로 명시한다. NUMBER(18,6), TIMESTAMP(6), DATE도 명시 매핑한다.
- Quartz 3.20.1의 기본 호스팅을 사용한다. 골격에서는 RAMJobStore의 비중복 진단 Job만 실행한다. 업무 Job/영구 QRTZ 스키마는 해당 기능 구현 단계에서 공식 스크립트로 추가한다. JOB_RUN이 Quartz 저장소를 대체하지 않는다.
- Redis 7.2 Alpine은 로컬 설정 캐시용이다. 메시지 브로커·유료 SaaS가 아니다. 게이트웨이만 Redis에 연결한다.
- 기본 AI Mock, 클라우드와 실제 외부 발송은 시작 시 정책 검사로 차단한다. 유료 제공자 SDK·키 발급·모델 다운로드는 없다.

## 사용자 인증

- Backend가 USER_ACCOUNT/USER_ROLE을 조회하고 ASP.NET Core PasswordHasher로 검증한다. 외부 Identity 서비스 및 전체 Identity EF 스키마는 도입하지 않는다. 현재 테이블에 맞춘 작은 인증 저장소를 사용한다.
- 로그인/내정보/로그아웃/비밀번호 변경/CSRF 발급을 기본 인증 골격으로 제공한다. 생산 API/회원 관리 UI 구현을 완료했다고 표현하지 않는다.
- 이름·역할·user ID·security_stamp가 들어 있는 보안 쿠키를 발급하고, Backend 요청마다 활성 상태·잠금·stamp를 원본 DB와 확인한다. 역할/비밀번호/비활성 변경 시 stamp 갱신으로 기존 쿠키를 거절한다.
- 쿠키: mes.session, HttpOnly, Secure, SameSite=Lax, Path=/, 30분 만료·슬라이딩 없음. 초기 개발 HTTPS는 localhost용 자체서명 인증서를 사용한다. 운영 인증서 신뢰/키 암호화는 별도 운영 설정이다.
- Web 호스트와 Backend만 동일한 Data Protection 키 볼륨·applicationName·cookie scheme을 공유한다. 다른 서비스에는 사용자 쿠키 키를 주지 않는다. 키는 Git에 넣지 않고 볼륨 삭제와 세션 무효화를 구분한다.
- Web의 /api 프록시는 같은 origin의 사용자 요청을 Backend에 전달한다. YARP를 Web 내부의 HTTP 프록시 라이브러리로만 사용하며 별도의 진입 게이트웨이 서비스를 추가하지 않는다. AI/설비 분기 게이트웨이와 역할이 다르다.
- Backend는 Web의 고정 app-network 주소 <LOCAL_NETWORK>에서 전달된 X-Forwarded-Proto 한 단계만 신뢰한다. app 내부 대역은 <LOCAL_NETWORK>이며 다른 대역으로 바꾸면 두 값을 함께 변경한다. 임의 프록시 전체를 신뢰하거나 Secure 쿠키를 해제하지 않는다.
- /internal은 Web에서 프록시하지 않는다. Blazor의 서버 측 업무 호출을 구현할 때 사용자별 세션과 circuit 재검증을 구현해야 하며 전역 CookieContainer를 공유하지 않는다. 골격 홈은 공개 상태 안내 페이지이며 관리자/작업자 전체 UI는 후속이다.
- 변경 요청은 X-CSRF-TOKEN을 검사하고 로그인도 예외가 아니다. 로그인 후 사용자 정체성이 바뀌면 CSRF 토큰을 다시 받는다. CSRF 실패403, 로그인 실패401을 일정한 형식으로 반환한다.
- 로그인 실패5회면 15분 잠금. 잠금/실패 누계는 DB 트랜잭션과 사용자 행 잠금으로 갱신한다. 애플리케이션 rate limit을 함께 둔다. 비활성·존재하지 않는 사용자·암호 오류를 상세 구분해 노출하지 않는다.
- ADMIN/WORKER 조합은 기존 스키마대로 표현하되 ADMIN이 포함된 계정의 작업자 생산 명령은 거절한다. 행 범위·자기 마감 금지는 각 업무 구현의 서버 트랜잭션에서 검사한다.
- 초기 관리자는 명시적인 로컬 bootstrap 명령으로만 생성한다. 무조건적인 시작 시 관리자 재생성/비밀번호 초기화는 하지 않는다. 암호는 로컬 ignored secret 파일로 전달하고 화면/로그에 출력하지 않는다.

## 서비스 인증과 수집 허가

- 각 서비스는 자신의 client secret 파일로 Backend /internal/v1/auth/token에 서비스 JWT를 요청한다. 이는 로컬 client-credentials bootstrap이며 OpenAI 키나 유료 OAuth 서비스가 아니다.
- Backend만 서비스 서명 private key와 client secret 목록을 가진다. 나머지 서비스는 검증 public key와 자기 client secret만 가진다. Web은 내부 서비스 자격을 갖지 않는다.
- 서비스 token: issuer=factorymes-backend, audience=수신 서비스, client_id/sub, 서버가 허용한 scope, iat/nbf/exp, 5분 수명. scope/audience는 요청 body로 확대할 수 없다. 사용 중인 JWT 라이브러리로 생성/검증하고 자체 서명 알고리즘을 만들지 않는다.
- /internal/v1/status는 diagnostics:read의 테스트 지점이다. 실제 수집/분석 내부 endpoint는 업무 구현 전까지 미구현이며 인증 성공을 업무 성공으로 위장하지 않는다.
- client credential endpoint는 내부 네트워크 전용이다. 공개 Web /api 경로로 접근할 수 없다. 개발 Compose 내부 HTTP는 격리 네트워크에서만 사용하며 운영 시 내부 TLS/키 교체를 추가한다.
- 수집 허가는 별도 키·audience의 단기 서명 증빙이다. 15초/갱신5초/측정시각 검증/수신유예60초, target/instance/configVersion/configHash 바인딩을 따른다. 서비스 JWT로 수집 허가를 대체하지 못한다.
- 전문 수집 증빙 검증기는 ValidateLifetime을 유지하면서 측정시각·수신시각에 맞는 사용자 정의 lifetime 검사와 서명/issuer/audience 검사를 함께 수행한다. 일반 서비스 토큰 검증의 만료를 끄지 않는다.
- 실제 lease 발급·원본 설정 확인 API는 연동 기능 구현 단계에서 완성한다. 골격은 재사용 가능한 증빙 검증 및 부정 사례 테스트를 포함한다.

## Oracle 매핑

- 승인 DDL에서 31개 CLR entity와 테이블/컬럼/키/FK 매핑을 생성한다. 소스는 Backend 내부에만 두며 Gateway/Connector에서 DbContext를 참조하지 않는다.
- Oracle 비인용 객체는 대문자로 정규화되므로 HasColumnName/ToTable을 실제 대문자로 설정한다.
- NUMBER(19,0)/NUMBER(12,0)/version은 long, NUMBER(18,6)은 decimal, NUMBER(1,0) flag는 bool + 정수 변환, 문자열/JSON CLOB은 string으로 매핑한다. JSON 구조 검증은 계약과 기존 IS JSON 제약이 담당한다.
- UTC TIMESTAMP는 DateTime: 쓰기에서 Kind=Utc를 요구하고 읽기에서 UTC Kind를 부여한다. DateTime.Now/Unspecified를 조용히 UTC로 간주하지 않는다.
- BUSINESS_DATE는 시간대 없는 KST DateTime.Date 값으로 DATE에 바인딩한다. UTC 순간으로 변환하거나 YYYYMMDD 숫자로 저장하지 않는다.
- 일반 version은 concurrency token이며 저장 시 original+1, updated_at_utc 갱신을 적용한다. config_version은 설정 서비스가 명시적으로 올리고 heartbeat로 올리지 않는다.
- IDENTITY는 ValueGeneratedOnAdd, PK 없는 행·NULL FK·복합 FK를 승인 DDL대로 매핑한다. NULL unique 컬럼을 EF AlternateKey로 잘못 강제하지 않는다. 참조 대상 복합 UQ만 principal key로 사용한다.
- 조건부 UNIQUE/JSON CHECK/기존 인덱스는 DDL 소유다. ORM migration으로 재생성하지 않는다. 데이터 삭제 cascade를 추가하지 않는다.
- 수량 합계·정정 권한·실적 hash·상태 전이는 ORM이 자동 보장하지 않으며 후속 업무 트랜잭션이 담당한다.
- 검증은 EF 모델 전체 키/FK와 실Oracle에서 31테이블 유계 조회, NUMBER/DATE/TIMESTAMP 매개변수 왕복으로 수행한다. 조회 성공을 모든 업무 상태/경쟁 테스트 통과로 표현하지 않는다.

## Compose와 로컬 비밀

- services: web, mes-backend, integration-gateway, equipment-connector, ai-connector, jobs-worker, equipment-simulator, oracle, redis. tooling은 명시적인 tools profile이다.
- Web HTTPS는 127.0.0.1:7443, Oracle 로컬 진단은 127.0.0.1:1527만 공개한다. 내부 서비스와 Redis 포트는 호스트에 공개하지 않는다.
- Web은 app 네트워크, 내부 서비스는 internal app 네트워크, Oracle/Redis는 internal data 네트워크로 분리한다. Backend만 Oracle에 연결하고 Gateway만 Redis에 연결한다. simulator는 Equipment Connector와만 통신한다.
- 새 프로젝트 전용 Oracle 볼륨을 사용한다. 기존 mes-ddl-verify 및 가족 미디어 컨테이너/볼륨을 삭제·변경하지 않는다.
- 첫 빈 Oracle 볼륨 초기화에서 전용 factorymes 계정으로 01_tables→02_indexes만 적용한다. SYS 스키마에 업무 테이블을 만들지 않는다. 99_drop_all·업무 rollback 테스트는 자동 초기화에서 제외한다.
- 비밀 초기화 도구가 offline CSPRNG로 DB 암호·client secret·서명 키·localhost 인증서를 만든다. Git ignored .secrets에 저장하고 Compose secrets 파일로 필요한 서비스에만 mount한다. 비밀 원문을 .env/이미지/로그/사용자 보고에 넣지 않는다.
- health/live는 프로세스, health/ready는 의존성 상태를 구분한다. Backend는 Oracle, Gateway는 Redis 준비를 확인한다. 초기화 지연은 healthcheck와 앱 재시도로 처리한다.
- stop/start/down은 데이터 볼륨을 유지한다. down -v는 README 기본 명령으로 안내하지 않는다. 컨테이너/볼륨 전체 삭제는 별도 사용자 요청 없이 하지 않는다.
- 시작 시 서비스 정책은 외부 AI/메일/채팅 실행 옵션이 true면 실패한다. 현재 골격에 외부 provider SDK·발송 Job·자동 AI 추론을 넣지 않는다.

## 서비스 분리 수준

솔루션/저장소는 하나지만 Web·Backend·Gateway·Equipment Connector·AI Connector·Jobs·Simulator는 각자의 .csproj와 Program.cs를 가진 7개 독립 서버 프로세스다. Oracle/Redis를 합하면 상시 컨테이너9개이고, 공통 Foundation 라이브러리·Tooling CLI를 합하면 C# 프로젝트9개다. 서비스 폴더가 하나의 실행 파일에 병합되는 구조가 아니다.

반면 Backend 내부의 Orders/Alarms/Notifications 등은 아직 한 서버의 기능 모듈이다. 전체 업무가 독립 DB를 소유하는 완전한 MSA가 아니라 모듈형 업무 백엔드+별도 연동 서비스 구조다. 사용자의 단독 개발 범위를 유지하면서 나중에 다른 호스트로 분리 배포할 수 있다.

## 골격 완료 기준과 후속

골격 완료: 서비스별 .csproj/solution, 버전 고정, 빌드 성공, 31테이블 매핑 검증, 로컬 HTTPS/인증·서비스 경계·healthcheck/Mock 정책 검증, Compose 구성 검사 및 가능한 로컬 기동 확인, 계약/DDL/문서 사본과 실행 안내.

후속 구현: 관리자/작업자 업무 화면·작업지시/실적 → 원본 설정/Redis → 실제 수집·허가/경보·Outbox → Mock 분석. 최신 계약 v1.1의 98개 API 전체가 구현된 것으로 표시하지 않는다. 외부 비용 기능은 별도 지시 전 비활성이다.

공식 근거: [Oracle EF Core 10 패키지](http<LOCAL_PATH>), [Oracle EF Core 매핑/호환성](http<LOCAL_PATH>), [.NET 지원 정책](http<LOCAL_PATH>), [ASP.NET Core 인증](http<LOCAL_PATH>), [Oracle 컨테이너 초기화](http<LOCAL_PATH>).

계약 기준 갱신: 공유 기록의 사용자 확정에 따라 api-contracts-v1.1을 구현 기준으로 사용한다. 기존 v1 사본은 참고용으로 보존한다.
