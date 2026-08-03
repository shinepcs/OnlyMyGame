using System;
using System.Collections.Generic;
using System.Linq;

namespace OnlyMyGame.Core
{
    public enum ResourceType { Food, Wood, Stone, Iron, Coin }
    public enum FactionKind { Player, Skeleton, Neutral }
    public enum BuildingType { Headquarters, Warehouse, Workshop, Watchtower, Market, Barracks }
    public enum EventType { TurnStart, TurnEnd, Move, Attack, Kill, Gather, Build, Trade, RelationChanged, TileEntered }
    public enum EffectType { Resource, Sp, Relation, Status, Spawn, UnlockAction, Schedule }
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
        public int Get(ResourceType type) => type == ResourceType.Food ? food : type == ResourceType.Wood ? wood : type == ResourceType.Stone ? stone : type == ResourceType.Iron ? iron : coin;
        public void Add(ResourceType type, int value) { if (type == ResourceType.Food) food = Math.Max(0, food + value); else if (type == ResourceType.Wood) wood = Math.Max(0, wood + value); else if (type == ResourceType.Stone) stone = Math.Max(0, stone + value); else if (type == ResourceType.Iron) iron = Math.Max(0, iron + value); else coin = Math.Max(0, coin + value); }
        public bool Spend(ResourceType type, int value) { if (Get(type) < value) return false; Add(type, -value); return true; }
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
            result.valid = result.errors.Count == 0;
            if (!result.valid) result.diagnostics.Add("규칙은 공개된 다음 턴부터 적용되며, 즉시 승리·패배·음수 자원·과도한 생성은 허용되지 않습니다.");
            return result;
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
                foreach (var effect in rule.effects) Apply(effect, game, log, rule.name);
            }
        }
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
            if (effect.type == EffectType.Resource && player != null) player.resources.Add(effect.resource, effect.amount);
            else if (effect.type == EffectType.Sp && player != null) player.sp = Math.Min(player.maxSp, Math.Max(0, player.sp + effect.amount));
            else if (effect.type == EffectType.Relation) foreach (var f in game.factions.Where(f => f.kind != FactionKind.Player)) f.relationToPlayer = Math.Max(-100, Math.Min(100, f.relationToPlayer + effect.amount));
            else if (effect.type == EffectType.Status && !string.IsNullOrEmpty(effect.key)) State[effect.key] = effect.amount;
            log.Add("[규칙] " + source + ": " + effect.type + " 효과 적용");
        }
    }

    public static class GameRules
    {
        public static int BuildingCost(BuildingType type) => type == BuildingType.Headquarters ? 0 : type == BuildingType.Warehouse || type == BuildingType.Watchtower ? 3 : 5;
        public static void StartTurn(GameSnapshotV1 game)
        {
            foreach (var faction in game.factions) faction.sp = Math.Max(3, faction.maxSp + game.buildings.Count(b => b.factionId == faction.id && b.type == BuildingType.Barracks));
            foreach (var building in game.buildings.Where(b => b.factionId == 1)) { var player = game.factions.First(f => f.id == 1); if (building.type == BuildingType.Workshop) player.resources.Add(ResourceType.Iron, building.level); if (building.type == BuildingType.Market) player.resources.Add(ResourceType.Coin, building.level); }
        }
        public static void CountAction(GameSnapshotV1 game, CommandType type) { var stat = game.actionStats.FirstOrDefault(s => s.type == type); if (stat == null) game.actionStats.Add(new ActionStat { type = type, count = 1 }); else stat.count++; }
        public static bool HeadquartersAlive(GameSnapshotV1 game) => game.buildings.Any(b => b.factionId == 1 && b.type == BuildingType.Headquarters && b.hp > 0);
        public static int Progress(GameSnapshotV1 game, string key) { if (key == "turn") return game.turn; if (key == "kills") return game.actionStats.Where(x => x.type == CommandType.Attack).Sum(x => x.count); if (key == "buildings") return game.buildings.Count(x => x.factionId == 1); if (key == "coin") return game.factions.First(x => x.id == 1).resources.coin; return game.actionStats.Where(x => x.type.ToString().ToLowerInvariant() == (key ?? "").ToLowerInvariant()).Sum(x => x.count); }
        public static bool IsVictoryComplete(GameSnapshotV1 game, VictoryContractV1 contract) => game.turn >= contract.achievableFromTurn && game.turn >= contract.announcedTurn + contract.minimumTurns && Progress(game, contract.progressKey) >= contract.target;
    }
}
