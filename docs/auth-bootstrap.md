# 로컬 서비스 인증 bootstrap

Backend POST /internal/v1/auth/token: {clientId, clientSecret, audience} → {accessToken, tokenType:"Bearer", expiresIn:300, scope:[]}.

client secret은 개발 파일이며 유료 API 키가 아닙니다. issuer factorymes-backend, 수신 서비스 audience, 서버 allowlist scope를 사용합니다. HTTP body를 로그에 기록하지 않습니다. Web /api는 /internal을 전달하지 않습니다.

서비스별 /internal/v1/status는 diagnostics:read가 필요합니다. 서비스 JWT를 브라우저/일반 사용자 인증에 재사용하지 않습니다. collection proof는 별도키·audience·측정/수신 시간 규약입니다.

현재 추가된 runtime endpoint만 구현 상태로 보며 업무 계약의 미구현 경로를 자동 성공/가짜 데이터로 응답하지 않습니다.
