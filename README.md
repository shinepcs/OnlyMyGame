# OnlyMyGame

Unity 6000.5.6f1 WebGL용, 매 턴 AI 규칙이 바뀌는 1인 SRPG 버티컬 슬라이스입니다.

## 실행

1. `Assets/Resources/OnlyMyGameConfig.json`의 `apiBaseUrl`을 NAS HTTPS 주소로 바꿉니다.
2. NAS에서 `OPENAI_API_KEY`, `ONLYMYGAME_ALLOWED_ORIGIN`, `ONLYMYGAME_DAILY_SALT`을 secret/environment로 제공하고 `docker compose up -d --build`를 실행합니다.
3. Unity에서 `Assets/Scenes/OnlyMyGame.unity`를 열어 플레이하거나 `BuildScript.BuildWebGL`로 WebGL을 빌드합니다.

API 키는 Unity 프로젝트·WebGL 산출물·Git에 포함하지 않습니다. `GET /health`가 설정 상태를 알려주며, 키가 없거나 AI 응답이 20초 안에 도착하지 않으면 게임은 마지막 해결 턴을 저장하고 재시도 메시지를 표시합니다.

## NAS API 자동 배포

Codex 작업이 끝날 때마다 프로젝트의 `Stop` 훅이 실행됩니다. API 이미지에 포함되는 파일(`Server/OnlyMyGame.Api`, `RuleCore.cs`, Docker Compose)의 해시가 마지막 성공 배포와 다를 때만, 현재 Mac의 변경 파일을 NAS로 직접 전송하고 `onlymygame-api` 컨테이너를 다시 빌드·시작합니다. Git commit·push나 GitHub Actions는 필요하지 않습니다. 변경이 없으면 아무 작업도 하지 않습니다.

한 번만 다음을 준비합니다.

1. NAS에서 SSH를 활성화하고, Docker 실행 권한과 이 저장소 checkout 권한이 있는 계정을 준비합니다.
2. Mac의 `~/.ssh/config`에 NAS 대상과 `StrictHostKeyChecking`용 호스트 키를 설정합니다. NAS SSH 포트는 `3442`를 사용합니다. SSH 키를 권장하며, 비밀번호 파일을 사용한다면 Mac에 `sshpass`가 설치돼 있어야 합니다.
3. `.codex/nas-deploy.env.example`을 `~/.config/onlymygame/nas-deploy.env`로 복사하고 실제 NAS checkout 경로를 입력합니다. `NAS_DEPLOY_PORT`의 기본값은 `3442`입니다.
4. Codex에서 프로젝트 훅을 신뢰합니다. 새 훅은 Codex가 검토·신뢰하기 전에는 실행되지 않습니다.

NAS의 `.env`에 있는 `OPENAI_API_KEY`, `ONLYMYGAME_ALLOWED_ORIGIN`, `ONLYMYGAME_DAILY_SALT`은 로컬·GitHub에 복사하지 않습니다.
