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
            var player = game.entities.First(x => x.factionId == 1 && x.alive);
            foreach (var tile in game.map) { tile.visible = tile.position.Distance(player.position) <= 2; tile.explored |= tile.visible; }
        }
    }

    public sealed class GameController : MonoBehaviour
    {
        private GameSnapshotV1 game; private readonly RuleVm vm = new RuleVm(); private readonly List<string> ledger = new List<string>(); private readonly List<CommandType> commands = new List<CommandType>();
        private Dictionary<HexCoord, GameObject> visuals = new Dictionary<HexCoord, GameObject>(); private bool waitingForRules; private string apiBase; private GUIStyle header, body, button;
        [Serializable] private sealed class SaveEnvelope { public int schemaVersion = 1; public string payload; public string checksum; }
        [Serializable] private sealed class ClientConfig { public string apiBaseUrl; }
        private const string SaveKey = "onlymygame.autosave.v1"; private const string BackupKey = "onlymygame.autosave.v1.backup";
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)] static void Create() { if (FindFirstObjectByType<GameController>() == null) new GameObject("OnlyMyGame").AddComponent<GameController>(); }
        private void Awake() { var config = Resources.Load<TextAsset>("OnlyMyGameConfig"); apiBase = config == null ? Environment.GetEnvironmentVariable("ONLYMYGAME_API_URL") ?? "" : JsonUtility.FromJson<ClientConfig>(config.text).apiBaseUrl ?? ""; LoadOrNew(); }
        private void BuildGui() { header = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } }; body = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true, normal = { textColor = Color.white } }; button = new GUIStyle(GUI.skin.button) { fontSize = 14 }; }
        private void LoadOrNew() { try { var saved = ReadSave(SaveKey) ?? ReadSave(BackupKey); game = saved ?? WorldGenerator.Create(Environment.TickCount); } catch { game = WorldGenerator.Create(Environment.TickCount); } BuildWorld(); ledger.Add("턴 " + game.turn + " — 세계가 열렸습니다."); }
        private void BuildWorld()
        {
            foreach (var item in visuals.Values) Destroy(item); visuals.Clear();
            foreach (var tile in game.map)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder); go.name = "Hex_" + tile.position; go.transform.position = HexToWorld(tile.position); go.transform.localScale = new Vector3(.92f, .08f, .92f);
                var renderer = go.GetComponent<Renderer>(); renderer.material.color = tile.terrain == "강" ? new Color(.12f,.35f,.7f) : tile.terrain == "숲" ? new Color(.15f,.45f,.19f) : tile.terrain == "언덕" ? new Color(.48f,.35f,.18f) : new Color(.38f,.64f,.25f); visuals[tile.position] = go;
            }
            if (Camera.main == null) { var cam = new GameObject("Quarter Camera").AddComponent<Camera>(); cam.transform.position = new Vector3(0, 15, -13); cam.transform.rotation = Quaternion.Euler(52, 0, 0); cam.orthographic = true; cam.orthographicSize = 10; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(.06f,.1f,.16f); }
            RenderVisibility();
        }
        private Vector3 HexToWorld(HexCoord p) => new Vector3((p.q + p.r * .5f) * 1.65f, 0, p.r * 1.43f);
        private void RenderVisibility()
        {
            foreach (var tile in game.map) if (visuals.TryGetValue(tile.position, out var go)) go.SetActive(tile.explored);
            foreach (var unit in game.entities.Where(x => x.alive)) { var marker = GameObject.Find("Unit_" + unit.id) ?? GameObject.CreatePrimitive(PrimitiveType.Capsule); marker.name = "Unit_" + unit.id; marker.transform.position = HexToWorld(unit.position) + Vector3.up * .5f; marker.transform.localScale = Vector3.one * .45f; marker.GetComponent<Renderer>().material.color = unit.factionId == 1 ? Color.cyan : unit.factionId == 2 ? Color.red : Color.yellow; marker.SetActive(game.map.First(t => t.position.Equals(unit.position)).visible); }
        }
        private int Cost(CommandType type) => type == CommandType.Move ? 1 : type == CommandType.Build || type == CommandType.Upgrade ? 3 : 2;
        private void Queue(CommandType command) { var player = game.factions.First(f => f.id == 1); if (!waitingForRules && commands.Sum(Cost) + Cost(command) <= player.sp) { commands.Add(command); ledger.Add(CommandKorean(command) + " 명령을 예약했습니다. 예상 SP " + (player.sp - commands.Sum(Cost))); } }
        private string CommandKorean(CommandType c) => c == CommandType.Move ? "이동" : c == CommandType.Gather ? "채집" : c == CommandType.Hunt ? "수렵" : c == CommandType.Attack ? "공격" : c == CommandType.Trade ? "거래" : c == CommandType.Hire ? "고용" : c == CommandType.Build ? "건설" : c == CommandType.Upgrade ? "강화" : "설득";
        private void EndTurn() { if (!waitingForRules) StartCoroutine(ResolveTurn()); }
        private void RunDynamic(DynamicActionV1 action)
        {
            var player = game.factions.First(f => f.id == 1);
            if (waitingForRules || game.turn < action.availableTurn || player.sp < action.spCost || !player.resources.Spend(action.resourceCost, action.resourceAmount)) { ledger.Add("동적 행동을 실행할 수 없습니다."); return; }
            player.sp -= action.spCost; foreach (var effect in action.effects) { if (effect.type == EffectType.Resource) player.resources.Add(effect.resource, effect.amount); else if (effect.type == EffectType.Sp) player.sp = Math.Min(player.maxSp, player.sp + effect.amount); else if (effect.type == EffectType.Relation) foreach (var f in game.factions.Where(f => f.id != 1)) f.relationToPlayer = Math.Max(-100, Math.Min(100, f.relationToPlayer + effect.amount)); }
            action.availableTurn = game.turn + Math.Max(1, action.cooldown); ledger.Add("AI 행동 실행: " + action.name);
        }
        private IEnumerator ResolveTurn()
        {
            waitingForRules = true; GameRules.StartTurn(game); var player = game.factions.First(f => f.id == 1); vm.Execute(OnlyMyGame.Core.EventType.TurnStart, game, ledger);
            foreach (var command in commands) ResolveCommand(command, player); commands.Clear(); ResolveAi(); vm.Execute(OnlyMyGame.Core.EventType.TurnEnd, game, ledger);
            if (!GameRules.HeadquartersAlive(game) && !game.entities.Any(u => u.factionId == 1 && u.alive)) { ledger.Add("본부와 복구 가능한 아군이 모두 사라져 원정이 끝났습니다."); Save(); waitingForRules = false; yield break; }
            game.turn++; game.luck = new DeterministicRandom(game.seed + game.turn * 7919).Next(1, 101); WorldGenerator.Reveal(game); RenderVisibility();
            foreach (var contract in game.victoryContracts.Where(c => GameRules.IsVictoryComplete(game, c))) ledger.Add("승리 계약 달성: " + contract.title);
            yield return StartCoroutine(RequestRules()); Save(); waitingForRules = false;
        }
        private void ResolveCommand(CommandType c, FactionState p)
        {
            p.sp -= Cost(c); GameRules.CountAction(game, c); var unit = game.entities.First(x => x.factionId == 1 && x.alive);
            if (c == CommandType.Move) { var to = new HexCoord(unit.position.q + 1, unit.position.r); if (game.map.Any(t => t.position.Equals(to) && t.terrain != "강")) unit.position = to; ledger.Add("원정대가 새 타일로 이동했습니다."); }
            else if (c == CommandType.Gather) { var tile = game.map.First(t => t.position.Equals(unit.position)); if (tile.amount > 0) { tile.amount--; p.resources.Add(tile.resource, 2); ledger.Add(tile.resource + " 2을 채집했습니다."); } }
            else if (c == CommandType.Hunt) { p.resources.Add(ResourceType.Food, game.luck > 60 ? 4 : 2); ledger.Add("수렵으로 식량을 확보했습니다."); }
            else if (c == CommandType.Attack) { var target = game.entities.FirstOrDefault(x => x.factionId != 1 && x.alive && x.position.Distance(unit.position) <= 2); if (target != null) { target.hp -= game.luck > 70 ? 3 : 2; if (target.hp <= 0) { target.alive = false; GameRules.CountAction(game, CommandType.Attack); ledger.Add("코믹한 일격! 적 유닛을 처치했습니다."); } else ledger.Add("적을 공격했습니다."); } else ledger.Add("사거리 안에 적이 없습니다."); }
            else if (c == CommandType.Trade) { if (p.resources.Spend(ResourceType.Food, 1)) { p.resources.Add(ResourceType.Coin, 2); game.factions.First(f => f.id == 3).relationToPlayer += 4; ledger.Add("장터단과 거래했습니다."); } }
            else if (c == CommandType.Hire) { if (p.resources.Spend(ResourceType.Coin, 3)) { game.entities.Add(new UnitState { id = 100 + game.entities.Count, factionId = 1, position = unit.position, tags = new List<string> { "고용병" } }); ledger.Add("고용병이 원정대에 합류했습니다."); } }
            else if (c == CommandType.Build) { var built = game.buildings.Where(b => b.factionId == 1).Select(b => b.type).ToList(); var type = !built.Contains(BuildingType.Warehouse) ? BuildingType.Warehouse : !built.Contains(BuildingType.Workshop) ? BuildingType.Workshop : !built.Contains(BuildingType.Watchtower) ? BuildingType.Watchtower : !built.Contains(BuildingType.Market) ? BuildingType.Market : BuildingType.Barracks; if (p.resources.Spend(ResourceType.Wood, GameRules.BuildingCost(type))) { game.buildings.Add(new BuildingState { id = 100 + game.buildings.Count, factionId = 1, position = unit.position, type = type }); ledger.Add(type + "을 건설했습니다."); } }
            else if (c == CommandType.Upgrade) { var building = game.buildings.LastOrDefault(b => b.factionId == 1); if (building != null && p.resources.Spend(ResourceType.Stone, 3)) { building.level++; ledger.Add(building.type + "을 " + building.level + "단계로 강화했습니다."); } }
            else { game.factions.Where(f => f.id != 1).ToList().ForEach(f => f.relationToPlayer += 3); ledger.Add("상대 세력에 설득을 시도했습니다."); }
        }
        private void ResolveAi() { foreach (var unit in game.entities.Where(x => x.factionId == 2 && x.alive)) { var player = game.entities.First(x => x.id == 1); if (unit.position.Distance(player.position) <= 2) { player.hp--; ledger.Add("스켈레톤이 덜컹거리며 공격했습니다!"); } else unit.position = HexCoord.Directions.OrderBy(d => (new HexCoord(unit.position.q + d.q, unit.position.r + d.r)).Distance(player.position)).Select(d => new HexCoord(unit.position.q + d.q, unit.position.r + d.r)).First(p => game.map.Any(t => t.position.Equals(p))); } }
        private IEnumerator RequestRules()
        {
            if (string.IsNullOrWhiteSpace(apiBase)) { ledger.Add("AI 서비스 주소가 설정되지 않아 다음 규칙 응답을 기다립니다. 저장 후 재시도할 수 있습니다."); yield break; }
            var request = new UnityWebRequest(apiBase.TrimEnd('/') + "/v1/rules/generate", "POST"); request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(game))); request.downloadHandler = new DownloadHandlerBuffer(); request.SetRequestHeader("Content-Type", "application/json"); request.SetRequestHeader("Idempotency-Key", game.runId + "-" + game.turn); request.timeout = 20; yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) { ledger.Add("AI 규칙 생성 실패: " + request.error + " — 재시도 또는 저장 후 나가기."); yield break; }
            var set = JsonUtility.FromJson<RuleSetV1>(request.downloadHandler.text); var validation = RuleValidator.Validate(set, game);
            if (!validation.valid) { ledger.Add("AI 응답이 안전성 검증을 통과하지 못했습니다: " + string.Join(", ", validation.errors)); yield break; }
            foreach (var rule in set.changes) { rule.appliedTurn = game.turn + 1; game.activeRules.Add(rule); ledger.Add("새 규칙 예고: " + rule.name + " — " + rule.description); }
            foreach (var action in set.actions ?? new List<DynamicActionV1>()) game.dynamicActions.Add(action);
            foreach (var contract in set.victoryContracts ?? new List<VictoryContractV1>()) { contract.announcedTurn = game.turn; contract.achievableFromTurn = Math.Max(contract.achievableFromTurn, game.turn + 1); game.victoryContracts.Add(contract); ledger.Add("새 승리 계약 예고: " + contract.title + " — " + contract.description); }
        }
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
            if (GUI.Button(new Rect(28, 192, 160, 34), waitingForRules ? "AI 규칙 생성 중…" : "명령 확정 · 턴 종료", button)) EndTurn();
            GUI.Label(new Rect(28, 238, 410, 24), "규칙 장부 (원인 → 조건 → 효과 → 지속 → 목표 영향)", header);
            var entries = ledger.Skip(Math.Max(0, ledger.Count - 10)).ToList(); GUI.Label(new Rect(28, 272, 410, Screen.height - 290), string.Join("\n\n", entries), body);
        }
    }
}
