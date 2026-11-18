# 로컬 Oracle·DataGrip 접속 (2026-11-16)

- 호스트: 127.0.0.1
- 포트: 1527 (컨테이너 내부는1521)
- 연결 방식: Service Name
- 서비스: FREEPDB1
- 사용자: factorymes
- 비밀번호: <LOCAL_PATH> 파일 내용
- URL: jdbc:oracle:thin:@//127.0.0.1:1527/FREEPDB1

웹 admin/portal 암호와 DB 암호는 다르다. 비밀 값은 문서/로그에 복사하지 않는다.
DataGrip Schemas 탭에서 FACTORYMES 스키마를 체크하고 Apply/동기화하면 테이블이 보인다.

실제 장애: Compose에는127.0.0.1:1527:1521이 있었으나 Oracle이 internal oracle-data에만 연결돼
HostConfig.PortBindings와 달리 NetworkSettings.Ports가 비어 있었다. 내부 리스너는 정상이고
Windows TCP는 실패했다. 단순 up으로는 바뀌지 않았다.
Oracle에만 oracle-local 브리지를 추가해 게시 경로를 만들고 기존 볼륨으로 컨테이너를 재생성했다.
다른 Backend/Redis/수집 네트워크의 internal 설정은 유지하고 포트는 루프백127.0.0.1에만 바인딩했다.
이는 로컬 개발 DB 도구용 경로다. LAN 전체 공개나 기존DB 초기화가 아니다.

수정 후 docker compose port oracle 1521 =127.0.0.1:1527,
Windows Test-NetConnection TcpTestSucceeded=True, 실제 .NET Oracle 드라이버로
factorymes 로그인과 USER_TABLES31개 조회를 확인했다. 사용자/암호/기존 데이터는 유지했다.
재진단: docker compose -f deploy/compose.yaml port oracle 1521,
Test-NetConnection 127.0.0.1 -Port 1527. ORA-12541은 비밀번호 검증 전 네트워크 단계다.
ORA-01017로 바뀌면 파일의 앱 암호를 사용했는지 확인하고 임의로 암호를 초기화하지 않는다.
