using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using OnlyMyGame.Core;
using OnlyMyGame.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace OnlyMyGame.Tests
{
    public sealed class GameRuntimePlayModeTests
    {
        private const string SaveKey = "onlymygame.autosave.v1";
        private const string BackupKey = "onlymygame.autosave.v1.backup";
        private const string TempKey = "onlymygame.autosave.v1.pending";
        private const string MainSceneName = "OnlyMyGame";
        private readonly Dictionary<string, string> preservedPreferences = new Dictionary<string, string>();
        private Scene smokeScene;
        private Scene isolationScene;
        private Scene previousActiveScene;

        [Serializable]
        private sealed class TestSaveEnvelope
        {
            public int schemaVersion = 2;
            public string payload;
            public string checksum;
        }

        [SetUp]
        public void PreserveGameSaves()
        {
            preservedPreferences.Clear();
            foreach (var key in SaveKeys())
            {
                if (PlayerPrefs.HasKey(key)) preservedPreferences[key] = PlayerPrefs.GetString(key, "");
                PlayerPrefs.DeleteKey(key);
            }
            PlayerPrefs.Save();
        }

        [TearDown]
        public void RestoreGameSaves()
        {
            foreach (var key in SaveKeys())
            {
                if (preservedPreferences.TryGetValue(key, out var value)) PlayerPrefs.SetString(key, value);
                else PlayerPrefs.DeleteKey(key);
            }
            PlayerPrefs.Save();
        }

        [UnityTearDown]
        public IEnumerator UnloadSmokeScene()
        {
            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                SceneManager.SetActiveScene(previousActiveScene);

            if (smokeScene.IsValid() && smokeScene.isLoaded)
            {
                var unloadSmoke = SceneManager.UnloadSceneAsync(smokeScene);
                if (unloadSmoke != null) yield return unloadSmoke;
            }

            // GameController builds its first world in Awake. When a scene is loaded
            // additively those loose visuals belong to the then-active scene, so keep
            // that work inside a disposable scene as well.
            if (isolationScene.IsValid() && isolationScene.isLoaded)
            {
                var unloadIsolation = SceneManager.UnloadSceneAsync(isolationScene);
                if (unloadIsolation != null) yield return unloadIsolation;
            }

            smokeScene = default;
            isolationScene = default;
            previousActiveScene = default;
        }

        [UnityTest]
        [Timeout(15000)]
        public IEnumerator MainSceneBootsHudWorldAndClickableCommandWithoutNetwork()
        {
            previousActiveScene = SceneManager.GetActiveScene();
            isolationScene = SceneManager.CreateScene("OnlyMyGame.PlayMode.SmokeIsolation");
            Assert.IsTrue(SceneManager.SetActiveScene(isolationScene), "additive 로드 전용 격리 씬을 활성화하지 못했습니다.");
            GameController controller = null;
            FieldInfo apiBaseField = null;
            void CaptureLoadedScene(Scene scene, LoadSceneMode mode)
            {
                if (!string.Equals(scene.name, MainSceneName, StringComparison.Ordinal)) return;
                smokeScene = scene;
                controller = FindInScene<GameController>(scene);
                if (controller == null) return;

                // SceneManager.sceneLoaded runs before Start. Clear only the private API
                // endpoint here so the real Start/HUD path executes without opening a
                // production connection or spending an AI request during tests.
                apiBaseField = typeof(GameController).GetField("apiBase", BindingFlags.Instance | BindingFlags.NonPublic);
                apiBaseField?.SetValue(controller, string.Empty);
            }

            SceneManager.sceneLoaded += CaptureLoadedScene;
            try
            {
                SceneManager.LoadScene(MainSceneName, LoadSceneMode.Additive);
                var loadDeadline = Time.realtimeSinceStartup + 5f;
                while (controller == null && Time.realtimeSinceStartup < loadDeadline) yield return null;
            }
            finally
            {
                SceneManager.sceneLoaded -= CaptureLoadedScene;
            }

            Assert.IsTrue(smokeScene.IsValid() && smokeScene.isLoaded, "메인 씬이 additive PlayMode 스모크로 로드되어야 합니다.");
            Assert.IsNotNull(controller, "메인 씬에 GameController가 있어야 합니다.");
            Assert.IsNotNull(apiBaseField, "PlayMode 네트워크 격리를 위한 API 주소 필드를 찾지 못했습니다.");
            Assert.IsTrue(SceneManager.SetActiveScene(smokeScene), "스모크 씬을 활성 씬으로 전환하지 못했습니다.");

            // Start, one Update, and the first UI layout pass must all run.
            yield return null;
            yield return null;

            Assert.IsNotNull(controller);
            Assert.IsTrue(controller.isActiveAndEnabled);
            Assert.IsNotNull(controller.Game);
            Assert.AreEqual(217, controller.Game.map.Count);
            Assert.IsTrue(controller.Game.planningPrepared, "첫 계획 턴이 실제 씬 Start 이후 준비되어야 합니다.");

            var camera = FindInScene<Camera>(smokeScene);
            var canvas = FindInScene<Canvas>(smokeScene);
            var hud = FindInScene<CommercialGameHud>(smokeScene);
            Assert.IsNotNull(camera, "메인 씬 카메라가 생성되어야 합니다.");
            Assert.IsTrue(camera.isActiveAndEnabled);
            Assert.IsNotNull(canvas, "메인 씬 Canvas가 생성되어야 합니다.");
            Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode);
            Assert.IsNotNull(hud, "CommercialGameHud가 실제 Canvas에 초기화되어야 합니다.");
            Assert.IsNotNull(
                hud.GetComponentsInChildren<Button>(true).SingleOrDefault(button => button.name == "Action_Capture"),
                "PRD의 점령 명령이 상용 HUD 행동 그리드에 노출되어야 합니다.");

            var newRun = hud.GetComponentsInChildren<Button>(true).SingleOrDefault(button => button.name == "NewRun");
            Assert.IsNotNull(newRun, "메인 메뉴에 새 원정 버튼이 생성되어야 합니다.");
            Assert.IsTrue(newRun.gameObject.activeInHierarchy && newRun.interactable, "저장 슬롯이 비어 있으면 새 원정 버튼을 클릭할 수 있어야 합니다.");
            newRun.onClick.Invoke();
            yield return null;

            Assert.IsTrue(controller.HasSavedRun, "새 원정 공개 흐름은 자동 저장 슬롯을 만들어야 합니다.");
            Assert.AreEqual(217, SceneObjectsNamed(smokeScene, "Hex_").Count, "실제 씬에 217개 핵사곤 시각 오브젝트가 있어야 합니다.");
            Assert.Greater(SceneObjectsNamed(smokeScene, "Unit_").Count, 0, "유닛 시각 오브젝트가 생성되어야 합니다.");
            Assert.Greater(SceneObjectsNamed(smokeScene, "Building_").Count, 0, "거점 시각 오브젝트가 생성되어야 합니다.");
            var luckWorld = SceneObjectsNamed(smokeScene, "LuckFeedback").SingleOrDefault();
            Assert.IsNotNull(luckWorld, "행운은 조명 수치뿐 아니라 월드 표지로 표현되어야 합니다.");
            var luckFeedback = luckWorld.GetComponent<LuckWorldFeedback>();
            Assert.IsNotNull(luckFeedback, "행운 표지는 턴 변화 연출 컴포넌트를 가져야 합니다.");
            Assert.AreEqual(controller.Game.luck, luckFeedback.CurrentLuck);
            var luckBadge = hud.GetComponentsInChildren<TextMeshProUGUI>(true).SingleOrDefault(text => text.name == "LuckBadge");
            Assert.IsNotNull(luckBadge, "상단 HUD에 전용 행운 배지가 있어야 합니다.");
            StringAssert.Contains(controller.Game.luck.ToString(), luckBadge.text);

            var player = controller.Game.entities.First(unit => unit.factionId == 1 && unit.alive);
            controller.SelectUnit(player.id);
            yield return null;
            var move = hud.GetComponentsInChildren<Button>(true).SingleOrDefault(button => button.name == "Action_Move");
            Assert.IsNotNull(move, "이동 명령 버튼이 HUD에 생성되어야 합니다.");
            Assert.IsTrue(move.gameObject.activeInHierarchy && move.interactable, "아군 선택 후 이동 명령을 클릭할 수 있어야 합니다.");
            move.onClick.Invoke();
            yield return null;

            Assert.IsTrue(controller.IsTargeting, "이동 버튼 클릭은 실제 대상 지정 상태로 진입해야 합니다.");
            Assert.Greater(SceneObjectsNamed(smokeScene, "Target_").Count, 0, "유효 이동 타일에 월드 하이라이트가 생성되어야 합니다.");

            controller.CancelTargeting();
            yield return null;
            var build = hud.GetComponentsInChildren<Button>(true).SingleOrDefault(button => button.name == "Action_Build");
            Assert.IsNotNull(build, "건설 명령 버튼이 HUD에 생성되어야 합니다.");
            Assert.IsTrue(build.interactable, "빈 타일의 아군은 건물 선택 창을 열 수 있어야 합니다.");
            build.onClick.Invoke();
            yield return null;

            var buildPicker = hud.GetComponentsInChildren<Transform>(true).SingleOrDefault(candidate => candidate.name == "BuildPicker");
            Assert.IsNotNull(buildPicker, "건설 명령은 전용 건물 선택 창을 만들어야 합니다.");
            Assert.IsTrue(buildPicker.gameObject.activeInHierarchy);
            var buildTypes = hud.GetComponentsInChildren<Button>(true)
                .Where(button => button.name.StartsWith("BuildType_", StringComparison.Ordinal))
                .ToList();
            Assert.AreEqual(6, buildTypes.Count, "PRD 건물 카탈로그 여섯 종류가 모두 선택 창에 노출되어야 합니다.");
            var headquarters = buildTypes.Single(button => button.name == "BuildType_Headquarters");
            var workshop = buildTypes.Single(button => button.name == "BuildType_Workshop");
            var barracks = buildTypes.Single(button => button.name == "BuildType_Barracks");
            Assert.IsFalse(headquarters.interactable, "살아있는 본부가 이미 있으면 두 번째 본부를 예약할 수 없어야 합니다.");
            Assert.IsTrue(workshop.interactable, "초기 목재 8·철 2로 작업장을 예약할 수 있어야 합니다.");
            Assert.IsFalse(barracks.interactable, "초기 철 2로 철 3이 필요한 병영을 예약할 수 없어야 합니다.");
            var workshopLabel = workshop.GetComponentInChildren<TextMeshProUGUI>(true);
            Assert.IsNotNull(workshopLabel, "작업장 버튼은 TMP 라벨을 가져야 합니다.");
            StringAssert.Contains("목재 5", workshopLabel.text);
            StringAssert.Contains("철 2", workshopLabel.text);

            var cancelBuild = hud.GetComponentsInChildren<Button>(true).SingleOrDefault(button => button.name == "BuildPickerCancel");
            Assert.IsNotNull(cancelBuild);
            cancelBuild.onClick.Invoke();
            Assert.IsFalse(buildPicker.gameObject.activeSelf, "건물 선택 취소는 선택 창만 닫아야 합니다.");
            Assert.AreEqual(0, controller.Commands.Count, "취소한 건물 선택은 명령이나 자원을 예약하면 안 됩니다.");

            build.onClick.Invoke();
            workshop.onClick.Invoke();
            yield return null;
            Assert.IsFalse(buildPicker.gameObject.activeSelf);
            Assert.AreEqual(1, controller.Commands.Count);
            Assert.AreEqual(CommandType.Build, controller.Commands[0].type);
            Assert.AreEqual(BuildingType.Workshop, controller.Commands[0].buildingType, "선택한 작업장 유형이 계획 명령에 명시적으로 보존되어야 합니다.");
        }

        [Test]
        public void GeneratedWorldHasPlayableFactionsAndVisibility()
        {
            var world = WorldGenerator.Create(20260803);
            Assert.AreEqual(217, world.map.Count);
            Assert.AreEqual(3, world.factions.Count);
            Assert.IsTrue(world.map.Exists(t => t.visible));
            Assert.IsTrue(GameRules.HeadquartersAlive(world));
        }

        [Test]
        public void GeneratedWorldStartsPlayerBesideRatherThanInsideHeadquarters()
        {
            var world = WorldGenerator.Create(20260804);
            var player = world.entities.Find(unit => unit.factionId == 1 && unit.alive);
            var headquarters = world.buildings.Find(building => building.factionId == 1 && building.type == BuildingType.Headquarters && building.hp > 0);

            Assert.IsNotNull(player);
            Assert.IsNotNull(headquarters);
            Assert.AreNotEqual(headquarters.position, player.position, "시작 유닛이 본부와 겹치면 선택과 가시성이 깨집니다.");
            Assert.AreEqual(1, player.position.Distance(headquarters.position), "시작 유닛은 본부와 인접한 타일에서 시작해야 합니다.");
        }

        [Test]
        public void TargetVisibilityRespectsFogOfWar()
        {
            var world = WorldGenerator.Create(20260805);
            var hidden = world.entities.Find(unit => unit.factionId != 1 && unit.alive).position;
            var tile = world.map.Find(candidate => candidate.position.Equals(hidden));
            var method = typeof(GameController).GetMethod("IsVisibleTile", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "대상 시야 검사 진입점을 찾지 못했습니다.");

            tile.visible = false;
            Assert.IsFalse((bool)method.Invoke(null, new object[] { world, hidden }), "안개 밖 대상 좌표를 명령 UI가 노출하면 안 됩니다.");
            tile.visible = true;
            Assert.IsTrue((bool)method.Invoke(null, new object[] { world, hidden }), "현재 보이는 대상 좌표는 명령 UI에서 선택할 수 있어야 합니다.");
        }

        [Test]
        public void BeginPlanningRestoresSpAndAppliesBuildingProductionOnce()
        {
            var world = WorldGenerator.Create(7);
            var player = world.factions.Find(f => f.id == 1);
            world.turn++;
            world.phase = RunPhase.Planning;
            world.planningPrepared = false;
            player.sp = 0;
            world.buildings.Add(new BuildingState { id = 99, factionId = 1, type = BuildingType.Market });
            var before = player.resources.coin;
            TurnResolver.BeginPlanning(world, world.journal);
            Assert.AreEqual(player.maxSp, player.sp);
            Assert.AreEqual(before + 1, player.resources.coin);

            TurnResolver.BeginPlanning(world, world.journal);
            Assert.AreEqual(before + 1, player.resources.coin, "같은 턴에 계획 준비를 다시 호출해도 생산이 누적되면 안 됩니다.");
        }

        [Test]
        public void InterruptedPendingSaveIsPromotedAndPrimaryBecomesBackup()
        {
            var primary = WorldGenerator.Create(101);
            primary.runId = "older-primary";
            var pending = WorldGenerator.Create(202);
            pending.runId = "newer-pending";
            var primaryRaw = Envelope(primary);
            PlayerPrefs.SetString(SaveKey, primaryRaw);
            PlayerPrefs.SetString(TempKey, Envelope(pending));
            PlayerPrefs.Save();

            var recovered = RecoverBestSave();

            Assert.AreEqual("newer-pending", recovered.runId);
            Assert.IsFalse(PlayerPrefs.HasKey(TempKey), "복구된 임시 세대는 승격 후 제거되어야 합니다.");
            Assert.AreEqual(primaryRaw, PlayerPrefs.GetString(BackupKey), "기존 주 세대는 백업으로 보존되어야 합니다.");
            Assert.AreEqual("newer-pending", RecoverBestSave().runId, "승격된 주 세대를 다음 시작에서도 읽어야 합니다.");
        }

        [Test]
        public void CorruptPendingAndPrimaryFallBackToBackupAndSelfHeal()
        {
            var backup = WorldGenerator.Create(303);
            backup.runId = "valid-backup";
            var backupRaw = Envelope(backup);
            PlayerPrefs.SetString(TempKey, "{broken-pending");
            PlayerPrefs.SetString(SaveKey, "{broken-primary");
            PlayerPrefs.SetString(BackupKey, backupRaw);
            PlayerPrefs.Save();

            var recovered = RecoverBestSave();

            Assert.AreEqual("valid-backup", recovered.runId);
            Assert.IsFalse(PlayerPrefs.HasKey(TempKey), "손상된 임시 세대는 반복 복구를 막기 위해 제거해야 합니다.");
            Assert.AreEqual(backupRaw, PlayerPrefs.GetString(SaveKey), "유효한 백업으로 손상된 주 세대를 자가 복구해야 합니다.");
        }

        [Test]
        public void SemanticallyInvalidPrimaryFallsBackToBackupAndSelfHeals()
        {
            var invalidPrimary = WorldGenerator.Create(304);
            invalidPrimary.runId = "checksum-valid-but-invalid";
            invalidPrimary.entities[1].id = invalidPrimary.entities[0].id;
            var backup = WorldGenerator.Create(305);
            backup.runId = "semantic-backup";
            var backupRaw = Envelope(backup);
            PlayerPrefs.SetString(SaveKey, Envelope(invalidPrimary));
            PlayerPrefs.SetString(BackupKey, backupRaw);
            PlayerPrefs.Save();

            var recovered = RecoverBestSave();

            Assert.AreEqual("semantic-backup", recovered.runId, "체크섬이 맞아도 중복 엔티티 ID가 있는 저장은 실행하면 안 됩니다.");
            Assert.AreEqual(backupRaw, PlayerPrefs.GetString(SaveKey), "심층 검증을 통과한 백업으로 주 세대를 자가 복구해야 합니다.");
        }

        [Test]
        public void LegacySchemaOneSaveNormalizesMissingCollectionsAndInterruptedResolution()
        {
            var legacy = WorldGenerator.Create(404);
            legacy.runId = "legacy-schema-one";
            legacy.turn = 7;
            legacy.phase = RunPhase.Resolving;
            legacy.planningPrepared = true;
            legacy.dynamicActions = null;
            legacy.ruleState = null;
            legacy.journal = null;
            PlayerPrefs.SetString(SaveKey, Envelope(legacy, 1));
            PlayerPrefs.Save();

            var recovered = RecoverBestSave();

            Assert.AreEqual(8, recovered.turn, "해결 도중 저장된 이전 세대는 이미 해결된 턴 다음 규칙 요청으로 복구해야 합니다.");
            Assert.AreEqual(RunPhase.AwaitingRules, recovered.phase);
            Assert.IsFalse(recovered.planningPrepared);
            Assert.IsNotNull(recovered.dynamicActions);
            Assert.IsNotNull(recovered.ruleState);
            Assert.IsNotNull(recovered.journal);
        }

        private static GameSnapshotV1 RecoverBestSave()
        {
            var method = typeof(GameController).GetMethod("RecoverBestSave", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "저장 복구 진입점을 찾지 못했습니다.");
            return (GameSnapshotV1)method.Invoke(null, null);
        }

        private static T FindInScene<T>(Scene scene) where T : Component
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            return scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<T>(true))
                .FirstOrDefault();
        }

        private static List<GameObject> SceneObjectsNamed(Scene scene, string prefix)
        {
            return Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(candidate => candidate.scene.IsValid() && candidate.scene.handle == scene.handle &&
                                    candidate.name.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
        }

        private static string Envelope(GameSnapshotV1 snapshot, int schemaVersion = 2)
        {
            var payload = JsonConvert.SerializeObject(snapshot);
            return JsonConvert.SerializeObject(new TestSaveEnvelope { schemaVersion = schemaVersion, payload = payload, checksum = Hash(payload) });
        }

        private static string Hash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var character in value)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return hash.ToString("X8");
            }
        }

        private static IEnumerable<string> SaveKeys()
        {
            yield return SaveKey;
            yield return BackupKey;
            yield return TempKey;
        }
    }
}
