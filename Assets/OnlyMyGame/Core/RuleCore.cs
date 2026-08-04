using System;
using System.Collections.Generic;
using System.Linq;

// Unity serializes this shared DTO graph through public fields and may hydrate
// missing members as null before RuleValidator rejects them. Keep nullable
// analysis enabled in the ASP.NET project, but disable it for this Unity-owned
// wire model where explicit runtime validation is the compatibility boundary.
#nullable disable

// These wire DTOs are serialized by Newtonsoft/System.Text.Json, not Unity's
// field serializer. Recursive rule graphs are intentional and bounded by the
// validators below, so Unity serialization-cycle diagnostics do not apply.
#pragma warning disable UAC1005
#pragma warning disable UAC1006
#pragma warning disable UAC1008

namespace OnlyMyGame.Core
{
    public enum ResourceType { None, Food, Wood, Stone, Iron, Coin }
    public enum FactionKind { Player, Skeleton, Neutral }
    public enum BuildingType { Headquarters, Warehouse, Workshop, Watchtower, Market, Barracks }
    public enum EventType { TurnStart, TurnEnd, Move, Attack, Kill, Gather, Build, Trade, RelationChanged, TileEntered, Capture }
    public enum EffectType { Resource, Sp, Relation, Status, Spawn, UnlockAction, Schedule, FactionSwitch, TypedState }
    public enum CompareOp { Always, Equal, GreaterOrEqual, LessOrEqual, HasTag, OwnerIs }
    public enum CommandType { Move, Gather, Hunt, Attack, Trade, Persuade, Hire, Build, Upgrade, Dynamic, Capture }
    public enum RunOutcome { Ongoing, Victory, Defeat }
    public enum RunPhase { Planning, Resolving, AwaitingRules, Terminal }
    public enum DynamicTargetKind { None, Tile, Unit, Building }
    public enum DynamicTargetOwnership { Any, Player, NonPlayer, Neutral }
    public enum DynamicTargetVisibility { Visible, Explored }

    [Serializable] public struct HexCoord : IEquatable<HexCoord>
    {
        public int q, r;
        public HexCoord(int q, int r) { this.q = q; this.r = r; }
        public int Distance(HexCoord other)
        {
            var dq = Math.Abs((long)q - other.q);
            var ds = Math.Abs((long)q + r - other.q - other.r);
            var dr = Math.Abs((long)r - other.r);
            return (int)Math.Min(int.MaxValue, (dq + ds + dr) / 2L);
        }
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
        public int Next(int min, int max)
        {
            if (max <= min) throw new ArgumentOutOfRangeException(nameof(max), "max must be greater than min.");
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            var range = (ulong)((long)max - min);
            return (int)((long)min + (long)((state * 2685821657736338717UL) % range));
        }
        public int Percent() => Next(0, 100);
    }

    [Serializable] public sealed class ResourceBag
    {
        public int food = 12, wood = 8, stone = 6, iron = 2, coin = 5;
        // 창고(Warehouse)가 자원 상한을 늘린다. 각 자원의 기본 상한.
        public int maxFood = 30, maxWood = 30, maxStone = 30, maxIron = 30, maxCoin = 30;
        public int Get(ResourceType type) => type == ResourceType.Food ? food : type == ResourceType.Wood ? wood : type == ResourceType.Stone ? stone : type == ResourceType.Iron ? iron : type == ResourceType.Coin ? coin : 0;
        public int Max(ResourceType type) => type == ResourceType.Food ? maxFood : type == ResourceType.Wood ? maxWood : type == ResourceType.Stone ? maxStone : type == ResourceType.Iron ? maxIron : type == ResourceType.Coin ? maxCoin : 0;
        public void Add(ResourceType type, int value)
        {
            if (type == ResourceType.Food) food = AddClamped(food, value, maxFood);
            else if (type == ResourceType.Wood) wood = AddClamped(wood, value, maxWood);
            else if (type == ResourceType.Stone) stone = AddClamped(stone, value, maxStone);
            else if (type == ResourceType.Iron) iron = AddClamped(iron, value, maxIron);
            else if (type == ResourceType.Coin) coin = AddClamped(coin, value, maxCoin);
        }
        public bool Spend(ResourceType type, int value) { if (value < 0 || !Enum.IsDefined(typeof(ResourceType), type)) return false; if (type == ResourceType.None) return value == 0; if (Get(type) < value) return false; Add(type, -value); return true; }
        // 창고 레벨 합계만큼 모든 자원 상한을 늘린다. 레벨 1 = +10, 레벨 2 = +20, ...
        public void ApplyWarehouseBonus(int warehouseLevels)
        {
            var bonus = Math.Max(0, Math.Min(RuleLimits.MaxEffectMagnitude, warehouseLevels)) * 10;
            maxFood = 30 + bonus; maxWood = 30 + bonus; maxStone = 30 + bonus; maxIron = 30 + bonus; maxCoin = 30 + bonus;
            food = Math.Max(0, Math.Min(food, maxFood)); wood = Math.Max(0, Math.Min(wood, maxWood)); stone = Math.Max(0, Math.Min(stone, maxStone)); iron = Math.Max(0, Math.Min(iron, maxIron)); coin = Math.Max(0, Math.Min(coin, maxCoin));
        }

        private static int AddClamped(int current, int amount, int maximum) => (int)Math.Max(0L, Math.Min(Math.Max(0L, maximum), (long)current + amount));
    }
    [Serializable] public sealed class TileState { public HexCoord position; public string terrain; public ResourceType resource; public int amount; public int owner; public bool explored; public bool visible; }
    [Serializable] public sealed class UnitState { public int id; public int factionId; public HexCoord position; public int hp = 5; public int speed = 2; public bool alive = true; public List<string> tags = new List<string>(); }
    [Serializable] public sealed class BuildingState { public int id; public int factionId; public HexCoord position; public BuildingType type; public int level = 1; public int hp = 12; }
    [Serializable] public sealed class FactionState { public int id; public string name; public FactionKind kind; public ResourceBag resources = new ResourceBag(); public int maxSp = 10; public int sp = 10; public int relationToPlayer; }
    [Serializable] public sealed class ActionStat { public CommandType type; public int count; }
    [Serializable] public sealed class RuleStateEntry { public string key; public int value; }
    public static class RuleLimits
    {
        public const int MaxActiveRules = 12;
        public const int MaxStoredRules = 48;
        public const int MaxDynamicActions = 32;
        public const int MaxVictoryContracts = 3;
        public const int MaxDynamicActionsPerRuleSet = 3;
        public const int MaxConditionNodes = 256;
        public const int MaxConditionDepth = 4;
        // Selector work is charged before a condition is evaluated. These caps
        // bound both one UI availability query and all rule attempts in a turn
        // on WebGL's single main thread.
        public const int MaxConditionWorkPerEvaluation = 32768;
        public const int MaxRuleConditionWorkPerTurn = 131072;
        public const int MaxEffectsPerRule = 16;
        public const int MaxRuleDispatchesPerTurn = 64;
        public const int MaxRuleActivationsPerTurn = 64;
        public const int MaxRuleEffectsPerTurn = 256;
        public const int MaxRuleSpawnsPerTurn = 4;
        public const int MaxEntities = 4096;
        public const int MaxBuildings = 2048;
        public const int MaxFactions = 64;
        public const int MaxMapTiles = 4096;
        public const int MaxTagsPerUnit = 32;
        public const int MaxJournalEntries = 512;
        public const int MaxStateVariables = 128;
        // A 30-turn commercial run can receive rules every turn. Capping the
        // newly reserved state identities per response keeps the worst-case
        // run below the persistent state ceiling without deleting run history.
        public const int MaxStateDefinitionsPerRule = 4;
        public const int MaxNewStateIdentitiesPerRuleSet = 4;
        public const int MaxStateSetElements = 32;
        public const int MaxRecentActionTurns = 6;
        public const int MaxRecentActionEntries = 128;
        public const int MaxDynamicTargetDistance = 32;
        public const int MaxDynamicTargetCandidates = 32;
        public const int MaxDynamicTargetScanCandidates = 4096;
        // Includes index construction, source collection scans, and deterministic
        // ordering for one selected actor. Keep this separate from expression and
        // binding budgets so a large observable world cannot monopolize WebGL's
        // main thread before those later budgets are reached.
        public const int MaxDynamicTargetResolutionWork = 131072;
        public const int MaxDynamicTargetConditionWork = 65536;
        public const int MaxDynamicTargetBindingWork = 65536;
        public const int MaxDynamicTargetValidationWork = 262144;
        public const int MaxDynamicTargetBatchActions = MaxDynamicActionsPerRuleSet;
        public const int MaxStateMagnitude = 1000000;
        public const int MaxIdentifierLength = 64;
        public const int MaxNameLength = 80;
        public const int MaxDescriptionLength = 600;
        public const int MaxScheduleDelay = 30;
        public const int MaxEffectMagnitude = 1000;
    }
    [Serializable] public sealed class RuleRuntimeBudget
    {
        public int turn = int.MinValue;
        [NonSerialized] internal int definitionRegistryTurn = int.MinValue;
        [NonSerialized] internal List<StateDefinitionV1> definitionRegistryDefinitions;
        public int dispatches;
        public int conditionWork;
        public int activations;
        public int effects;
        public int spawnedEntities;
        public int loggedLimits;
    }
    [Serializable] public sealed class GameSnapshotV1
    {
        public string runId;
        public int turn;
        public int seed;
        public int luck;
        public int playerKills;
        public RunOutcome outcome;
        public RunPhase phase;
        public string completedContractId;
        public bool planningPrepared;
        public List<TileState> map = new List<TileState>();
        public List<UnitState> entities = new List<UnitState>();
        public List<BuildingState> buildings = new List<BuildingState>();
        public List<FactionState> factions = new List<FactionState>();
        public List<ActionStat> actionStats = new List<ActionStat>();
        public List<RuleNodeV1> activeRules = new List<RuleNodeV1>();
        public List<VictoryContractV1> victoryContracts = new List<VictoryContractV1>();
        public List<DynamicActionV1> dynamicActions = new List<DynamicActionV1>();
        public List<RuleStateEntry> ruleState = new List<RuleStateEntry>();
        public List<TypedRuleStateEntryV1> typedRuleState = new List<TypedRuleStateEntryV1>();
        public List<ActionTurnStatV1> recentActionStats = new List<ActionTurnStatV1>();
        public RuleRuntimeBudget ruleBudget = new RuleRuntimeBudget();
        public List<string> journal = new List<string>();
        public string catalogHash = "kaykit-v1";
    }

    [Serializable] public sealed class ConditionNode { public CompareOp op; public string left; public int value; public string text; public List<ConditionNode> all; public PredicateExpressionV1 predicate; }
    [Serializable] public sealed class EffectNode { public EffectType type; public ResourceType resource; public int amount; public string target; public string key; public string value; public int delay; public StateMutationV1 stateMutation; }
    [Serializable] public sealed class RuleNodeV1 { public string id; public string name; public string description; public EventType trigger; public ConditionNode condition = new ConditionNode { op = CompareOp.Always }; public List<EffectNode> effects = new List<EffectNode>(); public List<StateDefinitionV1> stateDefinitions = new List<StateDefinitionV1>(); public int priority; public int durationTurns = 3; public int appliedTurn; public string worldCue; }
    [Serializable] public sealed class DynamicTargetSelectorV1
    {
        public DynamicTargetKind kind = DynamicTargetKind.None;
        public DynamicTargetOwnership ownership = DynamicTargetOwnership.Any;
        public DynamicTargetVisibility visibility = DynamicTargetVisibility.Visible;
        public int minDistance;
        public int maxDistance;
        public int maxCandidates = 16;
    }
    [Serializable] public sealed class DynamicActionV1 { public string id; public string name; public string description; public int spCost; public ResourceType resourceCost; public int resourceAmount; public int cooldown; public int availableTurn; public DynamicTargetSelectorV1 targetSelector = new DynamicTargetSelectorV1(); public ConditionNode condition = new ConditionNode { op = CompareOp.Always }; public List<EffectNode> effects = new List<EffectNode>(); }
    [Serializable] public sealed class VictoryContractV1 { public string id; public string title; public string description; public string progressKey; public int target; public int minimumTurns = 3; public int announcedTurn; public int achievableFromTurn; public int replaceWarningTurn; public string worldCue; }
    [Serializable] public sealed class RuleSetV1 { public string schemaVersion = "v1"; public string requestId; public int applyTurn; public string koreanSummary; public List<RuleNodeV1> changes = new List<RuleNodeV1>(); public List<DynamicActionV1> actions = new List<DynamicActionV1>(); public List<VictoryContractV1> victoryContracts = new List<VictoryContractV1>(); }
    [Serializable] public sealed class RuleValidationResult { public bool valid; public List<string> errors = new List<string>(); public List<string> diagnostics = new List<string>(); }

    internal sealed class RuleValidationWorkBudget
    {
        private int remaining;
        public bool Exhausted { get; private set; }

        public RuleValidationWorkBudget(int maximum) { remaining = Math.Max(0, maximum); }

        public bool TrySpend(long amount)
        {
            amount = Math.Max(1L, amount);
            if (amount > remaining)
            {
                remaining = 0;
                Exhausted = true;
                return false;
            }
            remaining -= (int)amount;
            return true;
        }
    }

    public static class RuleValidator
    {
        public const int MinimumFirstVictoryTurns = 18;

        public static RuleValidationResult ValidateSnapshot(GameSnapshotV1 snapshot)
        {
            var result = new RuleValidationResult { valid = false };
            if (snapshot == null)
            {
                result.errors.Add("SNAPSHOT_NULL");
                return result;
            }

            ValidateSnapshotBounds(snapshot, result);
            result.valid = result.errors.Count == 0;
            return result;
        }

        public static RuleValidationResult ValidateDynamicActionForRuntime(DynamicActionV1 action, GameSnapshotV1 snapshot)
        {
            var result = new RuleValidationResult { valid = false };
            if (snapshot == null)
            {
                result.errors.Add("SNAPSHOT_NULL");
                return result;
            }

            ValidateSnapshotBounds(snapshot, result);
            // Runtime button gating uses the current world, so a targeted action
            // must have at least one candidate whose bindings can execute. Stored
            // snapshot validation remains structural and may preserve actions that
            // become available again after visibility or ownership changes.
            ValidateDynamicAction(action, snapshot, result, false, true);
            DynamicActionTargeting.ValidateTargetAvailability(new[] { action }, snapshot, result.errors, "ACTION");
            result.valid = result.errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Rechecks a live action's bounded structure without scanning every actor
        /// for current targets. Ingress and save-load validation own the expensive
        /// receipt/world checks; HUD polling pairs this with the selected-actor
        /// resolver instead.
        /// </summary>
        public static RuleValidationResult ValidateDynamicActionStructureForRuntime(DynamicActionV1 action, GameSnapshotV1 snapshot)
        {
            var result = new RuleValidationResult { valid = false };
            if (snapshot == null)
            {
                result.errors.Add("SNAPSHOT_NULL");
                return result;
            }

            ValidateRuntimeCollectionShape(snapshot, result);
            ValidateDynamicAction(action, snapshot, result, false, false);
            result.valid = result.errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Rechecks references whose validity depends on the current world without
        /// performing the expensive all-actor target-availability scan. Execution
        /// uses this after HUD structure checks and immediately before mutation.
        /// </summary>
        public static RuleValidationResult ValidateDynamicActionCurrentWorldForRuntime(DynamicActionV1 action, GameSnapshotV1 snapshot)
        {
            var result = new RuleValidationResult { valid = false };
            if (snapshot == null)
            {
                result.errors.Add("SNAPSHOT_NULL");
                return result;
            }

            ValidateRuntimeCollectionShape(snapshot, result);
            ValidateDynamicAction(action, snapshot, result, false, true);
            result.valid = result.errors.Count == 0;
            return result;
        }

        public static RuleValidationResult Validate(RuleSetV1 set, GameSnapshotV1 snapshot)
        {
            var result = new RuleValidationResult { valid = false };
            if (set == null) { result.errors.Add("RULESET_NULL"); return result; }
            if (snapshot == null) { result.errors.Add("SNAPSHOT_NULL"); return result; }

            var activeRules = snapshot.activeRules ?? new List<RuleNodeV1>();
            var existingActions = snapshot.dynamicActions ?? new List<DynamicActionV1>();
            var existingGoals = snapshot.victoryContracts ?? new List<VictoryContractV1>();
            var factions = snapshot.factions ?? new List<FactionState>();
            var changes = set.changes ?? new List<RuleNodeV1>();
            var actions = set.actions ?? new List<DynamicActionV1>();
            var goals = set.victoryContracts ?? new List<VictoryContractV1>();

            ValidateSnapshotBounds(snapshot, result);
            if (!string.Equals(set.schemaVersion, "v1", StringComparison.Ordinal)) result.errors.Add("SCHEMA_VERSION_UNSUPPORTED");
            if (!IsBoundedText(set.requestId, RuleLimits.MaxIdentifierLength, false)) result.errors.Add("REQUEST_ID_INVALID");
            if (!IsBoundedText(set.koreanSummary, RuleLimits.MaxDescriptionLength, true)) result.errors.Add("SUMMARY_TOO_LONG");
            if ((long)set.applyTurn < snapshot.turn || (long)set.applyTurn > (long)snapshot.turn + 1L) result.errors.Add("APPLY_TURN_INVALID");
            if (set.changes == null || changes.Count < 1 || changes.Count > 3) result.errors.Add("RULE_COUNT_1_TO_3");
            if (set.actions == null) result.errors.Add("ACTIONS_NULL");
            else if (set.actions.Count > RuleLimits.MaxDynamicActionsPerRuleSet) result.errors.Add("RULESET_ACTION_LIMIT");
            if (set.victoryContracts == null) result.errors.Add("VICTORY_CONTRACTS_NULL");
            var needsFirstContract = snapshot.turn >= 2 && !existingGoals.Any(goal => goal != null);
            if (needsFirstContract && goals.Count == 0) result.errors.Add("VICTORY_CONTRACT_REQUIRED");
            if (needsFirstContract && goals.All(goal => goal == null || goal.minimumTurns < MinimumFirstVictoryTurns)) result.errors.Add("FIRST_VICTORY_TOO_EARLY");

            var nextTurn = snapshot.turn == int.MaxValue ? int.MaxValue : snapshot.turn + 1;
            var effectiveApplyTurn = set.applyTurn >= snapshot.turn && set.applyTurn <= nextTurn ? set.applyTurn : nextTurn;
            var activeIdsAtApply = ExistingIds(activeRules.Where(rule => rule != null && GameRules.IsRuleActive(rule, effectiveApplyTurn)).Select(rule => rule.id));
            var storedRuleIds = ExistingIds(activeRules.Where(rule => rule != null).Select(rule => rule.id));
            var actionIds = ExistingIds(existingActions.Where(action => action != null).Select(action => action.id));
            var goalIds = ExistingIds(existingGoals.Where(goal => goal != null).Select(goal => goal.id));
            if (activeIdsAtApply.Count + CountDistinctAdditions(changes, activeIdsAtApply, rule => rule?.id) > RuleLimits.MaxActiveRules) result.errors.Add("ACTIVE_RULE_LIMIT");
            if (storedRuleIds.Count + CountDistinctAdditions(changes, storedRuleIds, rule => rule?.id) > RuleLimits.MaxStoredRules) result.errors.Add("STORED_RULE_LIMIT");
            if (actionIds.Count + CountDistinctAdditions(actions, actionIds, action => action?.id) > RuleLimits.MaxDynamicActions) result.errors.Add("DYNAMIC_ACTION_LIMIT");
            if (goalIds.Count + CountDistinctAdditions(goals, goalIds, goal => goal?.id) > RuleLimits.MaxVictoryContracts) result.errors.Add("VICTORY_LIMIT");

            ValidateUniqueIds(changes.Where(x => x != null).Select(x => x.id), "DUPLICATE_RULE_ID", result);
            ValidateUniqueIds(actions.Where(x => x != null).Select(x => x.id), "DUPLICATE_ACTION_ID", result);
            ValidateUniqueIds(goals.Where(x => x != null).Select(x => x.id), "DUPLICATE_VICTORY_ID", result);
            foreach (var rule in changes) ValidateRule(rule, snapshot, effectiveApplyTurn, result);
            foreach (var action in actions) ValidateDynamicAction(action, snapshot, result, false, true);
            DynamicActionTargeting.ValidateTargetAvailability(actions, snapshot, result.errors, "ACTION");
            foreach (var goal in goals) ValidateVictory(goal, snapshot, result);
            RuleExpressionValidator.ValidateProjectedDefinitions(changes, actions, snapshot, result.errors);

            if (factions.Count == 0 || factions.Any(f => f == null || f.maxSp < 3 || f.resources == null)) result.errors.Add("MIN_SP_OR_FACTION_STATE_VIOLATION");
            var declaredSpawns = SumSpawnAmounts(changes.SelectMany(r => r?.effects ?? new List<EffectNode>())) +
                                 SumSpawnAmounts(actions.SelectMany(a => a?.effects ?? new List<EffectNode>()));
            if (declaredSpawns > RuleLimits.MaxRuleSpawnsPerTurn) result.errors.Add("SPAWN_BUDGET_EXCEEDED");

            if (result.errors.Count == 0 && !SimulateSixTurns(set, snapshot)) result.errors.Add("SIX_TURN_SIMULATION_FAILED");
            result.valid = result.errors.Count == 0;
            if (!result.valid) result.diagnostics.Add("규칙은 공개된 다음 턴부터 적용되며, 즉시 승리·패배·음수 자원·과도한 생성은 허용되지 않습니다.");
            return result;
        }

        private static void ValidateSnapshotBounds(GameSnapshotV1 snapshot, RuleValidationResult result)
        {
            if (snapshot.turn < 0 || snapshot.turn > RuleLimits.MaxStateMagnitude || snapshot.luck < -RuleLimits.MaxStateMagnitude || snapshot.luck > RuleLimits.MaxStateMagnitude || snapshot.playerKills < 0 || snapshot.playerKills > RuleLimits.MaxStateMagnitude)
                result.errors.Add("SNAPSHOT_SCALAR_LIMIT");
            if (snapshot.map == null || snapshot.entities == null || snapshot.buildings == null || snapshot.factions == null || snapshot.actionStats == null || snapshot.activeRules == null || snapshot.victoryContracts == null || snapshot.dynamicActions == null || snapshot.ruleState == null)
                result.errors.Add("SNAPSHOT_LIST_NULL");
            if ((snapshot.map?.Count ?? 0) > RuleLimits.MaxMapTiles) result.errors.Add("MAP_TILE_LIMIT");
            if ((snapshot.entities?.Count ?? 0) > RuleLimits.MaxEntities) result.errors.Add("ENTITY_LIMIT");
            if ((snapshot.buildings?.Count ?? 0) > RuleLimits.MaxBuildings) result.errors.Add("BUILDING_LIMIT");
            if ((snapshot.factions?.Count ?? 0) > RuleLimits.MaxFactions) result.errors.Add("FACTION_LIMIT");
            if ((snapshot.journal?.Count ?? 0) > RuleLimits.MaxJournalEntries || (snapshot.journal ?? new List<string>()).Any(entry => entry != null && entry.Length > RuleLimits.MaxDescriptionLength)) result.errors.Add("JOURNAL_LIMIT");

            ValidateMapState(snapshot, result);
            ValidateUniqueEntityState(snapshot, result);
            var actionTypes = new HashSet<CommandType>();
            foreach (var stat in snapshot.actionStats ?? new List<ActionStat>())
            {
                if (stat == null || !Enum.IsDefined(typeof(CommandType), stat.type) || stat.count < 0 || stat.count > RuleLimits.MaxStateMagnitude || !actionTypes.Add(stat.type)) result.errors.Add("ACTION_STAT_INVALID");
            }
            var ruleBudget = snapshot.ruleBudget;
            var validBudgetFlags = 1 | 2 | 4 | 8 | 16 | 32 | 64;
            if (ruleBudget != null && ruleBudget.turn != int.MinValue && (ruleBudget.turn < 0 || ruleBudget.turn > snapshot.turn || ruleBudget.dispatches < 0 || ruleBudget.dispatches > RuleLimits.MaxRuleDispatchesPerTurn || ruleBudget.conditionWork < 0 || ruleBudget.conditionWork > RuleLimits.MaxRuleConditionWorkPerTurn || ruleBudget.activations < 0 || ruleBudget.activations > RuleLimits.MaxRuleActivationsPerTurn || ruleBudget.effects < 0 || ruleBudget.effects > RuleLimits.MaxRuleEffectsPerTurn || ruleBudget.spawnedEntities < 0 || ruleBudget.spawnedEntities > RuleLimits.MaxRuleSpawnsPerTurn || (ruleBudget.loggedLimits & ~validBudgetFlags) != 0)) result.errors.Add("RULE_RUNTIME_BUDGET_INVALID");
            var states = snapshot.ruleState ?? new List<RuleStateEntry>();
            if (states.Count > RuleLimits.MaxStateVariables) result.errors.Add("STATE_VARIABLE_LIMIT");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in states)
            {
                if (state == null || !IsBoundedText(state.key, RuleLimits.MaxIdentifierLength, false)) { result.errors.Add("STATE_KEY_INVALID"); continue; }
                if (!keys.Add(state.key)) result.errors.Add("STATE_KEY_DUPLICATE:" + SafeId(state.key));
                if (!IsBoundedStateValue(state.value)) result.errors.Add("STATE_VALUE_LIMIT:" + SafeId(state.key));
            }
            RuleExpressionValidator.ValidateSnapshot(snapshot, result.errors);
            if ((snapshot.activeRules?.Count ?? 0) > RuleLimits.MaxStoredRules) result.errors.Add("STORED_RULE_LIMIT");
            if ((snapshot.dynamicActions?.Count ?? 0) > RuleLimits.MaxDynamicActions) result.errors.Add("DYNAMIC_ACTION_LIMIT");
            if ((snapshot.victoryContracts?.Count ?? 0) > RuleLimits.MaxVictoryContracts) result.errors.Add("VICTORY_LIMIT");
            ValidateStoredRuleContent(snapshot, result);
        }

        private static void ValidateRuntimeCollectionShape(GameSnapshotV1 snapshot, RuleValidationResult result)
        {
            if (snapshot.map == null || snapshot.entities == null || snapshot.buildings == null || snapshot.factions == null)
            {
                result.errors.Add("SNAPSHOT_LIST_NULL");
                return;
            }
            if (snapshot.map.Count > RuleLimits.MaxMapTiles) result.errors.Add("MAP_TILE_LIMIT");
            if (snapshot.entities.Count > RuleLimits.MaxEntities) result.errors.Add("ENTITY_LIMIT");
            if (snapshot.buildings.Count > RuleLimits.MaxBuildings) result.errors.Add("BUILDING_LIMIT");
            if (snapshot.factions.Count > RuleLimits.MaxFactions) result.errors.Add("FACTION_LIMIT");
        }

        private static void ValidateStoredRuleContent(GameSnapshotV1 snapshot, RuleValidationResult result)
        {
            // Collection-count failures already make the snapshot invalid. Bound the deep
            // walk as well so malformed JSON cannot amplify validation work after parsing.
            var storedRules = (snapshot.activeRules ?? new List<RuleNodeV1>()).Take(RuleLimits.MaxStoredRules + 1).ToList();
            var storedActions = (snapshot.dynamicActions ?? new List<DynamicActionV1>()).Take(RuleLimits.MaxDynamicActions + 1).ToList();
            var storedGoals = (snapshot.victoryContracts ?? new List<VictoryContractV1>()).Take(RuleLimits.MaxVictoryContracts + 1).ToList();

            ValidateUniqueIds(storedRules.Where(rule => rule != null).Select(rule => rule.id), "SNAPSHOT_RULE_ID_DUPLICATE", result);
            ValidateUniqueIds(storedActions.Where(action => action != null).Select(action => action.id), "SNAPSHOT_ACTION_ID_DUPLICATE", result);
            ValidateUniqueIds(storedGoals.Where(goal => goal != null).Select(goal => goal.id), "SNAPSHOT_VICTORY_ID_DUPLICATE", result);

            var activeRuleCount = 0;
            foreach (var rule in storedRules)
            {
                ValidateStoredRule(rule, snapshot, result);
                if (rule != null && GameRules.IsRuleActive(rule, snapshot.turn)) activeRuleCount++;
            }
            if (activeRuleCount > RuleLimits.MaxActiveRules) result.errors.Add("ACTIVE_RULE_LIMIT");

            foreach (var action in storedActions)
            {
                if (action == null) result.errors.Add("SNAPSHOT_DYNAMIC_ACTION_NULL");
                else ValidateDynamicAction(action, snapshot, result, false, false);
            }
            foreach (var goal in storedGoals) ValidateStoredVictory(goal, snapshot, result);
        }

        private static void ValidateUniqueEntityState(GameSnapshotV1 snapshot, RuleValidationResult result)
        {
            var factionIds = new HashSet<int>();
            foreach (var faction in snapshot.factions ?? new List<FactionState>())
            {
                if (faction == null || faction.id <= 0 || faction.id > RuleLimits.MaxStateMagnitude || !factionIds.Add(faction.id) || !Enum.IsDefined(typeof(FactionKind), faction.kind) || !IsValidResourceBag(faction.resources) || faction.maxSp < 3 || faction.maxSp > RuleLimits.MaxEffectMagnitude + 10 || faction.sp < 0 || faction.sp > faction.maxSp || faction.relationToPlayer < -100 || faction.relationToPlayer > 100) result.errors.Add("FACTION_STATE_INVALID");
            }
            if (!(snapshot.factions ?? new List<FactionState>()).Any(f => f != null && f.id == 1 && f.kind == FactionKind.Player)) result.errors.Add("PLAYER_FACTION_MISSING");

            var mapPositions = new HashSet<HexCoord>((snapshot.map ?? new List<TileState>()).Where(tile => tile != null).Select(tile => tile.position));
            var unitIds = new HashSet<int>();
            foreach (var unit in snapshot.entities ?? new List<UnitState>())
            {
                if (unit == null || unit.id <= 0 || unit.id > RuleLimits.MaxStateMagnitude || !unitIds.Add(unit.id) || !factionIds.Contains(unit.factionId) || !mapPositions.Contains(unit.position) || unit.hp < 0 || unit.hp > RuleLimits.MaxStateMagnitude || unit.alive && unit.hp <= 0 || unit.speed < 0 || unit.speed > RuleLimits.MaxEffectMagnitude || (unit.tags?.Count ?? 0) > RuleLimits.MaxTagsPerUnit || (unit.tags ?? new List<string>()).Any(tag => !IsBoundedText(tag, RuleLimits.MaxIdentifierLength, false))) result.errors.Add("UNIT_STATE_INVALID");
            }

            var buildingIds = new HashSet<int>();
            foreach (var building in snapshot.buildings ?? new List<BuildingState>())
            {
                if (building == null || building.id <= 0 || building.id > RuleLimits.MaxStateMagnitude || !buildingIds.Add(building.id) || !factionIds.Contains(building.factionId) || !mapPositions.Contains(building.position) || !Enum.IsDefined(typeof(BuildingType), building.type) || building.level < 1 || building.level > RuleLimits.MaxEffectMagnitude || building.hp < 0 || building.hp > RuleLimits.MaxStateMagnitude) result.errors.Add("BUILDING_STATE_INVALID");
            }
        }

        private static void ValidateMapState(GameSnapshotV1 snapshot, RuleValidationResult result)
        {
            var factionIds = new HashSet<int>((snapshot.factions ?? new List<FactionState>()).Where(faction => faction != null && faction.id > 0).Select(faction => faction.id));
            var positions = new HashSet<HexCoord>();
            foreach (var tile in snapshot.map ?? new List<TileState>())
            {
                if (tile == null)
                {
                    result.errors.Add("TILE_STATE_INVALID");
                    continue;
                }

                var coordinateInRange = Math.Abs((long)tile.position.q) <= RuleLimits.MaxStateMagnitude && Math.Abs((long)tile.position.r) <= RuleLimits.MaxStateMagnitude;
                var ownerExists = tile.owner == 0 || factionIds.Contains(tile.owner);
                var stateIsValid = coordinateInRange && positions.Add(tile.position) &&
                                   IsBoundedText(tile.terrain, RuleLimits.MaxIdentifierLength, false) &&
                                   Enum.IsDefined(typeof(ResourceType), tile.resource) && tile.amount >= 0 && tile.amount <= RuleLimits.MaxStateMagnitude &&
                                   ownerExists && (!tile.visible || tile.explored);
                if (!stateIsValid) result.errors.Add("TILE_STATE_INVALID");
            }
        }

        private static void ValidateRule(RuleNodeV1 rule, GameSnapshotV1 snapshot, int applyTurn, RuleValidationResult result)
        {
            if (rule == null) { result.errors.Add("RULE_NULL"); return; }
            ValidateRuleContent(rule, snapshot, "RULE", result, true);
            var id = SafeId(rule.id);
            if (rule.appliedTurn != 0 && rule.appliedTurn != applyTurn) result.errors.Add("RULE_APPLY_TURN_MISMATCH:" + id);
        }

        private static void ValidateStoredRule(RuleNodeV1 rule, GameSnapshotV1 snapshot, RuleValidationResult result)
        {
            if (rule == null) { result.errors.Add("SNAPSHOT_RULE_NULL"); return; }
            ValidateRuleContent(rule, snapshot, "SNAPSHOT_RULE", result, false);
            var id = SafeId(rule.id);
            // Existing rules may be expired or may be scheduled for a future trigger. Only
            // impossible negative/far-future application turns are rejected here.
            if (rule.appliedTurn < 0 || (long)rule.appliedTurn > (long)snapshot.turn + RuleLimits.MaxScheduleDelay) result.errors.Add("STORED_RULE_APPLY_TURN_INVALID:" + id);
        }

        private static void ValidateRuleContent(
            RuleNodeV1 rule,
            GameSnapshotV1 snapshot,
            string source,
            RuleValidationResult result,
            bool requireCurrentWorldReferences)
        {
            var currentWorldBudget = requireCurrentWorldReferences
                ? new RuleValidationWorkBudget(RuleLimits.MaxConditionWorkPerEvaluation)
                : null;
            var id = SafeId(rule.id);
            if (!IsBoundedText(rule.id, RuleLimits.MaxIdentifierLength, false) || !IsBoundedText(rule.name, RuleLimits.MaxNameLength, false)) result.errors.Add("RULE_ID_OR_NAME:" + id);
            if (!IsBoundedText(rule.description, RuleLimits.MaxDescriptionLength, false) || !IsBoundedText(rule.worldCue, RuleLimits.MaxNameLength, true)) result.errors.Add("RULE_TEXT_LIMIT:" + id);
            if (!Enum.IsDefined(typeof(EventType), rule.trigger)) result.errors.Add("RULE_TRIGGER_INVALID:" + id);
            if (rule.durationTurns < 1 || rule.durationTurns > 30) result.errors.Add("RULE_DURATION_INVALID:" + id);
            if (!IsBoundedStateValue(rule.priority)) result.errors.Add("RULE_PRIORITY_INVALID:" + id);
            ValidateConditionTree(rule.condition, snapshot, source + ":" + id, result, null, requireCurrentWorldReferences, currentWorldBudget);
            ValidateEffects(rule.effects, snapshot, source + ":" + id, result, null, requireCurrentWorldReferences, currentWorldBudget);
            RuleExpressionValidator.ValidateRule(rule, snapshot, source + ":" + id, result.errors, requireCurrentWorldReferences, currentWorldBudget);
        }

        private static void ValidateDynamicAction(
            DynamicActionV1 action,
            GameSnapshotV1 snapshot,
            RuleValidationResult result,
            bool requireTargetCandidate,
            bool requireCurrentWorldReferences)
        {
            if (action == null) { result.errors.Add("DYNAMIC_ACTION_NULL"); return; }
            var currentWorldBudget = requireCurrentWorldReferences
                ? new RuleValidationWorkBudget(RuleLimits.MaxConditionWorkPerEvaluation)
                : null;
            var id = SafeId(action.id);
            if (!IsBoundedText(action.id, RuleLimits.MaxIdentifierLength, false) || !IsBoundedText(action.name, RuleLimits.MaxNameLength, false)) result.errors.Add("DYNAMIC_ACTION_ID_OR_NAME:" + id);
            if (!IsBoundedText(action.description, RuleLimits.MaxDescriptionLength, false)) result.errors.Add("DYNAMIC_ACTION_TEXT_LIMIT:" + id);
            if (action.spCost < 0 || action.spCost > 10 || action.cooldown < 0 || action.cooldown > RuleLimits.MaxScheduleDelay) result.errors.Add("DYNAMIC_ACTION_COST_OR_COOLDOWN:" + id);
            if (action.resourceAmount < 0 || action.resourceAmount > RuleLimits.MaxEffectMagnitude) result.errors.Add("DYNAMIC_ACTION_RESOURCE_AMOUNT:" + id);
            if (!Enum.IsDefined(typeof(ResourceType), action.resourceCost) || action.resourceAmount > 0 && action.resourceCost == ResourceType.None) result.errors.Add("DYNAMIC_ACTION_RESOURCE_TYPE:" + id);
            // A ready action may legitimately have an old availableTurn. Cooldowns can only
            // place it within the configured scheduling window ahead of the snapshot.
            if (action.availableTurn < 0 || (long)action.availableTurn > (long)snapshot.turn + RuleLimits.MaxScheduleDelay) result.errors.Add("DYNAMIC_ACTION_AVAILABLE_TURN:" + id);
            if (action.spCost == 0 && action.resourceAmount == 0 && action.cooldown == 0) result.errors.Add("DYNAMIC_ACTION_FREE_REPEAT:" + id);
            DynamicActionTargeting.ValidateSelectorAndBindings(action, snapshot, result.errors, "ACTION:" + id, requireTargetCandidate);
            ValidateConditionTree(action.condition, snapshot, "ACTION:" + id, result, action.targetSelector, requireCurrentWorldReferences, currentWorldBudget);
            ValidateEffects(action.effects, snapshot, "ACTION:" + id, result, action.targetSelector, requireCurrentWorldReferences, currentWorldBudget);
            RuleExpressionValidator.ValidateAction(action, snapshot, "ACTION:" + id, result.errors, requireCurrentWorldReferences, currentWorldBudget);
        }

        private static void ValidateVictory(VictoryContractV1 goal, GameSnapshotV1 snapshot, RuleValidationResult result)
        {
            if (goal == null) { result.errors.Add("VICTORY_NULL"); return; }
            var id = SafeId(goal.id);
            var nextTurn = (long)snapshot.turn + 1L;
            if (!IsBoundedText(goal.id, RuleLimits.MaxIdentifierLength, false) || !IsBoundedText(goal.title, RuleLimits.MaxNameLength, false)) result.errors.Add("VICTORY_ID_OR_TITLE:" + id);
            if (!IsBoundedText(goal.description, RuleLimits.MaxDescriptionLength, false) || !IsBoundedText(goal.worldCue, RuleLimits.MaxNameLength, true)) result.errors.Add("VICTORY_TEXT_LIMIT:" + id);
            var existing = (snapshot.victoryContracts ?? new List<VictoryContractV1>()).FirstOrDefault(contract => contract != null && string.Equals(contract.id, goal.id, StringComparison.Ordinal));
            var unchangedDefinition = existing != null && SameVictoryDefinition(existing, goal);
            var historicalTimingIsValid = existing != null && unchangedDefinition &&
                                          goal.announcedTurn == existing.announcedTurn &&
                                          goal.achievableFromTurn == existing.achievableFromTurn &&
                                          (goal.replaceWarningTurn == existing.replaceWarningTurn || goal.replaceWarningTurn == snapshot.turn);
            var newTimingIsValid = goal.announcedTurn >= 0 && goal.announcedTurn <= nextTurn && goal.achievableFromTurn > snapshot.turn && (long)goal.achievableFromTurn <= (long)snapshot.turn + RuleLimits.MaxScheduleDelay;
            if (goal.target <= 0 || goal.target > RuleLimits.MaxStateMagnitude || goal.minimumTurns < 3 || goal.minimumTurns > RuleLimits.MaxScheduleDelay || goal.replaceWarningTurn < 0 || goal.replaceWarningTurn > RuleLimits.MaxStateMagnitude || !historicalTimingIsValid && !newTimingIsValid) result.errors.Add("INVALID_VICTORY:" + id);
            if (!IsKnownProgressKey(goal.progressKey)) result.errors.Add("UNKNOWN_PROGRESS_KEY:" + id);
            if (existing != null && !unchangedDefinition && ((long)snapshot.turn < (long)existing.announcedTurn + Math.Max(3, existing.minimumTurns) || existing.replaceWarningTurn <= 0 || existing.replaceWarningTurn >= snapshot.turn))
                result.errors.Add("VICTORY_REPLACEMENT_NOT_WARNED:" + id);
            if (existing != null && unchangedDefinition && goal.replaceWarningTurn == snapshot.turn && (long)snapshot.turn + 1L < (long)existing.announcedTurn + Math.Max(3, existing.minimumTurns))
                result.errors.Add("VICTORY_WARNING_TOO_EARLY:" + id);
        }

        private static void ValidateStoredVictory(VictoryContractV1 goal, GameSnapshotV1 snapshot, RuleValidationResult result)
        {
            if (goal == null) { result.errors.Add("SNAPSHOT_VICTORY_NULL"); return; }
            var id = SafeId(goal.id);
            if (!IsBoundedText(goal.id, RuleLimits.MaxIdentifierLength, false) || !IsBoundedText(goal.title, RuleLimits.MaxNameLength, false)) result.errors.Add("VICTORY_ID_OR_TITLE:" + id);
            if (!IsBoundedText(goal.description, RuleLimits.MaxDescriptionLength, false) || !IsBoundedText(goal.worldCue, RuleLimits.MaxNameLength, true)) result.errors.Add("VICTORY_TEXT_LIMIT:" + id);
            if (goal.target <= 0 || goal.target > RuleLimits.MaxStateMagnitude || goal.minimumTurns < 3 || goal.minimumTurns > RuleLimits.MaxScheduleDelay || goal.replaceWarningTurn < 0 || goal.replaceWarningTurn > RuleLimits.MaxStateMagnitude) result.errors.Add("VICTORY_BOUNDS_INVALID:" + id);
            if (!IsKnownProgressKey(goal.progressKey)) result.errors.Add("UNKNOWN_PROGRESS_KEY:" + id);

            // Stored contracts are allowed to have been announced and become achievable in
            // the past. Validate their internal chronology instead of reapplying new-contract
            // requirements relative to the current turn.
            var latestAnnouncement = (long)snapshot.turn + 1L;
            if (goal.announcedTurn < 0 || goal.announcedTurn > latestAnnouncement || goal.achievableFromTurn <= goal.announcedTurn || (long)goal.achievableFromTurn > (long)goal.announcedTurn + RuleLimits.MaxScheduleDelay || goal.replaceWarningTurn > 0 && goal.replaceWarningTurn < goal.announcedTurn)
                result.errors.Add("STORED_VICTORY_TIMELINE_INVALID:" + id);
        }

        private static void ValidateEffects(
            List<EffectNode> effects,
            GameSnapshotV1 snapshot,
            string source,
            RuleValidationResult result,
            DynamicTargetSelectorV1 targetSelector = null,
            bool requireCurrentWorldReferences = true,
            RuleValidationWorkBudget currentWorldBudget = null)
        {
            if (effects == null) { result.errors.Add("EFFECTS_NULL:" + source); return; }
            if (effects.Count < 1 || effects.Count > RuleLimits.MaxEffectsPerRule) result.errors.Add("EFFECT_COUNT:" + source);
            var inspectCount = Math.Min(effects.Count, RuleLimits.MaxEffectsPerRule + 1);
            for (var i = 0; i < inspectCount; i++) ValidateEffect(effects[i], snapshot, source + ":" + i, result, targetSelector, requireCurrentWorldReferences, currentWorldBudget);
        }

        private static void ValidateEffect(
            EffectNode effect,
            GameSnapshotV1 snapshot,
            string source,
            RuleValidationResult result,
            DynamicTargetSelectorV1 targetSelector,
            bool requireCurrentWorldReferences,
            RuleValidationWorkBudget currentWorldBudget)
        {
            if (effect == null) { result.errors.Add("EFFECT_NULL:" + source); return; }
            if (!Enum.IsDefined(typeof(EffectType), effect.type)) { result.errors.Add("EFFECT_TYPE_INVALID:" + source); return; }
            if (!Enum.IsDefined(typeof(ResourceType), effect.resource)) result.errors.Add("EFFECT_RESOURCE_TYPE_INVALID:" + source);
            if (!IsBoundedText(effect.target, RuleLimits.MaxIdentifierLength, true) || !IsBoundedText(effect.key, RuleLimits.MaxIdentifierLength, true) || !IsBoundedText(effect.value, RuleLimits.MaxDescriptionLength, true)) result.errors.Add("EFFECT_TEXT_LIMIT:" + source);
            switch (effect.type)
            {
                case EffectType.Resource:
                    if (!IsRealResource(effect.resource) || effect.amount < 1 || effect.amount > RuleLimits.MaxEffectMagnitude) result.errors.Add("RESOURCE_EFFECT_INVALID:" + source);
                    break;
                case EffectType.Sp:
                    if (effect.amount == 0 || effect.amount < -10 || effect.amount > 10) result.errors.Add("SP_EFFECT_INVALID:" + source);
                    break;
                case EffectType.Relation:
                    if (effect.amount == 0 || effect.amount < -100 || effect.amount > 100) result.errors.Add("RELATION_EFFECT_INVALID:" + source);
                    break;
                case EffectType.Status:
                    if (!IsBoundedText(effect.key, RuleLimits.MaxIdentifierLength, false) || !IsBoundedStateValue(effect.amount)) result.errors.Add("STATUS_EFFECT_INVALID:" + source);
                    break;
                case EffectType.Spawn:
                    if (effect.amount < 1 || effect.amount > RuleLimits.MaxRuleSpawnsPerTurn ||
                        !(string.Equals(effect.target, DynamicActionTargeting.OwnerToken, StringComparison.Ordinal) && targetSelector != null && targetSelector.kind != DynamicTargetKind.None) &&
                        !(requireCurrentWorldReferences ? IsFactionTarget(effect.target, snapshot, currentWorldBudget) : IsFactionTargetShapeSafe(effect.target))) result.errors.Add("SPAWN_EFFECT_INVALID:" + source);
                    break;
                case EffectType.UnlockAction:
                    if (!IsBoundedText(effect.key, RuleLimits.MaxNameLength, false) || effect.amount < 1 || effect.amount > 10) result.errors.Add("UNLOCK_EFFECT_INVALID:" + source);
                    break;
                case EffectType.Schedule:
                    if (!IsKnownEvent(effect.key) || effect.delay < 1 || effect.delay > RuleLimits.MaxScheduleDelay || !IsRealResource(effect.resource) || effect.amount < 1 || effect.amount > RuleLimits.MaxEffectMagnitude) result.errors.Add("SCHEDULE_EFFECT_INVALID:" + source);
                    break;
                case EffectType.FactionSwitch:
                {
                    var dynamicTarget = string.Equals(effect.target, DynamicActionTargeting.TargetToken, StringComparison.Ordinal) && targetSelector?.kind == DynamicTargetKind.Unit;
                    var targetIdShape = int.TryParse(effect.target, out var unitId) && unitId > 0 && unitId <= RuleLimits.MaxStateMagnitude;
                    var factionIdShape = int.TryParse(effect.key, out var factionId) && factionId > 0 && factionId <= RuleLimits.MaxStateMagnitude;
                    if (!requireCurrentWorldReferences)
                    {
                        if (!dynamicTarget && !targetIdShape || !factionIdShape) result.errors.Add("FACTION_SWITCH_EFFECT_INVALID:" + source);
                        break;
                    }
                    var referenceWork = (long)(snapshot.factions?.Count ?? 0) +
                                        (dynamicTarget ? 0 : snapshot.entities?.Count ?? 0);
                    if (currentWorldBudget == null || !currentWorldBudget.TrySpend(referenceWork))
                    {
                        result.errors.Add("CURRENT_WORLD_WORK_LIMIT:" + source);
                        break;
                    }
                    var targetUnit = targetIdShape ? (snapshot.entities ?? new List<UnitState>()).FirstOrDefault(u => u != null && u.id == unitId) : null;
                    var targetFactionExists = factionIdShape && (snapshot.factions ?? new List<FactionState>()).Any(f => f != null && f.id == factionId);
                    if (string.Equals(effect.target, "player", StringComparison.OrdinalIgnoreCase) || !dynamicTarget && (targetUnit == null || targetUnit.factionId == 1) || !targetFactionExists) result.errors.Add("FACTION_SWITCH_EFFECT_INVALID:" + source);
                    break;
                }
                case EffectType.TypedState:
                    if (effect.stateMutation == null) result.errors.Add("TYPED_STATE_EFFECT_INVALID:" + source);
                    break;
            }
            if (requireCurrentWorldReferences && currentWorldBudget?.Exhausted == true && !result.errors.Contains("CURRENT_WORLD_WORK_LIMIT:" + source))
                result.errors.Add("CURRENT_WORLD_WORK_LIMIT:" + source);
        }

        private static void ValidateConditionTree(
            ConditionNode root,
            GameSnapshotV1 snapshot,
            string source,
            RuleValidationResult result,
            DynamicTargetSelectorV1 targetSelector = null,
            bool requireCurrentWorldReferences = true,
            RuleValidationWorkBudget currentWorldBudget = null)
        {
            if (root == null) { result.errors.Add("CONDITION_ROOT_NULL:" + source); return; }
            var pending = new Stack<Tuple<ConditionNode, int>>();
            var seen = new HashSet<ConditionNode>();
            pending.Push(Tuple.Create(root, 1));
            var count = 0;
            var enqueued = 1;
            while (pending.Count > 0)
            {
                var item = pending.Pop();
                var node = item.Item1;
                var depth = item.Item2;
                if (node == null) { result.errors.Add("CONDITION_NULL:" + source); continue; }
                if (!seen.Add(node)) { result.errors.Add("CONDITION_CYCLE:" + source); continue; }
                count++;
                if (count > RuleLimits.MaxConditionNodes) { result.errors.Add("AST_NODE_LIMIT:" + source); break; }
                if (depth > RuleLimits.MaxConditionDepth) result.errors.Add("AST_DEPTH_LIMIT:" + source);
                ValidateConditionNode(node, snapshot, source, result, targetSelector, requireCurrentWorldReferences, currentWorldBudget);
                if (node.all == null) continue;
                for (var i = node.all.Count - 1; i >= 0; i--)
                {
                    if (enqueued >= RuleLimits.MaxConditionNodes + 1)
                    {
                        result.errors.Add("AST_NODE_LIMIT:" + source);
                        pending.Clear();
                        break;
                    }
                    pending.Push(Tuple.Create(node.all[i], depth + 1));
                    enqueued++;
                }
            }
        }

        private static void ValidateConditionNode(
            ConditionNode node,
            GameSnapshotV1 snapshot,
            string source,
            RuleValidationResult result,
            DynamicTargetSelectorV1 targetSelector,
            bool requireCurrentWorldReferences,
            RuleValidationWorkBudget currentWorldBudget)
        {
            if (!Enum.IsDefined(typeof(CompareOp), node.op)) { result.errors.Add("CONDITION_OP_INVALID:" + source); return; }
            if (node.op == CompareOp.HasTag)
            {
                if (!IsBoundedText(node.text, RuleLimits.MaxIdentifierLength, false) || !IsValidTagSelector(node.left, snapshot, targetSelector, requireCurrentWorldReferences, currentWorldBudget)) result.errors.Add("HAS_TAG_CONDITION_INVALID:" + source);
            }
            else if (node.op == CompareOp.OwnerIs)
            {
                if (!IsValidOwner(node.value, snapshot, requireCurrentWorldReferences, currentWorldBudget) || !IsValidTileSelector(node.left, node.text, snapshot, targetSelector, requireCurrentWorldReferences, currentWorldBudget)) result.errors.Add("OWNER_CONDITION_INVALID:" + source);
            }
            else if (node.op != CompareOp.Always && !IsBoundedText(node.left, RuleLimits.MaxIdentifierLength, false)) result.errors.Add("NUMERIC_CONDITION_LEFT_INVALID:" + source);
            if (!IsBoundedStateValue(node.value)) result.errors.Add("CONDITION_VALUE_LIMIT:" + source);
            if (requireCurrentWorldReferences && currentWorldBudget?.Exhausted == true && !result.errors.Contains("CURRENT_WORLD_WORK_LIMIT:" + source))
                result.errors.Add("CURRENT_WORLD_WORK_LIMIT:" + source);
        }

        private static bool IsKnownProgressKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "turn", "kills", "buildings", "coin", "territory", "alliances", "move", "gather", "hunt", "attack", "trade", "persuade", "hire", "build", "upgrade", "capture" };
            return known.Contains(key);
        }

        private static bool SimulateSixTurns(RuleSetV1 set, GameSnapshotV1 snapshot)
        {
            foreach (var goal in set.victoryContracts ?? new List<VictoryContractV1>())
            {
                if (!CanReachVictoryInSixTurns(goal, snapshot)) return false;
            }

            // It is not enough for this response's optional contracts to be valid:
            // after applying replacements, the run must still expose at least one
            // reachable route to victory. Otherwise an old impossible contract can
            // survive forever while every subsequent response remains superficially
            // valid by omitting victoryContracts.
            var effective = (snapshot.victoryContracts ?? new List<VictoryContractV1>())
                .Where(goal => goal != null)
                .ToDictionary(goal => goal.id ?? string.Empty, goal => goal, StringComparer.Ordinal);
            foreach (var goal in set.victoryContracts ?? new List<VictoryContractV1>())
                if (goal != null) effective[goal.id ?? string.Empty] = goal;

            if (effective.Count == 0) return snapshot.turn < 2;
            return effective.Values.Any(goal => CanReachVictoryInSixTurns(goal, snapshot));
        }

        private static bool CanReachVictoryInSixTurns(VictoryContractV1 goal, GameSnapshotV1 snapshot)
        {
            if (goal == null || !IsKnownProgressKey(goal.progressKey)) return false;
            var current = GameRules.Progress(snapshot, goal.progressKey);
            var perTurn = MaxProgressPerTurn(goal.progressKey, snapshot);
            var sixTurnMaximum = (long)current + perTurn * 6L;
            if (TryGetProgressCeiling(goal.progressKey, snapshot, current, out var hardCeiling))
                sixTurnMaximum = Math.Min(sixTurnMaximum, hardCeiling);
            return (long)goal.target <= sixTurnMaximum;
        }

        private static int MaxProgressPerTurn(string key, GameSnapshotV1 snapshot)
        {
            if (string.Equals(key, "turn", StringComparison.OrdinalIgnoreCase)) return 1;
            var player = (snapshot.factions ?? new List<FactionState>()).FirstOrDefault(f => f != null && f.kind == FactionKind.Player);
            var sp = Math.Max(3, player?.maxSp ?? 10);
            if (string.Equals(key, "move", StringComparison.OrdinalIgnoreCase)) return sp;
            if (string.Equals(key, "territory", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "alliances", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "capture", StringComparison.OrdinalIgnoreCase)) return Math.Max(1, sp / GameRules.CaptureSpCost);
            if (string.Equals(key, "buildings", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "build", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "upgrade", StringComparison.OrdinalIgnoreCase)) return Math.Max(1, sp / 3);
            if (string.Equals(key, "coin", StringComparison.OrdinalIgnoreCase)) return Math.Max(2, sp);
            return Math.Max(1, sp / 2);
        }

        private static bool TryGetProgressCeiling(string key, GameSnapshotV1 snapshot, int current, out long ceiling)
        {
            ceiling = RuleLimits.MaxStateMagnitude;
            if (string.Equals(key, "territory", StringComparison.OrdinalIgnoreCase))
            {
                ceiling = (snapshot.map ?? new List<TileState>()).Count(tile => tile != null);
                return true;
            }
            if (string.Equals(key, "alliances", StringComparison.OrdinalIgnoreCase))
            {
                var factions = snapshot.factions ?? new List<FactionState>();
                var livingTargetFactions = new HashSet<int>((snapshot.entities ?? new List<UnitState>())
                    .Where(unit => unit != null && unit.alive && unit.factionId != 1)
                    .Select(unit => unit.factionId));
                var player = factions.FirstOrDefault(faction => faction != null && faction.kind == FactionKind.Player);
                var hasPlayerActor = (snapshot.entities ?? new List<UnitState>()).Any(unit => unit != null && unit.alive && unit.factionId == 1);
                var persuasionBudget = hasPlayerActor ? Math.Max(0, player?.maxSp ?? 0) / GameRules.CommandCost(CommandType.Persuade) * 6 : 0;
                var reachableAdditions = 0;
                foreach (var requiredActions in factions
                    .Where(faction => faction != null && faction.kind != FactionKind.Player && faction.relationToPlayer < 60 && livingTargetFactions.Contains(faction.id))
                    .Select(faction => (Math.Max(0, 60 - faction.relationToPlayer) + 7) / 8)
                    .OrderBy(actions => actions))
                {
                    if (requiredActions > persuasionBudget) break;
                    persuasionBudget -= requiredActions;
                    reachableAdditions++;
                }
                ceiling = Math.Min(factions.Count(faction => faction != null && faction.kind != FactionKind.Player), (long)Math.Max(0, current) + reachableAdditions);
                return true;
            }
            if (string.Equals(key, "capture", StringComparison.OrdinalIgnoreCase))
            {
                var remainingCapturableTiles = (snapshot.map ?? new List<TileState>()).Count(tile => tile != null && tile.owner != 1);
                ceiling = Math.Min(RuleLimits.MaxStateMagnitude, (long)Math.Max(0, current) + remainingCapturableTiles);
                return true;
            }
            if (string.Equals(key, "kills", StringComparison.OrdinalIgnoreCase))
            {
                var remainingEnemies = (snapshot.entities ?? new List<UnitState>()).Count(unit => unit != null && unit.alive && unit.factionId != 1);
                ceiling = Math.Min(RuleLimits.MaxStateMagnitude, (long)Math.Max(0, current) + remainingEnemies);
                return true;
            }
            if (string.Equals(key, "coin", StringComparison.OrdinalIgnoreCase))
            {
                var player = (snapshot.factions ?? new List<FactionState>()).FirstOrDefault(faction => faction != null && faction.kind == FactionKind.Player);
                ceiling = Math.Max(0, player?.resources?.maxCoin ?? 0);
                return true;
            }
            if (string.Equals(key, "buildings", StringComparison.OrdinalIgnoreCase))
            {
                var buildings = (snapshot.buildings ?? new List<BuildingState>()).Where(building => building != null).ToList();
                var occupied = new HashSet<HexCoord>(buildings.Select(building => building.position));
                var emptyMapTiles = (snapshot.map ?? new List<TileState>()).Count(tile => tile != null && !occupied.Contains(tile.position));
                var remainingCapacity = Math.Max(0, RuleLimits.MaxBuildings - buildings.Count);
                ceiling = Math.Min(RuleLimits.MaxStateMagnitude, (long)Math.Max(0, current) + Math.Min(remainingCapacity, emptyMapTiles));
                return true;
            }
            return false;
        }

        private static long SumSpawnAmounts(IEnumerable<EffectNode> effects)
        {
            long total = 0;
            foreach (var effect in effects ?? Enumerable.Empty<EffectNode>()) if (effect != null && effect.type == EffectType.Spawn && effect.amount > 0) total += effect.amount;
            return total;
        }

        private static void ValidateUniqueIds(IEnumerable<string> ids, string code, RuleValidationResult result)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids ?? Enumerable.Empty<string>()) if (!string.IsNullOrEmpty(id) && !seen.Add(id)) result.errors.Add(code + ":" + SafeId(id));
        }

        private static HashSet<string> ExistingIds(IEnumerable<string> ids) => new HashSet<string>((ids ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)), StringComparer.Ordinal);

        private static int CountDistinctAdditions<T>(IEnumerable<T> incoming, HashSet<string> existingIds, Func<T, string> getId) where T : class
        {
            var additions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in incoming ?? Enumerable.Empty<T>())
            {
                var id = item == null ? null : getId(item);
                if (!string.IsNullOrEmpty(id) && !existingIds.Contains(id)) additions.Add(id);
            }
            return additions.Count;
        }

        private static bool SameVictoryDefinition(VictoryContractV1 left, VictoryContractV1 right)
        {
            return left != null && right != null &&
                   string.Equals(left.title, right.title, StringComparison.Ordinal) &&
                   string.Equals(left.description, right.description, StringComparison.Ordinal) &&
                   string.Equals(left.progressKey, right.progressKey, StringComparison.OrdinalIgnoreCase) &&
                   left.target == right.target && left.minimumTurns == right.minimumTurns &&
                   string.Equals(left.worldCue, right.worldCue, StringComparison.Ordinal);
        }

        private static bool IsFactionTarget(string target, GameSnapshotV1 snapshot, RuleValidationWorkBudget currentWorldBudget)
        {
            if (currentWorldBudget == null || !currentWorldBudget.TrySpend(snapshot.factions?.Count ?? 0)) return false;
            if (string.IsNullOrEmpty(target) || string.Equals(target, "player", StringComparison.OrdinalIgnoreCase)) return (snapshot.factions ?? new List<FactionState>()).Any(f => f != null && f.kind == FactionKind.Player);
            if (int.TryParse(target, out var id)) return (snapshot.factions ?? new List<FactionState>()).Any(f => f != null && f.id == id);
            return (snapshot.factions ?? new List<FactionState>()).Any(f => f != null && string.Equals(f.kind.ToString(), target, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsFactionTargetShapeSafe(string target)
        {
            if (string.IsNullOrEmpty(target) || string.Equals(target, "player", StringComparison.OrdinalIgnoreCase)) return true;
            if (int.TryParse(target, out var id)) return id > 0 && id <= RuleLimits.MaxStateMagnitude;
            return Enum.TryParse<FactionKind>(target, true, out var kind) && Enum.IsDefined(typeof(FactionKind), kind);
        }

        private static bool IsValidTagSelector(
            string selector,
            GameSnapshotV1 snapshot,
            DynamicTargetSelectorV1 targetSelector,
            bool requireCurrentWorldReferences,
            RuleValidationWorkBudget currentWorldBudget)
        {
            if (DynamicActionTargeting.IsTagBindingSelector(selector, targetSelector)) return true;
            if (string.IsNullOrEmpty(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase) || string.Equals(selector, "player", StringComparison.OrdinalIgnoreCase)) return true;
            // Exact unit selectors are syntax-checked without confirming hidden
            // existence; otherwise validation retries become an ID oracle.
            if (TryParseSelectorId(selector, "unit:", out var unitId)) return unitId > 0 && unitId <= RuleLimits.MaxStateMagnitude;
            if (TryParseSelectorId(selector, "faction:", out var factionId))
                return factionId > 0 && factionId <= RuleLimits.MaxStateMagnitude &&
                       (!requireCurrentWorldReferences || currentWorldBudget != null && currentWorldBudget.TrySpend(snapshot.factions?.Count ?? 0) &&
                        (snapshot.factions ?? new List<FactionState>()).Any(f => f != null && f.id == factionId));
            return false;
        }

        private static bool IsValidTileSelector(
            string left,
            string text,
            GameSnapshotV1 snapshot,
            DynamicTargetSelectorV1 targetSelector,
            bool requireCurrentWorldReferences,
            RuleValidationWorkBudget currentWorldBudget)
        {
            var selector = string.IsNullOrEmpty(left) ? text : left;
            if (DynamicActionTargeting.IsOwnerBindingSelector(selector, targetSelector)) return true;
            if (string.IsNullOrEmpty(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase) || string.Equals(selector, "player_tile", StringComparison.OrdinalIgnoreCase)) return true;
            if (!TryParseHex(selector, out var coord)) return false;
            var bounded = Math.Abs((long)coord.q) <= RuleLimits.MaxStateMagnitude && Math.Abs((long)coord.r) <= RuleLimits.MaxStateMagnitude;
            return bounded && (!requireCurrentWorldReferences || currentWorldBudget != null && currentWorldBudget.TrySpend(snapshot.map?.Count ?? 0) &&
                               (snapshot.map ?? new List<TileState>()).Any(t => t != null && t.position.Equals(coord)));
        }

        private static bool IsValidOwner(
            int owner,
            GameSnapshotV1 snapshot,
            bool requireCurrentWorldReferences,
            RuleValidationWorkBudget currentWorldBudget) =>
            owner == 0 || owner > 0 && owner <= RuleLimits.MaxStateMagnitude &&
            (!requireCurrentWorldReferences || currentWorldBudget != null && currentWorldBudget.TrySpend(snapshot.factions?.Count ?? 0) &&
             (snapshot.factions ?? new List<FactionState>()).Any(f => f != null && f.id == owner));
        private static bool IsValidResourceBag(ResourceBag resources) => resources != null &&
            IsValidResource(resources.food, resources.maxFood) && IsValidResource(resources.wood, resources.maxWood) &&
            IsValidResource(resources.stone, resources.maxStone) && IsValidResource(resources.iron, resources.maxIron) &&
            IsValidResource(resources.coin, resources.maxCoin);
        private static bool IsValidResource(int amount, int maximum) => maximum >= 0 && maximum <= RuleLimits.MaxStateMagnitude && amount >= 0 && amount <= maximum;
        private static bool IsRealResource(ResourceType type) => Enum.IsDefined(typeof(ResourceType), type) && type != ResourceType.None;
        private static bool IsKnownEvent(string value) => !string.IsNullOrEmpty(value) && Enum.TryParse<EventType>(value, true, out var eventType) && Enum.IsDefined(typeof(EventType), eventType);
        private static bool IsBoundedStateValue(int value) => value >= -RuleLimits.MaxStateMagnitude && value <= RuleLimits.MaxStateMagnitude;
        private static bool IsBoundedText(string value, int maxLength, bool allowEmpty) => allowEmpty ? value == null || value.Length <= maxLength : !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;
        private static string SafeId(string value) => string.IsNullOrEmpty(value) ? "<empty>" : value.Substring(0, Math.Min(value.Length, RuleLimits.MaxIdentifierLength));
        private static bool TryParseSelectorId(string value, string prefix, out int id) { id = 0; return value != null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(value.Substring(prefix.Length), out id); }
        private static bool TryParseHex(string value, out HexCoord coord)
        {
            coord = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var raw = value.StartsWith("tile:", StringComparison.OrdinalIgnoreCase) ? value.Substring(5) : value;
            var parts = raw.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var q) || !int.TryParse(parts[1], out var r)) return false;
            coord = new HexCoord(q, r);
            return true;
        }
    }

    public sealed class RuleVm
    {
        private const int DispatchLimitFlag = 1;
        private const int ActivationLimitFlag = 2;
        private const int EffectLimitFlag = 4;
        private const int SpawnLimitFlag = 8;
        private const int StateLimitFlag = 16;
        private const int CollectionLimitFlag = 32;
        private const int ConditionWorkLimitFlag = 64;

        public void Execute(EventType trigger, GameSnapshotV1 game, List<string> log)
        {
            if (game == null) return;
            var budget = EnsureBudget(game);
            if (budget.dispatches >= RuleLimits.MaxRuleDispatchesPerTurn)
            {
                LogLimit(budget, DispatchLimitFlag, log, "턴당 규칙 조건 시도 예산에 도달했습니다.");
                return;
            }
            if (budget.conditionWork >= RuleLimits.MaxRuleConditionWorkPerTurn)
            {
                LogLimit(budget, ConditionWorkLimitFlag, log, "턴당 규칙 조건 계산 예산에 도달했습니다.");
                return;
            }
            // Definitions are a run-level registry, not a side effect of whichever
            // owner rule happens to trigger first. Materialize/reset the complete
            // active registry before any condition can reference it.
            if (!RuleExpressionRuntime.EnsureActiveDefinitions(game))
            {
                LogLimit(budget, StateLimitFlag, log, "활성 규칙의 타입 상태 정의가 서로 충돌하거나 저장 한도를 넘었습니다.");
                return;
            }
            var rules = (game.activeRules ?? new List<RuleNodeV1>())
                .Where(r => r != null && r.trigger == trigger && GameRules.IsRuleActive(r, game.turn))
                .OrderByDescending(r => r.priority)
                .ThenBy(r => r.id ?? "", StringComparer.Ordinal)
                .ToList();
            foreach (var rule in rules)
            {
                // Charge an attempt and its conservative selector/AST cost before
                // evaluation. False conditions therefore consume the same bounded
                // runway as true ones and cannot amplify work across repeated events.
                if (budget.dispatches >= RuleLimits.MaxRuleDispatchesPerTurn)
                {
                    LogLimit(budget, DispatchLimitFlag, log, "턴당 규칙 조건 시도 예산에 도달했습니다.");
                    break;
                }
                budget.dispatches++;
                if (!TryReserveConditionWork(rule.condition, game, budget))
                {
                    LogLimit(budget, ConditionWorkLimitFlag, log, "턴당 규칙 조건 계산 예산에 도달했습니다.");
                    break;
                }
                if (!ConditionMatchesReserved(rule.condition, game)) continue;
                var effects = (rule.effects ?? new List<EffectNode>()).Take(RuleLimits.MaxEffectsPerRule + 1).ToList();
                if (!CanApplyWorldRuleEffects(effects, game, budget))
                {
                    LogLimit(budget, StateLimitFlag, log, "규칙의 모든 효과를 안전하게 적용할 수 없어 이번 발동을 건너뛰었습니다.");
                    continue;
                }
                // 모든 이벤트 호출을 합친 rule activation 수가 런타임 연쇄 실행 예산이다.
                if (budget.activations >= RuleLimits.MaxRuleActivationsPerTurn)
                {
                    LogLimit(budget, ActivationLimitFlag, log, "턴당 규칙 연쇄 실행 예산에 도달했습니다.");
                    break;
                }
                budget.activations++;
                ApplyValidatedEffects(effects, game, log, rule.name ?? rule.id ?? "규칙");
                if (budget.effects >= RuleLimits.MaxRuleEffectsPerTurn) return;
            }
        }

        public static bool ConditionMatches(ConditionNode condition, GameSnapshotV1 game)
        {
            if (game == null) return false;
            if (!TryEstimateConditionWork(condition, game, out var work) || work > RuleLimits.MaxConditionWorkPerEvaluation) return false;
            return ConditionMatchesReserved(condition, game);
        }

        public static bool TryConditionMatchesWithinBudget(ConditionNode condition, GameSnapshotV1 game, int availableWork, out bool matches, out int usedWork)
        {
            matches = false;
            usedWork = 0;
            var maximumWork = Math.Min(RuleLimits.MaxConditionWorkPerEvaluation, availableWork);
            if (game == null || maximumWork < 1 || !TryEstimateConditionWork(condition, game, out usedWork, maximumWork) ||
                usedWork > RuleLimits.MaxConditionWorkPerEvaluation || usedWork > availableWork) return false;
            matches = ConditionMatchesReserved(condition, game);
            return true;
        }

        private static bool ConditionMatchesReserved(ConditionNode condition, GameSnapshotV1 game)
        {
            var visited = 0;
            return Matches(condition, game, 1, new HashSet<ConditionNode>(), ref visited);
        }

        private static bool TryReserveConditionWork(ConditionNode condition, GameSnapshotV1 game, RuleRuntimeBudget budget)
        {
            if (!TryEstimateConditionWork(condition, game, out var work) ||
                work > RuleLimits.MaxConditionWorkPerEvaluation ||
                work > RuleLimits.MaxRuleConditionWorkPerTurn - budget.conditionWork)
            {
                budget.conditionWork = RuleLimits.MaxRuleConditionWorkPerTurn;
                return false;
            }
            budget.conditionWork += work;
            return true;
        }

        private static bool TryEstimateConditionWork(
            ConditionNode condition,
            GameSnapshotV1 game,
            out int work,
            int maximumWork = RuleLimits.MaxConditionWorkPerEvaluation)
        {
            var estimator = new ConditionWorkEstimator(game, maximumWork);
            return estimator.TryEstimate(condition, out work);
        }

        private sealed class ConditionWorkEstimator
        {
            private readonly GameSnapshotV1 game;
            private readonly int maximumWork;
            private readonly HashSet<object> path = new HashSet<object>();
            private int nodes;
            private long work;

            public ConditionWorkEstimator(GameSnapshotV1 game, int maximumWork)
            {
                this.game = game;
                this.maximumWork = Math.Max(0, Math.Min(RuleLimits.MaxConditionWorkPerEvaluation, maximumWork));
            }

            public bool TryEstimate(ConditionNode condition, out int estimated)
            {
                var valid = VisitCondition(condition, 1);
                estimated = (int)Math.Min((long)maximumWork + 1L, Math.Max(1L, work));
                return valid;
            }

            private bool VisitCondition(ConditionNode node, int depth)
            {
                if (node == null) { AddWork(1); return WithinEvaluationBudget; }
                if (!Enter(node, depth)) return false;
                try
                {
                    if (node.predicate != null)
                    {
                        if (!VisitPredicate(node.predicate, depth + 1)) return false;
                    }
                    else if (!Enum.IsDefined(typeof(CompareOp), node.op)) return false;
                    else if (node.op == CompareOp.HasTag) AddWork(HasTagCost());
                    else if (node.op == CompareOp.OwnerIs) AddWork(OwnerCost());
                    else if (node.op != CompareOp.Always) AddWork(LegacyNumericCost());
                    if (!WithinEvaluationBudget) return false;

                    foreach (var child in node.all ?? new List<ConditionNode>())
                        if (!VisitCondition(child, depth + 1)) return false;
                    return true;
                }
                finally { path.Remove(node); }
            }

            private bool VisitPredicate(PredicateExpressionV1 predicate, int depth)
            {
                if (!Enter(predicate, depth) || !Enum.IsDefined(typeof(PredicateExpressionOp), predicate.op)) return false;
                try
                {
                    if (predicate.op == PredicateExpressionOp.All || predicate.op == PredicateExpressionOp.Any)
                    {
                        var children = predicate.children ?? new List<PredicateExpressionV1>();
                        if (children.Count < 1) return false;
                        foreach (var child in children) if (!VisitPredicate(child, depth + 1)) return false;
                    }
                    else if (predicate.op == PredicateExpressionOp.Not)
                    {
                        if (!VisitPredicate(predicate.child, depth + 1)) return false;
                    }
                    else if (predicate.op == PredicateExpressionOp.BoolState) AddWork(StateCost());
                    else if (predicate.op == PredicateExpressionOp.SetContains) AddWork(StateCost() + RuleLimits.MaxStateSetElements);
                    else
                    {
                        if (!VisitNumber(predicate.left, depth + 1) || !VisitNumber(predicate.right, depth + 1)) return false;
                    }
                    return WithinEvaluationBudget;
                }
                finally { path.Remove(predicate); }
            }

            private bool VisitNumber(NumberExpressionV1 expression, int depth)
            {
                if (!Enter(expression, depth) || !Enum.IsDefined(typeof(NumberExpressionOp), expression.op)) return false;
                try
                {
                    if (expression.op == NumberExpressionOp.State) AddWork(StateCost());
                    else if (expression.op == NumberExpressionOp.Add || expression.op == NumberExpressionOp.Subtract || expression.op == NumberExpressionOp.Multiply || expression.op == NumberExpressionOp.Divide)
                    {
                        if (!VisitNumber(expression.left, depth + 1) || !VisitNumber(expression.right, depth + 1)) return false;
                    }
                    else if (expression.op == NumberExpressionOp.CountUnits) AddWork(UnitSelectorCost());
                    else if (expression.op == NumberExpressionOp.CountBuildings) AddWork(BuildingSelectorCost());
                    else if (expression.op == NumberExpressionOp.CountTiles) AddWork(TileSelectorCost());
                    else if (expression.op == NumberExpressionOp.Distance) AddWork(2L * PositionSelectorCost());
                    else if (expression.op == NumberExpressionOp.RecentActionRatio) AddWork(RecentActionCost());
                    return WithinEvaluationBudget;
                }
                finally { path.Remove(expression); }
            }

            private bool Enter(object node, int depth)
            {
                if (node == null || depth > RuleLimits.MaxConditionDepth || nodes >= RuleLimits.MaxConditionNodes || !path.Add(node)) return false;
                nodes++;
                AddWork(1);
                if (WithinEvaluationBudget) return true;
                path.Remove(node);
                return false;
            }

            private void AddWork(long amount)
            {
                work = Math.Min((long)maximumWork + 1L, work + Math.Max(0L, amount));
            }

            private bool WithinEvaluationBudget => work <= maximumWork;

            private long UnitSelectorCost() => CollectionCost(game.entities, RuleLimits.MaxEntities) + CollectionCost(game.map, RuleLimits.MaxMapTiles) + CollectionCost(game.factions, RuleLimits.MaxFactions);
            private long BuildingSelectorCost() => CollectionCost(game.buildings, RuleLimits.MaxBuildings) + CollectionCost(game.map, RuleLimits.MaxMapTiles) + CollectionCost(game.factions, RuleLimits.MaxFactions);
            private long TileSelectorCost() => 2L * CollectionCost(game.map, RuleLimits.MaxMapTiles) + CollectionCost(game.factions, RuleLimits.MaxFactions);
            private long PositionSelectorCost() => CollectionCost(game.entities, RuleLimits.MaxEntities) + CollectionCost(game.buildings, RuleLimits.MaxBuildings) + CollectionCost(game.map, RuleLimits.MaxMapTiles);
            private long OwnerCost() => CollectionCost(game.entities, RuleLimits.MaxEntities) + CollectionCost(game.map, RuleLimits.MaxMapTiles);
            private long LegacyNumericCost() => CollectionCost(game.factions, RuleLimits.MaxFactions) + CollectionCost(game.ruleState, RuleLimits.MaxStateVariables);
            private long StateCost() => CollectionCost(game.typedRuleState, RuleLimits.MaxStateVariables) + CollectionCost(game.factions, RuleLimits.MaxFactions) + CollectionCost(game.entities, RuleLimits.MaxEntities) + CollectionCost(game.buildings, RuleLimits.MaxBuildings) + CollectionCost(game.map, RuleLimits.MaxMapTiles);
            private long RecentActionCost() => CollectionCost(game.recentActionStats, RuleLimits.MaxRecentActionEntries);

            private long HasTagCost()
            {
                var entities = game.entities ?? new List<UnitState>();
                if (entities.Count > RuleLimits.MaxEntities) return (long)maximumWork + 1L;
                long total = CollectionCost(game.map, RuleLimits.MaxMapTiles) + entities.Count;
                foreach (var unit in entities)
                {
                    var tags = unit?.tags;
                    if ((tags?.Count ?? 0) > RuleLimits.MaxTagsPerUnit) return (long)maximumWork + 1L;
                    total += tags?.Count ?? 0;
                    if (total > maximumWork) return (long)maximumWork + 1L;
                }
                return total;
            }

            private long CollectionCost<T>(ICollection<T> collection, int maximum)
            {
                if (collection == null) return 0;
                return collection.Count > maximum ? (long)maximumWork + 1L : collection.Count;
            }
        }

        public int ApplyValidatedEffects(IEnumerable<EffectNode> effects, GameSnapshotV1 game, List<string> log, string source)
        {
            if (game == null || effects == null) return 0;
            var budget = EnsureBudget(game);
            var processed = 0;
            var applied = 0;
            foreach (var effect in effects)
            {
                if (processed >= RuleLimits.MaxEffectsPerRule)
                {
                    LogLimit(budget, CollectionLimitFlag, log, "한 번에 적용할 수 있는 규칙 효과 한도에 도달했습니다.");
                    break;
                }
                if (budget.effects >= RuleLimits.MaxRuleEffectsPerTurn)
                {
                    LogLimit(budget, EffectLimitFlag, log, "턴당 규칙 효과 실행 예산에 도달했습니다.");
                    break;
                }
                processed++;
                budget.effects++;
                if (Apply(effect, game, log, string.IsNullOrWhiteSpace(source) ? "동적 행동" : source, budget)) applied++;
            }
            return applied;
        }

        private bool CanApplyWorldRuleEffects(IReadOnlyList<EffectNode> effects, GameSnapshotV1 game, RuleRuntimeBudget budget)
        {
            if (effects == null || effects.Count < 1 || effects.Count > RuleLimits.MaxEffectsPerRule) return false;
            var hasTypedMutation = effects.Any(effect => effect?.type == EffectType.TypedState);
            // A typed expression can observe world mutations earlier in the same
            // effect list (for example Spawn followed by Add(CountUnits)). Replaying
            // only typed state against the original world therefore is not a valid
            // atomicity check. Simulate the complete ordered slice on a detached
            // snapshot and require every effect to succeed before touching live state.
            // Rules without typed mutations retain their documented capped/partial
            // legacy behavior.
            if (hasTypedMutation && (long)budget.effects + effects.Count > RuleLimits.MaxRuleEffectsPerTurn) return false;
            if (hasTypedMutation)
            {
                var shadow = CloneForAtomicPreflight(game, budget);
                var shadowBudget = shadow.ruleBudget;
                var shadowLog = new List<string>();
                foreach (var effect in effects)
                {
                    if (shadowBudget.effects >= RuleLimits.MaxRuleEffectsPerTurn) return false;
                    shadowBudget.effects++;
                    if (!Apply(effect, shadow, shadowLog, "원자성 사전 검사", shadowBudget)) return false;
                }
            }

            var legacy = game.ruleState ?? new List<RuleStateEntry>();
            var newLegacyKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var effect in effects.Where(effect => effect?.type == EffectType.Status))
                if (!legacy.Any(entry => entry != null && string.Equals(entry.key, effect.key, StringComparison.Ordinal))) newLegacyKeys.Add(effect.key);
            if (legacy.Count + (game.typedRuleState?.Count ?? 0) + newLegacyKeys.Count > RuleLimits.MaxStateVariables) return false;

            return true;
        }

        private static GameSnapshotV1 CloneForAtomicPreflight(GameSnapshotV1 game, RuleRuntimeBudget budget)
        {
            return new GameSnapshotV1
            {
                runId = game.runId,
                turn = game.turn,
                seed = game.seed,
                luck = game.luck,
                playerKills = game.playerKills,
                outcome = game.outcome,
                phase = game.phase,
                completedContractId = game.completedContractId,
                planningPrepared = game.planningPrepared,
                map = (game.map ?? new List<TileState>()).Select(tile => tile == null ? null : new TileState
                {
                    position = tile.position,
                    terrain = tile.terrain,
                    resource = tile.resource,
                    amount = tile.amount,
                    owner = tile.owner,
                    explored = tile.explored,
                    visible = tile.visible
                }).ToList(),
                entities = (game.entities ?? new List<UnitState>()).Select(unit => unit == null ? null : new UnitState
                {
                    id = unit.id,
                    factionId = unit.factionId,
                    position = unit.position,
                    hp = unit.hp,
                    speed = unit.speed,
                    alive = unit.alive,
                    tags = new List<string>(unit.tags ?? new List<string>())
                }).ToList(),
                buildings = (game.buildings ?? new List<BuildingState>()).Select(building => building == null ? null : new BuildingState
                {
                    id = building.id,
                    factionId = building.factionId,
                    position = building.position,
                    type = building.type,
                    level = building.level,
                    hp = building.hp
                }).ToList(),
                factions = (game.factions ?? new List<FactionState>()).Select(faction => faction == null ? null : new FactionState
                {
                    id = faction.id,
                    name = faction.name,
                    kind = faction.kind,
                    resources = CloneResources(faction.resources),
                    maxSp = faction.maxSp,
                    sp = faction.sp,
                    relationToPlayer = faction.relationToPlayer
                }).ToList(),
                actionStats = (game.actionStats ?? new List<ActionStat>()).Select(stat => stat == null ? null : new ActionStat { type = stat.type, count = stat.count }).ToList(),
                activeRules = new List<RuleNodeV1>(game.activeRules ?? new List<RuleNodeV1>()),
                victoryContracts = new List<VictoryContractV1>(game.victoryContracts ?? new List<VictoryContractV1>()),
                dynamicActions = new List<DynamicActionV1>(game.dynamicActions ?? new List<DynamicActionV1>()),
                ruleState = (game.ruleState ?? new List<RuleStateEntry>()).Select(entry => entry == null ? null : new RuleStateEntry { key = entry.key, value = entry.value }).ToList(),
                typedRuleState = (game.typedRuleState ?? new List<TypedRuleStateEntryV1>()).Select(entry => entry == null ? null : new TypedRuleStateEntryV1
                {
                    scope = entry.scope,
                    scopeId = entry.scopeId,
                    key = entry.key,
                    valueType = entry.valueType,
                    koreanName = entry.koreanName,
                    iconToken = entry.iconToken,
                    colorHex = entry.colorHex,
                    numberValue = entry.numberValue,
                    boolValue = entry.boolValue,
                    setValue = new List<string>(entry.setValue ?? new List<string>()),
                    stateTurn = entry.stateTurn
                }).ToList(),
                recentActionStats = (game.recentActionStats ?? new List<ActionTurnStatV1>()).Select(stat => stat == null ? null : new ActionTurnStatV1 { turn = stat.turn, type = stat.type, count = stat.count }).ToList(),
                ruleBudget = new RuleRuntimeBudget
                {
                    turn = budget.turn,
                    dispatches = budget.dispatches,
                    conditionWork = budget.conditionWork,
                    activations = budget.activations,
                    effects = budget.effects,
                    spawnedEntities = budget.spawnedEntities,
                    loggedLimits = budget.loggedLimits
                },
                journal = new List<string>(game.journal ?? new List<string>()),
                catalogHash = game.catalogHash
            };
        }

        private static ResourceBag CloneResources(ResourceBag resources)
        {
            if (resources == null) return null;
            return new ResourceBag
            {
                food = resources.food,
                wood = resources.wood,
                stone = resources.stone,
                iron = resources.iron,
                coin = resources.coin,
                maxFood = resources.maxFood,
                maxWood = resources.maxWood,
                maxStone = resources.maxStone,
                maxIron = resources.maxIron,
                maxCoin = resources.maxCoin
            };
        }

        private static bool Matches(ConditionNode node, GameSnapshotV1 game, int depth, HashSet<ConditionNode> path, ref int visited)
        {
            if (node == null) return true;
            if (depth > RuleLimits.MaxConditionDepth || visited >= RuleLimits.MaxConditionNodes || !path.Add(node)) return false;
            visited++;
            bool primary;
            if (node.predicate != null)
            {
                primary = RuleExpressionRuntime.TryEvaluatePredicate(node.predicate, game, out var expressionMatch) && expressionMatch;
            }
            else if (node.op == CompareOp.Always) primary = true;
            else if (node.op == CompareOp.HasTag) primary = HasTag(node, game);
            else if (node.op == CompareOp.OwnerIs) primary = OwnerIs(node, game);
            else
            {
                var current = GetNumericState(game, node.left);
                primary = node.op == CompareOp.Equal ? current == node.value :
                    node.op == CompareOp.GreaterOrEqual ? current >= node.value :
                    node.op == CompareOp.LessOrEqual && current <= node.value;
            }
            if (primary)
            {
                foreach (var child in node.all ?? new List<ConditionNode>())
                {
                    if (!Matches(child, game, depth + 1, path, ref visited)) { primary = false; break; }
                }
            }
            path.Remove(node);
            return primary;
        }

        private bool Apply(EffectNode effect, GameSnapshotV1 game, List<string> log, string source, RuleRuntimeBudget budget)
        {
            if (effect == null || !Enum.IsDefined(typeof(EffectType), effect.type)) return false;
            var factions = game.factions ?? (game.factions = new List<FactionState>());
            var player = factions.FirstOrDefault(f => f != null && f.kind == FactionKind.Player);
            var applied = false;
            switch (effect.type)
            {
                case EffectType.Resource:
                    if (player?.resources != null && IsRealResource(effect.resource) && effect.amount != 0 && Math.Abs((long)effect.amount) <= RuleLimits.MaxEffectMagnitude)
                    {
                        player.resources.Add(effect.resource, effect.amount);
                        applied = true;
                    }
                    break;
                case EffectType.Sp:
                    if (player != null && effect.amount != 0 && effect.amount >= -10 && effect.amount <= 10)
                    {
                        // A world rule must not erase every action at turn start. If SP
                        // was already spent below the three-point safety floor, a later
                        // event may not refund it, but it also cannot reduce it further.
                        var negativeFloor = Math.Min(Math.Max(0, player.sp), Math.Min(3, Math.Max(0, player.maxSp)));
                        var minimum = effect.amount < 0 ? negativeFloor : 0;
                        player.sp = (int)Math.Min(Math.Max(0, player.maxSp), Math.Max(minimum, (long)player.sp + effect.amount));
                        applied = true;
                    }
                    break;
                case EffectType.Relation:
                    if (effect.amount != 0 && effect.amount >= -100 && effect.amount <= 100)
                    {
                        IEnumerable<FactionState> relationTargets = factions.Where(f => f != null && f.kind != FactionKind.Player);
                        if (!string.IsNullOrEmpty(effect.target) && effect.target.StartsWith("faction:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!int.TryParse(effect.target.Substring(8), out var targetFactionId)) break;
                            relationTargets = relationTargets.Where(f => f.id == targetFactionId);
                        }
                        var changed = false;
                        foreach (var f in relationTargets)
                        {
                            f.relationToPlayer = (int)Math.Max(-100L, Math.Min(100L, (long)f.relationToPlayer + effect.amount));
                            changed = true;
                        }
                        applied = changed;
                    }
                    break;
                case EffectType.Status:
                    applied = SetState(game, effect.key, effect.amount);
                    if (!applied) LogLimit(budget, StateLimitFlag, log, "규칙 상태 변수 한도 또는 값 범위를 벗어나 상태 효과를 무시했습니다.");
                    break;
                case EffectType.Spawn:
                    var spawnFaction = ResolveFaction(game, effect.target);
                    var entities = game.entities ?? (game.entities = new List<UnitState>());
                    var remaining = Math.Min(RuleLimits.MaxRuleSpawnsPerTurn - budget.spawnedEntities, RuleLimits.MaxEntities - entities.Count);
                    var requested = effect.amount;
                    if (spawnFaction != null && requested > 0 && remaining > 0)
                    {
                        var playerUnit = entities.FirstOrDefault(u => u != null && u.factionId == 1 && u.alive);
                        var spawnPos = playerUnit?.position ?? entities.FirstOrDefault(u => u != null && u.factionId == spawnFaction.id)?.position ?? new HexCoord(0, 0);
                        var count = Math.Min(requested, remaining);
                        var actual = 0;
                        for (var i = 0; i < count; i++)
                        {
                            var id = NextUnitId(game);
                            if (id < 0) break;
                            var tag = string.IsNullOrWhiteSpace(effect.key) ? "소환" : effect.key.Substring(0, Math.Min(effect.key.Length, RuleLimits.MaxIdentifierLength));
                            entities.Add(new UnitState { id = id, factionId = spawnFaction.id, position = spawnPos, tags = new List<string> { tag } });
                            budget.spawnedEntities++;
                            actual++;
                        }
                        if (actual > 0) SafeLog(log, "[규칙] " + source + ": 유닛 " + actual + "명을 생성했습니다.");
                        // Dynamic actions use the return value to roll back partial effects.
                        // World rules still keep this safely capped partial spawn.
                        applied = actual == requested;
                    }
                    if (requested > remaining) LogLimit(budget, SpawnLimitFlag, log, "턴당 규칙 생성 엔티티 한도에 도달했습니다.");
                    break;
                case EffectType.UnlockAction:
                    var actions = game.dynamicActions ?? (game.dynamicActions = new List<DynamicActionV1>());
                    if (!string.IsNullOrWhiteSpace(effect.key) && effect.key.Length <= RuleLimits.MaxNameLength && effect.amount >= 1 && effect.amount <= 10 && actions.Count < RuleLimits.MaxDynamicActions && !actions.Any(a => a != null && string.Equals(a.name, effect.key, StringComparison.Ordinal)))
                    {
                        var actionId = NextDeterministicId("rule-action", actions.Where(a => a != null).Select(a => a.id));
                        var rewardResource = IsRealResource(effect.resource) ? effect.resource : ResourceType.Food;
                        var description = effect.value ?? "규칙으로 해금된 행동입니다.";
                        if (description.Length > RuleLimits.MaxDescriptionLength) description = description.Substring(0, RuleLimits.MaxDescriptionLength);
                        actions.Add(new DynamicActionV1
                        {
                            id = actionId,
                            name = effect.key,
                            description = description,
                            spCost = effect.amount,
                            cooldown = 1,
                            availableTurn = SaturatingAdd(game.turn, 1),
                            effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = rewardResource, amount = 2 } }
                        });
                        SafeLog(log, "[규칙] " + source + ": 새 행동 '" + effect.key + "'을 잠금 해제했습니다.");
                        applied = true;
                    }
                    else if (actions.Count >= RuleLimits.MaxDynamicActions) LogLimit(budget, CollectionLimitFlag, log, "동적 행동 저장 한도에 도달했습니다.");
                    break;
                case EffectType.Schedule:
                    var activeRules = game.activeRules ?? (game.activeRules = new List<RuleNodeV1>());
                    var scheduledTurn = SaturatingAdd(game.turn, effect.delay);
                    var hasScheduledCapacity = activeRules.Count(rule => rule != null && GameRules.IsRuleActive(rule, scheduledTurn)) < RuleLimits.MaxActiveRules;
                    if (activeRules.Count < RuleLimits.MaxStoredRules && hasScheduledCapacity && effect.delay >= 1 && effect.delay <= RuleLimits.MaxScheduleDelay && effect.amount >= 1 && effect.amount <= RuleLimits.MaxEffectMagnitude && IsRealResource(effect.resource) && TryParseEvent(effect.key, out var scheduledEvent))
                    {
                        var scheduledId = NextDeterministicId("scheduled-" + game.turn + "-" + scheduledEvent, activeRules.Where(r => r != null).Select(r => r.id));
                        var description = effect.value ?? "예약 이벤트";
                        if (description.Length > RuleLimits.MaxDescriptionLength) description = description.Substring(0, RuleLimits.MaxDescriptionLength);
                        activeRules.Add(new RuleNodeV1 { id = scheduledId, name = "예약된 " + scheduledEvent, description = description, trigger = scheduledEvent, durationTurns = 1, appliedTurn = scheduledTurn, effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = effect.resource, amount = effect.amount } } });
                        applied = true;
                    }
                    else if (activeRules.Count >= RuleLimits.MaxStoredRules || !hasScheduledCapacity) LogLimit(budget, CollectionLimitFlag, log, "규칙 저장 또는 예약 턴의 활성 한도에 도달했습니다.");
                    break;
                case EffectType.FactionSwitch:
                    if (int.TryParse(effect.target ?? "", out var unitId) && int.TryParse(effect.key ?? "", out var newFaction))
                    {
                        var unit = (game.entities ?? new List<UnitState>()).FirstOrDefault(u => u != null && u.id == unitId);
                        var faction = factions.FirstOrDefault(f => f != null && f.id == newFaction);
                        if (unit != null && faction != null && unit.factionId != 1 && unit.factionId != newFaction)
                        {
                            unit.factionId = newFaction;
                            SafeLog(log, "[규칙] " + source + ": 유닛 " + unitId + "이 세력 " + newFaction + "으로 전환되었습니다.");
                            applied = true;
                        }
                    }
                    break;
                case EffectType.TypedState:
                    applied = RuleExpressionRuntime.ApplyStateMutation(effect.stateMutation, game);
                    if (!applied) LogLimit(budget, StateLimitFlag, log, "타입 상태 효과가 타입·범위 검증을 통과하지 못해 무시했습니다.");
                    break;
            }
            if (applied) SafeLog(log, "[규칙] " + source + ": " + effect.type + " 효과 적용");
            return applied;
        }

        private static int GetNumericState(GameSnapshotV1 game, string key)
        {
            if (string.IsNullOrEmpty(key)) return 0;
            if (string.Equals(key, "luck", StringComparison.OrdinalIgnoreCase)) return game.luck;
            if (string.Equals(key, "turn", StringComparison.OrdinalIgnoreCase)) return game.turn;
            if (string.Equals(key, "kills", StringComparison.OrdinalIgnoreCase)) return game.playerKills;
            var player = (game.factions ?? new List<FactionState>()).FirstOrDefault(f => f != null && f.kind == FactionKind.Player);
            if (string.Equals(key, "sp", StringComparison.OrdinalIgnoreCase)) return player?.sp ?? 0;
            if (string.Equals(key, "food", StringComparison.OrdinalIgnoreCase)) return player?.resources?.food ?? 0;
            if (string.Equals(key, "wood", StringComparison.OrdinalIgnoreCase)) return player?.resources?.wood ?? 0;
            if (string.Equals(key, "stone", StringComparison.OrdinalIgnoreCase)) return player?.resources?.stone ?? 0;
            if (string.Equals(key, "iron", StringComparison.OrdinalIgnoreCase)) return player?.resources?.iron ?? 0;
            if (string.Equals(key, "coin", StringComparison.OrdinalIgnoreCase)) return player?.resources?.coin ?? 0;
            var stateKey = key.StartsWith("state:", StringComparison.OrdinalIgnoreCase) ? key.Substring(6) : key;
            var entry = (game.ruleState ?? new List<RuleStateEntry>()).FirstOrDefault(x => x != null && string.Equals(x.key, stateKey, StringComparison.Ordinal));
            return entry?.value ?? 0;
        }

        private static bool SetState(GameSnapshotV1 game, string key, int value)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > RuleLimits.MaxIdentifierLength || value < -RuleLimits.MaxStateMagnitude || value > RuleLimits.MaxStateMagnitude) return false;
            var states = game.ruleState ?? (game.ruleState = new List<RuleStateEntry>());
            var entry = states.FirstOrDefault(x => x != null && string.Equals(x.key, key, StringComparison.Ordinal));
            if (entry != null) { entry.value = value; return true; }
            if (states.Count + (game.typedRuleState?.Count ?? 0) >= RuleLimits.MaxStateVariables) return false;
            states.Add(new RuleStateEntry { key = key, value = value });
            return true;
        }

        private static bool HasTag(ConditionNode node, GameSnapshotV1 game)
        {
            if (string.IsNullOrEmpty(node.text)) return false;
            var visiblePositions = new HashSet<HexCoord>((game.map ?? new List<TileState>()).Where(tile => tile != null && tile.visible).Select(tile => tile.position));
            IEnumerable<UnitState> units = (game.entities ?? new List<UnitState>()).Where(u => u != null && u.alive && (u.factionId == 1 || visiblePositions.Contains(u.position)));
            var selector = node.left ?? "any";
            if (string.Equals(selector, "player", StringComparison.OrdinalIgnoreCase)) units = units.Where(u => u.factionId == 1);
            else if (TryParseSelectorId(selector, "unit:", out var unitId)) units = units.Where(u => u.id == unitId);
            else if (TryParseSelectorId(selector, "faction:", out var factionId)) units = units.Where(u => u.factionId == factionId);
            else if (!string.IsNullOrEmpty(selector) && !string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase)) return false;
            return units.Any(u => (u.tags ?? new List<string>()).Any(tag => string.Equals(tag, node.text, StringComparison.OrdinalIgnoreCase)));
        }

        private static bool OwnerIs(ConditionNode node, GameSnapshotV1 game)
        {
            var tiles = game.map ?? new List<TileState>();
            var selector = string.IsNullOrEmpty(node.left) ? node.text : node.left;
            if (string.IsNullOrEmpty(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase))
                return tiles.Any(t => t != null && t.owner == node.value && (node.value == 1 || t.visible));
            if (string.Equals(selector, "player_tile", StringComparison.OrdinalIgnoreCase))
            {
                var playerUnit = (game.entities ?? new List<UnitState>()).FirstOrDefault(u => u != null && u.factionId == 1 && u.alive);
                return playerUnit != null && tiles.Any(t => t != null && t.position.Equals(playerUnit.position) && t.owner == node.value);
            }
            return TryParseHex(selector, out var coord) && tiles.Any(t => t != null && t.position.Equals(coord) && t.owner == node.value && (node.value == 1 || t.visible));
        }

        private static RuleRuntimeBudget EnsureBudget(GameSnapshotV1 game)
        {
            var budget = game.ruleBudget ?? (game.ruleBudget = new RuleRuntimeBudget());
            if (budget.turn != game.turn)
            {
                budget.turn = game.turn;
                budget.dispatches = 0;
                budget.conditionWork = 0;
                budget.activations = 0;
                budget.effects = 0;
                budget.spawnedEntities = 0;
                budget.loggedLimits = 0;
            }
            else
            {
                budget.dispatches = Math.Max(0, Math.Min(RuleLimits.MaxRuleDispatchesPerTurn, budget.dispatches));
                budget.conditionWork = Math.Max(0, Math.Min(RuleLimits.MaxRuleConditionWorkPerTurn, budget.conditionWork));
                budget.activations = Math.Max(0, Math.Min(RuleLimits.MaxRuleActivationsPerTurn, budget.activations));
                budget.effects = Math.Max(0, Math.Min(RuleLimits.MaxRuleEffectsPerTurn, budget.effects));
                budget.spawnedEntities = Math.Max(0, Math.Min(RuleLimits.MaxRuleSpawnsPerTurn, budget.spawnedEntities));
                budget.loggedLimits &= DispatchLimitFlag | ActivationLimitFlag | EffectLimitFlag | SpawnLimitFlag | StateLimitFlag | CollectionLimitFlag | ConditionWorkLimitFlag;
            }
            return budget;
        }

        private static void LogLimit(RuleRuntimeBudget budget, int flag, List<string> log, string message)
        {
            if ((budget.loggedLimits & flag) != 0) return;
            budget.loggedLimits |= flag;
            SafeLog(log, message);
        }

        private static void SafeLog(List<string> log, string message) { if (log != null) log.Add(message); }
        private static int NextUnitId(GameSnapshotV1 game)
        {
            var entities = game.entities ?? (game.entities = new List<UnitState>());
            if (entities.Count == 0) return 1;
            var max = entities.Where(x => x != null).Select(x => x.id).DefaultIfEmpty(0).Max();
            return max < 0 || max >= RuleLimits.MaxStateMagnitude ? -1 : Math.Max(1, max + 1);
        }

        private static string NextDeterministicId(string prefix, IEnumerable<string> existingIds)
        {
            var existing = new HashSet<string>((existingIds ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrEmpty(id)), StringComparer.Ordinal);
            for (var sequence = 0; sequence < int.MaxValue; sequence++)
            {
                var candidate = prefix + "-" + sequence;
                if (!existing.Contains(candidate)) return candidate;
            }
            return prefix + "-full";
        }

        private static FactionState ResolveFaction(GameSnapshotV1 game, string target)
        {
            var factions = game.factions ?? new List<FactionState>();
            if (string.IsNullOrEmpty(target) || string.Equals(target, "player", StringComparison.OrdinalIgnoreCase)) return factions.FirstOrDefault(f => f != null && f.kind == FactionKind.Player);
            if (int.TryParse(target, out var id)) return factions.FirstOrDefault(f => f != null && f.id == id);
            return factions.FirstOrDefault(f => f != null && string.Equals(f.kind.ToString(), target, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryParseSelectorId(string value, string prefix, out int id)
        {
            id = 0;
            return value != null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(value.Substring(prefix.Length), out id);
        }

        private static bool IsRealResource(ResourceType type) => Enum.IsDefined(typeof(ResourceType), type) && type != ResourceType.None;
        private static bool TryParseEvent(string value, out EventType eventType) => Enum.TryParse(value, true, out eventType) && Enum.IsDefined(typeof(EventType), eventType);
        private static int SaturatingAdd(int value, int amount) => (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)value + amount));

        private static bool TryParseHex(string value, out HexCoord coord)
        {
            coord = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var raw = value.StartsWith("tile:", StringComparison.OrdinalIgnoreCase) ? value.Substring(5) : value;
            var parts = raw.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var q) || !int.TryParse(parts[1], out var r)) return false;
            coord = new HexCoord(q, r);
            return true;
        }
    }

    public static class GameRules
    {
        public const int CaptureSpCost = 2;
        public static int BuildingCost(BuildingType type)
        {
            if (type == BuildingType.Headquarters) return 8;
            return type == BuildingType.Warehouse || type == BuildingType.Watchtower ? 3 : 5;
        }
        public static int BuildingIronCost(BuildingType type)
        {
            if (type == BuildingType.Headquarters) return 4;
            if (type == BuildingType.Workshop) return 2;
            if (type == BuildingType.Barracks) return 3;
            return 0;
        }
        public static int CommandCost(CommandType type) => type == CommandType.Move ? 1 : type == CommandType.Build || type == CommandType.Upgrade ? 3 : type == CommandType.Capture ? CaptureSpCost : 2;
        public static bool IsRuleActive(RuleNodeV1 rule, int turn) => rule != null && turn >= rule.appliedTurn && (long)turn < (long)rule.appliedTurn + Math.Max(1, rule.durationTurns);
        public static void StartTurn(GameSnapshotV1 game)
        {
            if (game == null) return;
            var buildings = game.buildings ?? (game.buildings = new List<BuildingState>());
            foreach (var faction in game.factions ?? (game.factions = new List<FactionState>()))
            {
                if (faction == null) continue;
                faction.resources = faction.resources ?? new ResourceBag();
                var barracksLevels = BoundedBuildingLevels(buildings, faction.id, BuildingType.Barracks);
                faction.maxSp = Math.Max(3, 10 + barracksLevels);
                faction.sp = faction.maxSp;
                var warehouseLevels = BoundedBuildingLevels(buildings, faction.id, BuildingType.Warehouse);
                faction.resources.ApplyWarehouseBonus(warehouseLevels);
                var workshopLevels = BoundedBuildingLevels(buildings, faction.id, BuildingType.Workshop);
                var marketLevels = BoundedBuildingLevels(buildings, faction.id, BuildingType.Market);
                if (workshopLevels > 0) faction.resources.Add(ResourceType.Iron, workshopLevels);
                if (marketLevels > 0) faction.resources.Add(ResourceType.Coin, marketLevels);
            }
        }
        public static void CountAction(GameSnapshotV1 game, CommandType type)
        {
            if (game == null || !Enum.IsDefined(typeof(CommandType), type)) return;
            var stats = game.actionStats ?? (game.actionStats = new List<ActionStat>());
            var stat = stats.FirstOrDefault(s => s != null && s.type == type);
            if (stat == null) stats.Add(new ActionStat { type = type, count = 1 });
            else if (stat.count < RuleLimits.MaxStateMagnitude) stat.count++;

            PruneRecentActionStats(game);
            var history = game.recentActionStats;
            var recent = history.FirstOrDefault(entry => entry.turn == game.turn && entry.type == type);
            if (recent == null)
            {
                if (history.Count >= RuleLimits.MaxRecentActionEntries)
                {
                    var oldest = history.OrderBy(entry => entry.turn).ThenBy(entry => (int)entry.type).First();
                    history.Remove(oldest);
                }
                history.Add(new ActionTurnStatV1 { turn = game.turn, type = type, count = 1 });
            }
            else if (recent.count < RuleLimits.MaxStateMagnitude) recent.count++;
            history.Sort((left, right) => left.turn != right.turn ? left.turn.CompareTo(right.turn) : ((int)left.type).CompareTo((int)right.type));
        }

        public static void PruneRecentActionStats(GameSnapshotV1 game)
        {
            if (game == null) return;
            var history = game.recentActionStats ?? (game.recentActionStats = new List<ActionTurnStatV1>());
            var minimumTurn = Math.Max(0, game.turn - RuleLimits.MaxRecentActionTurns + 1);
            history.RemoveAll(entry => entry == null || entry.turn < minimumTurn || entry.turn > game.turn || !Enum.IsDefined(typeof(CommandType), entry.type) || entry.count <= 0 || entry.count > RuleLimits.MaxStateMagnitude);
            history.Sort((left, right) => left.turn != right.turn ? left.turn.CompareTo(right.turn) : ((int)left.type).CompareTo((int)right.type));
            while (history.Count > RuleLimits.MaxRecentActionEntries) history.RemoveAt(0);
        }
        // 감시탑이 시야를 확장한다. 기본 반경 2 + 감시탑 레벨 합계 1당 +1.
        public static int VisibilityRange(GameSnapshotV1 game, int factionId)
        {
            var baseRange = 2;
            if (game == null) return baseRange;
            var watchtowers = BoundedBuildingLevels(game.buildings ?? new List<BuildingState>(), factionId, BuildingType.Watchtower);
            return baseRange + watchtowers;
        }
        public static bool HeadquartersAlive(GameSnapshotV1 game) => game != null && (game.buildings ?? new List<BuildingState>()).Any(b => b != null && b.factionId == 1 && b.type == BuildingType.Headquarters && b.hp > 0);
        public static int Progress(GameSnapshotV1 game, string key)
        {
            if (game == null || string.IsNullOrEmpty(key)) return 0;
            if (string.Equals(key, "turn", StringComparison.OrdinalIgnoreCase)) return game.turn;
            if (string.Equals(key, "kills", StringComparison.OrdinalIgnoreCase)) return game.playerKills;
            if (string.Equals(key, "buildings", StringComparison.OrdinalIgnoreCase)) return (game.buildings ?? new List<BuildingState>()).Count(x => x != null && x.factionId == 1);
            if (string.Equals(key, "coin", StringComparison.OrdinalIgnoreCase)) return (game.factions ?? new List<FactionState>()).FirstOrDefault(x => x != null && x.id == 1)?.resources?.coin ?? 0;
            if (string.Equals(key, "territory", StringComparison.OrdinalIgnoreCase)) return (game.map ?? new List<TileState>()).Count(tile => tile != null && tile.owner == 1);
            if (string.Equals(key, "alliances", StringComparison.OrdinalIgnoreCase)) return (game.factions ?? new List<FactionState>()).Count(faction => faction != null && faction.kind != FactionKind.Player && faction.relationToPlayer >= 60);
            long total = (game.actionStats ?? new List<ActionStat>()).Where(x => x != null && string.Equals(x.type.ToString(), key, StringComparison.OrdinalIgnoreCase)).Sum(x => (long)Math.Max(0, x.count));
            return (int)Math.Min(RuleLimits.MaxStateMagnitude, total);
        }
        public static int CollisionTieBreakScore(int factionId, int luck, int seededRoll)
        {
            var boundedRoll = Math.Max(0, Math.Min(99, seededRoll));
            var luckBonus = factionId == 1 ? Math.Max(1, Math.Min(100, luck)) : 50;
            return boundedRoll + luckBonus;
        }
        public static bool IsVictoryComplete(GameSnapshotV1 game, VictoryContractV1 contract) => game != null && contract != null && game.turn >= contract.achievableFromTurn && (long)game.turn >= (long)contract.announcedTurn + Math.Max(3, contract.minimumTurns) && Progress(game, contract.progressKey) >= contract.target;
        public static RunOutcome EvaluateOutcome(GameSnapshotV1 game)
        {
            if (game == null) return RunOutcome.Defeat;
            if (game.outcome != RunOutcome.Ongoing) return game.outcome;
            if (!HeadquartersAlive(game) && !(game.entities ?? new List<UnitState>()).Any(u => u != null && u.factionId == 1 && u.alive)) return RunOutcome.Defeat;
            var completed = (game.victoryContracts ?? new List<VictoryContractV1>()).FirstOrDefault(c => IsVictoryComplete(game, c));
            if (completed != null)
            {
                game.completedContractId = completed.id;
                return RunOutcome.Victory;
            }
            return RunOutcome.Ongoing;
        }
        public static void PruneExpiredRules(GameSnapshotV1 game)
        {
            if (game?.activeRules == null) return;
            game.activeRules.RemoveAll(r => r == null || !IsRuleActive(r, game.turn) && (long)game.turn >= (long)r.appliedTurn + Math.Max(1, r.durationTurns));
        }

        private static int BoundedBuildingLevels(IEnumerable<BuildingState> buildings, int factionId, BuildingType type)
        {
            long total = 0;
            foreach (var building in buildings ?? Enumerable.Empty<BuildingState>())
            {
                if (building == null || building.factionId != factionId || building.type != type) continue;
                total += Math.Max(0, Math.Min(RuleLimits.MaxEffectMagnitude, building.level));
                if (total >= RuleLimits.MaxEffectMagnitude) return RuleLimits.MaxEffectMagnitude;
            }
            return (int)total;
        }
    }

    // 1단계: 동시 계획 턴 — PRD 고정 해결 순서와 시드 기반 난수 판정을 구현한다.
    public sealed class PlannedCommand
    {
        public int factionId;
        public int unitId;
        public CommandType type;
        public HexCoord target;
        public int priority;
        // Warehouse is the safe legacy default when an older serialized command has
        // no explicit buildingType field. New player commands always set this field.
        public BuildingType buildingType = BuildingType.Warehouse;
        internal bool hasReservedActorPosition;
        internal HexCoord reservedActorPosition;
    }

    public static class TurnResolver
    {
        // PRD 고정 해결 순서: 턴 시작 → 이동·충돌 → 거래·외교 → 전투 → 점령·채집·건설 → 지속 효과 → 승패 판정
        public static void BeginPlanning(GameSnapshotV1 game, List<string> log)
        {
            if (game.planningPrepared || game.outcome != RunOutcome.Ongoing) return;
            GameRules.PruneExpiredRules(game);
            GameRules.StartTurn(game);
            new RuleVm().Execute(EventType.TurnStart, game, log);
            game.planningPrepared = true;
        }

        public static void Resolve(GameSnapshotV1 game, List<PlannedCommand> playerCommands, DeterministicRandom random, List<string> log)
        {
            BeginPlanning(game, log);
            var vm = new RuleVm();

            // 2. AI 동시 계획: 턴 시작 상태 스냅샷만 보고 계획 (플레이어 예약 명령을 모름)
            var aiPlans = PlanAi(game, random);
            var all = new List<PlannedCommand>();
            all.AddRange(playerCommands ?? new List<PlannedCommand>());
            all.AddRange(aiPlans);
            all = ValidateAndSpendCommands(game, all, log);

            // 3. 이동·충돌 해결
            ResolveMovement(game, all, random, log, vm);

            // 4. 거래·외교 해결
            ResolveDiplomacy(game, all, log, vm);

            // 5. 전투 해결
            ResolveCombat(game, all, random, log, vm);

            // 6. 전투 후 생존 상태를 기준으로 점령 해결
            ResolveCapture(game, all, log, vm);

            // 7. 채집·건설 해결
            ResolveGatherAndBuild(game, all, log, vm);

            // 8. 지속 효과
            vm.Execute(EventType.TurnEnd, game, log);
            game.outcome = GameRules.EvaluateOutcome(game);
            game.planningPrepared = false;
        }

        private static List<PlannedCommand> ValidateAndSpendCommands(GameSnapshotV1 game, List<PlannedCommand> commands, List<string> log)
        {
            var accepted = new List<PlannedCommand>();
            var reservations = new CommandReservationLedger(game);
            var candidates = (commands ?? new List<PlannedCommand>()).Where(command => command != null).ToList();
            foreach (var command in candidates)
            {
                command.hasReservedActorPosition = false;
            }
            // Movement is the first resolution phase, so reserve one valid projection per
            // unit before validating that unit's later actions, regardless of UI list order.
            var reservationOrder = candidates.Where(command => command.type == CommandType.Move)
                .Concat(candidates.Where(command => command.type != CommandType.Move));
            foreach (var command in reservationOrder)
            {
                var faction = (game.factions ?? new List<FactionState>()).FirstOrDefault(f => f != null && f.id == command.factionId);
                var unit = (game.entities ?? new List<UnitState>()).FirstOrDefault(u => u != null && u.id == command.unitId && u.factionId == command.factionId && u.alive);
                if (faction == null || unit == null) continue;
                if (!TryReserveCommand(game, command, faction, unit, reservations, out var rejection))
                {
                    if (log != null) log.Add(rejection);
                    continue;
                }
                accepted.Add(command);
            }
            reservations.CommitSp(game);
            return accepted;
        }

        private static bool TryReserveCommand(GameSnapshotV1 game, PlannedCommand command, FactionState faction, UnitState unit, CommandReservationLedger reservations, out string rejection)
        {
            rejection = "선택한 대상 또는 자원이 유효하지 않아 " + command.type + " 명령이 취소되었습니다.";
            if (!Enum.IsDefined(typeof(CommandType), command.type)) return false;
            var entities = game.entities ?? new List<UnitState>();
            var buildings = game.buildings ?? new List<BuildingState>();
            var map = game.map ?? new List<TileState>();
            var resourceCosts = new List<KeyValuePair<ResourceType, int>>();
            TileState gatherTile = null;
            UnitState hiredUnit = null;
            BuildingState upgradedBuilding = null;
            var actorPosition = command.type == CommandType.Move ? unit.position : reservations.ProjectedUnitPosition(unit);

            switch (command.type)
            {
                case CommandType.Move:
                    if (unit.position.Distance(command.target) != 1 || !map.Any(tile => tile != null && tile.position.Equals(command.target) && tile.terrain != "강")) return false;
                    if (!reservations.CanReserveMove(unit.id))
                    {
                        rejection = "같은 유닛에는 이동 명령을 하나만 예약할 수 있습니다.";
                        return false;
                    }
                    break;
                case CommandType.Gather:
                    gatherTile = map.FirstOrDefault(tile => tile != null && tile.position.Equals(actorPosition) && Enum.IsDefined(typeof(ResourceType), tile.resource) && tile.resource != ResourceType.None);
                    if (gatherTile == null || !reservations.CanReserveGather(gatherTile)) return false;
                    break;
                case CommandType.Hunt:
                    if (!map.Any(tile => tile != null && tile.position.Equals(actorPosition))) return false;
                    break;
                case CommandType.Attack:
                    if (actorPosition.Distance(command.target) > 2) return false;
                    if (!entities.Any(target => target != null && target.factionId != command.factionId && target.alive && target.position.Equals(command.target)) &&
                        !buildings.Any(target => target != null && target.factionId != command.factionId && target.hp > 0 && target.position.Equals(command.target))) return false;
                    break;
                case CommandType.Trade:
                case CommandType.Persuade:
                case CommandType.Hire:
                {
                    if (actorPosition.Distance(command.target) > 2) return false;
                    var targetUnit = entities.FirstOrDefault(target => target != null && target.factionId != command.factionId && target.alive && target.position.Equals(command.target));
                    var partner = targetUnit == null ? null : (game.factions ?? new List<FactionState>()).FirstOrDefault(candidate => candidate != null && candidate.id == targetUnit.factionId);
                    if (targetUnit == null || partner == null) return false;
                    if (command.type == CommandType.Trade) resourceCosts.Add(new KeyValuePair<ResourceType, int>(ResourceType.Food, 1));
                    if (command.type == CommandType.Hire)
                    {
                        if (partner.kind != FactionKind.Neutral || partner.relationToPlayer < 0 || !reservations.CanReserveHire(targetUnit.id)) return false;
                        hiredUnit = targetUnit;
                        resourceCosts.Add(new KeyValuePair<ResourceType, int>(ResourceType.Coin, 3));
                    }
                    break;
                }
                case CommandType.Build:
                {
                    if (!Enum.IsDefined(typeof(BuildingType), command.buildingType) ||
                        buildings.Any(building => building != null && building.position.Equals(actorPosition)) ||
                        !reservations.CanReserveBuild(actorPosition, command.factionId, command.buildingType)) return false;
                    resourceCosts.Add(new KeyValuePair<ResourceType, int>(ResourceType.Wood, GameRules.BuildingCost(command.buildingType)));
                    var ironCost = GameRules.BuildingIronCost(command.buildingType);
                    if (ironCost > 0) resourceCosts.Add(new KeyValuePair<ResourceType, int>(ResourceType.Iron, ironCost));
                    break;
                }
                case CommandType.Upgrade:
                {
                    upgradedBuilding = buildings.FirstOrDefault(candidate => candidate != null && candidate.factionId == command.factionId && candidate.hp > 0 && candidate.position.Equals(command.target));
                    if (upgradedBuilding == null || actorPosition.Distance(upgradedBuilding.position) > 1 || !reservations.CanReserveUpgrade(upgradedBuilding.id)) return false;
                    resourceCosts.Add(new KeyValuePair<ResourceType, int>(ResourceType.Stone, 3));
                    break;
                }
                case CommandType.Capture:
                {
                    var captureTile = map.FirstOrDefault(tile => tile != null && tile.position.Equals(actorPosition));
                    if (command.factionId != 1 || faction.kind != FactionKind.Player || !command.target.Equals(actorPosition) || captureTile == null || captureTile.owner == 1 || !reservations.CanReserveCapture(actorPosition)) return false;
                    break;
                }
                case CommandType.Dynamic:
                    return false;
                default:
                    return false;
            }

            var spCost = GameRules.CommandCost(command.type);
            if (!reservations.CanReserveSp(faction, spCost))
            {
                rejection = faction.name + "의 예약 가능한 SP가 부족해 " + command.type + " 명령이 취소되었습니다.";
                return false;
            }
            var unavailableCost = resourceCosts.FirstOrDefault(cost => !reservations.CanReserveResource(faction, cost.Key, cost.Value));
            if (unavailableCost.Value > 0)
            {
                rejection = command.type + " 명령에 필요한 " + unavailableCost.Key + " 자원이 이미 예약되었거나 부족합니다.";
                return false;
            }

            reservations.ReserveSp(faction.id, spCost);
            foreach (var cost in resourceCosts) reservations.ReserveResource(faction.id, cost.Key, cost.Value);
            if (command.type == CommandType.Move) reservations.ReserveMove(unit.id, command.target);
            else
            {
                command.reservedActorPosition = actorPosition;
                command.hasReservedActorPosition = true;
            }
            if (gatherTile != null) reservations.ReserveGather(gatherTile.position);
            if (hiredUnit != null) reservations.ReserveHire(hiredUnit.id);
            if (upgradedBuilding != null) reservations.ReserveUpgrade(upgradedBuilding.id);
            if (command.type == CommandType.Capture) reservations.ReserveCapture(actorPosition);
            if (command.type == CommandType.Build) reservations.ReserveBuild(actorPosition, command.factionId, command.buildingType);
            return true;
        }

        private sealed class CommandReservationLedger
        {
            private readonly GameSnapshotV1 game;
            private readonly Dictionary<int, int> sp = new Dictionary<int, int>();
            private readonly Dictionary<int, Dictionary<ResourceType, int>> resources = new Dictionary<int, Dictionary<ResourceType, int>>();
            private readonly Dictionary<HexCoord, int> gathered = new Dictionary<HexCoord, int>();
            private readonly HashSet<HexCoord> buildPositions = new HashSet<HexCoord>();
            private readonly HashSet<int> headquartersReservations = new HashSet<int>();
            private readonly HashSet<HexCoord> capturePositions = new HashSet<HexCoord>();
            private readonly HashSet<int> upgradedBuildings = new HashSet<int>();
            private readonly HashSet<int> hiredUnits = new HashSet<int>();
            private readonly Dictionary<int, HexCoord> projectedUnitPositions = new Dictionary<int, HexCoord>();
            private int buildingReservations;

            public CommandReservationLedger(GameSnapshotV1 game) { this.game = game; }

            public bool CanReserveSp(FactionState faction, int amount) => faction != null && amount >= 0 && (long)ReservedSp(faction.id) + amount <= faction.sp;
            public void ReserveSp(int factionId, int amount) { sp[factionId] = ReservedSp(factionId) + amount; }
            public bool CanReserveResource(FactionState faction, ResourceType type, int amount) => faction?.resources != null && amount >= 0 && (long)ReservedResource(faction.id, type) + amount <= faction.resources.Get(type);
            public void ReserveResource(int factionId, ResourceType type, int amount)
            {
                if (!resources.TryGetValue(factionId, out var factionResources)) resources[factionId] = factionResources = new Dictionary<ResourceType, int>();
                factionResources[type] = ReservedResource(factionId, type) + amount;
            }
            public bool CanReserveGather(TileState tile) => tile != null && tile.amount > ReservedGather(tile.position);
            public void ReserveGather(HexCoord position) { gathered[position] = ReservedGather(position) + 1; }
            public bool CanReserveMove(int unitId) => !projectedUnitPositions.ContainsKey(unitId);
            public void ReserveMove(int unitId, HexCoord position) { projectedUnitPositions[unitId] = position; }
            public HexCoord ProjectedUnitPosition(UnitState unit)
            {
                if (unit == null) return default;
                return projectedUnitPositions.TryGetValue(unit.id, out var position) ? position : unit.position;
            }
            public bool CanReserveHire(int unitId) => !hiredUnits.Contains(unitId);
            public void ReserveHire(int unitId) { hiredUnits.Add(unitId); }
            public bool CanReserveUpgrade(int buildingId) => !upgradedBuildings.Contains(buildingId);
            public void ReserveUpgrade(int buildingId) { upgradedBuildings.Add(buildingId); }
            public bool CanReserveCapture(HexCoord position) => !capturePositions.Contains(position);
            public void ReserveCapture(HexCoord position) { capturePositions.Add(position); }
            public bool CanReserveBuild(HexCoord position, int factionId, BuildingType type)
            {
                if (buildPositions.Contains(position) || (long)(game.buildings?.Count ?? 0) + buildingReservations >= RuleLimits.MaxBuildings) return false;
                if (type != BuildingType.Headquarters) return true;
                return !headquartersReservations.Contains(factionId) && !(game.buildings ?? new List<BuildingState>())
                    .Any(building => building != null && building.factionId == factionId && building.type == BuildingType.Headquarters && building.hp > 0);
            }
            public void ReserveBuild(HexCoord position, int factionId, BuildingType type)
            {
                buildPositions.Add(position);
                buildingReservations++;
                if (type == BuildingType.Headquarters) headquartersReservations.Add(factionId);
            }
            public void CommitSp(GameSnapshotV1 snapshot)
            {
                foreach (var pair in sp)
                {
                    var faction = (snapshot.factions ?? new List<FactionState>()).FirstOrDefault(candidate => candidate != null && candidate.id == pair.Key);
                    if (faction != null) faction.sp = Math.Max(0, faction.sp - pair.Value);
                }
            }

            private int ReservedSp(int factionId) => sp.TryGetValue(factionId, out var amount) ? amount : 0;
            private int ReservedResource(int factionId, ResourceType type) => resources.TryGetValue(factionId, out var factionResources) && factionResources.TryGetValue(type, out var amount) ? amount : 0;
            private int ReservedGather(HexCoord position) => gathered.TryGetValue(position, out var amount) ? amount : 0;
        }

        private static List<PlannedCommand> PlanAi(GameSnapshotV1 game, DeterministicRandom random)
        {
            var plans = new List<PlannedCommand>();
            var player = game.entities.FirstOrDefault(x => x.factionId == 1 && x.alive);
            var headquarters = game.buildings.FirstOrDefault(b => b.factionId == 1 && b.type == BuildingType.Headquarters && b.hp > 0);
            if (player == null && headquarters == null) return plans;
            var targetPosition = player?.position ?? headquarters.position;
            foreach (var unit in game.entities.Where(x => x.factionId != 1 && x.alive))
            {
                var faction = game.factions.FirstOrDefault(f => f.id == unit.factionId);
                if (faction == null) continue;
                var hostile = faction.kind == FactionKind.Skeleton || faction.relationToPlayer <= -25;
                if (hostile && unit.position.Distance(targetPosition) <= 2)
                {
                    plans.Add(new PlannedCommand { factionId = unit.factionId, unitId = unit.id, type = CommandType.Attack, target = targetPosition, priority = 1 });
                }
                else if (hostile)
                {
                    var next = HexCoord.Directions
                        .Select(d => new HexCoord(unit.position.q + d.q, unit.position.r + d.r))
                        .Where(p => game.map.Any(t => t.position.Equals(p) && t.terrain != "강"))
                        .OrderBy(p => p.Distance(targetPosition))
                        .FirstOrDefault();
                    plans.Add(new PlannedCommand { factionId = unit.factionId, unitId = unit.id, type = CommandType.Move, target = next, priority = 0 });
                }
                else
                {
                    plans.Add(new PlannedCommand { factionId = unit.factionId, unitId = unit.id, type = CommandType.Gather, target = unit.position, priority = 0 });
                }
            }
            return plans;
        }

        private static void ResolveMovement(GameSnapshotV1 game, List<PlannedCommand> commands, DeterministicRandom random, List<string> log, RuleVm vm)
        {
            var moves = commands.Where(c => c.type == CommandType.Move).Where(c =>
            {
                var unit = game.entities.FirstOrDefault(u => u.id == c.unitId && u.alive);
                var tile = game.map.FirstOrDefault(t => t.position.Equals(c.target));
                var valid = unit != null && unit.position.Distance(c.target) == 1 && tile != null && tile.terrain != "강";
                if (!valid) log.Add("이동할 수 없는 타일을 지정해 명령이 취소되었습니다.");
                return valid;
            }).ToList();
            var provisional = new List<PlannedCommand>();
            foreach (var group in moves.GroupBy(c => c.target))
            {
                var candidates = group.ToList();
                if (candidates.Count == 0) continue;
                // 속도 우선, 동률이면 HUD 행운 보정과 시드 기반 난수로 결정
                var winner = candidates
                    .Select(command => new { command, seededRoll = random.Next(0, 100) })
                    .OrderByDescending(candidate => Speed(game, candidate.command.unitId))
                    .ThenByDescending(candidate => GameRules.CollisionTieBreakScore(candidate.command.factionId, game.luck, candidate.seededRoll))
                    .ThenBy(candidate => candidate.command.factionId)
                    .ThenBy(candidate => candidate.command.unitId)
                    .First().command;
                provisional.Add(winner);
                foreach (var c in candidates)
                {
                    if (c != winner) log.Add("유닛 " + c.unitId + "은 이동 충돌로 제자리에 머뭅니다.");
                }
            }
            // A move into an occupied tile is only safe when every occupant will
            // actually vacate it. Propagate a blocked tail backwards through the
            // dependency graph so A->B, B->C cannot stack A on B when C is fixed.
            var movingIds = new HashSet<int>(provisional.Select(c => c.unitId));
            var occupantsByPosition = (game.entities ?? new List<UnitState>())
                .Where(unit => unit != null && unit.alive)
                .GroupBy(unit => unit.position)
                .ToDictionary(group => group.Key, group => group.ToList());
            var dependentsByOccupant = new Dictionary<int, List<int>>();
            var blockedIds = new HashSet<int>();
            var blockedQueue = new Queue<int>();
            foreach (var move in provisional)
            {
                if (!occupantsByPosition.TryGetValue(move.target, out var occupants)) continue;
                foreach (var occupant in occupants.Where(unit => unit.id != move.unitId))
                {
                    if (!movingIds.Contains(occupant.id))
                    {
                        if (blockedIds.Add(move.unitId)) blockedQueue.Enqueue(move.unitId);
                        continue;
                    }
                    if (!dependentsByOccupant.TryGetValue(occupant.id, out var dependents))
                    {
                        dependents = new List<int>();
                        dependentsByOccupant[occupant.id] = dependents;
                    }
                    dependents.Add(move.unitId);
                }
            }
            while (blockedQueue.Count > 0)
            {
                var blockedOccupant = blockedQueue.Dequeue();
                if (!dependentsByOccupant.TryGetValue(blockedOccupant, out var dependents)) continue;
                foreach (var dependent in dependents)
                {
                    if (blockedIds.Add(dependent)) blockedQueue.Enqueue(dependent);
                }
            }
            foreach (var move in provisional.Where(candidate => blockedIds.Contains(candidate.unitId)))
                log.Add("유닛 " + move.unitId + "은 점유된 타일 앞에서 멈췄습니다.");

            var resolvedMoves = provisional
                .Where(candidate => !blockedIds.Contains(candidate.unitId))
                .Select(move => new
                {
                    command = move,
                    unit = game.entities.FirstOrDefault(candidate => candidate.id == move.unitId && candidate.alive)
                })
                .Where(resolved => resolved.unit != null)
                .ToList();
            // Commit the entire successful movement phase before any rule observes it.
            // Player progress is part of that same state transition.
            foreach (var resolved in resolvedMoves)
            {
                resolved.unit.position = resolved.command.target;
                if (resolved.unit.factionId == 1) GameRules.CountAction(game, CommandType.Move);
            }
            // Preserve the existing deterministic event order after the atomic commit.
            foreach (var resolved in resolvedMoves)
            {
                log.Add("유닛 " + resolved.unit.id + "이 이동했습니다.");
                vm.Execute(EventType.Move, game, log);
                vm.Execute(EventType.TileEntered, game, log);
            }
        }

        private static int Speed(GameSnapshotV1 game, int unitId)
        {
            var unit = game.entities.FirstOrDefault(u => u.id == unitId);
            return unit?.speed ?? 1;
        }

        private static bool IsAtReservedActorPosition(UnitState unit, PlannedCommand command, List<string> log)
        {
            if (unit != null && (!command.hasReservedActorPosition || unit.position.Equals(command.reservedActorPosition))) return true;
            if (log != null) log.Add("이동이 완료되지 않아 " + command.type + " 명령이 취소되었습니다.");
            return false;
        }

        private static void ResolveDiplomacy(GameSnapshotV1 game, List<PlannedCommand> commands, List<string> log, RuleVm vm)
        {
            foreach (var c in commands.Where(x => x.type == CommandType.Trade || x.type == CommandType.Persuade || x.type == CommandType.Hire))
            {
                var faction = game.factions.FirstOrDefault(f => f.id == c.factionId);
                var actor = game.entities.FirstOrDefault(u => u.id == c.unitId && u.alive);
                if (faction == null || actor == null) continue;
                if (!IsAtReservedActorPosition(actor, c, log)) continue;
                var targetUnit = game.entities.FirstOrDefault(u => u.factionId != c.factionId && u.alive && u.position.Equals(c.target) && u.position.Distance(actor.position) <= 2);
                var partner = targetUnit == null ? null : game.factions.FirstOrDefault(f => f.id == targetUnit.factionId);
                if (c.type == CommandType.Trade)
                {
                    if (partner != null && faction.resources.Spend(ResourceType.Food, 1))
                    {
                        faction.resources.Add(ResourceType.Coin, 2);
                        partner.relationToPlayer = Math.Max(-100, Math.Min(100, partner.relationToPlayer + 4));
                        if (c.factionId == 1) GameRules.CountAction(game, c.type);
                        log.Add("거래가 성사되었습니다.");
                        vm.Execute(EventType.Trade, game, log);
                        vm.Execute(EventType.RelationChanged, game, log);
                    }
                    else log.Add("거래 상대 또는 식량이 부족합니다.");
                }
                else if (c.type == CommandType.Persuade)
                {
                    if (partner != null)
                    {
                        partner.relationToPlayer = Math.Max(-100, Math.Min(100, partner.relationToPlayer + 8));
                        if (c.factionId == 1) GameRules.CountAction(game, c.type);
                        log.Add(partner.name + "을 설득해 관계가 개선되었습니다.");
                        vm.Execute(EventType.RelationChanged, game, log);
                    }
                    else log.Add("선택한 설득 대상이 없거나 사거리를 벗어났습니다.");
                }
                else if (c.type == CommandType.Hire)
                {
                    if (targetUnit != null && partner != null && partner.kind == FactionKind.Neutral && partner.relationToPlayer >= 0 && faction.resources.Spend(ResourceType.Coin, 3))
                    {
                        targetUnit.factionId = c.factionId;
                        if (!targetUnit.tags.Contains("고용병")) targetUnit.tags.Add("고용병");
                        if (c.factionId == 1) GameRules.CountAction(game, c.type);
                        log.Add("고용병이 세력에 합류했습니다.");
                    }
                    else log.Add("고용 조건을 충족하지 못했습니다.");
                }
            }
        }

        private static void ResolveCombat(GameSnapshotV1 game, List<PlannedCommand> commands, DeterministicRandom random, List<string> log, RuleVm vm)
        {
            // PRD "동시 처치" 재현: 모든 공격의 데미지를 먼저 계산한 뒤 일괄 적용한다.
            // 공격자와 대상이 서로를 동시에 공격하면 둘 다 처치될 수 있어야 한다.
            var attacks = commands.Where(x => x.type == CommandType.Attack).ToList();
            var pendingUnits = new List<Tuple<UnitState, int, int>>();
            var pendingBuildings = new List<Tuple<BuildingState, int, int>>();
            foreach (var c in attacks)
            {
                var attacker = game.entities.FirstOrDefault(u => u.id == c.unitId);
                if (attacker == null || !attacker.alive) continue;
                if (!IsAtReservedActorPosition(attacker, c, log)) continue;
                var target = game.entities.FirstOrDefault(u => u.factionId != c.factionId && u.alive && u.position.Equals(c.target) && u.position.Distance(attacker.position) <= 2);
                var targetBuilding = target == null ? game.buildings.FirstOrDefault(b => b.factionId != c.factionId && b.hp > 0 && b.position.Equals(c.target) && b.position.Distance(attacker.position) <= 2) : null;
                if (target == null && targetBuilding == null) { log.Add("선택한 대상이 사거리를 벗어났습니다."); continue; }
                // 시드 기반 난수로 데미지 결정 (예상 범위 2~3)
                var lucky = game.luck >= 70;
                var damage = lucky || random.Percent() < 20 ? 3 : 2;
                if (target != null)
                {
                    pendingUnits.Add(Tuple.Create(target, damage, c.factionId));
                    log.Add("유닛 " + attacker.id + "이 유닛 " + target.id + "을 공격합니다 (피해 " + damage + ").");
                }
                else
                {
                    pendingBuildings.Add(Tuple.Create(targetBuilding, damage, c.factionId));
                    log.Add("유닛 " + attacker.id + "이 " + targetBuilding.type + "을 공격합니다 (피해 " + damage + ").");
                }
                if (c.factionId == 1) GameRules.CountAction(game, CommandType.Attack);
                vm.Execute(EventType.Attack, game, log);
            }
            // 일괄 적용
            foreach (var hit in pendingUnits)
            {
                var target = hit.Item1;
                target.hp = (int)Math.Max(0L, (long)target.hp - hit.Item2);
                if (target.hp == 0 && target.alive)
                {
                    target.alive = false;
                    if (hit.Item3 == 1 && game.playerKills < RuleLimits.MaxStateMagnitude) game.playerKills++;
                    log.Add("코믹한 일격! 유닛 " + target.id + "을 처치했습니다.");
                    vm.Execute(EventType.Kill, game, log);
                }
            }
            foreach (var hit in pendingBuildings)
            {
                var target = hit.Item1;
                target.hp = (int)Math.Max(0L, (long)target.hp - hit.Item2);
                if (target.hp == 0) log.Add(target.type + "이 무너졌습니다.");
            }
        }

        private static void ResolveCapture(GameSnapshotV1 game, List<PlannedCommand> commands, List<string> log, RuleVm vm)
        {
            foreach (var command in commands.Where(candidate => candidate.type == CommandType.Capture))
            {
                var unit = (game.entities ?? new List<UnitState>()).FirstOrDefault(candidate => candidate != null && candidate.id == command.unitId && candidate.factionId == 1 && candidate.alive);
                if (unit == null || !IsAtReservedActorPosition(unit, command, log)) continue;
                var tile = (game.map ?? new List<TileState>()).FirstOrDefault(candidate => candidate != null && candidate.position.Equals(unit.position));
                if (tile == null || tile.owner == 1 || !command.target.Equals(unit.position))
                {
                    log?.Add("점령할 수 없는 타일입니다.");
                    continue;
                }

                var enemyUnitRemains = (game.entities ?? new List<UnitState>()).Any(candidate => candidate != null && candidate.factionId != 1 && candidate.alive && candidate.position.Equals(unit.position));
                var enemyStrongholdRemains = (game.buildings ?? new List<BuildingState>()).Any(candidate => candidate != null && candidate.factionId != 1 && candidate.hp > 0 && candidate.position.Equals(unit.position));
                if (enemyUnitRemains || enemyStrongholdRemains)
                {
                    log?.Add("살아있는 적 유닛 또는 적 거점이 남아 점령에 실패했습니다.");
                    continue;
                }

                tile.owner = 1;
                GameRules.CountAction(game, CommandType.Capture);
                log?.Add("유닛 " + unit.id + "이 " + tile.position + " 타일을 원정대 영토로 점령했습니다.");
                vm.Execute(EventType.Capture, game, log);
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
                if (!IsAtReservedActorPosition(unit, c, log)) continue;
                if (c.type == CommandType.Gather)
                {
                    var tile = game.map.FirstOrDefault(t => t.position.Equals(unit.position));
                    if (tile != null && tile.amount > 0)
                    {
                        tile.amount--;
                        faction.resources.Add(tile.resource, 2);
                        if (c.factionId == 1) GameRules.CountAction(game, c.type);
                        log.Add(tile.resource + " 2을 채집했습니다.");
                        vm.Execute(EventType.Gather, game, log);
                    }
                    else log.Add("이 타일에는 채집할 자원이 없습니다.");
                }
                else if (c.type == CommandType.Hunt)
                {
                    var tile = game.map.FirstOrDefault(t => t.position.Equals(unit.position));
                    var amount = (tile != null && tile.terrain == "숲" ? 3 : 2) + (game.luck >= 70 ? 1 : 0);
                    faction.resources.Add(ResourceType.Food, amount);
                    if (c.factionId == 1) GameRules.CountAction(game, c.type);
                    log.Add("수렵으로 식량 " + amount + "을 확보했습니다.");
                    vm.Execute(EventType.Gather, game, log);
                }
                else if (c.type == CommandType.Build)
                {
                    if (game.buildings.Any(b => b.position.Equals(unit.position))) { log.Add("이미 건물이 있는 타일입니다."); continue; }
                    var type = Enum.IsDefined(typeof(BuildingType), c.buildingType) ? c.buildingType : BuildingType.Warehouse;
                    var woodCost = GameRules.BuildingCost(type);
                    var ironCost = GameRules.BuildingIronCost(type);
                    if (faction.resources.Get(ResourceType.Wood) >= woodCost && faction.resources.Get(ResourceType.Iron) >= ironCost)
                    {
                        faction.resources.Spend(ResourceType.Wood, woodCost);
                        if (ironCost > 0) faction.resources.Spend(ResourceType.Iron, ironCost);
                        var id = game.buildings.Count == 0 ? 1 : game.buildings.Max(b => b.id) + 1;
                        game.buildings.Add(new BuildingState { id = id, factionId = c.factionId, position = unit.position, type = type });
                        var tile = game.map.FirstOrDefault(t => t.position.Equals(unit.position));
                        if (tile != null) tile.owner = c.factionId;
                        if (c.factionId == 1) GameRules.CountAction(game, c.type);
                        log.Add(type + "을 건설했습니다.");
                        vm.Execute(EventType.Build, game, log);
                    }
                    else log.Add(type + " 건설에 필요한 목재 또는 철이 부족합니다.");
                }
                else if (c.type == CommandType.Upgrade)
                {
                    var building = game.buildings.FirstOrDefault(b => b.factionId == c.factionId && b.hp > 0 && b.position.Equals(c.target));
                    if (building != null && unit.position.Distance(building.position) <= 1 && faction.resources.Spend(ResourceType.Stone, 3))
                    {
                        building.level++;
                        building.hp += 3;
                        if (c.factionId == 1) GameRules.CountAction(game, c.type);
                        log.Add(building.type + "을 " + building.level + "단계로 강화했습니다.");
                        vm.Execute(EventType.Build, game, log);
                    }
                    else log.Add("강화할 건물 또는 석재가 부족합니다.");
                }
            }
        }
    }
}

#pragma warning restore UAC1008
#pragma warning restore UAC1006
#pragma warning restore UAC1005
