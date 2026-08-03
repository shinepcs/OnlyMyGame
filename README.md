# OnlyMyGame

Unity 6000.5.6f1 WebGL용, 매 턴 AI 규칙이 바뀌는 1인 SRPG 버티컬 슬라이스입니다.

## 실행

1. `Assets/Resources/OnlyMyGameConfig.json`의 `apiBaseUrl`을 NAS HTTPS 주소로 바꿉니다.
2. NAS에서 `OPENAI_API_KEY`, `ONLYMYGAME_ALLOWED_ORIGIN`, `ONLYMYGAME_DAILY_SALT`을 secret/environment로 제공하고 `docker compose up -d --build`를 실행합니다.
3. Unity에서 `Assets/Scenes/OnlyMyGame.unity`를 열어 플레이하거나 `BuildScript.BuildWebGL`로 WebGL을 빌드합니다.

API 키는 Unity 프로젝트·WebGL 산출물·Git에 포함하지 않습니다. `GET /health`가 설정 상태를 알려주며, 키가 없거나 AI 응답이 20초 안에 도착하지 않으면 게임은 마지막 해결 턴을 저장하고 재시도 메시지를 표시합니다.
