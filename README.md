# FactoryMES System

FactoryMES의 Oracle 스키마, Docker Compose, 통합 검증과 공개 설계 문서를 관리합니다.
실행 서비스 소스는 각자의 저장소에 있으며 이 저장소 Dockerfile은 검증 도구만 빌드합니다.

## 저장소 경계

9개 실행 저장소는 `build/repositories.lock.json`에 기록된 형제 폴더에 둡니다.
웹은 Host와 Blazor WebAssembly Client를 같은 웹 저장소에서 빌드합니다.
공통 계약과 기반 기능은 `factorymes-platform`의 정확한 버전 NuGet을 사용합니다.
Oracle 스키마와 데이터는 현재 공유합니다. 서비스별 DB 분리는 구현하지 않았습니다.

통합 검증 도구는 Backend 및 EquipmentConnector 내부 로직 검증을 위해
`FactoryMES.Backend.TestSupport`와 `FactoryMES.EquipmentConnector.TestSupport`의
정확한 1.0.0 패키지에 결합되어 있습니다. 이는 버전이 고정된 화이트박스 테스트이며
서비스 구현과 완전히 독립적인 블랙박스 테스트라는 의미가 아닙니다.
HTTP·브라우저 시나리오와 함께 내부 정책 회귀를 확인하려는 선택입니다.

## 깨끗한 환경에서 재현

필요 도구는 Docker Compose, .NET SDK(global.json), PowerShell 7, Node.js입니다.
이 저장소를 빈 작업 디렉터리에 clone한 뒤 실행합니다.

```powershell
./tools/bootstrap-workspace.ps1
./tools/check-system.ps1
dotnet run --project tools/FactoryMES.Tooling -- init-secrets .secrets
./tools/configure-local.ps1 -SecretsDirectory .secrets `
  -AppSubnet '<LOCAL_SUBNET>' -WebAddress '<LOCAL_PROXY_ADDRESS>'
./tools/stack.ps1 -Action Start -WithoutLocalAi
docker compose -f deploy/compose.yaml --profile tools run --rm tooling init-admin
docker compose -f deploy/compose.yaml --profile tools run --rm tooling init-demo
docker compose -f deploy/compose.yaml --profile tools run --rm tooling verify
```

bootstrap은 공개 릴리스에서 SHA256으로 고정한 패키지를 다운로드하고,
없는 서비스 checkout을 검토된 commit으로 생성합니다. 기존 checkout은 자동으로
reset하거나 checkout하지 않습니다. 실제 상태는 ignored `.workspace-lock.json`에 남습니다.
소스 고정 manifest의 commit이 비어 있으면 새 checkout 생성은 실패합니다.
빈 Oracle 볼륨은 첫 기동 시 `database/oracle` DDL로 스키마를 설치합니다.
위 init-admin/init-demo는 개발 관리자·작업자와 시연 기준정보를 명시적으로 준비합니다.
초기 비밀번호는 로컬 secret 파일을 읽으며 문서·로그에 출력하지 않습니다.
기존 데이터가 있으면 초기화 명령을 다시 실행하지 않습니다.

기존 개발 환경을 이어 쓰려면 기존 secret 디렉터리를 지정하고
`-DiscoverExistingNetwork`로 현재 Docker 네트워크 값을 읽을 수 있습니다.
실제 경로·비밀번호·주소는 ignored `.env`와 `.secrets`에만 보관합니다.
Compose project name `factorymes`와 기존 named volume 이름을 유지합니다.
`Stop`은 컨테이너만 정지하고 볼륨을 제거하지 않습니다.

로컬 AI는 기본 stack 명령에서 포함되지만 위 무료 Mock 재현은 명시적으로 제외합니다.
GPU 모델 다운로드 및 실제 외부 이메일·채팅 발송은 자동으로 실행하지 않습니다.
13개 실행 서비스 구성은 로컬 AI 포함 기준입니다. 각 기능은 서비스별 문서를 참고합니다.

## 검증과 서비스 단독 배포

`tools/check-system.ps1`은 잠금 복원·빌드·실제 Modbus 프로토콜 검증을 실행합니다.
CI는 공개 저장소의 표준 Ubuntu runner에서 이것과 도구 이미지만 빌드합니다.
Oracle, GPU, 유료 API, 외부 메일·채팅, artifacts/cache 저장소는 사용하지 않습니다.
전체 Oracle·브라우저 통합 검증은 로컬에서 기존 `tools/verify-*.ps1`로 실행합니다.
브라우저 스크립트 전에는 `tools/import-local-environment.ps1`을 dot-source합니다.
Windows Edge는 자동 탐색하며 다른 환경에서는 Playwright Chromium을 설치하거나
`MES_BROWSER_EXECUTABLE`을 지정합니다. 결과·스크린샷은 공개하지 않습니다.

`tools/deploy-service.ps1 -Service <service> -Image <reviewed-image>`는 해당 서비스만
`up --no-build --no-deps`로 갱신합니다. 같은 서비스의 로컬 태그 또는 해당 GHCR 저장소의
SHA256 digest만 받습니다. 배포 전 컨테이너·이미지를 ignored `.deploy`에 기록하고,
다른 서비스 컨테이너 ID와 이미지가 그대로인지 비교합니다. 실패 시 자동 롤백은 하지 않으며
기록한 이전 이미지로 다시 실행합니다. 실제 원격 서버 자동 배포는 구성하지 않았습니다.
