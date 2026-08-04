using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using OnlyMyGame.Core;
using UnityEngine;
using UnityEngine.Networking;

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
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0), tags = new List<string> { "탐험가" } });
            game.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(5, -2), tags = new List<string> { "스켈레톤" } });
            game.entities.Add(new UnitState { id = 3, factionId = 3, position = new HexCoord(-4, 3), tags = new List<string> { "상인" } });
            game.entities.Add(new UnitState { id = 4, factionId = 2, position = new HexCoord(4, -1), tags = new List<string> { "스켈레톤" } });
            game.entities.Add(new UnitState { id = 5, factionId = 3, position = new HexCoord(-3, 4), tags = new List<string> { "상인" } });
            game.buildings.Add(new BuildingState { id = 1, factionId = 1, position = new HexCoord(0, 0), type = BuildingType.Headquarters });
            game.buildings.Add(new BuildingState { id = 2, factionId = 2, position = new HexCoord(5, -2), type = BuildingType.Headquarters });
            game.activeRules.Add(new RuleNodeV1 { id = "welcome", name = "개척의 첫걸음", description = "매 턴 시작에 식량 1을 얻습니다.", trigger = OnlyMyGame.Core.EventType.TurnStart, durationTurns = 30, appliedTurn = 1, effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } }, worldCue = "깃발" });
            Reveal(game); return game;
        }
        public static void Reveal(GameSnapshotV1 game)
        {
            var player = game.entities.FirstOrDefault(x => x.factionId == 1 && x.alive);
            if (player == null) return; // 플레이어 유닛이 없으면 시야 갱신 없음
            var range = GameRules.VisibilityRange(game, 1);
            foreach (var tile in game.map) { tile.visible = tile.position.Distance(player.position) <= range; tile.explored |= tile.visible; }
        }
    }

    public sealed class GameController : MonoBehaviour
    {
        private GameSnapshotV1 game; private readonly RuleVm vm = new RuleVm(); private readonly List<string> ledger = new List<string>(); private readonly List<PlannedCommand> commands = new List<PlannedCommand>();
        private Dictionary<HexCoord, GameObject> visuals = new Dictionary<HexCoord, GameObject>(); private bool waitingForRules; private bool blockedOnRules; private string blockReason = ""; private string apiBase; private GUIStyle header, body, button; private GamePresentationCatalog presentation;
        [Serializable] private sealed class SaveEnvelope { public int schemaVersion = 1; public string payload; public string checksum; }
        [Serializable] private sealed class ClientConfig { public string apiBaseUrl; }
        private const string SaveKey = "onlymygame.autosave.v1"; private const string BackupKey = "onlymygame.autosave.v1.backup";
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Create() { if (FindFirstObjectByType<GameController>() == null) new GameObject("OnlyMyGame").AddComponent<GameController>(); }
        private void Awake()
        {
            var config = Resources.Load<TextAsset>("OnlyMyGameConfig");
            var configuredApiBase = config == null ? "" : JsonUtility.FromJson<ClientConfig>(config.text).apiBaseUrl ?? "";
            apiBase = IsUsableApiBase(configuredApiBase) ? configuredApiBase : Environment.GetEnvironmentVariable("ONLYMYGAME_API_URL") ?? "";
            presentation = Resources.Load<GamePresentationCatalog>("OnlyMyGamePresentation"); LoadOrNew();
        }
        private void BuildGui()
        {
            var font = Resources.Load<Font>("Fonts/NanumGothic-Regular");
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            header = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            body = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, normal = { textColor = Color.white } };
            button = new GUIStyle(GUI.skin.button) { fontSize = 14 };
            if (font != null) { header.font = font; body.font = font; button.font = font; }
        }
        private void LoadOrNew() { try { var saved = ReadSave(SaveKey) ?? ReadSave(BackupKey); game = saved ?? WorldGenerator.Create(Environment.TickCount); } catch { game = WorldGenerator.Create(Environment.TickCount); } BuildWorld(); ledger.Add("턴 " + game.turn + " — 세계가 열렸습니다."); }
        private void BuildWorld()
        {
            foreach (var item in visuals.Values) Destroy(item); visuals.Clear();
            foreach (var tile in game.map)
            {
                var prefab = tile.terrain == "강" ? presentation?.waterTile : presentation?.grassTile;
                var go = Spawn(prefab, PrimitiveType.Cylinder); go.name = "Hex_" + tile.position; go.transform.position = HexToWorld(tile.position); go.transform.localScale = prefab == null ? new Vector3(.92f, .08f, .92f) : Vector3.one * .82f;
                var tint = tile.terrain == "강" ? new Color(.35f, .65f, 1f) : tile.terrain == "숲" ? new Color(.38f, .72f, .36f) : tile.terrain == "언덕" ? new Color(.76f, .58f, .32f) : new Color(.64f, .86f, .45f); Tint(go, tint); visuals[tile.position] = go;
            }
            if (Camera.main == null)
            {
                var cam = new GameObject("Quarter Camera").AddComponent<Camera>(); cam.transform.position = new Vector3(0, 12, -10); cam.transform.rotation = Quaternion.Euler(52, 0, 0); cam.orthographic = true; cam.orthographicSize = 5.4f; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(.035f, .07f, .12f);
                var light = new GameObject("World Sun").AddComponent<Light>(); light.type = LightType.Directional; light.color = new Color(1f, .91f, .73f); light.intensity = 1.35f; light.transform.rotation = Quaternion.Euler(50, -35, 0);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat; RenderSettings.ambientLight = new Color(.28f, .37f, .48f);
            }
            RenderVisibility();
        }
        private Vector3 HexToWorld(HexCoord p) => new Vector3((p.q + p.r * .5f) * 1.65f, 0, p.r * 1.43f);
        private static void Tint(GameObject visual, Color tint) { foreach (var renderer in visual.GetComponentsInChildren<Renderer>()) { renderer.material.color = tint; } }
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
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader != null)
            {
                foreach (var renderer in go.GetComponentsInChildren<Renderer>())
                {
                    renderer.material = new Material(shader);
                }
            }
            return go;
        }
        private void RenderVisibility()
        {
            // 행운에 따른 월드 조명 표현 (행운이 높을수록 따뜻하고 밝게)
            if (Camera.main != null)
            {
                var sun = GameObject.Find("World Sun");
                if (sun != null)
                {
                    var light = sun.GetComponent<Light>();
                    if (light != null)
                    {
                        var luckFactor = game.luck / 100f;
                        light.intensity = 1.1f + luckFactor * 0.6f;
                        light.color = Color.Lerp(new Color(.7f, .75f, .9f), new Color(1f, .91f, .73f), luckFactor);
                    }
                }
            }
            foreach (var tile in game.map) if (visuals.TryGetValue(tile.position, out var go)) go.SetActive(tile.explored);
            foreach (var building in game.buildings)
            {
                var prefab = building.type == BuildingType.Headquarters ? (building.factionId == 1 ? presentation?.playerHeadquarters : presentation?.enemyHeadquarters) : presentation?.settlement;
                var landmark = GameObject.Find("Building_" + building.id) ?? Spawn(prefab, PrimitiveType.Cube); landmark.name = "Building_" + building.id; landmark.transform.position = HexToWorld(building.position) + Vector3.up * .1f; landmark.transform.localScale = Vector3.one * (.38f + building.level * .04f); Tint(landmark, building.factionId == 1 ? new Color(.55f, .82f, 1f) : new Color(1f, .42f, .42f)); landmark.SetActive(game.map.First(t => t.position.Equals(building.position)).visible);
            }
            foreach (var unit in game.entities.Where(x => x.alive))
            {
                var prefab = unit.factionId == 1 ? presentation?.playerUnit : unit.factionId == 2 ? presentation?.skeletonUnit : presentation?.neutralUnit;
                var marker = GameObject.Find("Unit_" + unit.id) ?? Spawn(prefab, PrimitiveType.Capsule); marker.name = "Unit_" + unit.id; marker.transform.position = HexToWorld(unit.position) + Vector3.up * .12f; marker.transform.localScale = Vector3.one * .42f; Tint(marker, unit.factionId == 1 ? new Color(.4f, .9f, 1f) : unit.factionId == 2 ? new Color(1f, .32f, .32f) : new Color(1f, .82f, .3f)); marker.SetActive(game.map.First(t => t.position.Equals(unit.position)).visible);
                var label = GameObject.Find("UnitLabel_" + unit.id) ?? new GameObject("UnitLabel_" + unit.id); if (label.GetComponent<TextMesh>() == null) { var text = label.AddComponent<TextMesh>(); text.characterSize = .22f; text.fontSize = 48; text.anchor = TextAnchor.MiddleCenter; text.color = Color.white; var font = Resources.Load<Font>("Fonts/NanumGothic-Regular"); if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); if (font != null) text.font = font; }
                label.GetComponent<TextMesh>().text = unit.factionId == 1 ? "★" : unit.factionId == 2 ? "☠" : "¤"; label.transform.position = marker.transform.position + Vector3.up * .62f; if (Camera.main != null) label.transform.rotation = Camera.main.transform.rotation; label.SetActive(marker.activeSelf);
            }
            RenderRuleCues();
        }
        // 5단계: 새 규칙을 영향받는 타일·유닛·거점에 표지판으로 표현한다.
        private void RenderRuleCues()
        {
            // 기존 표지판 제거
            foreach (var old in GameObject.FindGameObjectsWithTag("RuleCue")) Destroy(old);
            var active = game.activeRules.Where(r => game.turn <= r.appliedTurn + r.durationTurns).ToList();
            if (active.Count == 0) return;
            var playerUnit = game.entities.FirstOrDefault(u => u.factionId == 1 && u.alive);
            if (playerUnit == null) return;
            var cuePos = playerUnit.position;
            for (var i = 0; i < Math.Min(active.Count, 3); i++)
            {
                var rule = active[i];
                var cue = new GameObject("RuleCue_" + i);
                cue.tag = "RuleCue";
                var text = cue.AddComponent<TextMesh>();
                text.characterSize = .18f; text.fontSize = 40; text.anchor = TextAnchor.MiddleCenter; text.color = new Color(1f, .9f, .3f);
                var font = Resources.Load<Font>("Fonts/NanumGothic-Regular");
                if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (font != null) text.font = font;
                text.text = "◆ " + rule.name;
                var offset = new HexCoord(playerUnit.position.q + (i + 1), playerUnit.position.r - (i + 1));
                cue.transform.position = HexToWorld(offset) + Vector3.up * .5f;
                if (Camera.main != null) cue.transform.rotation = Camera.main.transform.rotation;
            }
        }
        private int Cost(CommandType type) => type == CommandType.Move ? 1 : type == CommandType.Build || type == CommandType.Upgrade ? 3 : 2;
        private void Queue(CommandType command)
        {
            var player = game.factions.First(f => f.id == 1);
            if (waitingForRules || commands.Sum(c => Cost(c.type)) + Cost(command) > player.sp) return;
            var unit = game.entities.First(x => x.factionId == 1 && x.alive);
            var planned = new PlannedCommand { factionId = 1, unitId = unit.id, type = command, target = unit.position };
            if (command == CommandType.Move) planned.target = new HexCoord(unit.position.q + 1, unit.position.r);
            commands.Add(planned);
            ledger.Add(CommandKorean(command) + " 명령을 예약했습니다. 예상 SP " + (player.sp - commands.Sum(c => Cost(c.type))) + " / 예상 결과: " + ExpectedRange(command));
        }
        private string ExpectedRange(CommandType c)
        {
            if (c == CommandType.Attack) return "피해 2~3, 행운 70 이상이면 3";
            if (c == CommandType.Hunt) return "식량 2, 행운 60 이상이면 4";
            if (c == CommandType.Trade) return "식량 1 → 화폐 2, 관계 +4";
            if (c == CommandType.Hire) return "화폐 3 → 고용병 1";
            if (c == CommandType.Build) return "목재 3~5 → 건물 1채";
            if (c == CommandType.Upgrade) return "석재 3 → 건물 레벨 +1";
            return "";
        }
        private string CommandKorean(CommandType c) => c == CommandType.Move ? "이동" : c == CommandType.Gather ? "채집" : c == CommandType.Hunt ? "수렵" : c == CommandType.Attack ? "공격" : c == CommandType.Trade ? "거래" : c == CommandType.Hire ? "고용" : c == CommandType.Build ? "건설" : c == CommandType.Upgrade ? "강화" : "설득";
        private void EndTurn()
        {
            if (blockedOnRules) return;
            if (!waitingForRules) StartCoroutine(ResolveTurn());
        }
        private void RetryRules()
        {
            if (blockedOnRules && IsUsableApiBase(apiBase))
            {
                blockedOnRules = false;
                StartCoroutine(RequestRules());
            }
        }
        private void SaveAndQuit()
        {
            if (!blockedOnRules) return;
            Save();
            Application.Quit();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }
        private void RunDynamic(DynamicActionV1 action)
        {
            var player = game.factions.First(f => f.id == 1);
            if (waitingForRules || game.turn < action.availableTurn || player.sp < action.spCost || !player.resources.Spend(action.resourceCost, action.resourceAmount)) { ledger.Add("동적 행동을 실행할 수 없습니다."); return; }
            player.sp -= action.spCost; foreach (var effect in action.effects) { if (effect.type == EffectType.Resource) player.resources.Add(effect.resource, effect.amount); else if (effect.type == EffectType.Sp) player.sp = Math.Min(player.maxSp, player.sp + effect.amount); else if (effect.type == EffectType.Relation) foreach (var f in game.factions.Where(f => f.id != 1)) f.relationToPlayer = Math.Max(-100, Math.Min(100, f.relationToPlayer + effect.amount)); }
            action.availableTurn = game.turn + Math.Max(1, action.cooldown); ledger.Add("AI 행동 실행: " + action.name);
        }
        private IEnumerator ResolveTurn()
        {
            waitingForRules = true;
            // PRD 고정 해결 순서: 턴 시작 → 이동·충돌 → 거래·외교 → 전투 → 채집·건설 → 지속 효과 → 승패 판정
            var random = new DeterministicRandom(game.seed + game.turn * 7919);
            TurnResolver.Resolve(game, commands, random, ledger);
            commands.Clear();
            if (!GameRules.HeadquartersAlive(game) && !game.entities.Any(u => u.factionId == 1 && u.alive)) { ledger.Add("본부와 복구 가능한 아군이 모두 사라져 원정이 끝났습니다."); Save(); waitingForRules = false; yield break; }
            game.turn++; game.luck = new DeterministicRandom(game.seed + game.turn * 7919).Next(1, 101); WorldGenerator.Reveal(game); RenderVisibility();
            foreach (var contract in game.victoryContracts.Where(c => GameRules.IsVictoryComplete(game, c))) ledger.Add("승리 계약 달성: " + contract.title);
            yield return StartCoroutine(RequestRules()); Save(); waitingForRules = false;
        }
        private IEnumerator RequestRules()
        {
            waitingForRules = true;
            if (!IsUsableApiBase(apiBase))
            {
                ledger.Add("AI 서비스 주소가 설정되지 않았습니다. OnlyMyGameConfig.json의 apiBaseUrl을 NAS HTTPS 주소로 바꾼 뒤 재시도하세요.");
                BlockOnRules("AI 서비스 주소가 설정되지 않았습니다.");
                yield break;
            }
            var request = new UnityWebRequest(apiBase.TrimEnd('/') + "/v1/rules/generate", "POST"); request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(game))); request.downloadHandler = new DownloadHandlerBuffer(); request.SetRequestHeader("Content-Type", "application/json"); request.SetRequestHeader("Idempotency-Key", game.runId + "-" + game.turn); request.timeout = 20; yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                ledger.Add("AI 규칙 생성 실패: " + request.error);
                BlockOnRules("AI 규칙 생성에 실패했습니다: " + request.error);
                yield break;
            }
            var set = JsonUtility.FromJson<RuleSetV1>(request.downloadHandler.text); var validation = RuleValidator.Validate(set, game);
            if (!validation.valid)
            {
                ledger.Add("AI 응답이 안전성 검증을 통과하지 못했습니다: " + string.Join(", ", validation.errors));
                BlockOnRules("AI 응답이 안전성 검증을 통과하지 못했습니다.");
                yield break;
            }
            blockedOnRules = false; blockReason = "";
            foreach (var rule in set.changes) { rule.appliedTurn = game.turn + 1; game.activeRules.Add(rule); ledger.Add("새 규칙 예고: " + rule.name + " — " + rule.description); }
            foreach (var action in set.actions ?? new List<DynamicActionV1>()) game.dynamicActions.Add(action);
            foreach (var contract in set.victoryContracts ?? new List<VictoryContractV1>()) { contract.announcedTurn = game.turn; contract.achievableFromTurn = Math.Max(contract.achievableFromTurn, game.turn + 1); game.victoryContracts.Add(contract); ledger.Add("새 승리 계약 예고: " + contract.title + " — " + contract.description); }
        }
        private void BlockOnRules(string reason)
        {
            blockedOnRules = true; blockReason = reason;
        }
        private static bool IsUsableApiBase(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !uri.Host.EndsWith(".example", StringComparison.OrdinalIgnoreCase);
        private GameSnapshotV1 ReadSave(string key) { var raw = PlayerPrefs.GetString(key, ""); if (string.IsNullOrEmpty(raw)) return null; var envelope = JsonUtility.FromJson<SaveEnvelope>(raw); return envelope != null && envelope.schemaVersion == 1 && envelope.checksum == Hash(envelope.payload) ? JsonUtility.FromJson<GameSnapshotV1>(envelope.payload) : null; }
        private void Save() { var payload = JsonUtility.ToJson(game); var envelope = JsonUtility.ToJson(new SaveEnvelope { payload = payload, checksum = Hash(payload) }); PlayerPrefs.SetString(BackupKey, PlayerPrefs.GetString(SaveKey, "")); PlayerPrefs.SetString(SaveKey, envelope); PlayerPrefs.Save(); }
        private static string Hash(string value) { unchecked { uint hash = 2166136261; foreach (var c in value) { hash ^= c; hash *= 16777619; } return hash.ToString("X8"); } }
        private void OnGUI()
        {
            if (header == null) BuildGui();
            if (game == null) return; var player = game.factions.First(f => f.id == 1); GUI.Box(new Rect(12, 12, 450, Screen.height - 24), ""); GUI.Label(new Rect(28, 24, 420, 32), "OnlyMyGame — 턴 " + game.turn + " / 행운 " + game.luck, header);
            var goal = game.victoryContracts.LastOrDefault(); var goalText = goal == null ? "현재 목표: AI 규칙 응답 대기" : "목표: " + goal.title + " (" + GameRules.Progress(game, goal.progressKey) + "/" + goal.target + ")";
            GUI.Label(new Rect(28, 62, 420, 58), "SP " + player.sp + "/" + player.maxSp + "    식량 " + player.resources.food + "  목재 " + player.resources.wood + "  석재 " + player.resources.stone + "  철 " + player.resources.iron + "  화폐 " + player.resources.coin + "\n" + goalText, body);
            var x = 28; foreach (var command in new[] { CommandType.Move, CommandType.Gather, CommandType.Hunt, CommandType.Attack, CommandType.Trade, CommandType.Hire }) { if (GUI.Button(new Rect(x, 125, 64, 30), CommandKorean(command), button)) Queue(command); x += 69; }
            x = 28; foreach (var command in new[] { CommandType.Build, CommandType.Upgrade, CommandType.Persuade }) { if (GUI.Button(new Rect(x, 158, 72, 28), CommandKorean(command), button)) Queue(command); x += 77; }
            x = 270; foreach (var action in game.dynamicActions.Take(2)) { if (GUI.Button(new Rect(x, 158, 88, 28), action.name, button)) RunDynamic(action); x += 92; }
            if (blockedOnRules)
            {
                GUI.Box(new Rect(Screen.width / 2 - 180, Screen.height / 2 - 80, 360, 160), "");
                GUI.Label(new Rect(Screen.width / 2 - 160, Screen.height / 2 - 64, 320, 64), "AI 규칙 생성 차단됨\n" + blockReason, body);
                if (GUI.Button(new Rect(Screen.width / 2 - 160, Screen.height / 2 + 10, 140, 30), "재시도", button)) RetryRules();
                if (GUI.Button(new Rect(Screen.width / 2 + 20, Screen.height / 2 + 10, 140, 30), "저장 후 나가기", button)) SaveAndQuit();
                return;
            }
            if (GUI.Button(new Rect(28, 192, 160, 34), waitingForRules ? "AI 규칙 생성 중…" : "명령 확정 · 턴 종료", button)) EndTurn();
            // 5단계: 규칙 장부 — 발생 원인 → 새 조건 → 효과 → 지속 시간 → 승리 조건 영향
            GUI.Label(new Rect(28, 238, 410, 24), "규칙 장부 (원인 → 조건 → 효과 → 지속 → 목표 영향)", header);
            var ruleBook = new List<string>();
            foreach (var rule in game.activeRules.Where(r => game.turn <= r.appliedTurn + r.durationTurns).Take(4))
            {
                var remaining = rule.appliedTurn + rule.durationTurns - game.turn;
                ruleBook.Add("[" + rule.name + "] 원인: " + rule.trigger + " → 조건: " + (rule.condition?.op ?? CompareOp.Always) + " → 효과: " + string.Join(", ", rule.effects.Select(e => e.type.ToString())) + " → 지속: " + remaining + "턴 → 목표 영향: " + (game.victoryContracts.Any(v => v.progressKey == rule.id) ? "있음" : "없음"));
            }
            var entries = ruleBook.Concat(ledger.Skip(Math.Max(0, ledger.Count - 6))).ToList();
            GUI.Label(new Rect(28, 272, 410, Screen.height - 290), string.Join("\n\n", entries), body);
        }
    }
}
