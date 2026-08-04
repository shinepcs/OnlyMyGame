using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using OnlyMyGame.Core;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace OnlyMyGame.Runtime
{
    public static class WorldGenerator
    {
        public static GameSnapshotV1 Create(int seed)
        {
            var random = new DeterministicRandom(seed);
            var game = new GameSnapshotV1 { runId = Guid.NewGuid().ToString("N"), seed = seed, turn = 1, luck = random.Next(1, 101) };
            game.factions.Add(new FactionState { id = 1, name = "내 원정대", kind = FactionKind.Player });
            game.factions.Add(new FactionState { id = 2, name = "덜컹 스켈레톤", kind = FactionKind.Skeleton, relationToPlayer = -60 });
            game.factions.Add(new FactionState { id = 3, name = "느긋한 장터단", kind = FactionKind.Neutral, relationToPlayer = 10 });
            for (var q = -8; q <= 8; q++) for (var r = Math.Max(-8, -q - 8); r <= Math.Min(8, -q + 8); r++)
            {
                var p = new HexCoord(q, r); var roll = random.Percent();
                game.map.Add(new TileState { position = p, terrain = roll < 16 ? "강" : roll < 42 ? "숲" : roll < 62 ? "언덕" : "초원", resource = roll < 42 ? ResourceType.Wood : roll < 62 ? ResourceType.Stone : roll < 82 ? ResourceType.Food : ResourceType.Iron, amount = 2 + random.Next(0, 5), owner = 0 });
            }
            // Keep the starting explorer beside the headquarters so both remain readable and clickable.
            var start = game.map.First(t => t.position.Equals(new HexCoord(0, 0)));
            start.terrain = "초원"; start.resource = ResourceType.Food; start.amount = 4; start.owner = 1;
            var camp = game.map.First(t => t.position.Equals(new HexCoord(1, 0)));
            camp.terrain = "초원"; camp.resource = ResourceType.Wood; camp.amount = 4; camp.owner = 1;
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(1, 0), tags = new List<string> { "탐험가" } });
            game.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(5, -2), tags = new List<string> { "스켈레톤" } });
            game.entities.Add(new UnitState { id = 3, factionId = 3, position = new HexCoord(-4, 3), tags = new List<string> { "상인" } });
            game.entities.Add(new UnitState { id = 4, factionId = 2, position = new HexCoord(4, -1), tags = new List<string> { "스켈레톤" } });
            game.entities.Add(new UnitState { id = 5, factionId = 3, position = new HexCoord(-3, 4), tags = new List<string> { "상인" } });
            game.buildings.Add(new BuildingState { id = 1, factionId = 1, position = new HexCoord(0, 0), type = BuildingType.Headquarters });
            game.buildings.Add(new BuildingState { id = 2, factionId = 2, position = new HexCoord(5, -2), type = BuildingType.Headquarters });
            game.activeRules.Add(new RuleNodeV1 { id = "welcome", name = "개척의 첫걸음", description = "매 턴 시작에 식량 1을 얻습니다.", trigger = OnlyMyGame.Core.EventType.TurnStart, durationTurns = 30, appliedTurn = 1, effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } }, worldCue = "깃발" });
            TurnResolver.BeginPlanning(game, game.journal);
            Reveal(game); return game;
        }
        public static void Reveal(GameSnapshotV1 game)
        {
            var scouts = game.entities.Where(x => x.factionId == 1 && x.alive).Select(x => x.position).ToList();
            scouts.AddRange(game.buildings.Where(b => b.factionId == 1 && b.type == BuildingType.Watchtower && b.hp > 0).Select(b => b.position));
            if (scouts.Count == 0) return;
            var range = GameRules.VisibilityRange(game, 1);
            foreach (var tile in game.map) { tile.visible = scouts.Any(position => tile.position.Distance(position) <= range); tile.explored |= tile.visible; }
        }
    }

    public sealed class GameController : MonoBehaviour
    {
        private static readonly int BaseColorProperty = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorProperty = Shader.PropertyToID("_Color");
        private static MaterialPropertyBlock tintProperties;
        private static Material runtimeLitMaterial;
        private static Material runtimeUnlitMaterial;
        private GameSnapshotV1 game;
        private readonly List<string> ledger = new List<string>();
        private readonly List<PlannedCommand> commands = new List<PlannedCommand>();
        private Dictionary<HexCoord, GameObject> tileVisuals = new Dictionary<HexCoord, GameObject>();
        private Dictionary<int, GameObject> unitVisuals = new Dictionary<int, GameObject>();
        private Dictionary<int, GameObject> buildingVisuals = new Dictionary<int, GameObject>();
        private Dictionary<int, TweenMover> unitMovers = new Dictionary<int, TweenMover>();
        private bool waitingForRules;
        private bool blockedOnRules;
        private string blockReason = "";
        private string apiBase;
        private CommercialGameHud commercialHud;
        private QuarterViewCameraController cameraController;
        private GameFeedback feedback;
        private readonly List<GameObject> targetHighlights = new List<GameObject>();
        private readonly List<GameObject> ruleCueVisuals = new List<GameObject>();
        private GameObject luckFeedbackVisual;
        private LuckWorldFeedback luckFeedback;
        private CommandType? targetingCommand;
        private DynamicActionV1 targetingDynamicAction;
        private int targetingDynamicActorId = -1;
        private string targetingPrompt = "";
        private string serviceStatus = "AI 연결 확인 중";
        private bool serviceOnline;
        private bool serviceChecking = true;
        private bool compatibilityChecked;
        private bool serviceCompatible;
        private string expectedApiVersion = "v1";
        private string expectedCompatibilityVersion = "rules-v4-targeting-2026-08";
        private string sessionToken = "";
        private float sessionValidUntilRealtime;
        private bool sessionReady;
        private string sessionFailure = "";
        private float retryRulesAvailableAtRealtime;

        [Header("프리젠테이션 카탈로그 (에디터에서 교체)")]
        [SerializeField] private GamePresentationCatalog presentation;

        // 선택 & 버블 UI
        private int selectedUnitId = -1;
        private int selectedBuildingId = -1;
        [Header("씬 배치 UI (에디터에서 교체)")]
        [SerializeField] private CommandBubble commandBubble;
        [SerializeField] private GameObject selectionRing;

        // HUD
        [SerializeField] private Canvas hudCanvas;
        [SerializeField] private Text hudResources;
        [SerializeField] private Text hudGoal;
        [SerializeField] private Text hudLog;
        [SerializeField] private Button endTurnButton;
        [SerializeField] private Text endTurnButtonText;
        [SerializeField] private GameObject blockPanel;
        [SerializeField] private Text blockText;

        // 카메라 & 조명
        [SerializeField] private Camera mainCamera;
        [SerializeField] private Light worldSun;

        [Serializable] private sealed class SaveEnvelope { public int schemaVersion = 2; public string payload; public string checksum; }
        [Serializable] private sealed class ClientConfig
        {
            public string apiBaseUrl;
            public string schemaVersion;
            public string compatibilityVersion;
        }
        [Serializable] private sealed class SessionResponse { public string token; public int expiresInSeconds; }
        [Serializable] private sealed class ServiceErrorResponse { public string error; public int retryAfterSeconds; }
        [Serializable] private sealed class HealthResponse
        {
            public string status;
            public string apiVersion;
            public string compatibilityVersion;
        }
        private const string SaveKey = "onlymygame.autosave.v1";
        private const string BackupKey = "onlymygame.autosave.v1.backup";
        private const string TempKey = "onlymygame.autosave.v1.pending";
        private const string PreviousRunKey = "onlymygame.previous-run.v1";
        private const int CommercialDynamicActionLimit = RuleLimits.MaxDynamicActionsPerRuleSet;
        private static readonly BuildingType[] PlayerBuildTypes =
        {
            BuildingType.Headquarters, BuildingType.Warehouse, BuildingType.Workshop,
            BuildingType.Watchtower, BuildingType.Market, BuildingType.Barracks
        };

        private void Awake()
        {
            var config = Resources.Load<TextAsset>("OnlyMyGameConfig");
            var configuredApiBase = "";
            if (config != null)
            {
                try
                {
                    var parsed = JsonUtility.FromJson<ClientConfig>(config.text);
                    configuredApiBase = parsed?.apiBaseUrl ?? "";
                    if (!string.IsNullOrWhiteSpace(parsed?.schemaVersion)) expectedApiVersion = parsed.schemaVersion;
                    if (!string.IsNullOrWhiteSpace(parsed?.compatibilityVersion)) expectedCompatibilityVersion = parsed.compatibilityVersion;
                }
                catch (Exception)
                {
                    configuredApiBase = "";
                }
            }
            apiBase = IsUsableApiBase(configuredApiBase) ? configuredApiBase : Environment.GetEnvironmentVariable("ONLYMYGAME_API_URL") ?? "";
            // presentation은 씬의 GameController 인스펙터에서 직접 연결한다. (Resources.Load 불필요)
            LoadOrNew();
        }

        private void Start()
        {
            if (mainCamera == null) mainCamera = Camera.main;
            if (mainCamera != null)
            {
                if (FindObjectsByType<AudioListener>().Length == 0) mainCamera.gameObject.AddComponent<AudioListener>();
                cameraController = mainCamera.GetComponent<QuarterViewCameraController>() ?? mainCamera.gameObject.AddComponent<QuarterViewCameraController>();
                cameraController.Configure(6.6f);
            }
            feedback = GetComponent<GameFeedback>() ?? gameObject.AddComponent<GameFeedback>();
            feedback.Initialize(mainCamera);
            EnsureHudCanvas();
            commercialHud = hudCanvas.GetComponent<CommercialGameHud>() ?? hudCanvas.gameObject.AddComponent<CommercialGameHud>();
            commercialHud.Initialize(this);
            commercialHud.SetDynamicActionHandler(RunDynamicFromHud);
            commercialHud.ShowMainMenu(HasSavedRun);
            FocusPlayer(true);
            StartCoroutine(CheckServiceHealth());
            RefreshHud();
        }

        private void LoadOrNew()
        {
            var saved = RecoverBestSave();
            game = saved ?? WorldGenerator.Create(Environment.TickCount);
            ledger.Clear();
            if (game.journal != null) ledger.AddRange(game.journal);
            if (game.outcome != RunOutcome.Ongoing) game.phase = RunPhase.Terminal;
            else if (game.phase == RunPhase.Planning && !game.planningPrepared) TurnResolver.BeginPlanning(game, ledger);
            else if (game.phase == RunPhase.AwaitingRules)
            {
                blockedOnRules = true;
                waitingForRules = true;
                blockReason = "저장된 원정이 다음 세계 규칙을 기다리고 있습니다.";
            }
            TrimDynamicActions();
            BuildWorld();
            ledger.Add("턴 " + game.turn + " — 세계가 열렸습니다.");
        }

        private void EnsureHudCanvas()
        {
            if (hudCanvas == null)
            {
                var canvasObject = new GameObject("GameHud", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                hudCanvas = canvasObject.GetComponent<Canvas>();
            }
            hudCanvas.transform.localScale = Vector3.one;
            hudCanvas.gameObject.SetActive(true);
        }

        // ==================== 월드 생성 ====================

        private void BuildWorld()
        {
            foreach (var item in tileVisuals.Values) Destroy(item);
            foreach (var item in unitVisuals.Values) Destroy(item);
            foreach (var item in buildingVisuals.Values) Destroy(item);
            if (luckFeedbackVisual != null) Destroy(luckFeedbackVisual);
            luckFeedbackVisual = null;
            luckFeedback = null;
            tileVisuals.Clear();
            unitVisuals.Clear();
            buildingVisuals.Clear();
            unitMovers.Clear();

            foreach (var tile in game.map)
            {
                var prefab = SelectTilePrefab(tile.terrain);
                var go = Spawn(prefab, PrimitiveType.Cylinder);
                go.name = "Hex_" + tile.position;
                go.transform.position = HexToWorld(tile.position);
                go.transform.localScale = prefab == null ? new Vector3(.92f, .08f, .92f) : Vector3.one * .82f;
                if (prefab == null) Tint(go, TileColor(tile.terrain));
                tileVisuals[tile.position] = go;
                EnsureHitCollider(go, new Vector3(0, 0.12f, 0), new Vector3(1.7f, 0.3f, 1.5f));
                var tileClick = go.AddComponent<TileClickHandler>();
                tileClick.Init(this, tile.position);

                // 지형 장식 (숲 → 나무, 언덕 → 바위, 초원 → 풀)
                SpawnTerrainDecoration(tile);

                // 자원 표시 (타일에 자원이 있으면 해당 자원 프리팹 배치)
                SpawnResourceVisual(tile);
            }

            EnsureCameraAndLight();

            foreach (var building in game.buildings) SpawnBuildingVisual(building);
            foreach (var unit in game.entities.Where(x => x.alive)) SpawnUnitVisual(unit);
            CreateLuckFeedback();

            RenderVisibility();
        }

        private void CreateLuckFeedback()
        {
            luckFeedbackVisual = new GameObject("LuckFeedback");
            luckFeedbackVisual.tag = "RuleCue";

            var markerPrefab = presentation?.flagPlayer ?? presentation?.propTarget;
            var marker = Spawn(markerPrefab, PrimitiveType.Cylinder);
            marker.name = "LuckSign";
            marker.transform.SetParent(luckFeedbackVisual.transform, false);
            marker.transform.localPosition = Vector3.zero;
            marker.transform.localScale = markerPrefab == null ? new Vector3(.24f, .3f, .24f) : marker.transform.localScale * .38f;
            foreach (var collider in marker.GetComponentsInChildren<Collider>(true)) collider.enabled = false;

            var label = new GameObject("LuckLabel");
            label.transform.SetParent(luckFeedbackVisual.transform, false);
            label.transform.localPosition = Vector3.up * 1.08f;
            var text = label.AddComponent<TextMesh>();
            text.characterSize = .14f;
            text.fontSize = 46;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontStyle = FontStyle.Bold;
            var font = Resources.Load<Font>("Fonts/NanumGothic-Regular") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null) text.font = font;
            var mainCamera = Camera.main;
            if (mainCamera != null) label.transform.rotation = mainCamera.transform.rotation;

            luckFeedback = luckFeedbackVisual.AddComponent<LuckWorldFeedback>();
            luckFeedback.Configure(marker.transform, label.transform, text, mainCamera != null ? mainCamera.transform : null);
            UpdateLuckFeedback(false);
        }

        private void UpdateLuckFeedback(bool celebrate)
        {
            if (luckFeedbackVisual == null || luckFeedback == null || game == null) return;
            var headquarters = game.buildings.FirstOrDefault(building => building != null && building.factionId == 1 && building.type == BuildingType.Headquarters && building.hp > 0);
            var playerUnit = game.entities.FirstOrDefault(unit => unit != null && unit.factionId == 1 && unit.alive);
            var anchor = headquarters != null ? headquarters.position : playerUnit != null ? playerUnit.position : new HexCoord(0, 0);
            luckFeedbackVisual.transform.position = HexToWorld(anchor) + Vector3.up * .1f;

            var anchorTile = game.map.FirstOrDefault(tile => tile != null && tile.position.Equals(anchor));
            luckFeedbackVisual.SetActive(anchorTile != null && anchorTile.visible && (headquarters != null || playerUnit != null));
            luckFeedback.BindCharacter(playerUnit != null && unitVisuals.TryGetValue(playerUnit.id, out var character) ? character.transform : null);
            luckFeedback.SetLuck(game.luck, celebrate);
        }

        private UnityEngine.Object SelectTilePrefab(string terrain)
        {
            if (terrain == "강") return presentation?.waterTile;
            if (terrain == "숲") return presentation?.forestTile;
            if (terrain == "언덕") return presentation?.hillTile;
            return presentation?.grassTile;
        }

        private static Color TileColor(string terrain)
        {
            if (terrain == "강") return new Color(.35f, .65f, 1f);
            if (terrain == "숲") return new Color(.38f, .72f, .36f);
            if (terrain == "언덕") return new Color(.76f, .58f, .32f);
            return new Color(.64f, .86f, .45f);
        }

        private void SpawnTerrainDecoration(TileState tile)
        {
            if (tile.terrain == "숲")
            {
                var tree = Spawn(presentation?.treeDecoration, PrimitiveType.Cube);
                tree.name = "Tree_" + tile.position;
                tree.transform.position = HexToWorld(tile.position) + new Vector3(0.25f, 0f, 0.2f);
                tree.transform.localScale = Vector3.one * 0.5f;
                tree.transform.SetParent(tileVisuals[tile.position].transform, true);
            }
            else if (tile.terrain == "언덕")
            {
                var rock = Spawn(presentation?.rockDecoration, PrimitiveType.Cube);
                rock.name = "Rock_" + tile.position;
                rock.transform.position = HexToWorld(tile.position) + new Vector3(-0.2f, 0f, 0.15f);
                rock.transform.localScale = Vector3.one * 0.4f;
                rock.transform.SetParent(tileVisuals[tile.position].transform, true);
            }
        }

        /// <summary>
        /// 타일에 자원이 있으면 해당 자원 프리셋을 배치한다.
        /// </summary>
        private void SpawnResourceVisual(TileState tile)
        {
            if (tile.resource == ResourceType.None || tile.amount <= 0) return;
            var prefab = SelectResourcePrefab(tile.resource);
            if (prefab == null) return;
            var resource = Spawn(prefab, PrimitiveType.Cube);
            resource.name = "Resource_" + tile.position;
            resource.transform.position = HexToWorld(tile.position) + new Vector3(-0.25f, 0f, -0.2f);
            resource.transform.localScale = Vector3.one * 0.35f;
            resource.transform.SetParent(tileVisuals[tile.position].transform, true);
        }

        private UnityEngine.Object SelectResourcePrefab(ResourceType type)
        {
            if (type == ResourceType.Wood) return presentation?.resourceWood;
            if (type == ResourceType.Stone) return presentation?.resourceStone;
            if (type == ResourceType.Iron) return presentation?.resourceIron;
            if (type == ResourceType.Food) return presentation?.resourceFood;
            return null;
        }

        private void SpawnUnitVisual(UnitState unit)
        {
            UnityEngine.Object prefab;
            if (unit.factionId == 1) prefab = unit.tags.Contains("고용병") ? presentation?.rangerUnit : presentation?.playerUnit;
            else if (unit.factionId == 2) prefab = unit.id % 3 == 0 ? presentation?.skeletonMageUnit : unit.id % 2 == 0 ? presentation?.skeletonRogueUnit : presentation?.skeletonUnit;
            else prefab = unit.id % 2 == 0 ? presentation?.rogueUnit : presentation?.neutralUnit;
            var marker = Spawn(prefab, PrimitiveType.Capsule);
            marker.name = "Unit_" + unit.id;
            marker.transform.position = HexToWorld(unit.position) + Vector3.up * .12f;
            // KayKit 캐릭터 프리셋은 이미 0.5 스케일로 저장되어 있다. 루트 배율을
            // 덮어쓰지 않고 살짝만 보정해야 인접 건물과 겹치지 않는다.
            marker.transform.localScale = prefab == null ? Vector3.one * .42f : marker.transform.localScale * .9f;
            if (prefab == null) Tint(marker, unit.factionId == 1 ? new Color(.4f, .9f, 1f) : unit.factionId == 2 ? new Color(1f, .32f, .32f) : new Color(1f, .82f, .3f));
            unitVisuals[unit.id] = marker;
            var mover = marker.AddComponent<TweenMover>();
            unitMovers[unit.id] = mover;
            EnsureHitCollider(marker, new Vector3(0, 0.85f, 0), new Vector3(1.1f, 1.8f, 1.1f));

            var baseRing = CreateDisc("FactionRing_" + unit.id, marker.transform.position + Vector3.up * 0.035f, 0.48f,
                unit.factionId == 1 ? new Color(0.16f, 0.75f, 1f, 0.9f) : unit.factionId == 2 ? new Color(1f, 0.2f, 0.22f, 0.9f) : new Color(1f, 0.75f, 0.2f, 0.9f));
            baseRing.transform.SetParent(marker.transform, true);

            // 이름 라벨
            var label = new GameObject("UnitLabel_" + unit.id);
            label.transform.SetParent(marker.transform, false);
            label.transform.localPosition = Vector3.up * 0.9f;
            var text = label.AddComponent<TextMesh>();
            text.characterSize = .2f;
            text.fontSize = 48;
            text.anchor = TextAnchor.MiddleCenter;
            text.color = Color.white;
            var font = Resources.Load<Font>("Fonts/NanumGothic-Regular");
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null) text.font = font;
            text.text = unit.factionId == 1 ? "아" : unit.factionId == 2 ? "적" : "중";
            if (Camera.main != null) label.transform.rotation = Camera.main.transform.rotation;

            // 클릭 감지
            var clicker = marker.AddComponent<UnitClickHandler>();
            clicker.Init(this, unit.id);
        }

        private void SpawnBuildingVisual(BuildingState building)
        {
            var prefab = SelectBuildingPrefab(building);
            var landmark = Spawn(prefab, PrimitiveType.Cube);
            landmark.name = "Building_" + building.id;
            landmark.transform.position = HexToWorld(building.position) + Vector3.up * .1f;
            // KayKit 건물 프리셋은 1.0 스케일이므로 프리셋이 있으면 레벨에 따라 살짝만 키운다
            landmark.transform.localScale = prefab == null ? Vector3.one * (.38f + building.level * .04f) : Vector3.one * (.7f + building.level * .05f);
            if (prefab == null) Tint(landmark, building.factionId == 1 ? new Color(.55f, .82f, 1f) : new Color(1f, .42f, .42f));
            buildingVisuals[building.id] = landmark;
            EnsureHitCollider(landmark, new Vector3(0, 0.9f, 0), new Vector3(1.5f, 2f, 1.5f));

            var flagPrefab = building.factionId == 1 ? presentation?.flagPlayer : building.factionId == 2 ? presentation?.flagEnemy : presentation?.flagNeutral;
            if (flagPrefab != null)
            {
                var flag = Spawn(flagPrefab, PrimitiveType.Cube);
                flag.name = "Flag_" + building.id;
                flag.transform.SetParent(landmark.transform, false);
                flag.transform.localPosition = new Vector3(0.5f, 0.15f, 0.15f);
                flag.transform.localScale = Vector3.one * 0.55f;
            }

            var clicker = landmark.AddComponent<BuildingClickHandler>();
            clicker.Init(this, building.id);
        }

        private UnityEngine.Object SelectBuildingPrefab(BuildingState building)
        {
            if (building.type == BuildingType.Headquarters) return building.factionId == 1 ? presentation?.playerHeadquarters : presentation?.enemyHeadquarters;
            if (building.type == BuildingType.Warehouse) return presentation?.lumbermill ?? presentation?.home;
            if (building.type == BuildingType.Workshop) return presentation?.blacksmith;
            if (building.type == BuildingType.Watchtower) return presentation?.tower;
            if (building.type == BuildingType.Market) return presentation?.market;
            if (building.type == BuildingType.Barracks) return presentation?.barracks;
            return presentation?.settlement;
        }

        private static void EnsureHitCollider(GameObject visual, Vector3 center, Vector3 size)
        {
            var collider = visual.GetComponent<BoxCollider>();
            if (collider == null) collider = visual.AddComponent<BoxCollider>();
            collider.center = center;
            collider.size = size;
            collider.isTrigger = false;
        }

        private void EnsureCameraAndLight()
        {
            // 씬에 배치된 카메라/조명을 우선 사용하고, 없으면 폴백 생성
            if (Camera.main == null && mainCamera == null)
            {
                var cam = new GameObject("Quarter Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
                cam.transform.position = new Vector3(0, 12, -10);
                cam.transform.rotation = Quaternion.Euler(52, 0, 0);
                cam.orthographic = true;
                cam.orthographicSize = 5.4f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(.035f, .07f, .12f);
                mainCamera = cam;
            }
            if (worldSun == null)
            {
                var sun = GameObject.Find("World Sun");
                if (sun != null) worldSun = sun.GetComponent<Light>();
            }
            if (worldSun == null)
            {
                var light = new GameObject("World Sun").AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = new Color(1f, .91f, .73f);
                light.intensity = 1.35f;
                light.transform.rotation = Quaternion.Euler(50, -35, 0);
                worldSun = light;
            }
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(.28f, .37f, .48f);
        }

        private Vector3 HexToWorld(HexCoord p) => new Vector3((p.q + p.r * .5f) * 1.65f, 0, p.r * 1.43f);

        private static void Tint(GameObject visual, Color tint)
        {
            foreach (var renderer in visual.GetComponentsInChildren<Renderer>()) SetRendererColor(renderer, tint);
        }

        private static void SetRendererColor(Renderer renderer, Color tint)
        {
            if (renderer == null) return;
            tintProperties ??= new MaterialPropertyBlock();
            tintProperties.Clear();
            renderer.GetPropertyBlock(tintProperties);
            tintProperties.SetColor(BaseColorProperty, tint);
            tintProperties.SetColor(ColorProperty, tint);
            renderer.SetPropertyBlock(tintProperties);
        }

        private static Material RuntimeMaterial(bool unlit)
        {
            var cached = unlit ? runtimeUnlitMaterial : runtimeLitMaterial;
            if (cached != null) return cached;
            var shader = unlit
                ? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit")
                : Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) return null;
            cached = new Material(shader)
            {
                name = unlit ? "OnlyMyGame Runtime Unlit" : "OnlyMyGame Runtime Lit",
                hideFlags = HideFlags.HideAndDontSave
            };
            if (unlit) runtimeUnlitMaterial = cached;
            else runtimeLitMaterial = cached;
            return cached;
        }

        private static GameObject Spawn(UnityEngine.Object prefab, PrimitiveType fallback)
        {
            if (prefab == null) return CreatePrimitiveWithUrp(fallback);
            var instance = Instantiate(prefab);
            if (instance is GameObject gameObject) return gameObject;
            if (instance is Component component) return component.gameObject;
            Destroy(instance);
            return CreatePrimitiveWithUrp(fallback);
        }

        private static GameObject CreatePrimitiveWithUrp(PrimitiveType type)
        {
            var go = GameObject.CreatePrimitive(type);
            var material = RuntimeMaterial(false);
            if (material != null)
            {
                foreach (var renderer in go.GetComponentsInChildren<Renderer>())
                    renderer.sharedMaterial = material;
            }
            return go;
        }

        private static GameObject CreateDisc(string name, Vector3 position, float radius, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = new Vector3(radius, 0.025f, radius);
            var collider = go.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            var renderer = go.GetComponent<Renderer>();
            var material = RuntimeMaterial(true);
            if (renderer != null && material != null)
            {
                renderer.sharedMaterial = material;
                SetRendererColor(renderer, color);
            }
            return go;
        }

        private void RenderVisibility()
        {
            if (worldSun != null)
            {
                var luckFactor = game.luck / 100f;
                worldSun.intensity = 1.1f + luckFactor * 0.6f;
                worldSun.color = Color.Lerp(new Color(.7f, .75f, .9f), new Color(1f, .91f, .73f), luckFactor);
            }
            foreach (var tile in game.map)
            {
                if (!tileVisuals.TryGetValue(tile.position, out var go)) continue;
                // Keep the full board silhouette readable while concealing undiscovered details.
                go.SetActive(true);
                var tint = tile.visible ? Color.white : tile.explored ? new Color(.24f, .3f, .4f, 1f) : new Color(.075f, .095f, .14f, 1f);
                foreach (var renderer in go.GetComponentsInChildren<Renderer>(true)) SetRendererColor(renderer, tint);
                var resource = go.transform.Find("Resource_" + tile.position);
                if (resource != null) resource.gameObject.SetActive(tile.visible && tile.amount > 0);
            }
            foreach (var building in game.buildings)
            {
                if (buildingVisuals.TryGetValue(building.id, out var go))
                {
                    var tile = game.map.FirstOrDefault(t => t.position.Equals(building.position));
                    go.SetActive(building.hp > 0 && tile != null && tile.visible);
                }
            }
            foreach (var unit in game.entities)
            {
                if (unitVisuals.TryGetValue(unit.id, out var go))
                {
                    var tile = game.map.FirstOrDefault(t => t.position.Equals(unit.position));
                    go.SetActive(unit.alive && tile != null && tile.visible);
                }
            }
            UpdateLuckFeedback(false);
            RenderRuleCues();
        }

        private void RenderRuleCues()
        {
            foreach (var old in ruleCueVisuals.Where(old => old != null)) Destroy(old);
            ruleCueVisuals.Clear();
            var active = game.activeRules
                .Where(rule => rule != null && GameRules.IsRuleActive(rule, game.turn))
                .OrderByDescending(rule => rule.appliedTurn)
                .ThenByDescending(rule => rule.priority)
                .ThenBy(rule => rule.id ?? string.Empty, StringComparer.Ordinal)
                .Take(6)
                .ToList();
            if (active.Count == 0) return;
            var palette = new[]
            {
                new Color(1f, .78f, .22f), new Color(.3f, .86f, 1f), new Color(.78f, .46f, 1f),
                new Color(.4f, 1f, .55f), new Color(1f, .42f, .45f), new Color(1f, .68f, .36f)
            };
            for (var i = 0; i < active.Count; i++)
            {
                var rule = active[i];
                var targets = RuleCuePlanner.ResolveVisibleTargets(game, rule, 3);
                for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
                    SpawnRuleCue(rule, targets[targetIndex], palette[i % palette.Length], i, targetIndex);
            }
        }

        private void SpawnRuleCue(RuleNodeV1 rule, HexCoord target, Color color, int ruleIndex, int targetIndex)
        {
            var root = new GameObject("RuleCue_" + ruleIndex + "_" + targetIndex);
            root.tag = "RuleCue";
            root.transform.position = HexToWorld(target) + Vector3.up * .08f;
            ruleCueVisuals.Add(root);

            var markerPrefab = presentation?.propTarget ?? presentation?.flagNeutral;
            var marker = Spawn(markerPrefab, PrimitiveType.Cylinder);
            marker.name = "RuleCueMarker";
            marker.transform.SetParent(root.transform, false);
            marker.transform.localPosition = Vector3.zero;
            marker.transform.localScale = markerPrefab == null ? new Vector3(.3f, .035f, .3f) : marker.transform.localScale * .32f;
            foreach (var collider in marker.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            Tint(marker, color);

            var label = new GameObject("RuleCueLabel");
            label.transform.SetParent(root.transform, false);
            label.transform.localPosition = Vector3.up * .88f;
            var text = label.AddComponent<TextMesh>();
            text.characterSize = .125f;
            text.fontSize = 42;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontStyle = FontStyle.Bold;
            text.color = color;
            var font = Resources.Load<Font>("Fonts/NanumGothic-Regular") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null) text.font = font;
            var displayName = SafeWorldCueText(rule.name, "새 세계 규칙", 18);
            var worldCue = SafeWorldCueText(rule.worldCue, "영향 대상", 18);
            text.text = "◆ " + displayName + "\n" + worldCue;
            var mainCamera = Camera.main;
            if (mainCamera != null) label.transform.rotation = mainCamera.transform.rotation;

            var pulse = root.AddComponent<RuleCuePulse>();
            pulse.Configure(marker.transform, label.transform, ruleIndex * 1.17f + targetIndex * .63f, mainCamera != null ? mainCamera.transform : null);
        }

        private static string SafeWorldCueText(string value, string fallback, int maximumLength)
        {
            var safe = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            safe = safe.Replace('<', '＜').Replace('>', '＞').Replace('\n', ' ').Replace('\r', ' ');
            return safe.Length <= maximumLength ? safe : safe.Substring(0, maximumLength - 1) + "…";
        }

        // ==================== 선택 & 버블 UI ====================

        public void SelectUnit(int unitId)
        {
            selectedUnitId = unitId;
            selectedBuildingId = -1;
            var unit = game.entities.FirstOrDefault(u => u.id == unitId);
            if (unit == null || !unit.alive) { ClearSelection(); return; }
            if (unitVisuals.TryGetValue(unitId, out var visual))
            {
                if (selectionRing != null)
                {
                    selectionRing.SetActive(true);
                    selectionRing.transform.position = visual.transform.position + Vector3.up * 0.05f;
                    var ringCollider = selectionRing.GetComponent<Collider>();
                    if (ringCollider != null) ringCollider.enabled = false;
                }
                feedback?.Selection(visual.transform.position);
            }
            commandBubble?.Hide();
            RefreshHud();
        }

        public void SelectBuilding(int buildingId)
        {
            selectedBuildingId = buildingId;
            selectedUnitId = -1;
            var building = game.buildings.FirstOrDefault(b => b.id == buildingId);
            if (building == null) { ClearSelection(); return; }
            if (buildingVisuals.TryGetValue(buildingId, out var visual))
            {
                if (selectionRing != null)
                {
                    selectionRing.SetActive(true);
                    selectionRing.transform.position = visual.transform.position + Vector3.up * 0.05f;
                    var ringCollider = selectionRing.GetComponent<Collider>();
                    if (ringCollider != null) ringCollider.enabled = false;
                }
                feedback?.Selection(visual.transform.position);
            }
            commandBubble?.Hide();
            RefreshHud();
        }

        public void ClearSelection()
        {
            selectedUnitId = -1;
            selectedBuildingId = -1;
            selectionRing?.SetActive(false);
            commandBubble?.Hide();
            RefreshHud();
        }

        private void ShowUnitBubbles(UnitState unit)
        {
            var player = game.factions.First(f => f.id == 1);
            var items = new List<CommandBubble.BubbleEntry>();
            var usedSp = commands.Sum(c => GameRules.CommandCost(c.type));

            if (unit.factionId == 1)
            {
                if (usedSp + GameRules.CommandCost(CommandType.Move) <= player.sp)
                    items.Add(MakeBubble("이동", new Color(.3f, .8f, 1f), "인접 이동 · 동속 충돌은 행운+시드 난수 (SP 1)", () => BeginCommand(CommandType.Move)));
                if (usedSp + GameRules.CommandCost(CommandType.Gather) <= player.sp)
                    items.Add(MakeBubble("채집", new Color(.4f, .9f, .4f), "자원 채집 (SP 2)", () => BeginCommand(CommandType.Gather)));
                if (usedSp + GameRules.CommandCost(CommandType.Hunt) <= player.sp)
                    items.Add(MakeBubble("수렵", new Color(.9f, .6f, .3f), "초원 식량 2 / 숲 3, 행운 70+ 보너스 1 (SP 2)", () => BeginCommand(CommandType.Hunt)));
                if (usedSp + GameRules.CommandCost(CommandType.Attack) <= player.sp)
                    items.Add(MakeBubble("공격", new Color(1f, .35f, .35f), "근접 공격 (SP 2)", () => BeginCommand(CommandType.Attack)));
                if (usedSp + GameRules.CommandCost(CommandType.Trade) <= player.sp)
                    items.Add(MakeBubble("거래", new Color(.9f, .8f, .3f), "식량→화폐 (SP 2)", () => BeginCommand(CommandType.Trade)));
                if (usedSp + GameRules.CommandCost(CommandType.Hire) <= player.sp)
                    items.Add(MakeBubble("고용", new Color(.7f, .5f, .9f), "화폐 3 → 용병 (SP 2)", () => BeginCommand(CommandType.Hire)));
                if (usedSp + GameRules.CommandCost(CommandType.Build) <= player.sp)
                    items.Add(MakeBubble("건설", new Color(.6f, .8f, .5f), "건물 종류와 비용을 선택해 건설 (SP 3)", () => BeginCommand(CommandType.Build)));
                if (usedSp + GameRules.CommandCost(CommandType.Upgrade) <= player.sp)
                    items.Add(MakeBubble("강화", new Color(.8f, .7f, .4f), "건물 강화 (SP 3)", () => BeginCommand(CommandType.Upgrade)));
                if (usedSp + GameRules.CommandCost(CommandType.Persuade) <= player.sp)
                    items.Add(MakeBubble("설득", new Color(.9f, .5f, .8f), "관계 개선 (SP 2)", () => BeginCommand(CommandType.Persuade)));
                if (usedSp + GameRules.CommandCost(CommandType.Capture) <= player.sp && CanBeginCommand(CommandType.Capture))
                    items.Add(MakeBubble("점령", new Color(.35f, .78f, 1f), "이동 후 적 유닛·거점이 없으면 영토로 확보 (SP 2)", () => BeginCommand(CommandType.Capture)));
            }
            else
            {
                items.Add(MakeBubble("정보", new Color(.6f, .6f, .6f), unit.tags.FirstOrDefault() ?? "유닛", () => { }));
            }

            if (unitVisuals.TryGetValue(unit.id, out var visual) && commandBubble != null)
            {
                commandBubble.Show(visual.transform, 1.1f, items);
            }
        }

        private void ShowBuildingBubbles(BuildingState building)
        {
            var items = new List<CommandBubble.BubbleEntry>();
            if (building.factionId == 1)
            {
                items.Add(MakeBubble("강화", new Color(.8f, .7f, .4f), "건물 레벨 +1 (SP 3)", () => BeginCommand(CommandType.Upgrade)));
            }
            else
            {
                items.Add(MakeBubble("정보", new Color(.6f, .6f, .6f), building.type + " Lv." + building.level, () => { }));
            }
            if (buildingVisuals.TryGetValue(building.id, out var visual) && commandBubble != null)
            {
                commandBubble.Show(visual.transform, 1.1f, items);
            }
        }

        private CommandBubble.BubbleEntry MakeBubble(string label, Color color, string tooltip, Action onClick)
        {
            return new CommandBubble.BubbleEntry { label = label, color = color, tooltip = tooltip, onClick = onClick };
        }

        // ==================== 명령 ====================

        public GameSnapshotV1 Game => game;
        public IReadOnlyList<PlannedCommand> Commands => commands;
        public IReadOnlyList<string> Ledger => ledger;
        public UnitState SelectedUnit => game?.entities.FirstOrDefault(u => u.id == selectedUnitId && u.alive);
        public BuildingState SelectedBuilding => game?.buildings.FirstOrDefault(b => b.id == selectedBuildingId && b.hp > 0);
        public int PlannedSp => commands.Sum(c => GameRules.CommandCost(c.type));
        public bool IsBusy => waitingForRules || game?.phase == RunPhase.Resolving || game?.phase == RunPhase.AwaitingRules;
        public bool IsBlocked => blockedOnRules;
        public bool IsTargeting => targetingCommand.HasValue || targetingDynamicAction != null;
        public string TargetingPrompt => targetingPrompt;
        public string BlockReason => blockReason;
        public string ServiceStatus => serviceStatus;
        public bool ServiceOnline => serviceOnline;
        public bool ServiceChecking => serviceChecking;
        public int RetryDelaySeconds => Mathf.Max(0, Mathf.CeilToInt(retryRulesAvailableAtRealtime - Time.realtimeSinceStartup));
        public bool CanRetryRules => RetryDelaySeconds <= 0;
        public bool HasSavedRun => SafeReadSave(TempKey) != null || SafeReadSave(SaveKey) != null || SafeReadSave(BackupKey) != null;
        public bool HasPreviousRun => SafeReadSave(PreviousRunKey) != null;
        public IReadOnlyList<DynamicActionV1> DynamicActions => game?.dynamicActions ?? (IReadOnlyList<DynamicActionV1>)Array.Empty<DynamicActionV1>();
        public IReadOnlyList<BuildingType> BuildTypes => PlayerBuildTypes;

        public bool CanBeginCommand(CommandType command)
        {
            if (game == null || game.outcome != RunOutcome.Ongoing || game.phase != RunPhase.Planning || IsBusy || blockedOnRules) return false;
            var player = game.factions.First(f => f.id == 1);
            var actor = SelectedUnit;
            if (command == CommandType.Upgrade)
            {
                if (SelectedBuilding == null || SelectedBuilding.factionId != 1) return false;
                actor = ClosestPlayerUnit(SelectedBuilding.position);
                if (actor == null || ProjectedActorPosition(actor).Distance(SelectedBuilding.position) > 1) return false;
            }
            if (actor == null || actor.factionId != 1) return false;
            // A unit may combine different action categories in the shared-SP system,
            // while re-selecting the same category edits that reservation.
            var replacing = commands.LastOrDefault(c => c.unitId == actor.id && c.type == command);
            var usedWithoutActor = PlannedSp - (replacing == null ? 0 : GameRules.CommandCost(command));
            if (usedWithoutActor + GameRules.CommandCost(command) > player.sp) return false;
            var actorPosition = ProjectedActorPosition(actor);
            if (command == CommandType.Gather)
            {
                var tile = game.map.FirstOrDefault(candidate => candidate != null && candidate.position.Equals(actorPosition));
                var alreadyReserved = commands.Count(planned => planned != null && planned != replacing && planned.type == CommandType.Gather && planned.target.Equals(actorPosition));
                if (tile == null || tile.resource == ResourceType.None || tile.amount <= alreadyReserved) return false;
            }
            if (command == CommandType.Hunt && !game.map.Any(tile => tile != null && tile.position.Equals(actorPosition))) return false;
            if (command == CommandType.Capture)
            {
                var tile = game.map.FirstOrDefault(candidate => candidate != null && candidate.position.Equals(actorPosition));
                if (tile == null || tile.owner == 1 || commands.Any(planned => planned != null && planned != replacing && planned.type == CommandType.Capture && planned.target.Equals(actorPosition))) return false;
            }
            if (command == CommandType.Build)
                return !game.buildings.Any(b => b.position.Equals(actorPosition)) &&
                       !commands.Any(planned => planned != null && planned != replacing && planned.type == CommandType.Build && planned.target.Equals(actorPosition));
            if (!HasPlanningResources(command, replacing)) return false;
            if (command == CommandType.Upgrade && commands.Any(planned => planned != null && planned != replacing && planned.type == CommandType.Upgrade && planned.target.Equals(SelectedBuilding.position))) return false;
            return true;
        }

        public bool CanBuildType(BuildingType type)
        {
            if (!Enum.IsDefined(typeof(BuildingType), type) || game == null || game.outcome != RunOutcome.Ongoing ||
                game.phase != RunPhase.Planning || IsBusy || blockedOnRules) return false;
            var player = game.factions.FirstOrDefault(faction => faction != null && faction.id == 1);
            var actor = SelectedUnit;
            if (player == null || actor == null || actor.factionId != 1) return false;
            var replacing = commands.LastOrDefault(command => command != null && command.unitId == actor.id && command.type == CommandType.Build);
            var usedWithoutBuild = PlannedSp - (replacing == null ? 0 : GameRules.CommandCost(CommandType.Build));
            if (usedWithoutBuild + GameRules.CommandCost(CommandType.Build) > player.sp) return false;
            var actorPosition = ProjectedActorPosition(actor);
            if (game.buildings.Any(building => building != null && building.position.Equals(actorPosition)) ||
                commands.Any(planned => planned != null && planned != replacing && planned.type == CommandType.Build && planned.target.Equals(actorPosition))) return false;
            if (type == BuildingType.Headquarters && game.buildings.Any(building => building != null && building.factionId == 1 && building.type == BuildingType.Headquarters && building.hp > 0)) return false;
            return HasPlanningResources(CommandType.Build, replacing, type);
        }

        public void BeginCommand(CommandType command)
        {
            if (!CanBeginCommand(command))
            {
                commercialHud?.Toast("이 행동을 지금 예약할 수 없습니다.", new Color(1f, .55f, .3f));
                return;
            }
            CancelTargeting(false);
            var actor = command == CommandType.Upgrade ? ClosestPlayerUnit(SelectedBuilding.position) : SelectedUnit;
            if (command == CommandType.Build)
            {
                commercialHud?.ShowBuildPicker();
                return;
            }
            if (command == CommandType.Move || command == CommandType.Attack || command == CommandType.Trade || command == CommandType.Persuade || command == CommandType.Hire)
            {
                targetingCommand = command;
                targetingPrompt = command == CommandType.Move ? "이동할 인접 타일을 선택하세요.  ·  우클릭/Esc 취소" : command == CommandType.Attack ? "공격할 적 유닛 또는 거점을 선택하세요.  ·  우클릭/Esc 취소" : CommandKorean(command) + " 상대를 선택하세요.  ·  우클릭/Esc 취소";
                ShowTargetHighlights(actor, command);
                RefreshHud();
                return;
            }
            var target = command == CommandType.Upgrade ? SelectedBuilding.position : ProjectedActorPosition(actor);
            Queue(command, actor.id, target);
        }

        public bool QueueBuildFromHud(BuildingType type)
        {
            if (!CanBuildType(type))
            {
                commercialHud?.Toast("선택한 건물을 지금 건설할 수 없습니다.", new Color(1f, .55f, .3f));
                return false;
            }
            var actor = SelectedUnit;
            return actor != null && Queue(CommandType.Build, actor.id, ProjectedActorPosition(actor), type);
        }

        private bool Queue(CommandType command, int unitId, HexCoord target, BuildingType buildingType = BuildingType.Warehouse)
        {
            var player = game.factions.First(f => f.id == 1);
            var previous = commands.LastOrDefault(c => c.unitId == unitId && c.type == command);
            var previousIndex = previous == null ? -1 : commands.IndexOf(previous);
            if (previousIndex >= 0) commands.RemoveAt(previousIndex);
            if (PlannedSp + GameRules.CommandCost(command) > player.sp)
            {
                if (previousIndex >= 0) commands.Insert(previousIndex, previous);
                commercialHud?.Toast("남은 SP가 부족합니다.", new Color(1f, .45f, .35f));
                return false;
            }
            if (!HasPlanningResources(command, null, buildingType))
            {
                if (previousIndex >= 0) commands.Insert(previousIndex, previous);
                commercialHud?.Toast("다른 명령에 예약된 자원을 제외하면 비용이 부족합니다.", new Color(1f, .55f, .3f));
                return false;
            }
            var planned = new PlannedCommand { factionId = 1, unitId = unitId, type = command, target = target, buildingType = buildingType };
            commands.Add(planned);
            var invalidatedCount = 0;
            if (command == CommandType.Move)
            {
                // Gather, hunt, and build resolve after movement. Keep their HUD
                // destination in sync when a move is queued or retargeted.
                foreach (var selfAction in commands.Where(candidate => candidate != null && candidate.unitId == unitId && IsSelfTileAction(candidate.type)))
                    selfAction.target = target;
                invalidatedCount = RemoveInvalidProjectedCommands(unitId, planned);
            }
            ledger.Add("[계획] " + (command == CommandType.Build ? BuildingName(buildingType) + " 건설" : CommandKorean(command)) + " 예약 · " + ExpectedRange(command));
            if (unitVisuals.TryGetValue(unitId, out var visual)) feedback?.CommandQueued(visual.transform.position);
            commercialHud?.Toast(invalidatedCount > 0
                ? CommandKorean(command) + " 예약 · 새 위치에서 불가능한 후속 명령 " + invalidatedCount + "개를 취소했습니다."
                : CommandKorean(command) + " 명령을 예약했습니다.", new Color(.5f, 1f, .7f));
            CancelTargeting(false);
            RefreshHud();
            return true;
        }

        public void UndoLastCommand()
        {
            if (commands.Count == 0 || IsBusy) return;
            var removed = commands[commands.Count - 1];
            commands.RemoveAt(commands.Count - 1);
            if (removed.type == CommandType.Move)
            {
                var actor = game.entities.FirstOrDefault(unit => unit != null && unit.id == removed.unitId && unit.alive);
                if (actor != null)
                {
                    foreach (var selfAction in commands.Where(candidate => candidate != null && candidate.unitId == removed.unitId && IsSelfTileAction(candidate.type)))
                        selfAction.target = actor.position;
                    RemoveInvalidProjectedCommands(removed.unitId, null);
                }
            }
            ledger.Add("[계획] " + CommandKorean(removed.type) + " 명령을 취소했습니다.");
            RefreshHud();
        }

        public void ClearCommands()
        {
            if (IsBusy) return;
            if (commands.Count > 0) ledger.Add("[계획] 예약된 명령을 모두 취소했습니다.");
            commands.Clear();
            CancelTargeting(false);
            RefreshHud();
        }

        public void CancelTargeting() => CancelTargeting(true);

        private void CancelTargeting(bool notify)
        {
            targetingCommand = null;
            targetingDynamicAction = null;
            targetingDynamicActorId = -1;
            targetingPrompt = "";
            foreach (var highlight in targetHighlights) if (highlight != null) Destroy(highlight);
            targetHighlights.Clear();
            if (notify) commercialHud?.Toast("대상 지정을 취소했습니다.", Color.white);
            RefreshHud();
        }

        private void ShowTargetHighlights(UnitState actor, CommandType command)
        {
            var positions = new List<HexCoord>();
            if (command == CommandType.Move)
            {
                positions.AddRange(HexCoord.Directions.Select(d => new HexCoord(actor.position.q + d.q, actor.position.r + d.r))
                    .Where(p => game.map.Any(t => t.position.Equals(p) && t.visible && t.terrain != "강")));
            }
            else
            {
                positions.AddRange(game.entities.Where(u => u.factionId != 1 && u.alive && IsValidCommandTarget(actor, command, u.position)).Select(u => u.position));
                if (command == CommandType.Attack) positions.AddRange(game.buildings.Where(b => b.factionId != 1 && b.hp > 0 && IsValidCommandTarget(actor, command, b.position)).Select(b => b.position));
            }
            foreach (var position in positions.Distinct())
            {
                var color = command == CommandType.Attack ? new Color(1f, .2f, .22f, .78f) : command == CommandType.Move ? new Color(.15f, .8f, 1f, .72f) : new Color(1f, .78f, .25f, .74f);
                targetHighlights.Add(CreateDisc("Target_" + position, HexToWorld(position) + Vector3.up * .16f, .68f, color));
            }
            if (positions.Count == 0)
            {
                commercialHud?.Toast("현재 범위에 유효한 대상이 없습니다.", new Color(1f, .55f, .3f));
                CancelTargeting(false);
            }
        }

        private bool TryTarget(HexCoord position, DynamicTargetKind clickedKind = DynamicTargetKind.Tile, int clickedTargetId = 0)
        {
            if (targetingDynamicAction != null) return TryTargetDynamicAction(position, clickedKind, clickedTargetId);
            if (!targetingCommand.HasValue) return false;
            var command = targetingCommand.Value;
            var actor = SelectedUnit;
            if (actor == null || actor.factionId != 1) { CancelTargeting(false); return true; }
            var valid = command == CommandType.Move
                ? actor.position.Distance(position) == 1 && game.map.Any(t => t.position.Equals(position) && t.visible && t.terrain != "강")
                : IsValidCommandTarget(actor, command, position);
            if (!valid)
            {
                commercialHud?.Toast("강조된 유효 대상을 선택하세요.", new Color(1f, .55f, .3f));
                return true;
            }
            Queue(command, actor.id, position);
            return true;
        }

        private bool IsValidCommandTarget(UnitState actor, CommandType command, HexCoord position)
        {
            if (actor == null || ProjectedActorPosition(actor).Distance(position) > 2) return false;
            // Targeting must never become a side-channel around fog of war. Hidden
            // entities are not rendered, so they must not be highlightable or
            // selectable through their otherwise-known snapshot coordinates.
            if (!IsVisibleTile(game, position)) return false;
            var targetUnit = game.entities.FirstOrDefault(unit => unit != null && unit.factionId != 1 && unit.alive && unit.position.Equals(position));
            if (command == CommandType.Attack)
                return targetUnit != null || game.buildings.Any(building => building != null && building.factionId != 1 && building.hp > 0 && building.position.Equals(position));
            if (targetUnit == null) return false;
            if (command != CommandType.Hire) return command == CommandType.Trade || command == CommandType.Persuade;

            var partner = game.factions.FirstOrDefault(faction => faction != null && faction.id == targetUnit.factionId);
            if (partner == null || partner.kind != FactionKind.Neutral || partner.relationToPlayer < 0) return false;
            var replacing = commands.LastOrDefault(planned => planned.unitId == actor.id && planned.type == CommandType.Hire);
            return !commands.Any(planned => planned != replacing && planned.type == CommandType.Hire && planned.target.Equals(position));
        }

        private static bool IsVisibleTile(GameSnapshotV1 snapshot, HexCoord position)
        {
            return snapshot?.map != null && snapshot.map.Any(tile => tile != null && tile.position.Equals(position) && tile.visible);
        }

        private HexCoord ProjectedActorPosition(UnitState actor)
        {
            if (actor == null) return default;
            var move = commands.LastOrDefault(planned => planned != null && planned.unitId == actor.id && planned.type == CommandType.Move);
            return move?.target ?? actor.position;
        }

        private static bool IsSelfTileAction(CommandType command)
        {
            return command == CommandType.Gather || command == CommandType.Hunt || command == CommandType.Build || command == CommandType.Capture;
        }

        private int RemoveInvalidProjectedCommands(int unitId, PlannedCommand moveToKeep)
        {
            var actor = game?.entities.FirstOrDefault(unit => unit != null && unit.id == unitId && unit.alive);
            if (actor == null) return 0;
            var invalid = commands.Where(planned => planned != null && planned != moveToKeep && planned.unitId == unitId && !IsQueuedCommandLocallyValid(actor, planned)).ToList();
            foreach (var planned in invalid)
            {
                commands.Remove(planned);
                ledger.Add("[계획] 이동 예정 위치에서 실행할 수 없어 " + CommandKorean(planned.type) + " 명령을 취소했습니다.");
            }
            return invalid.Count;
        }

        private bool IsQueuedCommandLocallyValid(UnitState actor, PlannedCommand planned)
        {
            if (actor == null || planned == null) return false;
            var actorPosition = ProjectedActorPosition(actor);
            var commandIndex = commands.IndexOf(planned);
            var earlier = commands.Take(Math.Max(0, commandIndex));
            if (planned.type == CommandType.Move) return true;
            if (planned.type == CommandType.Gather)
            {
                var tile = game.map.FirstOrDefault(candidate => candidate != null && candidate.position.Equals(actorPosition));
                var earlierReservations = earlier.Count(candidate => candidate != null && candidate.type == CommandType.Gather && candidate.target.Equals(actorPosition));
                return tile != null && tile.resource != ResourceType.None && tile.amount > earlierReservations;
            }
            if (planned.type == CommandType.Hunt) return game.map.Any(tile => tile != null && tile.position.Equals(actorPosition));
            if (planned.type == CommandType.Capture)
            {
                var tile = game.map.FirstOrDefault(candidate => candidate != null && candidate.position.Equals(actorPosition));
                return tile != null && tile.owner != 1 && !earlier.Any(candidate => candidate != null && candidate.type == CommandType.Capture && candidate.target.Equals(actorPosition));
            }
            if (planned.type == CommandType.Build)
                return Enum.IsDefined(typeof(BuildingType), planned.buildingType) &&
                       !game.buildings.Any(building => building != null && building.position.Equals(actorPosition)) &&
                       !earlier.Any(candidate => candidate != null && candidate.type == CommandType.Build && candidate.target.Equals(actorPosition)) &&
                       (planned.buildingType != BuildingType.Headquarters || !game.buildings.Any(building => building != null && building.factionId == 1 && building.type == BuildingType.Headquarters && building.hp > 0));
            if (planned.type == CommandType.Upgrade)
                return !earlier.Any(candidate => candidate != null && candidate.type == CommandType.Upgrade && candidate.target.Equals(planned.target)) &&
                    game.buildings.Any(building => building != null && building.factionId == 1 && building.hp > 0 && building.position.Equals(planned.target) && actorPosition.Distance(building.position) <= 1);
            if (planned.type == CommandType.Attack || planned.type == CommandType.Trade || planned.type == CommandType.Persuade || planned.type == CommandType.Hire)
                return IsValidCommandTarget(actor, planned.type, planned.target);
            return false;
        }

        private bool HasPlanningResources(CommandType requested, PlannedCommand excluded, BuildingType buildingType = BuildingType.Warehouse)
        {
            var player = game?.factions.FirstOrDefault(faction => faction != null && faction.id == 1);
            if (player?.resources == null) return false;

            var reserved = PlanningResourceReservations(excluded);
            if (requested == CommandType.Trade) AddReservation(reserved, ResourceType.Food, 1);
            else if (requested == CommandType.Hire) AddReservation(reserved, ResourceType.Coin, 3);
            else if (requested == CommandType.Upgrade) AddReservation(reserved, ResourceType.Stone, 3);
            else if (requested == CommandType.Build)
            {
                AddReservation(reserved, ResourceType.Wood, GameRules.BuildingCost(buildingType));
                AddReservation(reserved, ResourceType.Iron, GameRules.BuildingIronCost(buildingType));
            }
            return reserved.All(pair => pair.Value <= player.resources.Get(pair.Key));
        }

        private Dictionary<ResourceType, int> PlanningResourceReservations(PlannedCommand excluded)
        {
            var reserved = new Dictionary<ResourceType, int>();
            foreach (var planned in commands)
            {
                if (planned == null || planned == excluded) continue;
                if (planned.type == CommandType.Trade) AddReservation(reserved, ResourceType.Food, 1);
                else if (planned.type == CommandType.Hire) AddReservation(reserved, ResourceType.Coin, 3);
                else if (planned.type == CommandType.Upgrade) AddReservation(reserved, ResourceType.Stone, 3);
                else if (planned.type == CommandType.Build)
                {
                    var type = Enum.IsDefined(typeof(BuildingType), planned.buildingType) ? planned.buildingType : BuildingType.Warehouse;
                    AddReservation(reserved, ResourceType.Wood, GameRules.BuildingCost(type));
                    AddReservation(reserved, ResourceType.Iron, GameRules.BuildingIronCost(type));
                }
            }
            return reserved;
        }

        private static void AddReservation(IDictionary<ResourceType, int> reserved, ResourceType type, int amount)
        {
            reserved[type] = (reserved.TryGetValue(type, out var current) ? current : 0) + amount;
        }

        private UnitState ClosestPlayerUnit(HexCoord position) => game.entities.Where(u => u.factionId == 1 && u.alive).OrderBy(u => ProjectedActorPosition(u).Distance(position)).FirstOrDefault();

        public static string ExpectedRange(CommandType c)
        {
            if (c == CommandType.Move) return "인접 타일로 이동 · 동속 충돌은 행운 보정+시드 난수";
            if (c == CommandType.Gather) return "현재 타일 자원 2 획득";
            if (c == CommandType.Attack) return "피해 2~3, 행운 70 이상이면 3";
            if (c == CommandType.Hunt) return "초원 식량 2 / 숲 3, 행운 70 이상이면 +1";
            if (c == CommandType.Trade) return "식량 1 → 화폐 2, 관계 +4";
            if (c == CommandType.Hire) return "화폐 3 → 고용병 1";
            if (c == CommandType.Build) return "선택 건물 비용 지불 → 건물 1채";
            if (c == CommandType.Upgrade) return "석재 3 → 건물 레벨 +1";
            if (c == CommandType.Capture) return "이동 후 현재 타일 · 해결 시 살아있는 적 유닛·거점이 없으면 점령";
            return "";
        }

        public static string CommandKorean(CommandType c) => c == CommandType.Move ? "이동" : c == CommandType.Gather ? "채집" : c == CommandType.Hunt ? "수렵" : c == CommandType.Attack ? "공격" : c == CommandType.Trade ? "거래" : c == CommandType.Hire ? "고용" : c == CommandType.Build ? "건설" : c == CommandType.Upgrade ? "강화" : c == CommandType.Capture ? "점령" : "설득";

        public string Describe(PlannedCommand command)
        {
            var action = command.type == CommandType.Build ? BuildingName(command.buildingType) + " 건설" : CommandKorean(command.type);
            var resources = command.type == CommandType.Build
                ? " · 목재 " + GameRules.BuildingCost(command.buildingType) + (GameRules.BuildingIronCost(command.buildingType) > 0 ? " · 철 " + GameRules.BuildingIronCost(command.buildingType) : string.Empty)
                : string.Empty;
            return "유닛 " + command.unitId + " · " + action + " → " + command.target + "  (SP " + GameRules.CommandCost(command.type) + resources + ")";
        }

        public void EndTurnFromHud() => EndTurn();
        public void RetryRulesFromHud() => RetryRules();

        public void ContinueRun()
        {
            commercialHud?.HideMainMenu();
            FocusPlayer(true);
            RefreshHud();
        }

        public void StartNewRun()
        {
            if (HasSavedRun)
            {
                commercialHud?.ShowNewRunConfirmation();
                return;
            }
            ConfirmNewRun();
        }

        public void ConfirmNewRun()
        {
            StopAllCoroutines();
            var previous = ReadValidSaveRaw(TempKey, SaveKey, BackupKey);
            if (!string.IsNullOrEmpty(previous)) PlayerPrefs.SetString(PreviousRunKey, previous);
            PlayerPrefs.DeleteKey(SaveKey);
            PlayerPrefs.DeleteKey(BackupKey);
            PlayerPrefs.DeleteKey(TempKey);
            commands.Clear();
            CancelTargeting(false);
            selectedUnitId = -1;
            selectedBuildingId = -1;
            blockedOnRules = false;
            waitingForRules = false;
            blockReason = "";
            retryRulesAvailableAtRealtime = 0f;
            sessionToken = "";
            sessionReady = false;
            game = WorldGenerator.Create(unchecked(Environment.TickCount ^ (int)DateTime.UtcNow.Ticks));
            ledger.Clear();
            ledger.Add("턴 1 — 새로운 원정이 시작되었습니다.");
            BuildWorld();
            commercialHud?.HideMainMenu();
            FocusPlayer(true);
            Save();
            serviceChecking = true;
            StartCoroutine(CheckServiceHealth());
            RefreshHud();
        }

        public void RestorePreviousRun()
        {
            var restored = SafeReadSave(PreviousRunKey);
            if (restored == null)
            {
                commercialHud?.Toast("복구할 이전 원정이 없습니다.", new Color(1f, .55f, .3f));
                return;
            }

            StopAllCoroutines();
            var current = ReadValidSaveRaw(TempKey, SaveKey, BackupKey);
            var previous = PlayerPrefs.GetString(PreviousRunKey, "");
            if (!string.IsNullOrEmpty(current)) PlayerPrefs.SetString(PreviousRunKey, current);
            else PlayerPrefs.DeleteKey(PreviousRunKey);
            PlayerPrefs.SetString(SaveKey, previous);
            PlayerPrefs.DeleteKey(BackupKey);
            PlayerPrefs.DeleteKey(TempKey);
            PlayerPrefs.Save();

            commands.Clear();
            CancelTargeting(false);
            selectedUnitId = -1;
            selectedBuildingId = -1;
            blockedOnRules = false;
            waitingForRules = false;
            blockReason = "";
            retryRulesAvailableAtRealtime = 0f;
            sessionToken = "";
            sessionReady = false;
            LoadOrNew();
            commercialHud?.HideMainMenu();
            FocusPlayer(true);
            serviceChecking = true;
            StartCoroutine(CheckServiceHealth());
            RefreshHud();
        }

        public void SaveAndReturnToMenu()
        {
            if (game != null && (game.phase == RunPhase.Resolving || waitingForRules && !blockedOnRules))
            {
                commercialHud?.Toast("턴 처리가 끝난 뒤 메뉴로 돌아갈 수 있습니다.", new Color(1f, .78f, .28f));
                return;
            }
            Save();
            commercialHud?.ShowMainMenu(true);
            RefreshHud();
        }

        public void FocusPlayer(bool immediate = false)
        {
            var unit = game?.entities.FirstOrDefault(u => u.factionId == 1 && u.alive);
            var headquarters = game?.buildings.FirstOrDefault(b => b.factionId == 1 && b.type == BuildingType.Headquarters && b.hp > 0);
            if (unit != null) cameraController?.Focus(HexToWorld(unit.position), immediate);
            else if (headquarters != null) cameraController?.Focus(HexToWorld(headquarters.position), immediate);
        }

        public string BuildingName(BuildingType type)
        {
            if (type == BuildingType.Headquarters) return "원정 본부";
            if (type == BuildingType.Warehouse) return "창고";
            if (type == BuildingType.Workshop) return "작업장";
            if (type == BuildingType.Watchtower) return "감시탑";
            if (type == BuildingType.Market) return "시장";
            return "병영";
        }

        public string BuildingBenefit(BuildingType type, int level)
        {
            if (type == BuildingType.Headquarters) return "원정의 생존 거점입니다. 잃으면 복구가 어려워집니다.";
            if (type == BuildingType.Warehouse) return "모든 자원 보유 한도 +" + level * 10;
            if (type == BuildingType.Workshop) return "턴 시작 철 생산 +" + level;
            if (type == BuildingType.Watchtower) return "원정대 시야 반경 +" + level;
            if (type == BuildingType.Market) return "턴 시작 화폐 생산 +" + level;
            return "최대 SP +" + level;
        }

        public bool CanRunDynamic(DynamicActionV1 action)
        {
            if (!CanPayForDynamicAction(action) || !RuleValidator.ValidateDynamicActionCurrentWorldForRuntime(action, game).valid) return false;
            if (!DynamicActionTargeting.RequiresTarget(action)) return RuleVm.ConditionMatches(action.condition, game);
            var actor = SelectedUnit;
            return actor != null && actor.factionId == 1 &&
                   TryGetExecutableDynamicTargets(action, actor.id, out var candidates) && candidates.Count > 0;
        }

        public List<bool> CanRunDynamicsForHud(IReadOnlyList<DynamicActionV1> actions)
        {
            var count = Math.Min(actions?.Count ?? 0, CommercialDynamicActionLimit);
            var results = Enumerable.Repeat(false, count).ToList();
            if (count == 0 || game == null) return results;

            var targeted = new List<int>();
            for (var index = 0; index < count; index++)
            {
                var action = actions[index];
                // HUD refresh is allowed to be optimistic about current-world
                // effect references. Conditions are evaluated below, while the
                // complete reference validation remains on click and immediately
                // before mutation so polling cannot multiply full-world scans.
                if (!CanPayForDynamicAction(action)) continue;
                if (DynamicActionTargeting.RequiresTarget(action)) targeted.Add(index);
                else results[index] = RuleVm.ConditionMatches(action.condition, game);
            }

            var actor = SelectedUnit;
            if (targeted.Count == 0 || actor == null || actor.factionId != 1) return results;
            var targetedActions = targeted.Select(index => actions[index]).ToList();
            if (!DynamicActionTargeting.TryResolveExecutableCandidatesBatch(targetedActions, game, actor.id, out var candidatePages)) return results;
            for (var pageIndex = 0; pageIndex < targeted.Count; pageIndex++)
                results[targeted[pageIndex]] = candidatePages[pageIndex].Count > 0;
            return results;
        }

        public void RunDynamicFromHud(DynamicActionV1 action)
        {
            if (!CanRunDynamic(action))
            {
                commercialHud?.Toast("이 AI 행동은 지금 실행할 수 없습니다.", new Color(1f, .55f, .3f));
                return;
            }
            if (DynamicActionTargeting.RequiresTarget(action))
            {
                BeginDynamicActionTargeting(action);
                return;
            }
            ExecuteDynamicAction(action, null);
        }

        private bool CanPayForDynamicAction(DynamicActionV1 action)
        {
            if (action == null || game == null || game.phase != RunPhase.Planning || game.outcome != RunOutcome.Ongoing || IsBusy || blockedOnRules) return false;
            // Actions enter this list only after RuleValidator validation (or a
            // validated save load). Keep HUD polling O(1) here; targeted actions
            // still run the bounded selected-actor resolver in CanRunDynamic and
            // are resolved again immediately before execution.
            if (!(game.dynamicActions ?? new List<DynamicActionV1>()).Contains(action) ||
                game.turn < action.availableTurn || action.spCost < 0 || action.spCost > 10 ||
                action.cooldown < 0 || action.cooldown > RuleLimits.MaxScheduleDelay ||
                action.resourceAmount < 0 || action.resourceAmount > RuleLimits.MaxEffectMagnitude ||
                !Enum.IsDefined(typeof(ResourceType), action.resourceCost) ||
                action.resourceAmount > 0 && action.resourceCost == ResourceType.None ||
                !DynamicActionTargeting.IsSelectorShapeSafe(action.targetSelector)) return false;
            var player = (game.factions ?? new List<FactionState>()).FirstOrDefault(f => f != null && f.id == 1);
            if (player?.resources == null || player.sp - PlannedSp < action.spCost) return false;
            if (action.resourceAmount > 0)
            {
                if (action.resourceCost == ResourceType.None) return false;
                var reserved = PlanningResourceReservations(null);
                var plannedAmount = reserved.TryGetValue(action.resourceCost, out var amount) ? amount : 0;
                if ((long)plannedAmount + action.resourceAmount > player.resources.Get(action.resourceCost)) return false;
            }
            return RuleValidator.ValidateDynamicActionStructureForRuntime(action, game).valid;
        }

        private bool TryGetExecutableDynamicTargets(DynamicActionV1 action, int actorUnitId, out List<DynamicActionTargetV1> executable)
        {
            return DynamicActionTargeting.TryResolveExecutableCandidates(action, game, actorUnitId, out executable);
        }

        private void BeginDynamicActionTargeting(DynamicActionV1 action)
        {
            var actor = SelectedUnit;
            if (actor == null || actor.factionId != 1 || !TryGetExecutableDynamicTargets(action, actor.id, out var candidates) || candidates.Count == 0)
            {
                commercialHud?.Toast("선택한 아군이 실행할 수 있는 공개 대상이 없습니다.", new Color(1f, .55f, .3f));
                return;
            }
            // The target list is validated once when targeting begins. It is not
            // trusted after this point: click handling and execution both resolve it
            // again against current visibility and IDs.
            CancelTargeting(false);
            targetingDynamicAction = action;
            targetingDynamicActorId = actor.id;
            targetingPrompt = action.name + " · " + DynamicActionTargeting.DescribeSelector(action.targetSelector) + "을 선택하세요.  ·  우클릭/Esc 취소";
            foreach (var position in candidates.Select(candidate => candidate.tile).Distinct())
                targetHighlights.Add(CreateDisc("DynamicTarget_" + position, HexToWorld(position) + Vector3.up * .17f, .7f, new Color(.75f, .42f, 1f, .8f)));
            RefreshHud();
        }

        private bool TryTargetDynamicAction(HexCoord position, DynamicTargetKind clickedKind, int clickedTargetId)
        {
            var action = targetingDynamicAction;
            var actorId = targetingDynamicActorId;
            if (action == null) return false;
            if (!TryGetExecutableDynamicTargets(action, actorId, out var candidates))
            {
                CancelTargeting(false);
                commercialHud?.Toast("대상 검증 예산을 초과해 행동을 취소했습니다.", new Color(1f, .55f, .3f));
                return true;
            }
            var selected = DynamicActionTargeting.FindClickedCandidate(candidates, clickedKind, clickedTargetId, position);
            if (selected == null)
            {
                commercialHud?.Toast("강조된 종류의 유효 대상을 선택하세요.", new Color(1f, .55f, .3f));
                return true;
            }
            ExecuteDynamicAction(action, selected);
            return true;
        }

        private void ExecuteDynamicAction(DynamicActionV1 action, DynamicActionTargetV1 selectedTarget)
        {
            if (!CanPayForDynamicAction(action) || !RuleValidator.ValidateDynamicActionCurrentWorldForRuntime(action, game).valid)
            {
                CancelTargeting(false);
                commercialHud?.Toast("행동 비용·참조 또는 재사용 조건이 더 이상 유효하지 않습니다.", new Color(1f, .55f, .3f));
                return;
            }

            ConditionNode executionCondition;
            List<EffectNode> executionEffects;
            if (DynamicActionTargeting.RequiresTarget(action))
            {
                // Second validation after the click: exact target kind, ID, tile,
                // visibility, distance, binding and condition must still match.
                if (selectedTarget == null || !TryGetExecutableDynamicTargets(action, selectedTarget.actorUnitId, out var currentTargets))
                {
                    commercialHud?.Toast("대상 정보를 다시 확인할 수 없어 실행하지 않았습니다.", new Color(1f, .55f, .3f));
                    return;
                }
                var currentTarget = currentTargets.FirstOrDefault(candidate => DynamicActionTargeting.SameTarget(candidate, selectedTarget));
                if (currentTarget == null || !DynamicActionTargeting.TryBindExecution(action, currentTarget, out executionCondition, out executionEffects) ||
                    !RuleVm.TryConditionMatchesWithinBudget(executionCondition, game, RuleLimits.MaxDynamicTargetConditionWork, out var matches, out _) || !matches)
                {
                    commercialHud?.Toast("대상이 시야·거리·조건 검증을 더 이상 통과하지 않습니다.", new Color(1f, .55f, .3f));
                    return;
                }
                selectedTarget = currentTarget;
            }
            else
            {
                executionCondition = action.condition;
                executionEffects = action.effects;
                if (!RuleVm.ConditionMatches(executionCondition, game)) return;
            }

            GameSnapshotV1 beforeAction;
            try
            {
                beforeAction = JsonConvert.DeserializeObject<GameSnapshotV1>(JsonConvert.SerializeObject(game));
            }
            catch
            {
                beforeAction = null;
            }
            if (beforeAction == null)
            {
                commercialHud?.Toast("행동 안전 복원 지점을 만들지 못했습니다.", new Color(1f, .55f, .3f));
                return;
            }
            var ledgerCount = ledger.Count;
            var player = game.factions.First(f => f.id == 1);
            if (action.resourceAmount > 0 && !player.resources.Spend(action.resourceCost, action.resourceAmount)) return;
            player.sp -= action.spCost;
            var applied = new RuleVm().ApplyValidatedEffects(executionEffects, game, ledger, action.name);
            if (applied != executionEffects.Count)
            {
                game = beforeAction;
                if (ledger.Count > ledgerCount) ledger.RemoveRange(ledgerCount, ledger.Count - ledgerCount);
                CancelTargeting(false);
                commercialHud?.Toast("모든 효과를 적용할 수 없어 행동과 비용을 되돌렸습니다.", new Color(1f, .55f, .3f));
                RefreshHud();
                return;
            }
            var liveAction = (game.dynamicActions ?? new List<DynamicActionV1>()).FirstOrDefault(candidate =>
                candidate != null && (ReferenceEquals(candidate, action) || !string.IsNullOrEmpty(action.id) && string.Equals(candidate.id, action.id, StringComparison.Ordinal)));
            if (liveAction == null)
            {
                game = beforeAction;
                if (ledger.Count > ledgerCount) ledger.RemoveRange(ledgerCount, ledger.Count - ledgerCount);
                CancelTargeting(false);
                commercialHud?.Toast("행동 상태가 변경되어 비용과 효과를 되돌렸습니다.", new Color(1f, .55f, .3f));
                RefreshHud();
                return;
            }
            liveAction.availableTurn = (int)Math.Min(int.MaxValue, (long)game.turn + Math.Max(1, liveAction.cooldown));
            TrimDynamicActions();
            GameRules.CountAction(game, CommandType.Dynamic);
            ledger.Add("AI 행동 실행: " + action.name + (selectedTarget == null ? string.Empty : " · 대상 " + selectedTarget.kind + "@" + selectedTarget.tile) + " — " + action.description);
            var feedbackPosition = selectedTarget?.tile ?? game.entities.FirstOrDefault(u => u.factionId == 1 && u.alive)?.position;
            if (feedbackPosition.HasValue) feedback?.Reward(HexToWorld(feedbackPosition.Value), action.name);
            CancelTargeting(false);
            WorldGenerator.Reveal(game);
            StartCoroutine(AnimateResolvedTurn());
            RenderVisibility();
            Save();
            RefreshHud();
        }

        private void EndTurn()
        {
            if (game == null || game.phase != RunPhase.Planning || blockedOnRules || waitingForRules || game.outcome != RunOutcome.Ongoing) return;
            StartCoroutine(ResolveTurn());
        }

        private void RetryRules()
        {
            if (!blockedOnRules || game == null || game.phase != RunPhase.AwaitingRules) return;
            if (!CanRetryRules)
            {
                commercialHud?.Toast(RetryDelaySeconds + "초 뒤에 다시 요청할 수 있습니다.", new Color(1f, .78f, .28f));
                return;
            }
            if (!IsUsableApiBase(apiBase))
            {
                blockReason = "AI 서비스 주소가 올바르지 않습니다. 설정을 확인해 주세요.";
                RefreshHud();
                return;
            }
            blockedOnRules = false;
            blockReason = "";
            retryRulesAvailableAtRealtime = 0f;
            StartCoroutine(RequestRules());
        }

        private IEnumerator ResolveTurn()
        {
            if (game.phase != RunPhase.Planning || game.outcome != RunOutcome.Ongoing) yield break;
            waitingForRules = true;
            game.phase = RunPhase.Resolving;
            RefreshHud();
            CancelTargeting(false);
            ClearSelection();

            var beforeUnitHp = game.entities.ToDictionary(u => u.id, u => u.hp);
            var beforeBuildingHp = game.buildings.ToDictionary(b => b.id, b => b.hp);
            var player = game.factions.First(f => f.id == 1);
            var beforeResources = new Dictionary<ResourceType, int>
            {
                { ResourceType.Food, player.resources.food }, { ResourceType.Wood, player.resources.wood },
                { ResourceType.Stone, player.resources.stone }, { ResourceType.Iron, player.resources.iron }, { ResourceType.Coin, player.resources.coin }
            };
            var random = new DeterministicRandom(game.seed + game.turn * 7919);
            TurnResolver.Resolve(game, commands, random, ledger);
            commands.Clear();
            EmitResolutionFeedback(beforeUnitHp, beforeBuildingHp, beforeResources);
            WorldGenerator.Reveal(game);
            yield return StartCoroutine(AnimateResolvedTurn());
            RenderVisibility();

            if (game.outcome != RunOutcome.Ongoing)
            {
                game.phase = RunPhase.Terminal;
                waitingForRules = false;
                ledger.Add(game.outcome == RunOutcome.Victory ? "승리 계약을 완수해 원정에 성공했습니다." : "본부와 복구 가능한 아군이 모두 사라져 원정이 끝났습니다.");
                Save();
                RefreshHud();
                yield break;
            }

            game.turn++;
            if (!PrepareTypedStateForCurrentTurn(game))
            {
                ledger.Add("새 턴의 논리 상태를 안전하게 준비하지 못했습니다.");
                BlockOnRules("저장된 세계 규칙의 논리 상태가 충돌합니다. 원정을 다시 불러오거나 지원이 필요합니다.");
                yield break;
            }
            GameRules.PruneRecentActionStats(game);
            game.luck = new DeterministicRandom(game.seed + game.turn * 7919).Next(1, 101);
            UpdateLuckFeedback(true);
            var luckMessage = game.luck >= 70 ? "호운 — 전투와 수렵 보너스가 열립니다." : game.luck >= 35 ? "평운 — 기본 확률이 적용됩니다." : "흉운 — 이동 충돌의 변수를 주의하세요.";
            ledger.Add("턴 " + game.turn + " 행운 " + game.luck + " · " + luckMessage);
            commercialHud?.Toast("☀ 행운 " + game.luck + " · " + luckMessage, game.luck >= 70 ? new Color(.3f, .9f, 1f) : game.luck >= 35 ? new Color(1f, .78f, .28f) : new Color(1f, .38f, .34f));
            game.phase = RunPhase.AwaitingRules;
            game.planningPrepared = false;
            Save();
            yield return StartCoroutine(RequestRules());
        }

        private void EmitResolutionFeedback(Dictionary<int, int> beforeUnitHp, Dictionary<int, int> beforeBuildingHp, Dictionary<ResourceType, int> beforeResources)
        {
            foreach (var unit in game.entities)
            {
                if (!beforeUnitHp.TryGetValue(unit.id, out var hp) || unit.hp >= hp) continue;
                var position = unitVisuals.TryGetValue(unit.id, out var visual) ? visual.transform.position : HexToWorld(unit.position);
                feedback?.Hit(position, hp - unit.hp, !unit.alive || unit.hp <= 0);
            }
            foreach (var building in game.buildings)
            {
                if (!beforeBuildingHp.TryGetValue(building.id, out var hp) || building.hp >= hp) continue;
                var position = buildingVisuals.TryGetValue(building.id, out var visual) ? visual.transform.position : HexToWorld(building.position);
                feedback?.Hit(position, hp - building.hp, building.hp <= 0);
            }
            var player = game.factions.First(f => f.id == 1);
            var gains = new List<string>();
            foreach (var pair in beforeResources)
            {
                var gain = player.resources.Get(pair.Key) - pair.Value;
                if (gain > 0) gains.Add(pair.Key + " +" + gain);
            }
            var unitPosition = game.entities.FirstOrDefault(u => u.factionId == 1 && u.alive)?.position;
            if (gains.Count > 0 && unitPosition.HasValue) feedback?.Reward(HexToWorld(unitPosition.Value), string.Join("  ", gains));
        }

        /// <summary>
        /// 턴 해결 후 유닛/건물을 트윈으로 부드럽게 이동시킨다.
        /// </summary>
        private IEnumerator AnimateResolvedTurn()
        {
            // 새 유닛/건물 생성
            foreach (var unit in game.entities.Where(x => x.alive))
            {
                if (!unitVisuals.ContainsKey(unit.id)) SpawnUnitVisual(unit);
            }
            foreach (var building in game.buildings.Where(b => b.hp > 0))
            {
                if (!buildingVisuals.ContainsKey(building.id)) SpawnBuildingVisual(building);
            }

            // 기존 유닛을 트윈 이동
            foreach (var unit in game.entities.Where(x => x.alive))
            {
                if (unitMovers.TryGetValue(unit.id, out var mover))
                {
                    var target = HexToWorld(unit.position) + Vector3.up * .12f;
                    if (Vector3.Distance(mover.transform.position, target) > 0.01f)
                    {
                        mover.MoveTo(target);
                    }
                }
                if (unitVisuals.TryGetValue(unit.id, out var marker))
                {
                    var ring = marker.transform.Find("FactionRing_" + unit.id);
                    if (ring != null) Tint(ring.gameObject, unit.factionId == 1 ? new Color(0.16f, 0.75f, 1f, 0.9f) : unit.factionId == 2 ? new Color(1f, 0.2f, 0.22f, 0.9f) : new Color(1f, 0.75f, 0.2f, 0.9f));
                    var label = marker.transform.Find("UnitLabel_" + unit.id)?.GetComponent<TextMesh>();
                    if (label != null) label.text = unit.factionId == 1 ? "아" : unit.factionId == 2 ? "적" : "중";
                }
            }

            // 건물도 트윈 (레벨업 시 살짝)
            foreach (var building in game.buildings.Where(b => b.hp > 0))
            {
                if (buildingVisuals.TryGetValue(building.id, out var visual))
                {
                    var target = HexToWorld(building.position) + Vector3.up * .1f;
                    var mover = visual.GetComponent<TweenMover>();
                    if (mover == null) mover = visual.AddComponent<TweenMover>();
                    if (Vector3.Distance(visual.transform.position, target) > 0.01f)
                    {
                        mover.MoveTo(target);
                    }
                }
            }

            // 이동 애니메이션 완료 대기
            yield return new WaitForSeconds(0.5f);
            foreach (var unit in game.entities.Where(u => !u.alive || u.hp <= 0)) if (unitVisuals.TryGetValue(unit.id, out var visual)) visual.SetActive(false);
            foreach (var building in game.buildings.Where(b => b.hp <= 0)) if (buildingVisuals.TryGetValue(building.id, out var visual)) visual.SetActive(false);
        }

        private IEnumerator RequestRules()
        {
            waitingForRules = true;
            game.phase = RunPhase.AwaitingRules;
            RefreshHud();
            if (!IsUsableApiBase(apiBase))
            {
                ledger.Add("AI 서비스 주소가 설정되지 않았습니다. OnlyMyGameConfig.json의 apiBaseUrl을 NAS HTTPS 주소로 바꾼 뒤 재시도하세요.");
                BlockOnRules("AI 서비스 주소가 설정되지 않았습니다.");
                yield break;
            }
            if (compatibilityChecked && !serviceCompatible)
            {
                BlockOnRules("AI 서버 버전이 이 게임 빌드와 맞지 않습니다. 서버와 게임을 같은 릴리스로 업데이트해 주세요.");
                yield break;
            }
            UnityWebRequest request = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                yield return StartCoroutine(EnsureSession(attempt > 0));
                if (!sessionReady)
                {
                    serviceOnline = false;
                    serviceChecking = false;
                    serviceStatus = "AI 인증 실패 · 재시도 필요";
                    ledger.Add("AI 세션 발급 실패: " + sessionFailure);
                    BlockOnRules(sessionFailure);
                    yield break;
                }

                request = new UnityWebRequest(apiBase.TrimEnd('/') + "/v1/rules/generate", "POST");
                request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(game)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + sessionToken);
                request.SetRequestHeader("Idempotency-Key", game.runId + "-" + game.turn);
                request.SetRequestHeader("X-Unity-Version", Application.unityVersion);
                request.SetRequestHeader("X-Rules-Compatibility", expectedCompatibilityVersion);
                request.timeout = 20;
                yield return request.SendWebRequest();
                if (request.responseCode != 401 || attempt > 0) break;

                request.Dispose();
                request = null;
                sessionToken = "";
                sessionReady = false;
            }
            if (request == null)
            {
                BlockOnRules("AI 규칙 요청을 시작하지 못했습니다.");
                yield break;
            }
            if (request.result != UnityWebRequest.Result.Success)
            {
                var failure = DescribeServiceFailure(request, "AI 규칙 생성에 실패했습니다.");
                serviceOnline = false;
                serviceChecking = false;
                serviceStatus = "AI 오프라인 · 재시도 필요";
                ledger.Add("AI 규칙 생성 실패: " + failure);
                request.Dispose();
                BlockOnRules(failure);
                yield break;
            }
            var responseText = request.downloadHandler.text;
            request.Dispose();
            RuleSetV1 set;
            RuleValidationResult validation;
            try
            {
                set = JsonConvert.DeserializeObject<RuleSetV1>(responseText);
            }
            catch (Exception ex)
            {
                ledger.Add("AI 응답 해석 실패: " + ex.GetType().Name);
                BlockOnRules("AI 응답을 안전하게 해석하지 못했습니다.");
                yield break;
            }
            var expectedRequestId = game.runId + "-" + game.turn;
            if (set == null || !string.Equals(set.requestId, expectedRequestId, StringComparison.Ordinal) || set.applyTurn != game.turn)
            {
                ledger.Add("AI 응답 식별자 또는 적용 턴이 현재 요청과 일치하지 않습니다.");
                BlockOnRules("AI 응답이 현재 원정 턴과 일치하지 않습니다.");
                yield break;
            }
            try
            {
                validation = RuleValidator.Validate(set, game);
            }
            catch (Exception ex)
            {
                ledger.Add("AI 응답 안전성 검사 실패: " + ex.GetType().Name);
                BlockOnRules("AI 응답을 안전하게 검사하지 못했습니다.");
                yield break;
            }
            if (!validation.valid)
            {
                ledger.Add("AI 응답이 안전성 검증을 통과하지 못했습니다: " + string.Join(", ", validation.errors));
                BlockOnRules("AI 응답이 안전성 검증을 통과하지 못했습니다.");
                yield break;
            }
            blockedOnRules = false;
            blockReason = "";
            serviceOnline = true;
            serviceChecking = false;
            serviceStatus = "AI 연결됨 · 안전 검증 완료";
            retryRulesAvailableAtRealtime = 0f;
            var announcedRules = set.changes ?? new List<RuleNodeV1>();
            var announcedContracts = set.victoryContracts ?? new List<VictoryContractV1>();
            foreach (var rule in announcedRules)
            {
                var replacing = game.activeRules.Any(existing => existing != null && !string.IsNullOrEmpty(rule.id) && existing.id == rule.id);
                game.activeRules.RemoveAll(existing => existing != null && !string.IsNullOrEmpty(rule.id) && existing.id == rule.id);
                game.activeRules.Add(rule);
                ledger.Add((replacing ? "규칙 수정: " : "새 규칙: ") + rule.name + " — " + rule.description);
            }
            GameRules.PruneExpiredRules(game);
            foreach (var action in set.actions ?? new List<DynamicActionV1>())
            {
                game.dynamicActions.RemoveAll(existing => existing != null && !string.IsNullOrEmpty(action.id) && existing.id == action.id);
                game.dynamicActions.Add(action);
            }
            TrimDynamicActions();
            foreach (var contract in announcedContracts)
            {
                var previousContract = game.victoryContracts.FirstOrDefault(existing => existing != null && !string.IsNullOrEmpty(contract.id) && existing.id == contract.id);
                game.victoryContracts.RemoveAll(existing => existing != null && !string.IsNullOrEmpty(contract.id) && existing.id == contract.id);
                game.victoryContracts.Add(contract);
                var warningOnly = previousContract != null && contract.replaceWarningTurn == game.turn &&
                                  string.Equals(previousContract.title, contract.title, StringComparison.Ordinal) &&
                                  string.Equals(previousContract.progressKey, contract.progressKey, StringComparison.OrdinalIgnoreCase) &&
                                  previousContract.target == contract.target;
                ledger.Add(warningOnly
                    ? "승리 계약 교체 예고: " + contract.title + " — 다음 턴 이후 다른 계약으로 바뀔 수 있습니다."
                    : (previousContract == null ? "새 승리 계약: " : "승리 계약 갱신: ") + contract.title + " — " + contract.description);
            }
            game.phase = RunPhase.Planning;
            game.planningPrepared = false;
            TurnResolver.BeginPlanning(game, ledger);
            WorldGenerator.Reveal(game);
            yield return StartCoroutine(AnimateResolvedTurn());
            RenderVisibility();
            waitingForRules = false;
            Save();
            RefreshHud();
            commercialHud?.ShowRuleAnnouncement(set.koreanSummary, announcedRules, announcedContracts);
        }

        private IEnumerator EnsureSession(bool forceRefresh)
        {
            sessionReady = false;
            sessionFailure = "";
            if (!forceRefresh && !string.IsNullOrWhiteSpace(sessionToken) && Time.realtimeSinceStartup < sessionValidUntilRealtime - 30f)
            {
                sessionReady = true;
                yield break;
            }

            sessionToken = "";
            var body = JsonConvert.SerializeObject(new { runId = game.runId });
            var request = new UnityWebRequest(apiBase.TrimEnd('/') + "/v1/sessions", "POST");
            request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-Unity-Version", Application.unityVersion);
            request.SetRequestHeader("X-Rules-Compatibility", expectedCompatibilityVersion);
            request.timeout = 8;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                sessionFailure = DescribeServiceFailure(request, "AI 세션을 만들지 못했습니다.");
                request.Dispose();
                yield break;
            }

            try
            {
                var response = JsonConvert.DeserializeObject<SessionResponse>(request.downloadHandler.text);
                if (response == null || string.IsNullOrWhiteSpace(response.token) || response.expiresInSeconds < 60)
                    throw new JsonException("INVALID_SESSION_RESPONSE");
                sessionToken = response.token;
                sessionValidUntilRealtime = Time.realtimeSinceStartup + Mathf.Clamp(response.expiresInSeconds, 60, 86400);
                sessionReady = true;
            }
            catch (Exception)
            {
                sessionFailure = "AI 세션 응답이 올바르지 않습니다.";
            }
            request.Dispose();
        }

        private string DescribeServiceFailure(UnityWebRequest request, string fallback)
        {
            var retryAfter = 0;
            try
            {
                var response = JsonConvert.DeserializeObject<ServiceErrorResponse>(request.downloadHandler?.text ?? "");
                retryAfter = Math.Max(0, response?.retryAfterSeconds ?? 0);
            }
            catch (Exception)
            {
                retryAfter = 0;
            }
            if (retryAfter > 0) retryRulesAvailableAtRealtime = Time.realtimeSinceStartup + retryAfter;
            switch (request.responseCode)
            {
                case 401: return "AI 세션이 만료되었습니다. 다시 시도해 주세요.";
                case 409: return "이 턴의 규칙을 처리 중입니다. 잠시 후 다시 시도해 주세요.";
                case 413: return "원정 상태가 서버 전송 한도를 넘었습니다.";
                case 429: return retryAfter > 0
                    ? "AI 요청 복구 대기 중입니다. " + retryAfter + "초 후 다시 시도해 주세요."
                    : "AI 요청이 너무 잦습니다. 잠시 후 다시 시도해 주세요.";
                case 503: return "AI 규칙 서비스가 잠시 중단되었습니다. 잠시 후 다시 시도해 주세요.";
                default:
                    return string.IsNullOrWhiteSpace(request.error) ? fallback : fallback + " " + request.error;
            }
        }

        private void TrimDynamicActions()
        {
            if (game?.dynamicActions == null) return;
            game.dynamicActions.RemoveAll(action => action == null);
            while (game.dynamicActions.Count > CommercialDynamicActionLimit)
            {
                var retired = game.dynamicActions
                    .OrderBy(action => action.availableTurn)
                    .ThenBy(action => action.id ?? "", StringComparer.Ordinal)
                    .First();
                game.dynamicActions.Remove(retired);
                ledger.Add("특수 행동 교체: " + (string.IsNullOrWhiteSpace(retired.name) ? "이름 없는 행동" : retired.name));
            }
        }

        private void BlockOnRules(string reason)
        {
            blockedOnRules = true;
            waitingForRules = true;
            game.phase = RunPhase.AwaitingRules;
            blockReason = reason;
            Save();
            RefreshHud();
        }

        private IEnumerator CheckServiceHealth()
        {
            serviceChecking = true;
            compatibilityChecked = false;
            serviceCompatible = false;
            serviceStatus = "AI 연결 확인 중";
            RefreshHud();
            if (!IsUsableApiBase(apiBase))
            {
                serviceChecking = false;
                serviceOnline = false;
                serviceStatus = "AI 주소 설정 필요";
                RefreshHud();
                yield break;
            }
            var request = UnityWebRequest.Get(apiBase.TrimEnd('/') + "/health");
            request.timeout = 8;
            yield return request.SendWebRequest();
            serviceChecking = false;
            if (request.result != UnityWebRequest.Result.Success)
            {
                serviceOnline = false;
                serviceStatus = "AI 연결 안 됨";
                request.Dispose();
                RefreshHud();
                yield break;
            }
            try
            {
                var health = JsonConvert.DeserializeObject<HealthResponse>(request.downloadHandler.text);
                compatibilityChecked = true;
                serviceCompatible = health != null
                    && string.Equals(health.status, "ok", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(health.apiVersion, expectedApiVersion, StringComparison.Ordinal)
                    && string.Equals(health.compatibilityVersion, expectedCompatibilityVersion, StringComparison.Ordinal);
                serviceOnline = serviceCompatible;
                serviceStatus = serviceCompatible ? "AI 연결됨" : "AI 서버 버전 불일치";
            }
            catch (Exception)
            {
                compatibilityChecked = true;
                serviceCompatible = false;
                serviceOnline = false;
                serviceStatus = "AI 상태 응답 오류";
            }
            request.Dispose();
            RefreshHud();
        }

        private static bool IsUsableApiBase(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !uri.Host.EndsWith(".example", StringComparison.OrdinalIgnoreCase);

        // ==================== HUD ====================

        private void RefreshHud()
        {
            if (game == null) return;
            commercialHud?.Render();
            if (hudResources == null) return;
            var player = game.factions.First(f => f.id == 1);
            var goal = game.victoryContracts.LastOrDefault();
            var goalText = goal == null ? "현재 목표: AI 규칙 응답 대기" : "목표: " + goal.title + " (" + GameRules.Progress(game, goal.progressKey) + "/" + goal.target + ")";
            hudResources.text = "턴 " + game.turn + "   행운 " + game.luck + "   SP " + player.sp + "/" + player.maxSp +
                "   🍞" + player.resources.food + "   🪵" + player.resources.wood + "   🪨" + player.resources.stone +
                "   ⚙️" + player.resources.iron + "   🪙" + player.resources.coin;
            if (hudGoal != null) hudGoal.text = goalText;
            var recent = ledger.Skip(Math.Max(0, ledger.Count - 6)).ToList();
            if (hudLog != null) hudLog.text = string.Join("\n", recent);
            if (endTurnButtonText != null)
            {
                endTurnButtonText.text = waitingForRules ? "AI 규칙 생성 중…" : "명령 확정 · 턴 종료";
            }
            if (blockPanel != null)
            {
                blockPanel.SetActive(blockedOnRules);
                if (blockedOnRules) blockText.text = "AI 규칙 생성 차단됨\n" + blockReason;
            }
        }

        // ==================== 저장 ====================

        private static string ReadValidSaveRaw(params string[] keys)
        {
            foreach (var key in keys ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(key) || SafeReadSave(key) == null) continue;
                var raw = PlayerPrefs.GetString(key, "");
                if (!string.IsNullOrEmpty(raw)) return raw;
            }
            return "";
        }

        private static GameSnapshotV1 SafeReadSave(string key)
        {
            try
            {
                var raw = PlayerPrefs.GetString(key, "");
                if (string.IsNullOrEmpty(raw)) return null;
                var envelope = JsonConvert.DeserializeObject<SaveEnvelope>(raw);
                if (envelope == null || envelope.schemaVersion < 1 || envelope.schemaVersion > 2 || string.IsNullOrEmpty(envelope.payload) || envelope.checksum != Hash(envelope.payload)) return null;
                var snapshot = JsonConvert.DeserializeObject<GameSnapshotV1>(envelope.payload);
                if (!NormalizeSnapshot(snapshot)) return null;
                // A matching checksum proves only that the payload was written
                // intact. It does not prove that migrated state is semantically
                // safe to execute (unique IDs, valid faction references, bounded
                // rules, and so on). Reject it so recovery can try the backup.
                if (!RuleValidator.ValidateSnapshot(snapshot).valid) return null;
                return snapshot;
            }
            catch
            {
                return null;
            }
        }

        private static GameSnapshotV1 RecoverBestSave()
        {
            // A valid pending generation is the newest one: it is flushed before the
            // primary slot is replaced. Promote it on startup so an interruption
            // between those two writes cannot silently roll the run back a turn.
            var pending = SafeReadSave(TempKey);
            if (pending != null)
            {
                var pendingRaw = PlayerPrefs.GetString(TempKey, "");
                var primaryRaw = PlayerPrefs.GetString(SaveKey, "");
                if (SafeReadSave(SaveKey) != null && !string.Equals(primaryRaw, pendingRaw, StringComparison.Ordinal))
                    PlayerPrefs.SetString(BackupKey, primaryRaw);
                PlayerPrefs.SetString(SaveKey, pendingRaw);
                PlayerPrefs.DeleteKey(TempKey);
                PlayerPrefs.Save();
                return pending;
            }

            if (PlayerPrefs.HasKey(TempKey))
            {
                PlayerPrefs.DeleteKey(TempKey);
                PlayerPrefs.Save();
            }

            var primary = SafeReadSave(SaveKey);
            if (primary != null) return primary;

            var backup = SafeReadSave(BackupKey);
            if (backup == null) return null;

            // Self-heal a corrupt/missing primary slot after a successful backup read.
            PlayerPrefs.SetString(SaveKey, PlayerPrefs.GetString(BackupKey, ""));
            PlayerPrefs.Save();
            return backup;
        }

        private static bool NormalizeSnapshot(GameSnapshotV1 snapshot)
        {
            if (snapshot == null || string.IsNullOrEmpty(snapshot.runId) || snapshot.map == null || snapshot.map.Count == 0 || snapshot.factions == null || snapshot.factions.All(f => f == null || f.id != 1)) return false;
            if (snapshot.entities == null) snapshot.entities = new List<UnitState>();
            if (snapshot.buildings == null) snapshot.buildings = new List<BuildingState>();
            if (snapshot.actionStats == null) snapshot.actionStats = new List<ActionStat>();
            if (snapshot.activeRules == null) snapshot.activeRules = new List<RuleNodeV1>();
            if (snapshot.victoryContracts == null) snapshot.victoryContracts = new List<VictoryContractV1>();
            if (snapshot.dynamicActions == null) snapshot.dynamicActions = new List<DynamicActionV1>();
            if (snapshot.ruleState == null) snapshot.ruleState = new List<RuleStateEntry>();
            if (snapshot.typedRuleState == null) snapshot.typedRuleState = new List<TypedRuleStateEntryV1>();
            if (snapshot.recentActionStats == null) snapshot.recentActionStats = new List<ActionTurnStatV1>();
            if (snapshot.journal == null) snapshot.journal = new List<string>();
            snapshot.turn = Math.Max(1, snapshot.turn);
            snapshot.luck = Math.Max(1, Math.Min(100, snapshot.luck));
            foreach (var faction in snapshot.factions.Where(f => f != null))
            {
                if (faction.resources == null) faction.resources = new ResourceBag();
                faction.maxSp = Math.Max(3, Math.Min(30, faction.maxSp));
                faction.sp = Math.Max(0, Math.Min(faction.maxSp, faction.sp));
                faction.relationToPlayer = Math.Max(-100, Math.Min(100, faction.relationToPlayer));
            }
            if (snapshot.phase == RunPhase.Resolving)
            {
                snapshot.turn = snapshot.turn == int.MaxValue ? int.MaxValue : snapshot.turn + 1;
                snapshot.phase = RunPhase.AwaitingRules;
                snapshot.planningPrepared = false;
            }
            GameRules.PruneRecentActionStats(snapshot);
            if (snapshot.outcome != RunOutcome.Ongoing) snapshot.phase = RunPhase.Terminal;
            if (!PrepareTypedStateForCurrentTurn(snapshot)) return false;
            return true;
        }

        private static bool PrepareTypedStateForCurrentTurn(GameSnapshotV1 snapshot)
        {
            if (snapshot == null || !RuleExpressionRuntime.CanonicalizeStoredStateScopeIds(snapshot)) return false;
            return snapshot.outcome != RunOutcome.Ongoing || RuleExpressionRuntime.EnsureActiveDefinitions(snapshot);
        }

        private void Save()
        {
            if (game == null) return;
            game.journal = ledger.Skip(Math.Max(0, ledger.Count - 100)).ToList();
            var payload = JsonConvert.SerializeObject(game);
            var envelope = JsonConvert.SerializeObject(new SaveEnvelope { payload = payload, checksum = Hash(payload) });
            PlayerPrefs.SetString(TempKey, envelope);
            PlayerPrefs.Save();
            if (SafeReadSave(TempKey) == null) return;
            var previous = PlayerPrefs.GetString(SaveKey, "");
            if (!string.IsNullOrEmpty(previous) && SafeReadSave(SaveKey) != null) PlayerPrefs.SetString(BackupKey, previous);
            PlayerPrefs.SetString(SaveKey, envelope);
            PlayerPrefs.DeleteKey(TempKey);
            PlayerPrefs.Save();
        }

        private static string Hash(string value)
        {
            unchecked { uint hash = 2166136261; foreach (var c in value) { hash ^= c; hash *= 16777619; } return hash.ToString("X8"); }
        }

        private void Update()
        {
            // 클릭 선택 처리
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (Input.GetMouseButtonDown(0) && (eventSystem == null || !eventSystem.IsPointerOverGameObject()))
            {
                HandleWorldClick();
            }
            // 우클릭으로 선택 해제
            if (Input.GetMouseButtonDown(1))
            {
                if (IsTargeting) CancelTargeting();
                else ClearSelection();
            }
            // 선택 링 갱신
            if (selectionRing != null && selectionRing.activeSelf)
            {
                if (selectedUnitId >= 0 && unitVisuals.TryGetValue(selectedUnitId, out var uv))
                {
                    selectionRing.transform.position = uv.transform.position + Vector3.up * 0.05f;
                }
                else if (selectedBuildingId >= 0 && buildingVisuals.TryGetValue(selectedBuildingId, out var bv))
                {
                    selectionRing.transform.position = bv.transform.position + Vector3.up * 0.05f;
                }
            }
        }

        private void HandleWorldClick()
        {
            if (Camera.main == null) return;
            var ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (targetingDynamicAction != null)
            {
                TryTargetDynamicActionFromRay(ray);
                return;
            }
            if (Physics.Raycast(ray, out var hit, 200f))
            {
                // 버블 클릭이 우선
                var bubble = hit.collider.GetComponentInParent<BubbleClicker>();
                if (bubble != null)
                {
                    bubble.Trigger();
                    return;
                }
                var clicker = hit.collider.GetComponentInParent<UnitClickHandler>();
                if (clicker != null)
                {
                    var unit = game.entities.FirstOrDefault(u => u.id == clicker.UnitId);
                    if (unit != null && TryTarget(unit.position, DynamicTargetKind.Unit, unit.id)) return;
                    SelectUnit(clicker.UnitId);
                    return;
                }
                var bClicker = hit.collider.GetComponentInParent<BuildingClickHandler>();
                if (bClicker != null)
                {
                    var building = game.buildings.FirstOrDefault(b => b.id == bClicker.BuildingId);
                    if (building != null && TryTarget(building.position, DynamicTargetKind.Building, building.id)) return;
                    SelectBuilding(bClicker.BuildingId);
                    return;
                }
                var tileClicker = hit.collider.GetComponentInParent<TileClickHandler>();
                if (tileClicker != null)
                {
                    if (TryTarget(tileClicker.Position, DynamicTargetKind.Tile)) return;
                    ClearSelection();
                    return;
                }
            }
            ClearSelection();
        }

        private void TryTargetDynamicActionFromRay(Ray ray)
        {
            var action = targetingDynamicAction;
            var actorId = targetingDynamicActorId;
            if (action == null) return;
            if (!TryGetExecutableDynamicTargets(action, actorId, out var candidates))
            {
                CancelTargeting(false);
                commercialHud?.Toast("대상 검증 예산을 초과해 행동을 취소했습니다.", new Color(1f, .55f, .3f));
                return;
            }

            var hits = Physics.RaycastAll(ray, 200f).OrderBy(value => value.distance).Take(64).ToList();
            HexCoord? clickedTile = null;
            var clickedTargetId = 0;
            foreach (var hit in hits)
            {
                HexCoord? hitTile = null;
                var hitTargetId = 0;
                var tileClicker = hit.collider.GetComponentInParent<TileClickHandler>();
                if (tileClicker != null) hitTile = tileClicker.Position;
                var unitClicker = hit.collider.GetComponentInParent<UnitClickHandler>();
                if (unitClicker != null)
                {
                    var unit = game.entities.FirstOrDefault(value => value.id == unitClicker.UnitId);
                    if (unit != null)
                    {
                        hitTile = unit.position;
                        if (action.targetSelector.kind == DynamicTargetKind.Unit) hitTargetId = unitClicker.UnitId;
                    }
                }
                var buildingClicker = hit.collider.GetComponentInParent<BuildingClickHandler>();
                if (buildingClicker != null)
                {
                    var building = game.buildings.FirstOrDefault(value => value.id == buildingClicker.BuildingId);
                    if (building != null)
                    {
                        hitTile = building.position;
                        if (action.targetSelector.kind == DynamicTargetKind.Building) hitTargetId = buildingClicker.BuildingId;
                    }
                }
                if (!hitTile.HasValue) continue;
                if (!clickedTile.HasValue) clickedTile = hitTile;
                if (!hitTile.Value.Equals(clickedTile.Value)) continue;
                if (hitTargetId > 0 && clickedTargetId == 0) clickedTargetId = hitTargetId;
            }

            var selected = clickedTile.HasValue
                ? DynamicActionTargeting.FindClickedCandidate(
                    candidates,
                    action.targetSelector.kind,
                    clickedTargetId,
                    clickedTile.Value)
                : null;
            if (selected != null)
            {
                ExecuteDynamicAction(action, selected);
                return;
            }
            commercialHud?.Toast("강조된 종류의 유효 대상을 선택하세요.", new Color(1f, .55f, .3f));
        }
    }

    /// <summary>
    /// 유닛 클릭 감지 헬퍼.
    /// </summary>
    public sealed class UnitClickHandler : MonoBehaviour
    {
        private int unitId;

        public int UnitId => unitId;

        public void Init(GameController c, int id)
        {
            unitId = id;
        }
    }

    /// <summary>
    /// 건물 클릭 감지 헬퍼.
    /// </summary>
    public sealed class BuildingClickHandler : MonoBehaviour
    {
        private int buildingId;

        public int BuildingId => buildingId;

        public void Init(GameController c, int id)
        {
            buildingId = id;
        }
    }

    /// <summary>Selectable hex hit target used by the controller's single raycast path.</summary>
    public sealed class TileClickHandler : MonoBehaviour
    {
        private HexCoord position;
        public HexCoord Position => position;

        public void Init(GameController c, HexCoord value)
        {
            position = value;
        }
    }
}
