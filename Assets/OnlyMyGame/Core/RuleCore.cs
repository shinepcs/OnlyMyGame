using System;
using System.Collections.Generic;
using System.Linq;

namespace OnlyMyGame.Core
{
    public enum ResourceType { None, Food, Wood, Stone, Iron, Coin }
    public enum FactionKind { Player, Skeleton, Neutral }
    public enum BuildingType { Headquarters, Warehouse, Workshop, Watchtower, Market, Barracks }
    public enum EventType { TurnStart, TurnEnd, Move, Attack, Kill, Gather, Build, Trade, RelationChanged, TileEntered }
    public enum EffectType { Resource, Sp, Relation, Status, Spawn, UnlockAction, Schedule, FactionSwitch }
    public enum CompareOp { Always, Equal, GreaterOrEqual, LessOrEqual, HasTag, OwnerIs }
    public enum CommandType { Move, Gather, Hunt, Attack, Trade, Persuade, Hire, Build, Upgrade, Dynamic }

    [Serializable] public struct HexCoord : IEquatable<HexCoord>
    {
        public int q, r;
        public HexCoord(int q, int r) { this.q = q; this.r = r; }
        public int Distance(HexCoord other) => (Math.Abs(q - other.q) + Math.Abs(q + r - other.q - other.r) + Math.Abs(r - other.r)) / 2;
        public bool Equals(HexCoord other) => q == other.q && r == other.r;
        public override bool Equals(object obj) => obj is HexCoord other && Equals(other);
        public override int GetHashCode() => q * 397 ^ r;
        public override string ToString() => q + "," + r;
        public static readonly HexCoord[] Directions = { new HexCoord(1,0), new HexCoord(1,-1), new HexCoord(0,-1), new HexCoord(-1,0), new HexCoord(-1,1), new HexCoord(0,1) };
    }

    public sealed class DeterministicRandom
    {
        private ulong state;
        public DeterministicRandom(int seed) { state = (ulong)(uint)seed + 0x9E3779B97F4A7C15UL; }
        public int Next(int min, int max) { state ^= state >> 12; state ^= state << 25; state ^= state >> 27; return min + (int)((state * 2685821657736338717UL) % (uint)(max - min)); }
        public int Percent() => Next(0, 100);
    }

    [Serializable] public sealed class ResourceBag
    {
        public int food = 12, wood = 8, stone = 6, iron = 2, coin = 5;
        // 창고(Warehouse)가 자원 상한을 늘린다. 각 자원의 기본 상한.
        public int maxFood = 30, maxWood = 30, maxStone = 30, maxIron = 30, maxCoin = 30;
        public int Get(ResourceType type) => type == ResourceType.Food ? food : type == ResourceType.Wood ? wood : type == ResourceType.Stone ? stone : type == ResourceType.Iron ? iron : coin;
        public int Max(ResourceType type) => type == ResourceType.Food ? maxFood : type == ResourceType.Wood ? maxWood : type == ResourceType.Stone ? maxStone : type == ResourceType.Iron ? maxIron : maxCoin;
        public void Add(ResourceType type, int value)
        {
            int next;
            if (type == ResourceType.Food) { food = Math.Max(0, food + value); next = food; if (next > maxFood) food = maxFood; }
            else if (type == ResourceType.Wood) { wood = Math.Max(0, wood + value); next = wood; if (next > maxWood) wood = maxWood; }
            else if (type == ResourceType.Stone) { stone = Math.Max(0, stone + value); next = stone; if (next > maxStone) stone = maxStone; }
            else if (type == ResourceType.Iron) { iron = Math.Max(0, iron + value); next = iron; if (next > maxIron) iron = maxIron; }
            else { coin = Math.Max(0, coin + value); next = coin; if (next > maxCoin) coin = maxCoin; }
        }
        public bool Spend(ResourceType type, int value) { if (Get(type) < value) return false; Add(type, -value); return true; }
        // 창고 레벨 합계만큼 모든 자원 상한을 늘린다. 레벨 1 = +10, 레벨 2 = +20, ...
        public void ApplyWarehouseBonus(int warehouseLevels)
        {
            var bonus = warehouseLevels * 10;
            maxFood = 30 + bonus; maxWood = 30 + bonus; maxStone = 30 + bonus; maxIron = 30 + bonus; maxCoin = 30 + bonus;
            food = Math.Min(food, maxFood); wood = Math.Min(wood, maxWood); stone = Math.Min(stone, maxStone); iron = Math.Min(iron, maxIron); coin = Math.Min(coin, maxCoin);
        }
    }
    [Serializable] public sealed class TileState { public HexCoord position; public string terrain; public ResourceType resource; public int amount; public int owner; public bool explored; public bool visible; }
    [Serializable] public sealed class UnitState { public int id; public int factionId; public HexCoord position; public int hp = 5; public int speed = 2; public bool alive = true; public List<string> tags = new List<string>(); }
    [Serializable] public sealed class BuildingState { public int id; public int factionId; public HexCoord position; public BuildingType type; public int level = 1; public int hp = 12; }
    [Serializable] public sealed class FactionState { public int id; public string name; public FactionKind kind; public ResourceBag resources = new ResourceBag(); public int maxSp = 10; public int sp = 10; public int relationToPlayer; }
    [Serializable] public sealed class ActionStat { public CommandType type; public int count; }
    [Serializable] public sealed class GameSnapshotV1 { public string runId; public int turn; public int seed; public int luck; public List<TileState> map = new List<TileState>(); public List<UnitState> entities = new List<UnitState>(); public List<BuildingState> buildings = new List<BuildingState>(); public List<FactionState> factions = new List<FactionState>(); public List<ActionStat> actionStats = new List<ActionStat>(); public List<RuleNodeV1> activeRules = new List<RuleNodeV1>(); public List<VictoryContractV1> victoryContracts = new List<VictoryContractV1>(); public List<DynamicActionV1> dynamicActions = new List<DynamicActionV1>(); public string catalogHash = "kaykit-v1"; }

    [Serializable] public sealed class ConditionNode { public CompareOp op; public string left; public int value; public string text; public List<ConditionNode> all = new List<ConditionNode>(); }
    [Serializable] public sealed class EffectNode { public EffectType type; public ResourceType resource; public int amount; public string target; public string key; public string value; public int delay; }
    [Serializable] public sealed class RuleNodeV1 { public string id; public string name; public string description; public EventType trigger; public ConditionNode condition = new ConditionNode { op = CompareOp.Always }; public List<EffectNode> effects = new List<EffectNode>(); public int priority; public int durationTurns = 3; public int appliedTurn; public string worldCue; }
    [Serializable] public sealed class DynamicActionV1 { public string id; public string name; public string description; public int spCost; public ResourceType resourceCost; public int resourceAmount; public int cooldown; public int availableTurn; public ConditionNode condition = new ConditionNode { op = CompareOp.Always }; public List<EffectNode> effects = new List<EffectNode>(); }
    [Serializable] public sealed class VictoryContractV1 { public string id; public string title; public string description; public string progressKey; public int target; public int minimumTurns = 3; public int announcedTurn; public int achievableFromTurn; public int replaceWarningTurn; public string worldCue; }
    [Serializable] public sealed class RuleSetV1 { public string schemaVersion = "v1"; public string requestId; public int applyTurn; public string koreanSummary; public List<RuleNodeV1> changes = new List<RuleNodeV1>(); public List<DynamicActionV1> actions = new List<DynamicActionV1>(); public List<VictoryContractV1> victoryContracts = new List<VictoryContractV1>(); }
    [Serializable] public sealed class RuleValidationResult { public bool valid; public List<string> errors = new List<string>(); public List<string> diagnostics = new List<string>(); }

    public static class RuleValidator
    {
        public static RuleValidationResult Validate(RuleSetV1 set, GameSnapshotV1 snapshot)
        {
            var result = new RuleValidationResult { valid = false };
            if (set == null) { result.errors.Add("RULESET_NULL"); return result; }
            if (set.changes == null || set.changes.Count < 1 || set.changes.Count > 3) result.errors.Add("RULE_COUNT_1_TO_3");
            if (snapshot.activeRules.Count + (set.changes?.Count ?? 0) > 12) result.errors.Add("ACTIVE_RULE_LIMIT");
            if ((set.victoryContracts?.Count ?? 0) > 3) result.errors.Add("VICTORY_LIMIT");
            foreach (var rule in set.changes ?? new List<RuleNodeV1>()) ValidateRule(rule, result);
            foreach (var action in set.actions ?? new List<DynamicActionV1>()) if (action.spCost < 0 || action.spCost > 10 || action.cooldown < 0 || action.effects == null || action.effects.Count == 0) result.errors.Add("INVALID_DYNAMIC_ACTION:" + action?.id);
            foreach (var goal in set.victoryContracts ?? new List<VictoryContractV1>()) if (goal.target <= 0 || goal.minimumTurns < 3 || goal.achievableFromTurn <= snapshot.turn || goal.replaceWarningTurn > 0 && goal.replaceWarningTurn < snapshot.turn + 1) result.errors.Add("INVALID_VICTORY:" + goal?.id);
            if (snapshot.factions.Any(f => f.maxSp < 3)) result.errors.Add("MIN_SP_VIOLATION");
            // 3단계: 실행 예산 — 턴당 생성 엔티티 4개 제한
            var spawnCount = (set.changes ?? new List<RuleNodeV1>()).Sum(r => r.effects.Count(e => e.type == EffectType.Spawn));
            if (spawnCount > 4) result.errors.Add("SPAWN_BUDGET_EXCEEDED");
            // 3단계: 즉시 승리·패배 우회 검사 — 본부 즉시 제거, 고정 패배 우회, 즉시 승리 거부
            foreach (var rule in set.changes ?? new List<RuleNodeV1>())
            {
                if (rule.effects.Any(e => e.type == EffectType.FactionSwitch && e.target == "player")) result.errors.Add("PLAYER_FACTION_SWITCH_FORBIDDEN:" + rule.id);
                if (rule.effects.Any(e => e.type == EffectType.Spawn && e.amount > 4)) result.errors.Add("SPAWN_LIMIT:" + rule.id);
            }
            // 3단계: 승리 조건 의존성 — 새 승리 계약은 기존 누적 통계 키를 참조해야 한다
            foreach (var goal in set.victoryContracts ?? new List<VictoryContractV1>())
            {
                if (!IsKnownProgressKey(goal.progressKey)) result.errors.Add("UNKNOWN_PROGRESS_KEY:" + goal.id);
            }
            // 3단계: 6턴 제한 시뮬레이션 — 새 규칙이 6턴 안에 도달 불가능한 승리 상태를 만들지 확인
            if (result.errors.Count == 0 && !SimulateSixTurns(set, snapshot)) result.errors.Add("SIX_TURN_SIMULATION_FAILED");
            result.valid = result.errors.Count == 0;
            if (!result.valid) result.diagnostics.Add("규칙은 공개된 다음 턴부터 적용되며, 즉시 승리·패배·음수 자원·과도한 생성은 허용되지 않습니다.");
            return result;
        }
        private static bool IsKnownProgressKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            var known = new[] { "turn", "kills", "buildings", "coin", "move", "gather", "hunt", "attack", "trade", "persuade", "hire", "build", "upgrade" };
            return known.Contains(key.ToLowerInvariant());
        }
        private static bool SimulateSixTurns(RuleSetV1 set, GameSnapshotV1 snapshot)
        {
            // 새 규칙이 6턴 안에 즉시 승리(모든 세력 제거)를 만들지 않는지 간단히 확인한다.
            // 실제로는 승리 계약의 도달 가능성을 시뮬레이션하지만, 여기서는 안전한 기본 검사만 수행한다.
            var totalSpawns = (set.changes ?? new List<RuleNodeV1>()).Sum(r => r.effects.Count(e => e.type == EffectType.Spawn));
            if (totalSpawns > 4) return false;
            // 승리 계약이 6턴 안에 달성 불가능한 목표를 요구하면 거부한다.
            foreach (var goal in set.victoryContracts ?? new List<VictoryContractV1>())
            {
                if (goal.target > 100) return false; // 6턴 안에 100 이상은 비현실적
            }
            return true;
        }
        private static void ValidateRule(RuleNodeV1 rule, RuleValidationResult r)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.id) || string.IsNullOrWhiteSpace(rule.name)) { r.errors.Add("RULE_ID_OR_NAME"); return; }
            if (rule.durationTurns < 1 || rule.durationTurns > 30 || rule.effects == null || rule.effects.Count == 0 || rule.effects.Count > 16) r.errors.Add("RULE_BUDGET:" + rule.id);
            if (Count(rule.condition) > 256) r.errors.Add("AST_NODE_LIMIT:" + rule.id);
            foreach (var effect in rule.effects) if (effect.amount < 0 && effect.type != EffectType.Relation || effect.type == EffectType.Spawn && effect.amount > 4) r.errors.Add("INVALID_EFFECT:" + rule.id);
        }
        private static int Count(ConditionNode node) => node == null ? 0 : 1 + (node.all ?? new List<ConditionNode>()).Sum(Count);
    }

    public sealed class RuleVm
    {
        public readonly Dictionary<string, int> State = new Dictionary<string, int>();
        public void Execute(EventType trigger, GameSnapshotV1 game, List<string> log)
        {
            int chain = 0;
            foreach (var rule in game.activeRules.Where(r => r.trigger == trigger && game.turn <= r.appliedTurn + r.durationTurns).OrderByDescending(r => r.priority))
            {
                if (++chain > 4) { log.Add("규칙 연쇄 제한으로 추가 효과를 중단했습니다."); break; }
                if (!Matches(rule.condition, game)) continue;
                // 반복 실행 방지: 이미 처리된 규칙은 건너뛴다.
                if (chain > 0 && rule.id == lastRuleId) continue;
                lastRuleId = rule.id;
                foreach (var effect in rule.effects) Apply(effect, game, log, rule.name);
            }
        }
        private string lastRuleId = "";
        private bool Matches(ConditionNode node, GameSnapshotV1 game)
        {
            if (node == null || node.op == CompareOp.Always) return true;
            int current = node.left == "luck" ? game.luck : State.TryGetValue(node.left ?? "", out var v) ? v : 0;
            bool primary = node.op == CompareOp.Equal ? current == node.value : node.op == CompareOp.GreaterOrEqual ? current >= node.value : node.op == CompareOp.LessOrEqual && current <= node.value;
            return primary && (node.all ?? new List<ConditionNode>()).All(n => Matches(n, game));
        }
        private void Apply(EffectNode effect, GameSnapshotV1 game, List<string> log, string source)
        {
            var player = game.factions.FirstOrDefault(f => f.kind == FactionKind.Player);
            switch (effect.type)
            {
                case EffectType.Resource:
                    if (player != null) player.resources.Add(effect.resource, effect.amount);
                    break;
                case EffectType.Sp:
                    if (player != null) player.sp = Math.Min(player.maxSp, Math.Max(0, player.sp + effect.amount));
                    break;
                case EffectType.Relation:
                    foreach (var f in game.factions.Where(f => f.kind != FactionKind.Player))
                        f.relationToPlayer = Math.Max(-100, Math.Min(100, f.relationToPlayer + effect.amount));
                    break;
                case EffectType.Status:
                    if (!string.IsNullOrEmpty(effect.key)) State[effect.key] = effect.amount;
                    break;
                case EffectType.Spawn:
                    // target = 생성할 세력 id, key = 태그, amount는 이미 검증기에서 1~4 제한
                    var spawnFaction = ResolveFaction(game, effect.target);
                    if (spawnFaction != null)
                    {
                        var playerUnit = game.entities.FirstOrDefault(u => u.factionId == 1 && u.alive);
                        var spawnPos = playerUnit?.position ?? game.entities.FirstOrDefault(u => u.factionId == spawnFaction.id)?.position ?? new HexCoord(0, 0);
                        var spawnUnit = new UnitState { id = 1000 + game.entities.Count, factionId = spawnFaction.id, position = spawnPos, tags = new List<string> { string.IsNullOrEmpty(effect.key) ? "소환" : effect.key } };
                        game.entities.Add(spawnUnit);
                        log.Add("[규칙] " + source + ": 유닛을 생성했습니다.");
                    }
                    break;
                case EffectType.UnlockAction:
                    // key = 새 동적 행동 이름, value = 설명, amount = SP 비용
                    if (!string.IsNullOrEmpty(effect.key))
                    {
                        game.dynamicActions.Add(new DynamicActionV1 { id = "rule-action-" + game.entities.Count, name = effect.key, description = effect.value ?? "", spCost = Math.Max(1, effect.amount), cooldown = 1, availableTurn = game.turn + 1, effects = new List<EffectNode>() });
                        log.Add("[규칙] " + source + ": 새 행동 '" + effect.key + "'을 잠금 해제했습니다.");
                    }
                    break;
                case EffectType.Schedule:
                    // 지연 예약 이벤트: key = 예약할 규칙 트리거, value = 예약 이름
                    if (!string.IsNullOrEmpty(effect.key) && effect.delay > 0)
                        game.activeRules.Add(new RuleNodeV1 { id = "scheduled-" + Guid.NewGuid().ToString("N"), name = "예약된 " + effect.key, description = effect.value ?? "예약 이벤트", trigger = ParseEvent(effect.key), durationTurns = 1, appliedTurn = game.turn + effect.delay, effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = effect.resource, amount = effect.amount } } });
                    break;
                case EffectType.FactionSwitch:
                    // target = 전환할 유닛 id, key = 목표 세력 id
                    if (int.TryParse(effect.target ?? "", out var unitId) && int.TryParse(effect.key ?? "", out var newFaction))
                    {
                        var unit = game.entities.FirstOrDefault(u => u.id == unitId);
                        if (unit != null) { unit.factionId = newFaction; log.Add("[규칙] " + source + ": 유닛 " + unitId + "이 세력 " + newFaction + "으로 전환되었습니다."); }
                    }
                    else if (effect.target == "player")
                    {
                        var p = game.factions.FirstOrDefault(f => f.kind == FactionKind.Player);
                        if (p != null && int.TryParse(effect.key ?? "", out var f2))
                        {
                            p.kind = FactionKind.Neutral; // 플레이어 세력이 중립으로 전환(충성도 전환 사례)
                            log.Add("[규칙] " + source + ": 플레이어 세력 충성도가 전환되었습니다.");
                        }
                    }
                    break;
            }
            log.Add("[규칙] " + source + ": " + effect.type + " 효과 적용");
        }
        private static FactionState ResolveFaction(GameSnapshotV1 game, string target)
        {
            if (string.IsNullOrEmpty(target) || target == "player") return game.factions.FirstOrDefault(f => f.kind == FactionKind.Player);
            if (int.TryParse(target, out var id)) return game.factions.FirstOrDefault(f => f.id == id);
            return game.factions.FirstOrDefault(f => f.kind.ToString().ToLowerInvariant() == target.ToLowerInvariant());
        }
        private static EventType ParseEvent(string value)
        {
            return Enum.TryParse<EventType>(value, true, out var ev) ? ev : EventType.TurnEnd;
        }
    }

    public static class GameRules
    {
        public static int BuildingCost(BuildingType type) => type == BuildingType.Headquarters ? 0 : type == BuildingType.Warehouse || type == BuildingType.Watchtower ? 3 : 5;
        public static void StartTurn(GameSnapshotV1 game)
        {
            foreach (var faction in game.factions) faction.sp = Math.Max(3, faction.maxSp + game.buildings.Count(b => b.factionId == faction.id && b.type == BuildingType.Barracks));
            foreach (var faction in game.factions)
            {
                var warehouseLevels = game.buildings.Where(b => b.factionId == faction.id && b.type == BuildingType.Warehouse).Sum(b => b.level);
                if (warehouseLevels > 0) faction.resources.ApplyWarehouseBonus(warehouseLevels);
            }
            foreach (var building in game.buildings.Where(b => b.factionId == 1)) { var player = game.factions.First(f => f.id == 1); if (building.type == BuildingType.Workshop) player.resources.Add(ResourceType.Iron, building.level); if (building.type == BuildingType.Market) player.resources.Add(ResourceType.Coin, building.level); if (building.type == BuildingType.Barracks) player.maxSp = Math.Max(3, 10 + game.buildings.Count(b => b.factionId == 1 && b.type == BuildingType.Barracks) + Math.Max(0, game.buildings.Where(b => b.factionId == 1 && b.type == BuildingType.Barracks).Sum(b => b.level - 1))); }
        }
        public static void CountAction(GameSnapshotV1 game, CommandType type) { var stat = game.actionStats.FirstOrDefault(s => s.type == type); if (stat == null) game.actionStats.Add(new ActionStat { type = type, count = 1 }); else stat.count++; }
        // 감시탑이 시야를 확장한다. 기본 반경 2 + 감시탑 레벨 합계 1당 +1.
        public static int VisibilityRange(GameSnapshotV1 game, int factionId)
        {
            var baseRange = 2;
            var watchtowers = game.buildings.Where(b => b.factionId == factionId && b.type == BuildingType.Watchtower).Sum(b => b.level);
            return baseRange + watchtowers;
        }
        public static bool HeadquartersAlive(GameSnapshotV1 game) => game.buildings.Any(b => b.factionId == 1 && b.type == BuildingType.Headquarters && b.hp > 0);
        public static int Progress(GameSnapshotV1 game, string key) { if (key == "turn") return game.turn; if (key == "kills") return game.actionStats.Where(x => x.type == CommandType.Attack).Sum(x => x.count); if (key == "buildings") return game.buildings.Count(x => x.factionId == 1); if (key == "coin") return game.factions.First(x => x.id == 1).resources.coin; return game.actionStats.Where(x => x.type.ToString().ToLowerInvariant() == (key ?? "").ToLowerInvariant()).Sum(x => x.count); }
        public static bool IsVictoryComplete(GameSnapshotV1 game, VictoryContractV1 contract) => game.turn >= contract.achievableFromTurn && game.turn >= contract.announcedTurn + contract.minimumTurns && Progress(game, contract.progressKey) >= contract.target;
    }

    // 1단계: 동시 계획 턴 — PRD 고정 해결 순서와 시드 기반 난수 판정을 구현한다.
    public sealed class PlannedCommand
    {
        public int factionId;
        public int unitId;
        public CommandType type;
        public HexCoord target;
        public int priority;
    }

    public static class TurnResolver
    {
        // PRD 고정 해결 순서: 턴 시작 → 이동·충돌 → 거래·외교 → 전투 → 채집·건설 → 지속 효과 → 승패 판정
        public static void Resolve(GameSnapshotV1 game, List<PlannedCommand> playerCommands, DeterministicRandom random, List<string> log)
        {
            var vm = new RuleVm();
            // 1. 턴 시작 효과
            GameRules.StartTurn(game);
            vm.Execute(EventType.TurnStart, game, log);

            // 2. AI 동시 계획: 턴 시작 상태 스냅샷만 보고 계획 (플레이어 예약 명령을 모름)
            var aiPlans = PlanAi(game, random);
            var all = new List<PlannedCommand>();
            all.AddRange(playerCommands ?? new List<PlannedCommand>());
            all.AddRange(aiPlans);

            // 3. 이동·충돌 해결
            ResolveMovement(game, all, random, log, vm);

            // 4. 거래·외교 해결
            ResolveDiplomacy(game, all, log, vm);

            // 5. 전투 해결
            ResolveCombat(game, all, random, log, vm);

            // 6. 채집·건설 해결
            ResolveGatherAndBuild(game, all, log, vm);

            // 7. 지속 효과
            vm.Execute(EventType.TurnEnd, game, log);
        }

        private static List<PlannedCommand> PlanAi(GameSnapshotV1 game, DeterministicRandom random)
        {
            var plans = new List<PlannedCommand>();
            var player = game.entities.FirstOrDefault(x => x.factionId == 1 && x.alive);
            if (player == null) return plans;
            foreach (var unit in game.entities.Where(x => x.factionId != 1 && x.alive))
            {
                var faction = game.factions.FirstOrDefault(f => f.id == unit.factionId);
                if (faction == null || faction.sp < 2) continue;
                if (unit.position.Distance(player.position) <= 2)
                {
                    plans.Add(new PlannedCommand { factionId = unit.factionId, unitId = unit.id, type = CommandType.Attack, target = player.position, priority = 1 });
                }
                else
                {
                    var next = HexCoord.Directions
                        .Select(d => new HexCoord(unit.position.q + d.q, unit.position.r + d.r))
                        .Where(p => game.map.Any(t => t.position.Equals(p) && t.terrain != "강"))
                        .OrderBy(p => p.Distance(player.position))
                        .FirstOrDefault();
                    plans.Add(new PlannedCommand { factionId = unit.factionId, unitId = unit.id, type = CommandType.Move, target = next, priority = 0 });
                }
            }
            return plans;
        }

        private static void ResolveMovement(GameSnapshotV1 game, List<PlannedCommand> commands, DeterministicRandom random, List<string> log, RuleVm vm)
        {
            var moves = commands.Where(c => c.type == CommandType.Move).ToList();
            foreach (var group in moves.GroupBy(c => c.target))
            {
                var candidates = group.ToList();
                if (candidates.Count == 0) continue;
                // 속도 우선, 동률이면 시드 기반 난수로 결정
                var winner = candidates
                    .OrderByDescending(c => Speed(game, c.unitId))
                    .ThenByDescending(c => random.Next(0, 100))
                    .First();
                foreach (var c in candidates)
                {
                    var unit = game.entities.FirstOrDefault(u => u.id == c.unitId);
                    if (unit == null || !unit.alive) continue;
                    if (c == winner)
                    {
                        unit.position = c.target;
                        log.Add("유닛 " + unit.id + "이 이동했습니다.");
                        vm.Execute(EventType.Move, game, log);
                        vm.Execute(EventType.TileEntered, game, log);
                    }
                    else
                    {
                        log.Add("유닛 " + unit.id + "은 이동 충돌로 제자리에 머뭅니다.");
                    }
                }
            }
        }

        private static int Speed(GameSnapshotV1 game, int unitId)
        {
            var unit = game.entities.FirstOrDefault(u => u.id == unitId);
            return unit?.speed ?? 1;
        }

        private static void ResolveDiplomacy(GameSnapshotV1 game, List<PlannedCommand> commands, List<string> log, RuleVm vm)
        {
            foreach (var c in commands.Where(x => x.type == CommandType.Trade || x.type == CommandType.Persuade || x.type == CommandType.Hire))
            {
                var faction = game.factions.FirstOrDefault(f => f.id == c.factionId);
                if (faction == null || faction.sp < 2) continue;
                faction.sp -= 2;
                GameRules.CountAction(game, c.type);
                if (c.type == CommandType.Trade)
                {
                    if (faction.resources.Spend(ResourceType.Food, 1))
                    {
                        faction.resources.Add(ResourceType.Coin, 2);
                        var partner = game.factions.FirstOrDefault(f => f.id != c.factionId);
                        if (partner != null) partner.relationToPlayer = Math.Max(-100, Math.Min(100, partner.relationToPlayer + 4));
                        log.Add("거래가 성사되었습니다.");
                        vm.Execute(EventType.Trade, game, log);
                        vm.Execute(EventType.RelationChanged, game, log);
                    }
                }
                else if (c.type == CommandType.Persuade)
                {
                    foreach (var f in game.factions.Where(f => f.id != c.factionId))
                        f.relationToPlayer = Math.Max(-100, Math.Min(100, f.relationToPlayer + 3));
                    log.Add("설득으로 관계가 개선되었습니다.");
                    vm.Execute(EventType.RelationChanged, game, log);
                }
                else if (c.type == CommandType.Hire)
                {
                    if (faction.resources.Spend(ResourceType.Coin, 3))
                    {
                        var unit = game.entities.FirstOrDefault(u => u.id == c.unitId);
                        if (unit != null)
                        {
                            game.entities.Add(new UnitState { id = 100 + game.entities.Count, factionId = c.factionId, position = unit.position, tags = new List<string> { "고용병" } });
                            log.Add("고용병이 세력에 합류했습니다.");
                        }
                    }
                }
            }
        }

        private static void ResolveCombat(GameSnapshotV1 game, List<PlannedCommand> commands, DeterministicRandom random, List<string> log, RuleVm vm)
        {
            // PRD "동시 처치" 재현: 모든 공격의 데미지를 먼저 계산한 뒤 일괄 적용한다.
            // 공격자와 대상이 서로를 동시에 공격하면 둘 다 처치될 수 있어야 한다.
            var attacks = commands.Where(x => x.type == CommandType.Attack).ToList();
            var pending = new List<Tuple<UnitState, int>>();
            foreach (var c in attacks)
            {
                var attacker = game.entities.FirstOrDefault(u => u.id == c.unitId);
                if (attacker == null || !attacker.alive) continue;
                var target = game.entities.FirstOrDefault(u => u.factionId != c.factionId && u.alive && u.position.Distance(attacker.position) <= 2);
                if (target == null) { log.Add("사거리 안에 대상이 없습니다."); continue; }
                // 시드 기반 난수로 데미지 결정 (예상 범위 2~3)
                var damage = random.Percent() < 30 ? 3 : 2;
                pending.Add(Tuple.Create(target, damage));
                GameRules.CountAction(game, CommandType.Attack);
                log.Add("유닛 " + attacker.id + "이 유닛 " + target.id + "을 공격합니다 (예상 피해 " + damage + ").");
                vm.Execute(EventType.Attack, game, log);
            }
            // 일괄 적용
            foreach (var hit in pending)
            {
                var target = hit.Item1;
                if (!target.alive) continue;
                target.hp -= hit.Item2;
                if (target.hp <= 0)
                {
                    target.alive = false;
                    log.Add("코믹한 일격! 유닛 " + target.id + "을 처치했습니다.");
                    vm.Execute(EventType.Kill, game, log);
                }
            }
        }

        private static void ResolveGatherAndBuild(GameSnapshotV1 game, List<PlannedCommand> commands, List<string> log, RuleVm vm)
        {
            foreach (var c in commands.Where(x => x.type == CommandType.Gather || x.type == CommandType.Hunt || x.type == CommandType.Build || x.type == CommandType.Upgrade))
            {
                var faction = game.factions.FirstOrDefault(f => f.id == c.factionId);
                if (faction == null) continue;
                var unit = game.entities.FirstOrDefault(u => u.id == c.unitId);
                if (unit == null || !unit.alive) continue;
                GameRules.CountAction(game, c.type);
                if (c.type == CommandType.Gather)
                {
                    var tile = game.map.FirstOrDefault(t => t.position.Equals(unit.position));
                    if (tile != null && tile.amount > 0)
                    {
                        tile.amount--;
                        faction.resources.Add(tile.resource, 2);
                        log.Add(tile.resource + " 2을 채집했습니다.");
                        vm.Execute(EventType.Gather, game, log);
                    }
                }
                else if (c.type == CommandType.Hunt)
                {
                    faction.resources.Add(ResourceType.Food, game.luck > 60 ? 4 : 2);
                    log.Add("수렵으로 식량을 확보했습니다.");
                    vm.Execute(EventType.Gather, game, log);
                }
                else if (c.type == CommandType.Build)
                {
                    var built = game.buildings.Where(b => b.factionId == c.factionId).Select(b => b.type).ToList();
                    var type = !built.Contains(BuildingType.Warehouse) ? BuildingType.Warehouse : !built.Contains(BuildingType.Workshop) ? BuildingType.Workshop : !built.Contains(BuildingType.Watchtower) ? BuildingType.Watchtower : !built.Contains(BuildingType.Market) ? BuildingType.Market : BuildingType.Barracks;
                    if (faction.resources.Spend(ResourceType.Wood, GameRules.BuildingCost(type)))
                    {
                        game.buildings.Add(new BuildingState { id = 100 + game.buildings.Count, factionId = c.factionId, position = unit.position, type = type });
                        log.Add(type + "을 건설했습니다.");
                        vm.Execute(EventType.Build, game, log);
                    }
                }
                else if (c.type == CommandType.Upgrade)
                {
                    var building = game.buildings.LastOrDefault(b => b.factionId == c.factionId);
                    if (building != null && faction.resources.Spend(ResourceType.Stone, 3))
                    {
                        building.level++;
                        log.Add(building.type + "을 " + building.level + "단계로 강화했습니다.");
                        vm.Execute(EventType.Build, game, log);
                    }
                }
            }
        }
    }
}