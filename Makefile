SHELL := /bin/sh

PROJECT_DIR := $(abspath $(CURDIR))
UNITY_ARTIFACT_DIR := $(PROJECT_DIR)/artifacts/unity
UNITY_RESULT_DIR := $(UNITY_ARTIFACT_DIR)/test-results
UNITY_LOG_DIR := $(UNITY_ARTIFACT_DIR)/logs
SERVER_PROJECT := $(PROJECT_DIR)/Server/OnlyMyGame.Api/OnlyMyGame.Api.csproj
SERVER_TEST_PROJECT := $(PROJECT_DIR)/Server/OnlyMyGame.Api.Tests/OnlyMyGame.Api.Tests.csproj

.PHONY: unity-check unity-guard unity-artifacts unity-test-edit unity-test-play unity-test unity-build unity-ci server-test server-build server-verify

unity-check:
	@command -v unity >/dev/null 2>&1 || { echo "Unity CLI가 PATH에 없습니다." >&2; exit 1; }
	@unity doctor
	@unity editors

unity-guard:
	@command -v unity >/dev/null 2>&1 || { echo "Unity CLI가 PATH에 없습니다." >&2; exit 1; }
	@if unity status --format tsv 2>/dev/null | awk -v p="$(PROJECT_DIR)" '$$2 == "ready" && $$3 == p { found=1 } END { exit !found }'; then \
		echo "Unity 에디터가 $(PROJECT_DIR)을 열고 있습니다. 배치 검증 전에 에디터를 닫으세요." >&2; \
		exit 1; \
	fi

unity-artifacts:
	@mkdir -p "$(UNITY_RESULT_DIR)" "$(UNITY_LOG_DIR)"

unity-test-edit: unity-guard unity-artifacts
	@unity test "$(PROJECT_DIR)" --mode EditMode --output "$(UNITY_RESULT_DIR)/editmode.xml" --timeout 900 >"$(UNITY_LOG_DIR)/editmode.log" 2>&1 || { status=$$?; tail -n 160 "$(UNITY_LOG_DIR)/editmode.log"; exit $$status; }
	@test -s "$(UNITY_RESULT_DIR)/editmode.xml" || { echo "EditMode 결과 XML이 생성되지 않았습니다." >&2; exit 1; }
	@grep -Eq '(testcasecount|total)="[1-9][0-9]*"' "$(UNITY_RESULT_DIR)/editmode.xml" || { echo "EditMode 테스트가 한 건도 실행되지 않았습니다." >&2; exit 1; }

unity-test-play: unity-guard unity-artifacts
	@unity test "$(PROJECT_DIR)" --mode PlayMode --output "$(UNITY_RESULT_DIR)/playmode.xml" --timeout 900 >"$(UNITY_LOG_DIR)/playmode.log" 2>&1 || { status=$$?; tail -n 160 "$(UNITY_LOG_DIR)/playmode.log"; exit $$status; }
	@test -s "$(UNITY_RESULT_DIR)/playmode.xml" || { echo "PlayMode 결과 XML이 생성되지 않았습니다." >&2; exit 1; }
	@grep -Eq '(testcasecount|total)="[1-9][0-9]*"' "$(UNITY_RESULT_DIR)/playmode.xml" || { echo "PlayMode 테스트가 한 건도 실행되지 않았습니다." >&2; exit 1; }

unity-test:
	@$(MAKE) unity-test-edit
	@$(MAKE) unity-test-play

unity-build: unity-guard unity-artifacts
	@unity build "$(PROJECT_DIR)" --target WebGL --execute-method BuildScript.BuildWebGL -o "$(PROJECT_DIR)/build/webgl" -l "$(UNITY_LOG_DIR)/build-webgl.log"
	@test -f "$(PROJECT_DIR)/build/webgl/index.html" || { echo "WebGL index.html이 생성되지 않았습니다." >&2; exit 1; }

unity-ci:
	@$(MAKE) unity-check
	@$(MAKE) unity-test
	@$(MAKE) unity-build

server-test:
	@DOTNET_ROLL_FORWARD="$${DOTNET_ROLL_FORWARD:-Major}" dotnet test "$(SERVER_TEST_PROJECT)" --configuration Release --nologo

server-build:
	@dotnet build "$(SERVER_PROJECT)" --configuration Release --nologo

server-verify:
	@"$(PROJECT_DIR)/scripts/verify-server.sh"
