# OnlyMyGame

Unity 6000.5.6f1 WebGL용, 매 턴 AI 규칙이 바뀌는 1인 SRPG 버티컬 슬라이스입니다.

## 실행

1. `Assets/Resources/OnlyMyGameConfig.json`의 `apiBaseUrl`을 NAS HTTPS 주소로 바꿉니다. `schemaVersion`과 `compatibilityVersion`은 클라이언트·API 배포 호환성 계약이므로 서버 상수와 함께 변경합니다.
2. NAS에서 `OPENAI_API_KEY`, `ONLYMYGAME_ALLOWED_ORIGIN`, `ONLYMYGAME_DAILY_SALT`, `ONLYMYGAME_TRUSTED_PROXIES`, `ONLYMYGAME_GLOBAL_DAILY_LIMIT`를 secret/environment로 제공하고 `docker compose up -d --build`를 실행합니다. `ONLYMYGAME_DAILY_SALT`는 교체하지 않고 유지하는 충분히 긴 무작위 값이며, trusted proxy에는 실제 Synology reverse proxy에서 컨테이너로 들어오는 source IP만 지정합니다.
3. Unity에서 `Assets/Scenes/OnlyMyGame.unity`를 열어 플레이하거나 `BuildScript.BuildWebGL`로 WebGL을 빌드합니다.

API 키와 세션 토큰은 Unity 프로젝트·세이브·WebGL 산출물·Git에 포함하지 않습니다. 클라이언트는 실행별 단기 토큰을 `POST /v1/sessions`에서 받고, 이 토큰으로 규칙 생성 요청을 인증합니다. `GET /health`는 설정 상태와 `apiVersion`, `compatibilityVersion`을 제공하며, 키가 없거나 AI 응답이 20초 안에 도착하지 않으면 게임은 마지막 해결 턴을 저장하고 재시도 메시지를 표시합니다.

## 리소스 편집 (KayKit 프리팹 & 쇼케이스 씬)

게임은 `Assets/KayKit`의 FBX 에셋을 프리팹으로 변환해 사용합니다. 아직 프리팹이 생성되지 않았다면 게임이 임시 Primitive로 실행되므로, 아래 순서로 리소스를 먼저 빌드하세요.

### 1. 프리팹 + 카탈로그 생성

Unity 메뉴에서 **OnlyMyGame → Build KayKit Prefabs**를 실행합니다.

- `Assets/OnlyMyGame/Resources/KayKit/*.prefab` — FBX → 프리팹 변환 (유닛 0.5배, 나머지 1.0배 스케일)
- `Assets/OnlyMyGame/Resources/OnlyMyGamePresentation.asset` — 게임이 참조하는 프레젠테이션 카탈로그

### 2. 리소스 쇼케이스 씬 열기

Unity 메뉴에서 **OnlyMyGame → Open Resource Showcase Scene**을 실행하면
`Assets/Scenes/OnlyMyGame_ResourceShowcase.unity`가 생성되고 열립니다.

- 지형 타일, 유닛, 건물, 장식, 자원, 깃발, 소품을 섹션별 그리드로 배치
- 각 항목에 필드명 라벨이 표시되어 어떤 프리셋이 어떤 모습인지 한눈에 확인
- 프리셋의 스케일/회전을 조정하고 싶다면 해당 프리셋을 직접 수정하면 게임에 반영됩니다

### 3. 게임 실행

`Assets/Scenes/OnlyMyGame.unity`를 열어 플레이하면 카탈로그에 등록된 KayKit 프리셋이
지형·유닛·건물·자원·장식에 자동 적용됩니다. 카탈로그에 없는 항목만 Primitive 폴백으로 표시됩니다.

## NAS API 수동 배포

Codex `Stop` 훅은 서버 테스트와 Release 빌드만 수행하며 외부 시스템을 변경하지 않습니다. 프로덕션 배포는 변경 범위를 검토하고 커밋한 뒤 `./scripts/deploy-nas-api.sh`를 명시적으로 실행합니다. 스크립트는 dirty source를 오류로 거부하고 다시 검증한 뒤, 잠금이 걸린 불변 릴리스 디렉터리에서 NAS 컨테이너를 빌드·시작합니다. 새 API가 상태·API 버전·규칙 호환성 검사 중 하나라도 통과하지 못하면 직전 정상 릴리스를 복구합니다.

한 번만 다음을 준비합니다.

1. NAS에서 SSH를 활성화하고, Docker 실행 권한과 `NAS_DEPLOY_PATH` 쓰기 권한이 있는 계정을 준비합니다.
2. Mac의 `~/.ssh/config`에 NAS 대상과 `StrictHostKeyChecking`용 호스트 키를 설정합니다. NAS SSH 포트는 `3442`를 사용합니다. SSH 키를 권장하며, 비밀번호 파일을 사용한다면 Mac에 `sshpass`가 설치돼 있어야 합니다.
3. `.codex/nas-deploy.env.example`을 `~/.config/onlymygame/nas-deploy.env`로 복사하고 실제 NAS checkout 경로를 입력합니다. `NAS_DEPLOY_PORT`의 기본값은 `3442`입니다.
4. NAS `.env`에 실제 reverse proxy source IP를 `ONLYMYGAME_TRUSTED_PROXIES`로 지정하고, 일일 전체 OpenAI 호출 상한을 `ONLYMYGAME_GLOBAL_DAILY_LIMIT`로 정합니다. API의 host port는 `127.0.0.1:8080`에만 바인딩되므로 외부 접속은 TLS reverse proxy를 통합니다.
5. 배포 전에 `git status`, `./scripts/verify-server.sh`, 배포 대상 커밋을 확인한 뒤 수동 스크립트를 실행합니다.

동일 소스를 다시 빌드해야 할 때만 `ONLYMYGAME_FORCE_DEPLOY=1 ./scripts/deploy-nas-api.sh`를 사용합니다. GitHub Pages 워크플로는 `Assets/Resources/OnlyMyGameConfig.json`의 HTTPS API에 접속해 `/health`의 API·규칙 호환성이 현재 소스와 일치하는지 확인한 뒤에만 Unity 빌드를 시작하므로, 서버와 클라이언트가 함께 바뀐 릴리스는 NAS API를 먼저 배포합니다.

NAS의 `.env`에 있는 `OPENAI_API_KEY`, `ONLYMYGAME_ALLOWED_ORIGIN`, `ONLYMYGAME_DAILY_SALT`, `ONLYMYGAME_TRUSTED_PROXIES`, `ONLYMYGAME_GLOBAL_DAILY_LIMIT`는 로컬·GitHub에 복사하지 않습니다.

`main`의 API 또는 공통 규칙 코드가 바뀌면 `server-container.yml`이 검증을 다시 수행한 뒤
`ghcr.io/shinepcs/onlymygame-api`에 `linux/amd64`·`linux/arm64` 이미지를 `latest`와 커밋 SHA 태그로 게시합니다.
이미지에는 SBOM과 GitHub 빌드 출처 증명이 연결됩니다. 이미지 게시가 NAS 운영 배포를 자동 실행하지는 않으며,
실제 NAS 전환은 위 수동 배포 절차와 호환성 상태 확인을 거쳐야 합니다.

PR 코드는 persistent self-hosted Unity runner에서 자동 실행하지 않습니다. 관리자가 현재 커밋에 `safe-to-run-unity` 라벨을 적용한 이벤트에서만 실행하며, PR에 새 커밋이 추가되면 라벨을 제거한 뒤 다시 적용해 재승인합니다.
