using System;
using System.Collections.Generic;
using System.Linq;

#nullable disable

// Expression ASTs travel through JSON and are never persisted by Unity's field
// serializer. Their recursion is deliberately constrained by depth/node/work
// budgets, so Unity serialization-cycle diagnostics are not applicable here.
#pragma warning disable UAC1005
#pragma warning disable UAC1006
#pragma warning disable UAC1008

namespace OnlyMyGame.Core
{
    public enum RuleStateScope { Run, Turn, Faction, Unit, Building, Tile }
    public enum RuleStateValueType { Number, Boolean, Set }
    public enum NumberExpressionOp { Constant, State, Add, Subtract, Multiply, Divide, CountUnits, CountBuildings, CountTiles, Distance, RecentActionRatio }
    public enum PredicateExpressionOp { All, Any, Not, NumberEqual, NumberNotEqual, NumberGreater, NumberGreaterOrEqual, NumberLess, NumberLessOrEqual, BoolState, SetContains }
    public enum StateMutationOp { Set, Add, Toggle, SetAdd, SetRemove }

    [Serializable]
    public sealed class StateReferenceV1
    {
        public RuleStateScope scope;
        public string scopeId;
        public string key;
    }

    [Serializable]
    public sealed class StateDefinitionV1
    {
        public RuleStateScope scope;
        public string scopeId;
        public string key;
        public RuleStateValueType valueType;
        public string koreanName;
        public string iconToken;
        public string colorHex;
        public int initialNumber;
        public bool initialBool;
        public List<string> initialSet = new List<string>();
    }

    [Serializable]
    public sealed class TypedRuleStateEntryV1
    {
        public RuleStateScope scope;
        public string scopeId;
        public string key;
        public RuleStateValueType valueType;
        public string koreanName;
        public string iconToken;
        public string colorHex;
        public int numberValue;
        public bool boolValue;
        public List<string> setValue = new List<string>();
        public int stateTurn;
    }

    [Serializable]
    public sealed class ActionTurnStatV1
    {
        public int turn;
        public CommandType type;
        public int count;
    }

    [Serializable]
    public sealed class NumberExpressionV1
    {
        public NumberExpressionOp op;
        public int constant;
        public StateReferenceV1 state;
        public NumberExpressionV1 left;
        public NumberExpressionV1 right;
        public string selector;
        public string secondSelector;
        public CommandType action;
        public int recentTurns = 1;
    }

    [Serializable]
    public sealed class PredicateExpressionV1
    {
        public PredicateExpressionOp op;
        public List<PredicateExpressionV1> children = new List<PredicateExpressionV1>();
        public PredicateExpressionV1 child;
        public NumberExpressionV1 left;
        public NumberExpressionV1 right;
        public StateReferenceV1 state;
        public string element;
    }

    [Serializable]
    public sealed class StateMutationV1
    {
        public StateMutationOp op;
        public StateReferenceV1 state;
        public NumberExpressionV1 numberValue;
        public bool boolValue;
        public List<string> setValues = new List<string>();
        public string element;
    }

    public static class RuleExpressionRuntime
    {
        private sealed class EvaluationBudget
        {
            public int nodes;
            public readonly HashSet<object> path = new HashSet<object>();
        }

        public static bool EnsureDefinitions(IEnumerable<StateDefinitionV1> definitions, GameSnapshotV1 game)
        {
            var incoming = (definitions ?? Enumerable.Empty<StateDefinitionV1>()).Take(RuleLimits.MaxStateVariables + 1).ToList();
            if (incoming.Count > RuleLimits.MaxStateVariables) return false;
            return EnsureDefinitionBatch(incoming, game);
        }

        public static bool EnsureActiveDefinitions(GameSnapshotV1 game)
        {
            if (game == null) return false;
            var definitions = new List<StateDefinitionV1>();
            foreach (var rule in (game.activeRules ?? new List<RuleNodeV1>())
                         .Where(rule => rule != null && GameRules.IsRuleActive(rule, game.turn))
                         .OrderBy(rule => rule.id ?? "", StringComparer.Ordinal))
            {
                var owned = (rule.stateDefinitions ?? new List<StateDefinitionV1>()).Take(RuleLimits.MaxStateDefinitionsPerRule + 1).ToList();
                if (owned.Count > RuleLimits.MaxStateDefinitionsPerRule) return false;
                foreach (var definition in owned)
                {
                    definitions.Add(definition);
                }
            }
            if (definitions.Count > RuleLimits.MaxStateVariables) return false;
            var cache = game.ruleBudget ?? (game.ruleBudget = new RuleRuntimeBudget());
            var cachedDefinitions = cache.definitionRegistryDefinitions;
            // Installed definitions are immutable; ruleset replacement installs new
            // objects. A non-serialized reference cache therefore avoids rebuilding
            // world indexes for every event while still refreshing on replacement
            // and at every turn boundary (needed for Turn-scope resets).
            if (cache.definitionRegistryTurn == game.turn && cachedDefinitions != null && cachedDefinitions.Count == definitions.Count)
            {
                var unchanged = true;
                for (var index = 0; index < definitions.Count; index++)
                    if (!ReferenceEquals(cachedDefinitions[index], definitions[index])) { unchanged = false; break; }
                if (unchanged) return true;
            }
            if (!EnsureDefinitionBatch(definitions, game)) return false;
            cache.definitionRegistryTurn = game.turn;
            cache.definitionRegistryDefinitions = new List<StateDefinitionV1>(definitions);
            return true;
        }

        public static bool CanonicalizeStoredStateScopeIds(GameSnapshotV1 game)
        {
            if (game == null) return false;
            var states = game.typedRuleState ?? (game.typedRuleState = new List<TypedRuleStateEntryV1>());
            var normalized = new List<string>(states.Count);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in states)
            {
                if (entry == null || !Enum.IsDefined(typeof(RuleStateScope), entry.scope)) return false;
                var scopeId = RuleExpressionSelectors.NormalizeScopeId(entry.scope, entry.scopeId, game);
                if (!identities.Add(StateIdentity(entry.scope, scopeId, entry.key))) return false;
                normalized.Add(scopeId);
            }
            for (var index = 0; index < states.Count; index++) states[index].scopeId = normalized[index];
            return true;
        }

        public static bool CanApplyMutationsAtomically(IEnumerable<EffectNode> effects, GameSnapshotV1 game)
        {
            if (game == null) return false;
            var shadow = new GameSnapshotV1
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
                map = game.map,
                entities = game.entities,
                buildings = game.buildings,
                factions = game.factions,
                actionStats = game.actionStats,
                activeRules = game.activeRules,
                victoryContracts = game.victoryContracts,
                dynamicActions = game.dynamicActions,
                ruleState = game.ruleState,
                typedRuleState = (game.typedRuleState ?? new List<TypedRuleStateEntryV1>()).Select(CloneState).ToList(),
                recentActionStats = game.recentActionStats,
                ruleBudget = game.ruleBudget,
                journal = game.journal,
                catalogHash = game.catalogHash
            };
            foreach (var effect in effects ?? Enumerable.Empty<EffectNode>())
                if (effect?.type == EffectType.TypedState && !ApplyStateMutation(effect.stateMutation, shadow)) return false;
            return true;
        }

        private static bool EnsureDefinitionBatch(IReadOnlyList<StateDefinitionV1> incoming, GameSnapshotV1 game)
        {
            if (game == null) return false;
            if (incoming.Count == 0) return true;
            if (incoming.Count > RuleLimits.MaxStateVariables) return false;
            if (!CanonicalizeStoredStateScopeIds(game)) return false;
            if (!RuleExpressionSelectors.TryBuildWorldReferenceIndex(game, out var worldIndex)) return false;
            var states = game.typedRuleState ?? (game.typedRuleState = new List<TypedRuleStateEntryV1>());
            var legacyCount = game.ruleState?.Count ?? 0;
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var additions = 0;
            foreach (var definition in incoming)
            {
                if (!RuleExpressionSelectors.IsDefinitionShapeSafe(definition) ||
                    !RuleExpressionSelectors.IsScopeSelectorValid(definition.scope, definition.scopeId, worldIndex)) return false;
                var normalizedScopeId = RuleExpressionSelectors.NormalizeScopeId(definition.scope, definition.scopeId, game);
                var identity = StateIdentity(definition.scope, normalizedScopeId, definition.key);
                if (!identities.Add(identity)) return false;
                var entry = states.FirstOrDefault(candidate => SameState(candidate, definition.scope, normalizedScopeId, definition.key, game));
                if (entry == null) additions++;
                else if (!MatchesStoredContract(entry, definition)) return false;
            }
            if (legacyCount + states.Count + additions > RuleLimits.MaxStateVariables) return false;
            foreach (var definition in incoming)
            {
                var normalizedScopeId = RuleExpressionSelectors.NormalizeScopeId(definition.scope, definition.scopeId, game);
                var entry = states.FirstOrDefault(candidate => SameState(candidate, definition.scope, normalizedScopeId, definition.key, game));
                if (entry == null)
                {
                    entry = new TypedRuleStateEntryV1
                    {
                        scope = definition.scope,
                        scopeId = normalizedScopeId,
                        key = definition.key,
                        valueType = definition.valueType,
                        koreanName = definition.koreanName,
                        iconToken = definition.iconToken,
                        colorHex = definition.colorHex,
                        stateTurn = definition.scope == RuleStateScope.Turn ? game.turn : 0
                    };
                    ResetValue(entry, definition);
                    states.Add(entry);
                }
                else
                {
                    entry.koreanName = definition.koreanName;
                    entry.iconToken = definition.iconToken;
                    entry.colorHex = definition.colorHex;
                    if (entry.scope == RuleStateScope.Turn && entry.stateTurn != game.turn)
                    {
                        entry.stateTurn = game.turn;
                        ResetValue(entry, definition);
                    }
                }
            }
            return true;
        }

        public static bool TryGetState(GameSnapshotV1 game, StateReferenceV1 reference, out TypedRuleStateEntryV1 entry)
        {
            entry = null;
            if (game == null || reference == null || !RuleExpressionSelectors.IsStateReferenceSafe(reference, game)) return false;
            var normalizedScopeId = RuleExpressionSelectors.NormalizeScopeId(reference.scope, reference.scopeId, game);
            entry = (game.typedRuleState ?? new List<TypedRuleStateEntryV1>())
                .FirstOrDefault(candidate => SameState(candidate, reference.scope, normalizedScopeId, reference.key, game) && (reference.scope != RuleStateScope.Turn || candidate.stateTurn == game.turn));
            return entry != null;
        }

        public static bool TryReadNumber(GameSnapshotV1 game, StateReferenceV1 reference, out int value)
        {
            value = 0;
            if (!TryGetState(game, reference, out var entry) || entry.valueType != RuleStateValueType.Number) return false;
            value = entry.numberValue;
            return value >= -RuleLimits.MaxStateMagnitude && value <= RuleLimits.MaxStateMagnitude;
        }

        public static bool TryReadBool(GameSnapshotV1 game, StateReferenceV1 reference, out bool value)
        {
            value = false;
            if (!TryGetState(game, reference, out var entry) || entry.valueType != RuleStateValueType.Boolean) return false;
            value = entry.boolValue;
            return true;
        }

        public static bool TryReadSet(GameSnapshotV1 game, StateReferenceV1 reference, out IReadOnlyList<string> value)
        {
            value = Array.Empty<string>();
            if (!TryGetState(game, reference, out var entry) || entry.valueType != RuleStateValueType.Set || !RuleExpressionSelectors.IsSetSafe(entry.setValue)) return false;
            value = entry.setValue ?? (IReadOnlyList<string>)Array.Empty<string>();
            return true;
        }

        public static bool ApplyStateMutation(StateMutationV1 mutation, GameSnapshotV1 game)
        {
            if (mutation == null || !Enum.IsDefined(typeof(StateMutationOp), mutation.op) || !TryGetState(game, mutation.state, out var entry)) return false;
            if (entry.valueType == RuleStateValueType.Number)
            {
                if (mutation.op != StateMutationOp.Set && mutation.op != StateMutationOp.Add) return false;
                if (!TryEvaluateNumber(mutation.numberValue, game, out var evaluated)) return false;
                var next = mutation.op == StateMutationOp.Set ? (long)evaluated : (long)entry.numberValue + evaluated;
                if (next < -RuleLimits.MaxStateMagnitude || next > RuleLimits.MaxStateMagnitude) return false;
                entry.numberValue = (int)next;
                return true;
            }
            if (entry.valueType == RuleStateValueType.Boolean)
            {
                if (mutation.op == StateMutationOp.Set) entry.boolValue = mutation.boolValue;
                else if (mutation.op == StateMutationOp.Toggle) entry.boolValue = !entry.boolValue;
                else return false;
                return true;
            }
            if (entry.valueType != RuleStateValueType.Set) return false;
            var values = new HashSet<string>(entry.setValue ?? new List<string>(), StringComparer.Ordinal);
            if (mutation.op == StateMutationOp.Set)
            {
                if (!RuleExpressionSelectors.IsSetSafe(mutation.setValues)) return false;
                values = new HashSet<string>(mutation.setValues, StringComparer.Ordinal);
            }
            else if (mutation.op == StateMutationOp.SetAdd)
            {
                if (!RuleExpressionSelectors.IsSetElementSafe(mutation.element) || !values.Contains(mutation.element) && values.Count >= RuleLimits.MaxStateSetElements) return false;
                values.Add(mutation.element);
            }
            else if (mutation.op == StateMutationOp.SetRemove)
            {
                if (!RuleExpressionSelectors.IsSetElementSafe(mutation.element)) return false;
                values.Remove(mutation.element);
            }
            else return false;
            entry.setValue = values.OrderBy(item => item, StringComparer.Ordinal).ToList();
            return true;
        }

        public static bool TryEvaluateNumber(NumberExpressionV1 expression, GameSnapshotV1 game, out int value)
        {
            value = 0;
            if (expression == null || game == null) return false;
            return EvaluateNumber(expression, game, new EvaluationBudget(), 1, out value);
        }

        public static bool TryEvaluatePredicate(PredicateExpressionV1 predicate, GameSnapshotV1 game, out bool value)
        {
            value = false;
            if (predicate == null || game == null) return false;
            return EvaluatePredicate(predicate, game, new EvaluationBudget(), 1, out value);
        }

        public static int RecentActionRatio(GameSnapshotV1 game, CommandType action, int recentTurns)
        {
            if (game == null || !Enum.IsDefined(typeof(CommandType), action) || recentTurns < 1 || recentTurns > RuleLimits.MaxRecentActionTurns) return 0;
            var minimumTurn = Math.Max(0, game.turn - recentTurns + 1);
            long matching = 0;
            long total = 0;
            foreach (var stat in game.recentActionStats ?? new List<ActionTurnStatV1>())
            {
                if (stat == null || stat.turn < minimumTurn || stat.turn > game.turn || stat.count <= 0 || !Enum.IsDefined(typeof(CommandType), stat.type)) continue;
                total += stat.count;
                if (stat.type == action) matching += stat.count;
            }
            if (total <= 0) return 0;
            return (int)Math.Min(100L, matching * 100L / total);
        }

        private static bool EvaluateNumber(NumberExpressionV1 expression, GameSnapshotV1 game, EvaluationBudget budget, int depth, out int value)
        {
            value = 0;
            if (expression == null || depth > RuleLimits.MaxConditionDepth || budget.nodes >= RuleLimits.MaxConditionNodes || !budget.path.Add(expression)) return false;
            budget.nodes++;
            var valid = true;
            long result = 0;
            if (!Enum.IsDefined(typeof(NumberExpressionOp), expression.op)) valid = false;
            else if (expression.op == NumberExpressionOp.Constant) result = expression.constant;
            else if (expression.op == NumberExpressionOp.State) valid = TryReadNumber(game, expression.state, out value);
            else if (expression.op == NumberExpressionOp.Add || expression.op == NumberExpressionOp.Subtract || expression.op == NumberExpressionOp.Multiply || expression.op == NumberExpressionOp.Divide)
            {
                var left = 0;
                var right = 0;
                valid = EvaluateNumber(expression.left, game, budget, depth + 1, out left);
                if (valid) valid = EvaluateNumber(expression.right, game, budget, depth + 1, out right);
                if (valid)
                {
                    if (expression.op == NumberExpressionOp.Add) result = (long)left + right;
                    else if (expression.op == NumberExpressionOp.Subtract) result = (long)left - right;
                    else if (expression.op == NumberExpressionOp.Multiply) result = (long)left * right;
                    else if (right == 0) valid = false;
                    else result = left / (long)right;
                }
            }
            else if (expression.op == NumberExpressionOp.CountUnits) valid = RuleExpressionSelectors.TryCountUnits(game, expression.selector, out value);
            else if (expression.op == NumberExpressionOp.CountBuildings) valid = RuleExpressionSelectors.TryCountBuildings(game, expression.selector, out value);
            else if (expression.op == NumberExpressionOp.CountTiles) valid = RuleExpressionSelectors.TryCountTiles(game, expression.selector, out value);
            else if (expression.op == NumberExpressionOp.Distance) valid = RuleExpressionSelectors.TryResolvePosition(game, expression.selector, out var first) && RuleExpressionSelectors.TryResolvePosition(game, expression.secondSelector, out var second) && Assign(first.Distance(second), out value);
            else if (expression.op == NumberExpressionOp.RecentActionRatio)
            {
                valid = Enum.IsDefined(typeof(CommandType), expression.action) && expression.recentTurns >= 1 && expression.recentTurns <= RuleLimits.MaxRecentActionTurns;
                if (valid) value = RecentActionRatio(game, expression.action, expression.recentTurns);
            }
            if (valid && expression.op != NumberExpressionOp.State && expression.op != NumberExpressionOp.CountUnits && expression.op != NumberExpressionOp.CountBuildings && expression.op != NumberExpressionOp.CountTiles && expression.op != NumberExpressionOp.Distance && expression.op != NumberExpressionOp.RecentActionRatio)
            {
                valid = result >= -RuleLimits.MaxStateMagnitude && result <= RuleLimits.MaxStateMagnitude;
                if (valid) value = (int)result;
            }
            if (valid) valid = value >= -RuleLimits.MaxStateMagnitude && value <= RuleLimits.MaxStateMagnitude;
            budget.path.Remove(expression);
            return valid;
        }

        private static bool EvaluatePredicate(PredicateExpressionV1 predicate, GameSnapshotV1 game, EvaluationBudget budget, int depth, out bool value)
        {
            value = false;
            if (predicate == null || depth > RuleLimits.MaxConditionDepth || budget.nodes >= RuleLimits.MaxConditionNodes || !budget.path.Add(predicate) || !Enum.IsDefined(typeof(PredicateExpressionOp), predicate.op)) return false;
            budget.nodes++;
            var valid = true;
            if (predicate.op == PredicateExpressionOp.All || predicate.op == PredicateExpressionOp.Any)
            {
                var children = predicate.children ?? new List<PredicateExpressionV1>();
                if (children.Count == 0) valid = false;
                else
                {
                    value = predicate.op == PredicateExpressionOp.All;
                    foreach (var child in children)
                    {
                        if (!EvaluatePredicate(child, game, budget, depth + 1, out var childValue)) { valid = false; break; }
                        if (predicate.op == PredicateExpressionOp.All && !childValue) value = false;
                        if (predicate.op == PredicateExpressionOp.Any && childValue) value = true;
                    }
                }
            }
            else if (predicate.op == PredicateExpressionOp.Not)
            {
                valid = EvaluatePredicate(predicate.child, game, budget, depth + 1, out var childValue);
                if (valid) value = !childValue;
            }
            else if (predicate.op == PredicateExpressionOp.BoolState) valid = TryReadBool(game, predicate.state, out value);
            else if (predicate.op == PredicateExpressionOp.SetContains)
            {
                IReadOnlyList<string> values = Array.Empty<string>();
                valid = RuleExpressionSelectors.IsSetElementSafe(predicate.element) && TryReadSet(game, predicate.state, out values);
                if (valid) value = values.Contains(predicate.element);
            }
            else
            {
                var left = 0;
                var right = 0;
                valid = EvaluateNumber(predicate.left, game, budget, depth + 1, out left);
                if (valid) valid = EvaluateNumber(predicate.right, game, budget, depth + 1, out right);
                if (valid) value = predicate.op == PredicateExpressionOp.NumberEqual ? left == right :
                    predicate.op == PredicateExpressionOp.NumberNotEqual ? left != right :
                    predicate.op == PredicateExpressionOp.NumberGreater ? left > right :
                    predicate.op == PredicateExpressionOp.NumberGreaterOrEqual ? left >= right :
                    predicate.op == PredicateExpressionOp.NumberLess ? left < right :
                    predicate.op == PredicateExpressionOp.NumberLessOrEqual && left <= right;
            }
            budget.path.Remove(predicate);
            return valid;
        }

        private static bool Assign(int source, out int destination) { destination = source; return true; }

        private static bool SameState(TypedRuleStateEntryV1 entry, RuleStateScope scope, string scopeId, string key, GameSnapshotV1 game)
        {
            return entry != null && entry.scope == scope &&
                   string.Equals(RuleExpressionSelectors.NormalizeScopeId(entry.scope, entry.scopeId, game), scopeId ?? "", StringComparison.Ordinal) &&
                   string.Equals(entry.key, key, StringComparison.Ordinal);
        }

        private static bool MatchesStoredContract(TypedRuleStateEntryV1 entry, StateDefinitionV1 definition)
        {
            return entry.valueType == definition.valueType &&
                   string.Equals(entry.koreanName, definition.koreanName, StringComparison.Ordinal) &&
                   string.Equals(entry.iconToken, definition.iconToken, StringComparison.Ordinal) &&
                   string.Equals(entry.colorHex, definition.colorHex, StringComparison.OrdinalIgnoreCase);
        }

        private static TypedRuleStateEntryV1 CloneState(TypedRuleStateEntryV1 entry)
        {
            if (entry == null) return null;
            return new TypedRuleStateEntryV1
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
            };
        }

        private static string StateIdentity(RuleStateScope scope, string scopeId, string key) => (int)scope + "|" + (scopeId ?? "") + "|" + (key ?? "");

        private static void ResetValue(TypedRuleStateEntryV1 entry, StateDefinitionV1 definition)
        {
            entry.numberValue = definition.initialNumber;
            entry.boolValue = definition.initialBool;
            entry.setValue = (definition.initialSet ?? new List<string>()).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToList();
        }
    }

    internal sealed class RuleWorldReferenceIndex
    {
        public readonly HashSet<int> factionIds;
        public readonly HashSet<int> unitIds;
        public readonly HashSet<int> buildingIds;
        public readonly HashSet<HexCoord> tilePositions;

        public RuleWorldReferenceIndex(
            HashSet<int> factionIds,
            HashSet<int> unitIds,
            HashSet<int> buildingIds,
            HashSet<HexCoord> tilePositions)
        {
            this.factionIds = factionIds;
            this.unitIds = unitIds;
            this.buildingIds = buildingIds;
            this.tilePositions = tilePositions;
        }
    }

    internal static class RuleExpressionSelectors
    {
        public static bool TryBuildWorldReferenceIndex(GameSnapshotV1 game, out RuleWorldReferenceIndex index)
        {
            index = null;
            if (game?.factions == null || game.entities == null || game.buildings == null || game.map == null ||
                game.factions.Count > RuleLimits.MaxFactions || game.entities.Count > RuleLimits.MaxEntities ||
                game.buildings.Count > RuleLimits.MaxBuildings || game.map.Count > RuleLimits.MaxMapTiles) return false;
            var factionIds = new HashSet<int>();
            foreach (var faction in game.factions)
                if (faction == null || faction.id <= 0 || faction.id > RuleLimits.MaxStateMagnitude || !factionIds.Add(faction.id)) return false;
            var unitIds = new HashSet<int>();
            foreach (var unit in game.entities)
                if (unit == null || unit.id <= 0 || unit.id > RuleLimits.MaxStateMagnitude || !unitIds.Add(unit.id)) return false;
            var buildingIds = new HashSet<int>();
            foreach (var building in game.buildings)
                if (building == null || building.id <= 0 || building.id > RuleLimits.MaxStateMagnitude || !buildingIds.Add(building.id)) return false;
            var tilePositions = new HashSet<HexCoord>();
            foreach (var tile in game.map)
                if (tile == null || !CoordinateIsBounded(tile.position) || !tilePositions.Add(tile.position)) return false;
            index = new RuleWorldReferenceIndex(factionIds, unitIds, buildingIds, tilePositions);
            return true;
        }

        public static bool IsDefinitionShapeSafe(StateDefinitionV1 definition)
        {
            return definition != null && Enum.IsDefined(typeof(RuleStateScope), definition.scope) && Enum.IsDefined(typeof(RuleStateValueType), definition.valueType) &&
                   IsIdentifier(definition.key) && IsKoreanName(definition.koreanName) && IsIconToken(definition.iconToken) && IsColor(definition.colorHex) &&
                   IsScopeSelectorShapeSafe(definition.scope, definition.scopeId) && definition.initialNumber >= -RuleLimits.MaxStateMagnitude && definition.initialNumber <= RuleLimits.MaxStateMagnitude &&
                   (definition.valueType != RuleStateValueType.Set || IsSetSafe(definition.initialSet));
        }

        public static bool IsStateReferenceSafe(StateReferenceV1 reference, GameSnapshotV1 game)
        {
            return reference != null && Enum.IsDefined(typeof(RuleStateScope), reference.scope) && IsIdentifier(reference.key) && IsScopeSelectorValid(reference.scope, reference.scopeId, game);
        }

        public static bool IsStateReferenceShapeSafe(StateReferenceV1 reference)
        {
            if (reference == null || !Enum.IsDefined(typeof(RuleStateScope), reference.scope) || !IsIdentifier(reference.key)) return false;
            return IsScopeSelectorShapeSafe(reference.scope, reference.scopeId);
        }

        public static bool IsScopeSelectorShapeSafe(RuleStateScope scope, string scopeId)
        {
            if (!Enum.IsDefined(typeof(RuleStateScope), scope)) return false;
            if (scope == RuleStateScope.Run || scope == RuleStateScope.Turn) return string.IsNullOrEmpty(scopeId);
            if (scope == RuleStateScope.Faction)
                return string.Equals(scopeId, "player", StringComparison.OrdinalIgnoreCase) ||
                       TryPrefixedId(scopeId, "faction:", out var factionId) && factionId <= RuleLimits.MaxStateMagnitude;
            if (scope == RuleStateScope.Unit)
                return TryPrefixedId(scopeId, "unit:", out var unitId) && unitId <= RuleLimits.MaxStateMagnitude;
            if (scope == RuleStateScope.Building)
                return TryPrefixedId(scopeId, "building:", out var buildingId) && buildingId <= RuleLimits.MaxStateMagnitude;
            return scope == RuleStateScope.Tile && TryHex(scopeId, out var tile) && CoordinateIsBounded(tile);
        }

        public static bool IsNumberSelectorShapeSafe(NumberExpressionOp op, string selector)
        {
            if (selector != null && (selector.Length > RuleLimits.MaxIdentifierLength || selector.Any(char.IsControl))) return false;
            if (op == NumberExpressionOp.CountUnits)
            {
                if (string.IsNullOrEmpty(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase) || string.Equals(selector, "player", StringComparison.OrdinalIgnoreCase)) return true;
                return TryPrefixedId(selector, "faction:", out var factionId) && factionId <= RuleLimits.MaxStateMagnitude ||
                       TryPrefixedId(selector, "unit:", out var unitId) && unitId <= RuleLimits.MaxStateMagnitude;
            }
            if (op == NumberExpressionOp.CountBuildings)
            {
                if (string.IsNullOrEmpty(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase) || string.Equals(selector, "player", StringComparison.OrdinalIgnoreCase)) return true;
                if (TryPrefixedId(selector, "faction:", out var factionId)) return factionId <= RuleLimits.MaxStateMagnitude;
                if (TryPrefixedId(selector, "building:", out var buildingId)) return buildingId <= RuleLimits.MaxStateMagnitude;
                return selector.StartsWith("type:", StringComparison.OrdinalIgnoreCase) &&
                       Enum.TryParse<BuildingType>(selector.Substring(5), true, out var type) && Enum.IsDefined(typeof(BuildingType), type);
            }
            if (op == NumberExpressionOp.CountTiles)
            {
                if (string.IsNullOrEmpty(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(selector, "player_owned", StringComparison.OrdinalIgnoreCase) || string.Equals(selector, "neutral", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(selector, "visible", StringComparison.OrdinalIgnoreCase) || string.Equals(selector, "explored", StringComparison.OrdinalIgnoreCase)) return true;
                if (TryPrefixedNonNegativeId(selector, "owner:", out var owner)) return owner <= RuleLimits.MaxStateMagnitude;
                if (selector.StartsWith("terrain:", StringComparison.OrdinalIgnoreCase))
                {
                    var terrain = selector.Substring(8);
                    return terrain.Length > 0 && terrain.Length <= RuleLimits.MaxIdentifierLength && terrain.All(character => !char.IsControl(character));
                }
                return TryHex(selector, out var tile) && CoordinateIsBounded(tile);
            }
            if (op != NumberExpressionOp.Distance || string.IsNullOrWhiteSpace(selector)) return false;
            if (string.Equals(selector, "player_unit", StringComparison.OrdinalIgnoreCase) || string.Equals(selector, "player_hq", StringComparison.OrdinalIgnoreCase)) return true;
            if (TryPrefixedId(selector, "unit:", out var distanceUnitId)) return distanceUnitId <= RuleLimits.MaxStateMagnitude;
            if (TryPrefixedId(selector, "building:", out var distanceBuildingId)) return distanceBuildingId <= RuleLimits.MaxStateMagnitude;
            return TryHex(selector, out var distanceTile) && CoordinateIsBounded(distanceTile);
        }

        public static bool IsSetSafe(IEnumerable<string> values)
        {
            var list = (values ?? Enumerable.Empty<string>()).ToList();
            return list.Count <= RuleLimits.MaxStateSetElements && list.All(IsSetElementSafe) && list.Distinct(StringComparer.Ordinal).Count() == list.Count;
        }

        public static bool IsSetElementSafe(string value) => IsIdentifier(value);

        public static string NormalizeScopeId(RuleStateScope scope, string scopeId, GameSnapshotV1 game)
        {
            if (scope == RuleStateScope.Run || scope == RuleStateScope.Turn) return "";
            if (scope == RuleStateScope.Faction)
            {
                if (string.Equals(scopeId, "player", StringComparison.OrdinalIgnoreCase)) return "faction:1";
                return TryPrefixedId(scopeId, "faction:", out var factionId) ? "faction:" + factionId : scopeId ?? "";
            }
            if (scope == RuleStateScope.Unit) return TryPrefixedId(scopeId, "unit:", out var unitId) ? "unit:" + unitId : scopeId ?? "";
            if (scope == RuleStateScope.Building) return TryPrefixedId(scopeId, "building:", out var buildingId) ? "building:" + buildingId : scopeId ?? "";
            if (scope == RuleStateScope.Tile && TryHex(scopeId, out var tile)) return "tile:" + tile.q + "," + tile.r;
            return scopeId ?? "";
        }

        public static bool TryCountUnits(GameSnapshotV1 game, string selector, out int count)
        {
            count = 0;
            if (game == null) return false;
            var visiblePositions = VisiblePositions(game);
            IEnumerable<UnitState> units = (game.entities ?? new List<UnitState>()).Where(unit => unit != null && unit.alive && (unit.factionId == 1 || visiblePositions.Contains(unit.position)));
            if (string.IsNullOrEmpty(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase)) { count = units.Count(); return true; }
            if (string.Equals(selector, "player", StringComparison.OrdinalIgnoreCase)) units = units.Where(unit => unit.factionId == 1);
            else if (TryPrefixedId(selector, "faction:", out var factionId) && (game.factions ?? new List<FactionState>()).Any(faction => faction != null && faction.id == factionId)) units = units.Where(unit => unit.factionId == factionId);
            else if (TryPrefixedId(selector, "unit:", out var unitId)) units = units.Where(unit => unit.id == unitId);
            else return false;
            count = units.Count();
            return true;
        }

        public static bool TryCountBuildings(GameSnapshotV1 game, string selector, out int count)
        {
            count = 0;
            if (game == null) return false;
            var visiblePositions = VisiblePositions(game);
            IEnumerable<BuildingState> buildings = (game.buildings ?? new List<BuildingState>()).Where(building => building != null && building.hp > 0 && (building.factionId == 1 || visiblePositions.Contains(building.position)));
            if (string.IsNullOrEmpty(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase)) { count = buildings.Count(); return true; }
            if (string.Equals(selector, "player", StringComparison.OrdinalIgnoreCase)) buildings = buildings.Where(building => building.factionId == 1);
            else if (TryPrefixedId(selector, "faction:", out var factionId) && (game.factions ?? new List<FactionState>()).Any(faction => faction != null && faction.id == factionId)) buildings = buildings.Where(building => building.factionId == factionId);
            else if (TryPrefixedId(selector, "building:", out var buildingId)) buildings = buildings.Where(building => building.id == buildingId);
            else if (selector.StartsWith("type:", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<BuildingType>(selector.Substring(5), true, out var type) && Enum.IsDefined(typeof(BuildingType), type)) buildings = buildings.Where(building => building.type == type);
            else return false;
            count = buildings.Count();
            return true;
        }

        public static bool TryCountTiles(GameSnapshotV1 game, string selector, out int count)
        {
            count = 0;
            if (game == null) return false;
            IEnumerable<TileState> tiles = (game.map ?? new List<TileState>()).Where(tile => tile != null);
            if (string.IsNullOrEmpty(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase)) { count = tiles.Count(); return true; }
            if (string.Equals(selector, "player_owned", StringComparison.OrdinalIgnoreCase)) tiles = tiles.Where(tile => tile.owner == 1);
            else if (string.Equals(selector, "neutral", StringComparison.OrdinalIgnoreCase)) tiles = tiles.Where(tile => tile.visible && tile.owner == 0);
            else if (string.Equals(selector, "visible", StringComparison.OrdinalIgnoreCase)) tiles = tiles.Where(tile => tile.visible);
            else if (string.Equals(selector, "explored", StringComparison.OrdinalIgnoreCase)) tiles = tiles.Where(tile => tile.explored);
            else if (TryPrefixedNonNegativeId(selector, "owner:", out var owner) && (owner == 0 || (game.factions ?? new List<FactionState>()).Any(faction => faction != null && faction.id == owner))) tiles = tiles.Where(tile => (owner == 1 || tile.visible) && tile.owner == owner);
            else if (selector.StartsWith("terrain:", StringComparison.OrdinalIgnoreCase) && selector.Length > 8 && selector.Length <= 8 + RuleLimits.MaxIdentifierLength) tiles = tiles.Where(tile => tile.explored && string.Equals(tile.terrain, selector.Substring(8), StringComparison.Ordinal));
            else if (TryHex(selector, out var coord) && (game.map ?? new List<TileState>()).Any(tile => tile != null && tile.position.Equals(coord))) tiles = tiles.Where(tile => tile.explored && tile.position.Equals(coord));
            else return false;
            count = tiles.Count();
            return true;
        }

        public static bool TryResolvePosition(GameSnapshotV1 game, string selector, out HexCoord position)
        {
            position = default;
            if (game == null || string.IsNullOrWhiteSpace(selector) || selector.Length > RuleLimits.MaxIdentifierLength) return false;
            if (string.Equals(selector, "player_unit", StringComparison.OrdinalIgnoreCase))
            {
                UnitState unit = null;
                foreach (var candidate in game.entities ?? new List<UnitState>())
                    if (candidate != null && candidate.factionId == 1 && candidate.alive && (unit == null || candidate.id < unit.id)) unit = candidate;
                if (unit == null) return false;
                position = unit.position;
                return true;
            }
            if (string.Equals(selector, "player_hq", StringComparison.OrdinalIgnoreCase))
            {
                BuildingState building = null;
                foreach (var candidate in game.buildings ?? new List<BuildingState>())
                    if (candidate != null && candidate.factionId == 1 && candidate.type == BuildingType.Headquarters && candidate.hp > 0 && (building == null || candidate.id < building.id)) building = candidate;
                if (building == null) return false;
                position = building.position;
                return true;
            }
            if (TryPrefixedId(selector, "unit:", out var unitId))
            {
                var unit = (game.entities ?? new List<UnitState>()).FirstOrDefault(candidate => candidate != null && candidate.id == unitId);
                if (unit == null || !unit.alive || !IsObservableUnit(game, unit)) return false;
                position = unit.position;
                return true;
            }
            if (TryPrefixedId(selector, "building:", out var buildingId))
            {
                var building = (game.buildings ?? new List<BuildingState>()).FirstOrDefault(candidate => candidate != null && candidate.id == buildingId);
                if (building == null || building.hp <= 0 || !IsObservableBuilding(game, building)) return false;
                position = building.position;
                return true;
            }
            if (TryHex(selector, out var coord) && (game.map ?? new List<TileState>()).Any(tile => tile != null && tile.explored && tile.position.Equals(coord)))
            {
                position = coord;
                return true;
            }
            return false;
        }

        public static bool IsScopeSelectorValid(RuleStateScope scope, string scopeId, GameSnapshotV1 game)
        {
            if (game == null) return false;
            if (scope == RuleStateScope.Run || scope == RuleStateScope.Turn) return string.IsNullOrEmpty(scopeId);
            if (scope == RuleStateScope.Faction)
            {
                if (string.Equals(scopeId, "player", StringComparison.OrdinalIgnoreCase)) return (game.factions ?? new List<FactionState>()).Any(faction => faction != null && faction.id == 1);
                return TryPrefixedId(scopeId, "faction:", out var factionId) && (game.factions ?? new List<FactionState>()).Any(faction => faction != null && faction.id == factionId);
            }
            if (scope == RuleStateScope.Unit) return TryPrefixedId(scopeId, "unit:", out var unitId) && (game.entities ?? new List<UnitState>()).Any(unit => unit != null && unit.id == unitId);
            if (scope == RuleStateScope.Building) return TryPrefixedId(scopeId, "building:", out var buildingId) && (game.buildings ?? new List<BuildingState>()).Any(building => building != null && building.id == buildingId);
            return scope == RuleStateScope.Tile && TryHex(scopeId, out var coord) && (game.map ?? new List<TileState>()).Any(tile => tile != null && tile.position.Equals(coord));
        }

        public static bool IsScopeSelectorValid(RuleStateScope scope, string scopeId, RuleWorldReferenceIndex index)
        {
            if (index == null) return false;
            if (scope == RuleStateScope.Run || scope == RuleStateScope.Turn) return string.IsNullOrEmpty(scopeId);
            if (scope == RuleStateScope.Faction)
            {
                if (string.Equals(scopeId, "player", StringComparison.OrdinalIgnoreCase)) return index.factionIds.Contains(1);
                return TryPrefixedId(scopeId, "faction:", out var factionId) && index.factionIds.Contains(factionId);
            }
            if (scope == RuleStateScope.Unit) return TryPrefixedId(scopeId, "unit:", out var unitId) && index.unitIds.Contains(unitId);
            if (scope == RuleStateScope.Building) return TryPrefixedId(scopeId, "building:", out var buildingId) && index.buildingIds.Contains(buildingId);
            return scope == RuleStateScope.Tile && TryHex(scopeId, out var coord) && index.tilePositions.Contains(coord);
        }

        public static bool IsStateReferenceSafe(StateReferenceV1 reference, RuleWorldReferenceIndex index)
        {
            return reference != null && Enum.IsDefined(typeof(RuleStateScope), reference.scope) && IsIdentifier(reference.key) &&
                   IsScopeSelectorValid(reference.scope, reference.scopeId, index);
        }

        public static bool IsDefinitionTargetObservable(StateDefinitionV1 definition, GameSnapshotV1 game)
        {
            if (definition == null || game == null) return false;
            if (definition.scope == RuleStateScope.Run || definition.scope == RuleStateScope.Turn || definition.scope == RuleStateScope.Faction) return true;
            if (definition.scope == RuleStateScope.Unit && TryPrefixedId(definition.scopeId, "unit:", out var unitId))
            {
                var unit = (game.entities ?? new List<UnitState>()).FirstOrDefault(candidate => candidate != null && candidate.id == unitId);
                return unit != null && IsObservableUnit(game, unit);
            }
            if (definition.scope == RuleStateScope.Building && TryPrefixedId(definition.scopeId, "building:", out var buildingId))
            {
                var building = (game.buildings ?? new List<BuildingState>()).FirstOrDefault(candidate => candidate != null && candidate.id == buildingId);
                return building != null && IsObservableBuilding(game, building);
            }
            return definition.scope == RuleStateScope.Tile && TryHex(definition.scopeId, out var coord) &&
                   (game.map ?? new List<TileState>()).Any(tile => tile != null && tile.explored && tile.position.Equals(coord));
        }

        private static bool IsIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= RuleLimits.MaxIdentifierLength && value.All(character => !char.IsControl(character));
        private static bool IsKoreanName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= RuleLimits.MaxNameLength && value.Any(character => character >= '\uac00' && character <= '\ud7a3');
        private static bool IsIconToken(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= RuleLimits.MaxIdentifierLength && value.All(character => char.IsLetterOrDigit(character) || character == '-' || character == '_');
        private static bool IsColor(string value) => value != null && value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

        private static bool IsObservableUnit(GameSnapshotV1 game, UnitState unit) => unit != null && (unit.factionId == 1 || IsVisible(game, unit.position));
        private static bool IsObservableBuilding(GameSnapshotV1 game, BuildingState building) => building != null && (building.factionId == 1 || IsVisible(game, building.position));
        private static bool IsVisible(GameSnapshotV1 game, HexCoord position) => (game.map ?? new List<TileState>()).Any(tile => tile != null && tile.visible && tile.position.Equals(position));
        private static HashSet<HexCoord> VisiblePositions(GameSnapshotV1 game) => new HashSet<HexCoord>((game.map ?? new List<TileState>()).Where(tile => tile != null && tile.visible).Select(tile => tile.position));

        private static bool TryPrefixedId(string value, string prefix, out int id)
        {
            id = 0;
            return value != null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(value.Substring(prefix.Length), out id) && id > 0;
        }

        private static bool TryPrefixedNonNegativeId(string value, string prefix, out int id)
        {
            id = 0;
            return value != null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(value.Substring(prefix.Length), out id) && id >= 0;
        }

        private static bool TryHex(string value, out HexCoord coord)
        {
            coord = default;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var raw = value.StartsWith("tile:", StringComparison.OrdinalIgnoreCase) ? value.Substring(5) : value;
            var parts = raw.Split(',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var q) || !int.TryParse(parts[1], out var r)) return false;
            coord = new HexCoord(q, r);
            return true;
        }

        private static bool CoordinateIsBounded(HexCoord coord) =>
            Math.Abs((long)coord.q) <= RuleLimits.MaxStateMagnitude && Math.Abs((long)coord.r) <= RuleLimits.MaxStateMagnitude;
    }

    public static class RuleExpressionValidator
    {
        private sealed class ValidationContext
        {
            public readonly GameSnapshotV1 game;
            public readonly IList<string> errors;
            public readonly string source;
            public readonly DynamicTargetSelectorV1 dynamicTargetSelector;
            public readonly bool requireCurrentWorldReferences;
            public readonly Dictionary<string, RuleStateValueType> knownStates;
            public readonly HashSet<object> path = new HashSet<object>();
            public int nodes;
            private readonly RuleValidationWorkBudget currentWorldBudget;

            public ValidationContext(
                GameSnapshotV1 game,
                IList<string> errors,
                string source,
                IEnumerable<StateDefinitionV1> localDefinitions,
                DynamicTargetSelectorV1 dynamicTargetSelector = null,
                bool requireCurrentWorldReferences = true,
                RuleValidationWorkBudget currentWorldBudget = null)
            {
                this.game = game;
                this.errors = errors;
                this.source = source;
                this.dynamicTargetSelector = dynamicTargetSelector;
                this.requireCurrentWorldReferences = requireCurrentWorldReferences;
                this.currentWorldBudget = requireCurrentWorldReferences
                    ? currentWorldBudget ?? new RuleValidationWorkBudget(RuleLimits.MaxConditionWorkPerEvaluation)
                    : null;
                knownStates = BuildKnownStates(game, localDefinitions);
            }

            public bool Enter(object node, int depth)
            {
                if (node == null) { Add("EXPR_NULL"); return false; }
                if (!path.Add(node)) { Add("EXPR_CYCLE"); return false; }
                nodes++;
                if (nodes > RuleLimits.MaxConditionNodes) Add("AST_NODE_LIMIT");
                if (depth > RuleLimits.MaxConditionDepth) Add("AST_DEPTH_LIMIT");
                return nodes <= RuleLimits.MaxConditionNodes && depth <= RuleLimits.MaxConditionDepth;
            }

            public void Exit(object node) { if (node != null) path.Remove(node); }
            public void Add(string code) { errors.Add(code + ":" + source); }

            public bool TryReserveNumberSelector(NumberExpressionOp op)
            {
                if (!requireCurrentWorldReferences) return true;
                long work;
                if (op == NumberExpressionOp.CountUnits)
                    work = CollectionWork(game.map, RuleLimits.MaxMapTiles) + CollectionWork(game.entities, RuleLimits.MaxEntities) + CollectionWork(game.factions, RuleLimits.MaxFactions);
                else if (op == NumberExpressionOp.CountBuildings)
                    work = CollectionWork(game.map, RuleLimits.MaxMapTiles) + CollectionWork(game.buildings, RuleLimits.MaxBuildings) + CollectionWork(game.factions, RuleLimits.MaxFactions);
                else if (op == NumberExpressionOp.CountTiles)
                    work = 2L * CollectionWork(game.map, RuleLimits.MaxMapTiles) + CollectionWork(game.factions, RuleLimits.MaxFactions);
                else if (op == NumberExpressionOp.Distance)
                    work = CollectionWork(game.map, RuleLimits.MaxMapTiles) + CollectionWork(game.entities, RuleLimits.MaxEntities) + CollectionWork(game.buildings, RuleLimits.MaxBuildings);
                else work = 1;
                return TryReserveCurrentWorldWork(work);
            }

            public bool TryReserveStateReference(StateReferenceV1 reference)
            {
                if (!requireCurrentWorldReferences) return true;
                if (reference == null) return TryReserveCurrentWorldWork(1);
                if (reference.scope == RuleStateScope.Faction) return TryReserveCurrentWorldWork(CollectionWork(game.factions, RuleLimits.MaxFactions));
                if (reference.scope == RuleStateScope.Unit) return TryReserveCurrentWorldWork(CollectionWork(game.entities, RuleLimits.MaxEntities));
                if (reference.scope == RuleStateScope.Building) return TryReserveCurrentWorldWork(CollectionWork(game.buildings, RuleLimits.MaxBuildings));
                if (reference.scope == RuleStateScope.Tile) return TryReserveCurrentWorldWork(CollectionWork(game.map, RuleLimits.MaxMapTiles));
                return TryReserveCurrentWorldWork(1);
            }

            public bool TryReserveDefinitionTarget(StateDefinitionV1 definition, bool requireObservable)
            {
                if (!requireCurrentWorldReferences) return true;
                if (definition == null) return TryReserveCurrentWorldWork(1);
                long work;
                if (definition.scope == RuleStateScope.Faction)
                    work = CollectionWork(game.factions, RuleLimits.MaxFactions);
                else if (definition.scope == RuleStateScope.Unit)
                    work = CollectionWork(game.entities, RuleLimits.MaxEntities) * (requireObservable ? 2L : 1L) +
                           (requireObservable ? CollectionWork(game.map, RuleLimits.MaxMapTiles) : 0L);
                else if (definition.scope == RuleStateScope.Building)
                    work = CollectionWork(game.buildings, RuleLimits.MaxBuildings) * (requireObservable ? 2L : 1L) +
                           (requireObservable ? CollectionWork(game.map, RuleLimits.MaxMapTiles) : 0L);
                else if (definition.scope == RuleStateScope.Tile)
                    work = CollectionWork(game.map, RuleLimits.MaxMapTiles) * (requireObservable ? 2L : 1L);
                else return true;
                return TryReserveCurrentWorldWork(work);
            }

            private bool TryReserveCurrentWorldWork(long amount)
            {
                if (currentWorldBudget == null || !currentWorldBudget.TrySpend(amount))
                {
                    Add("EXPR_WORK_LIMIT");
                    return false;
                }
                return true;
            }

            private static long CollectionWork<T>(ICollection<T> collection, int maximum)
            {
                if (collection == null) return 1;
                return collection.Count > maximum ? (long)RuleLimits.MaxConditionWorkPerEvaluation + 1L : Math.Max(1, collection.Count);
            }
        }

        public static void ValidateSnapshot(GameSnapshotV1 game, IList<string> errors)
        {
            if (game == null || errors == null) return;
            var typed = game.typedRuleState ?? new List<TypedRuleStateEntryV1>();
            if ((game.ruleState?.Count ?? 0) + typed.Count > RuleLimits.MaxStateVariables) errors.Add("STATE_VARIABLE_LIMIT");
            var hasWorldIndex = RuleExpressionSelectors.TryBuildWorldReferenceIndex(game, out var worldIndex);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in typed.Take(RuleLimits.MaxStateVariables + 1))
            {
                if (entry == null || !Enum.IsDefined(typeof(RuleStateScope), entry.scope) || !Enum.IsDefined(typeof(RuleStateValueType), entry.valueType) ||
                    !hasWorldIndex || !RuleExpressionSelectors.IsStateReferenceSafe(ToReference(entry), worldIndex) || !MetadataSafe(entry.koreanName, entry.iconToken, entry.colorHex))
                {
                    errors.Add("TYPED_STATE_INVALID");
                    continue;
                }
                var normalizedScopeId = RuleExpressionSelectors.NormalizeScopeId(entry.scope, entry.scopeId, game);
                if (!string.Equals(entry.scopeId ?? "", normalizedScopeId, StringComparison.Ordinal)) errors.Add("TYPED_STATE_SCOPE_NOT_CANONICAL:" + entry.key);
                var identity = Identity(entry.scope, normalizedScopeId, entry.key);
                if (!identities.Add(identity)) errors.Add("TYPED_STATE_DUPLICATE:" + entry.key);
                if (entry.valueType == RuleStateValueType.Number && (entry.numberValue < -RuleLimits.MaxStateMagnitude || entry.numberValue > RuleLimits.MaxStateMagnitude)) errors.Add("TYPED_STATE_NUMBER_LIMIT:" + entry.key);
                if (entry.valueType == RuleStateValueType.Set && !RuleExpressionSelectors.IsSetSafe(entry.setValue)) errors.Add("STATE_SET_LIMIT:" + entry.key);
                if (entry.scope == RuleStateScope.Turn && (entry.stateTurn < 0 || entry.stateTurn > game.turn)) errors.Add("TURN_STATE_INVALID:" + entry.key);
            }

            var history = game.recentActionStats ?? new List<ActionTurnStatV1>();
            if (history.Count > RuleLimits.MaxRecentActionEntries) errors.Add("RECENT_ACTION_LIMIT");
            var historyKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stat in history.Take(RuleLimits.MaxRecentActionEntries + 1))
            {
                // Older bounded entries are accepted for save/backward compatibility;
                // ratios still read only the most recent configured window and the
                // client deterministically prunes at every turn boundary.
                if (stat == null || !Enum.IsDefined(typeof(CommandType), stat.type) || stat.turn < 0 || stat.turn > game.turn || stat.count < 1 || stat.count > RuleLimits.MaxStateMagnitude || !historyKeys.Add(stat.turn + ":" + (int)stat.type)) errors.Add("RECENT_ACTION_INVALID");
            }
            ValidateStoredDefinitionRegistry(game, errors);
        }

        public static void ValidateRule(
            RuleNodeV1 rule,
            GameSnapshotV1 game,
            string source,
            IList<string> errors,
            bool requireCurrentWorldReferences = true)
        {
            ValidateRule(
                rule,
                game,
                source,
                errors,
                requireCurrentWorldReferences,
                requireCurrentWorldReferences ? new RuleValidationWorkBudget(RuleLimits.MaxConditionWorkPerEvaluation) : null);
        }

        internal static void ValidateRule(
            RuleNodeV1 rule,
            GameSnapshotV1 game,
            string source,
            IList<string> errors,
            bool requireCurrentWorldReferences,
            RuleValidationWorkBudget currentWorldBudget)
        {
            if (rule == null || game == null || errors == null) return;
            var context = new ValidationContext(game, errors, source, rule.stateDefinitions, null, requireCurrentWorldReferences, currentWorldBudget);
            ValidateDefinitions(rule.stateDefinitions, context);
            WalkCondition(rule.condition, 1, context);
            foreach (var effect in (rule.effects ?? new List<EffectNode>()).Take(RuleLimits.MaxEffectsPerRule + 1))
            {
                if (!context.Enter(effect, 1)) { context.Exit(effect); continue; }
                if (effect?.type == EffectType.TypedState) ValidateMutation(effect.stateMutation, 2, context);
                context.Exit(effect);
            }
        }

        public static void ValidateAction(DynamicActionV1 action, GameSnapshotV1 game, string source, IList<string> errors, bool requireCurrentWorldReferences = true)
        {
            ValidateAction(
                action,
                game,
                source,
                errors,
                requireCurrentWorldReferences,
                requireCurrentWorldReferences ? new RuleValidationWorkBudget(RuleLimits.MaxConditionWorkPerEvaluation) : null);
        }

        internal static void ValidateAction(
            DynamicActionV1 action,
            GameSnapshotV1 game,
            string source,
            IList<string> errors,
            bool requireCurrentWorldReferences,
            RuleValidationWorkBudget currentWorldBudget)
        {
            if (action == null || game == null || errors == null) return;
            var context = new ValidationContext(game, errors, source, null, action.targetSelector, requireCurrentWorldReferences, currentWorldBudget);
            WalkCondition(action.condition, 1, context);
            foreach (var effect in (action.effects ?? new List<EffectNode>()).Take(RuleLimits.MaxEffectsPerRule + 1))
            {
                if (!context.Enter(effect, 1)) { context.Exit(effect); continue; }
                if (effect?.type == EffectType.TypedState) ValidateMutation(effect.stateMutation, 2, context);
                context.Exit(effect);
            }
        }

        public static void ValidateProjectedDefinitions(IEnumerable<RuleNodeV1> incomingRules, GameSnapshotV1 game, IList<string> errors)
        {
            ValidateProjectedDefinitions(incomingRules, Enumerable.Empty<DynamicActionV1>(), game, errors);
        }

        public static void ValidateProjectedDefinitions(IEnumerable<RuleNodeV1> incomingRules, IEnumerable<DynamicActionV1> incomingActions, GameSnapshotV1 game, IList<string> errors)
        {
            if (game == null || errors == null) return;
            var incoming = (incomingRules ?? Enumerable.Empty<RuleNodeV1>()).Where(rule => rule != null).ToList();
            var actions = (incomingActions ?? Enumerable.Empty<DynamicActionV1>()).Where(action => action != null).ToList();
            var ruleReplacements = new HashSet<string>(incoming.Where(rule => !string.IsNullOrEmpty(rule.id)).Select(rule => rule.id), StringComparer.Ordinal);
            var actionReplacements = new HashSet<string>(actions.Where(action => !string.IsNullOrEmpty(action.id)).Select(action => action.id), StringComparer.Ordinal);
            var relevantExisting = RelevantRules(game).ToList();
            var existingActions = (game.dynamicActions ?? new List<DynamicActionV1>()).Where(action => action != null).ToList();
            var persisted = (game.typedRuleState ?? new List<TypedRuleStateEntryV1>()).Where(entry => entry != null)
                .GroupBy(entry => Identity(entry.scope, RuleExpressionSelectors.NormalizeScopeId(entry.scope, entry.scopeId, game), entry.key), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var baselineTyped = new HashSet<string>(persisted.Keys, StringComparer.Ordinal);
            foreach (var rule in relevantExisting) AddDefinitionIdentities(rule.stateDefinitions, game, baselineTyped);
            var baselineLegacy = new HashSet<string>((game.ruleState ?? new List<RuleStateEntry>()).Where(entry => entry != null).Select(entry => entry.key), StringComparer.Ordinal);
            foreach (var rule in relevantExisting) AddStatusKeys(rule.effects, baselineLegacy);
            foreach (var action in existingActions) AddStatusKeys(action.effects, baselineLegacy);

            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
            var projectedTyped = new HashSet<string>(persisted.Keys, StringComparer.Ordinal);
            var projectedLegacy = new HashSet<string>((game.ruleState ?? new List<RuleStateEntry>()).Where(entry => entry != null).Select(entry => entry.key), StringComparer.Ordinal);
            foreach (var rule in relevantExisting.Where(rule => string.IsNullOrEmpty(rule.id) || !ruleReplacements.Contains(rule.id)))
            {
                AddOwnedDefinitions(rule, game, persisted, owners, signatures, projectedTyped, errors, "SNAPSHOT");
                AddStatusKeys(rule.effects, projectedLegacy);
            }
            foreach (var action in existingActions.Where(action => string.IsNullOrEmpty(action.id) || !actionReplacements.Contains(action.id)))
                AddStatusKeys(action.effects, projectedLegacy);

            var newIdentities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rule in incoming)
            {
                AddOwnedDefinitions(rule, game, persisted, owners, signatures, projectedTyped, errors, "RULESET");
                foreach (var definition in (rule.stateDefinitions ?? new List<StateDefinitionV1>()).Take(RuleLimits.MaxStateDefinitionsPerRule + 1))
                {
                    if (definition == null) continue;
                    var identity = Identity(definition.scope, RuleExpressionSelectors.NormalizeScopeId(definition.scope, definition.scopeId, game), definition.key);
                    if (!baselineTyped.Contains(identity)) newIdentities.Add("typed|" + identity);
                }
                foreach (var key in StatusKeys(rule.effects))
                {
                    projectedLegacy.Add(key);
                    if (!baselineLegacy.Contains(key)) newIdentities.Add("legacy|" + key);
                }
            }
            foreach (var action in actions)
            {
                foreach (var key in StatusKeys(action.effects))
                {
                    projectedLegacy.Add(key);
                    if (!baselineLegacy.Contains(key)) newIdentities.Add("legacy|" + key);
                }
            }

            if (projectedTyped.Count + projectedLegacy.Count > RuleLimits.MaxStateVariables) errors.Add("STATE_VARIABLE_LIMIT");
            if (newIdentities.Count > RuleLimits.MaxNewStateIdentitiesPerRuleSet) errors.Add("NEW_STATE_IDENTITY_LIMIT");
        }

        private static void ValidateDefinitions(IEnumerable<StateDefinitionV1> definitions, ValidationContext context)
        {
            var bounded = (definitions ?? Enumerable.Empty<StateDefinitionV1>()).Take(RuleLimits.MaxStateDefinitionsPerRule + 1).ToList();
            if (bounded.Count > RuleLimits.MaxStateDefinitionsPerRule) context.errors.Add("STATE_DEFINITION_LIMIT:" + context.source);
            if ((definitions ?? Enumerable.Empty<StateDefinitionV1>()).Take(RuleLimits.MaxStateVariables + 1).Count() > RuleLimits.MaxStateVariables) context.errors.Add("STATE_VARIABLE_LIMIT:" + context.source);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var requireObservable = context.source.StartsWith("RULE:", StringComparison.Ordinal);
            foreach (var definition in bounded)
            {
                if (definition?.valueType == RuleStateValueType.Set && !RuleExpressionSelectors.IsSetSafe(definition.initialSet)) context.errors.Add("STATE_SET_LIMIT:" + context.source);
                if (!RuleExpressionSelectors.IsDefinitionShapeSafe(definition)) { context.errors.Add("STATE_DEFINITION_INVALID:" + context.source); continue; }
                if (context.requireCurrentWorldReferences)
                {
                    if (!context.TryReserveDefinitionTarget(definition, requireObservable))
                    {
                        context.errors.Add("STATE_DEFINITION_INVALID:" + context.source);
                        continue;
                    }
                    if (!RuleExpressionSelectors.IsScopeSelectorValid(definition.scope, definition.scopeId, context.game))
                    {
                        context.errors.Add("STATE_DEFINITION_INVALID:" + context.source);
                        continue;
                    }
                    if (requireObservable && !RuleExpressionSelectors.IsDefinitionTargetObservable(definition, context.game))
                    {
                        context.errors.Add("STATE_DEFINITION_INVALID:" + context.source);
                        context.errors.Add("STATE_DEFINITION_HIDDEN_TARGET:" + context.source);
                    }
                }
                var identity = Identity(definition.scope, RuleExpressionSelectors.NormalizeScopeId(definition.scope, definition.scopeId, context.game), definition.key);
                if (!seen.Add(identity)) context.errors.Add("STATE_DEFINITION_DUPLICATE:" + context.source);
            }
        }

        private static void WalkCondition(ConditionNode node, int depth, ValidationContext context)
        {
            if (!context.Enter(node, depth)) { context.Exit(node); return; }
            if (node.predicate != null) WalkPredicate(node.predicate, depth + 1, context);
            foreach (var child in (node.all ?? new List<ConditionNode>()).Take(RuleLimits.MaxConditionNodes + 1)) WalkCondition(child, depth + 1, context);
            context.Exit(node);
        }

        private static void WalkPredicate(PredicateExpressionV1 predicate, int depth, ValidationContext context)
        {
            if (!context.Enter(predicate, depth)) { context.Exit(predicate); return; }
            if (!Enum.IsDefined(typeof(PredicateExpressionOp), predicate.op)) context.Add("PREDICATE_OP_INVALID");
            else if (predicate.op == PredicateExpressionOp.All || predicate.op == PredicateExpressionOp.Any)
            {
                if ((predicate.children?.Count ?? 0) < 1) context.Add("PREDICATE_CHILDREN_INVALID");
                if ((predicate.children?.Count ?? 0) > RuleLimits.MaxConditionNodes) context.Add("AST_NODE_LIMIT");
                foreach (var child in (predicate.children ?? new List<PredicateExpressionV1>()).Take(RuleLimits.MaxConditionNodes + 1)) WalkPredicate(child, depth + 1, context);
            }
            else if (predicate.op == PredicateExpressionOp.Not)
            {
                if (predicate.child == null) context.Add("PREDICATE_CHILDREN_INVALID");
                else WalkPredicate(predicate.child, depth + 1, context);
            }
            else if (predicate.op == PredicateExpressionOp.BoolState) ValidateStateReference(predicate.state, RuleStateValueType.Boolean, context);
            else if (predicate.op == PredicateExpressionOp.SetContains)
            {
                ValidateStateReference(predicate.state, RuleStateValueType.Set, context);
                if (!RuleExpressionSelectors.IsSetElementSafe(predicate.element)) context.Add("STATE_SET_ELEMENT_INVALID");
            }
            else
            {
                WalkNumber(predicate.left, depth + 1, context);
                WalkNumber(predicate.right, depth + 1, context);
            }
            context.Exit(predicate);
        }

        private static void WalkNumber(NumberExpressionV1 expression, int depth, ValidationContext context)
        {
            if (!context.Enter(expression, depth)) { context.Exit(expression); return; }
            if (!Enum.IsDefined(typeof(NumberExpressionOp), expression.op)) context.Add("NUMBER_OP_INVALID");
            else if (expression.op == NumberExpressionOp.Constant)
            {
                if (expression.constant < -RuleLimits.MaxStateMagnitude || expression.constant > RuleLimits.MaxStateMagnitude) context.Add("EXPR_ARITHMETIC_INVALID");
            }
            else if (expression.op == NumberExpressionOp.State) ValidateStateReference(expression.state, RuleStateValueType.Number, context);
            else if (expression.op == NumberExpressionOp.Add || expression.op == NumberExpressionOp.Subtract || expression.op == NumberExpressionOp.Multiply || expression.op == NumberExpressionOp.Divide)
            {
                WalkNumber(expression.left, depth + 1, context);
                WalkNumber(expression.right, depth + 1, context);
                TryConstant(expression, new HashSet<NumberExpressionV1>(), out _, out var invalid);
                if (invalid) context.Add("EXPR_ARITHMETIC_INVALID");
            }
            else if (expression.op == NumberExpressionOp.CountUnits)
            {
                if (!DynamicActionTargeting.TryValidateNumberSelectorBinding(expression.op, expression.selector, context.dynamicTargetSelector, out var binding) ||
                    !binding && !(context.requireCurrentWorldReferences
                        ? context.TryReserveNumberSelector(expression.op) && RuleExpressionSelectors.TryCountUnits(context.game, expression.selector, out _)
                        : RuleExpressionSelectors.IsNumberSelectorShapeSafe(expression.op, expression.selector))) context.Add("EXPR_SELECTOR_INVALID");
            }
            else if (expression.op == NumberExpressionOp.CountBuildings)
            {
                if (!DynamicActionTargeting.TryValidateNumberSelectorBinding(expression.op, expression.selector, context.dynamicTargetSelector, out var binding) ||
                    !binding && !(context.requireCurrentWorldReferences
                        ? context.TryReserveNumberSelector(expression.op) && RuleExpressionSelectors.TryCountBuildings(context.game, expression.selector, out _)
                        : RuleExpressionSelectors.IsNumberSelectorShapeSafe(expression.op, expression.selector))) context.Add("EXPR_SELECTOR_INVALID");
            }
            else if (expression.op == NumberExpressionOp.CountTiles)
            {
                if (!DynamicActionTargeting.TryValidateNumberSelectorBinding(expression.op, expression.selector, context.dynamicTargetSelector, out var binding) ||
                    !binding && !(context.requireCurrentWorldReferences
                        ? context.TryReserveNumberSelector(expression.op) && RuleExpressionSelectors.TryCountTiles(context.game, expression.selector, out _)
                        : RuleExpressionSelectors.IsNumberSelectorShapeSafe(expression.op, expression.selector))) context.Add("EXPR_SELECTOR_INVALID");
            }
            else if (expression.op == NumberExpressionOp.Distance)
            {
                var firstValid = DynamicActionTargeting.TryValidateNumberSelectorBinding(expression.op, expression.selector, context.dynamicTargetSelector, out var firstBinding) &&
                                 (firstBinding || (context.requireCurrentWorldReferences
                                     ? context.TryReserveNumberSelector(expression.op) && RuleExpressionSelectors.TryResolvePosition(context.game, expression.selector, out _)
                                     : RuleExpressionSelectors.IsNumberSelectorShapeSafe(expression.op, expression.selector)));
                var secondValid = DynamicActionTargeting.TryValidateNumberSelectorBinding(expression.op, expression.secondSelector, context.dynamicTargetSelector, out var secondBinding) &&
                                  (secondBinding || (context.requireCurrentWorldReferences
                                      ? context.TryReserveNumberSelector(expression.op) && RuleExpressionSelectors.TryResolvePosition(context.game, expression.secondSelector, out _)
                                      : RuleExpressionSelectors.IsNumberSelectorShapeSafe(expression.op, expression.secondSelector)));
                if (!firstValid || !secondValid) context.Add("EXPR_SELECTOR_INVALID");
            }
            else if (expression.op == NumberExpressionOp.RecentActionRatio && (!Enum.IsDefined(typeof(CommandType), expression.action) || expression.recentTurns < 1 || expression.recentTurns > RuleLimits.MaxRecentActionTurns)) context.Add("RECENT_RATIO_INVALID");
            context.Exit(expression);
        }

        private static void ValidateMutation(StateMutationV1 mutation, int depth, ValidationContext context)
        {
            if (!context.Enter(mutation, depth)) { context.Exit(mutation); return; }
            if (!Enum.IsDefined(typeof(StateMutationOp), mutation.op)) context.Add("STATE_MUTATION_OP_INVALID");
            else if (!TryKnownStateType(mutation.state, context, out var type)) { }
            else if (type == RuleStateValueType.Number)
            {
                if (mutation.op != StateMutationOp.Set && mutation.op != StateMutationOp.Add) context.Add("STATE_MUTATION_TYPE_INVALID");
                WalkNumber(mutation.numberValue, depth + 1, context);
            }
            else if (type == RuleStateValueType.Boolean)
            {
                if (mutation.op != StateMutationOp.Set && mutation.op != StateMutationOp.Toggle) context.Add("STATE_MUTATION_TYPE_INVALID");
            }
            else if (mutation.op == StateMutationOp.Set)
            {
                if (!RuleExpressionSelectors.IsSetSafe(mutation.setValues)) context.Add("STATE_SET_LIMIT");
            }
            else if (mutation.op == StateMutationOp.SetAdd || mutation.op == StateMutationOp.SetRemove)
            {
                if (!RuleExpressionSelectors.IsSetElementSafe(mutation.element)) context.Add("STATE_SET_ELEMENT_INVALID");
            }
            else context.Add("STATE_MUTATION_TYPE_INVALID");
            context.Exit(mutation);
        }

        private static void ValidateStateReference(StateReferenceV1 reference, RuleStateValueType expected, ValidationContext context)
        {
            if (TryKnownStateType(reference, context, out var actual) && actual != expected) context.Add("STATE_REFERENCE_TYPE_INVALID");
        }

        private static bool TryKnownStateType(StateReferenceV1 reference, ValidationContext context, out RuleStateValueType type)
        {
            type = default;
            var referenceIsSafe = context.requireCurrentWorldReferences
                ? context.TryReserveStateReference(reference) && RuleExpressionSelectors.IsStateReferenceSafe(reference, context.game)
                : RuleExpressionSelectors.IsStateReferenceShapeSafe(reference);
            if (!referenceIsSafe) { context.Add("STATE_REFERENCE_INVALID"); return false; }
            var identity = Identity(reference.scope, RuleExpressionSelectors.NormalizeScopeId(reference.scope, reference.scopeId, context.game), reference.key);
            if (!context.knownStates.TryGetValue(identity, out type)) { context.Add("STATE_REFERENCE_UNKNOWN"); return false; }
            return true;
        }

        private static Dictionary<string, RuleStateValueType> BuildKnownStates(GameSnapshotV1 game, IEnumerable<StateDefinitionV1> localDefinitions)
        {
            var known = new Dictionary<string, RuleStateValueType>(StringComparer.Ordinal);
            foreach (var entry in game.typedRuleState ?? new List<TypedRuleStateEntryV1>())
                if (entry != null) known[Identity(entry.scope, RuleExpressionSelectors.NormalizeScopeId(entry.scope, entry.scopeId, game), entry.key)] = entry.valueType;
            foreach (var rule in RelevantRules(game)) AddDefinitions(rule?.stateDefinitions, game, known);
            AddDefinitions(localDefinitions, game, known);
            return known;
        }

        private static void AddDefinitions(IEnumerable<StateDefinitionV1> definitions, GameSnapshotV1 game, IDictionary<string, RuleStateValueType> known)
        {
            foreach (var definition in (definitions ?? Enumerable.Empty<StateDefinitionV1>()).Take(RuleLimits.MaxStateDefinitionsPerRule + 1))
                if (definition != null) known[Identity(definition.scope, RuleExpressionSelectors.NormalizeScopeId(definition.scope, definition.scopeId, game), definition.key)] = definition.valueType;
        }

        private static void ValidateStoredDefinitionRegistry(GameSnapshotV1 game, IList<string> errors)
        {
            var persisted = (game.typedRuleState ?? new List<TypedRuleStateEntryV1>()).Where(entry => entry != null)
                .GroupBy(entry => Identity(entry.scope, RuleExpressionSelectors.NormalizeScopeId(entry.scope, entry.scopeId, game), entry.key), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
            var typed = new HashSet<string>(persisted.Keys, StringComparer.Ordinal);
            var legacy = new HashSet<string>((game.ruleState ?? new List<RuleStateEntry>()).Where(entry => entry != null).Select(entry => entry.key), StringComparer.Ordinal);
            foreach (var rule in RelevantRules(game))
            {
                AddOwnedDefinitions(rule, game, persisted, owners, signatures, typed, errors, "SNAPSHOT");
                AddStatusKeys(rule.effects, legacy);
            }
            foreach (var action in (game.dynamicActions ?? new List<DynamicActionV1>()).Where(action => action != null))
                AddStatusKeys(action.effects, legacy);
            if (typed.Count + legacy.Count > RuleLimits.MaxStateVariables) errors.Add("STATE_VARIABLE_LIMIT");
        }

        private static IEnumerable<RuleNodeV1> RelevantRules(GameSnapshotV1 game)
        {
            return (game.activeRules ?? new List<RuleNodeV1>())
                .Where(rule => rule != null && (long)game.turn < (long)rule.appliedTurn + Math.Max(1, rule.durationTurns))
                .OrderBy(rule => rule.id ?? "", StringComparer.Ordinal);
        }

        private static void AddOwnedDefinitions(
            RuleNodeV1 rule,
            GameSnapshotV1 game,
            IReadOnlyDictionary<string, TypedRuleStateEntryV1> persisted,
            IDictionary<string, string> owners,
            IDictionary<string, string> signatures,
            ISet<string> projected,
            IList<string> errors,
            string source)
        {
            if (rule == null) return;
            var owner = string.IsNullOrEmpty(rule.id) ? source + ":<missing>" : rule.id;
            foreach (var definition in (rule.stateDefinitions ?? new List<StateDefinitionV1>()).Take(RuleLimits.MaxStateDefinitionsPerRule + 1))
            {
                if (definition == null || !Enum.IsDefined(typeof(RuleStateScope), definition.scope)) continue;
                var identity = Identity(definition.scope, RuleExpressionSelectors.NormalizeScopeId(definition.scope, definition.scopeId, game), definition.key);
                var signature = DefinitionSignature(definition);
                if (owners.TryGetValue(identity, out var previousOwner))
                {
                    errors.Add((signatures.TryGetValue(identity, out var previousSignature) && !string.Equals(previousSignature, signature, StringComparison.Ordinal)
                        ? "STATE_DEFINITION_SIGNATURE_CONFLICT:"
                        : "STATE_DEFINITION_OWNER_CONFLICT:") + source + ":" + identity + ":" + previousOwner + ":" + owner);
                }
                else
                {
                    owners[identity] = owner;
                    signatures[identity] = signature;
                }
                if (persisted.TryGetValue(identity, out var entry) && !MatchesPersistedContract(entry, definition))
                    errors.Add("STATE_DEFINITION_PERSISTED_CONFLICT:" + source + ":" + identity);
                projected.Add(identity);
            }
        }

        private static void AddDefinitionIdentities(IEnumerable<StateDefinitionV1> definitions, GameSnapshotV1 game, ISet<string> identities)
        {
            foreach (var definition in (definitions ?? Enumerable.Empty<StateDefinitionV1>()).Take(RuleLimits.MaxStateDefinitionsPerRule + 1))
                if (definition != null) identities.Add(Identity(definition.scope, RuleExpressionSelectors.NormalizeScopeId(definition.scope, definition.scopeId, game), definition.key));
        }

        private static IEnumerable<string> StatusKeys(IEnumerable<EffectNode> effects)
        {
            return (effects ?? Enumerable.Empty<EffectNode>()).Take(RuleLimits.MaxEffectsPerRule + 1)
                .Where(effect => effect?.type == EffectType.Status && !string.IsNullOrWhiteSpace(effect.key))
                .Select(effect => effect.key);
        }

        private static void AddStatusKeys(IEnumerable<EffectNode> effects, ISet<string> keys)
        {
            foreach (var key in StatusKeys(effects)) keys.Add(key);
        }

        private static string DefinitionSignature(StateDefinitionV1 definition)
        {
            var initial = definition.valueType == RuleStateValueType.Number ? definition.initialNumber.ToString() :
                definition.valueType == RuleStateValueType.Boolean ? (definition.initialBool ? "1" : "0") :
                string.Join("\u001f", (definition.initialSet ?? new List<string>()).OrderBy(value => value, StringComparer.Ordinal));
            return (int)definition.valueType + "|" + (definition.koreanName ?? "") + "|" + (definition.iconToken ?? "") + "|" +
                   (definition.colorHex ?? "").ToUpperInvariant() + "|" + initial;
        }

        private static bool MatchesPersistedContract(TypedRuleStateEntryV1 entry, StateDefinitionV1 definition)
        {
            return entry != null && entry.valueType == definition.valueType &&
                   string.Equals(entry.koreanName, definition.koreanName, StringComparison.Ordinal) &&
                   string.Equals(entry.iconToken, definition.iconToken, StringComparison.Ordinal) &&
                   string.Equals(entry.colorHex, definition.colorHex, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryConstant(NumberExpressionV1 expression, HashSet<NumberExpressionV1> path, out long value, out bool invalid)
        {
            value = 0;
            invalid = false;
            if (expression == null || !path.Add(expression)) { invalid = true; return false; }
            if (expression.op == NumberExpressionOp.Constant) value = expression.constant;
            else if (expression.op == NumberExpressionOp.Add || expression.op == NumberExpressionOp.Subtract || expression.op == NumberExpressionOp.Multiply || expression.op == NumberExpressionOp.Divide)
            {
                var leftConstant = TryConstant(expression.left, path, out var left, out var leftInvalid);
                var rightConstant = TryConstant(expression.right, path, out var right, out var rightInvalid);
                if (expression.op == NumberExpressionOp.Divide && rightConstant && right == 0) rightInvalid = true;
                if (!leftConstant || !rightConstant)
                {
                    invalid = leftInvalid || rightInvalid;
                    path.Remove(expression);
                    return false;
                }
                if (leftInvalid || rightInvalid)
                {
                    invalid = true;
                    path.Remove(expression);
                    return true;
                }
                if (expression.op == NumberExpressionOp.Add) value = left + right;
                else if (expression.op == NumberExpressionOp.Subtract) value = left - right;
                else if (expression.op == NumberExpressionOp.Multiply) value = left * right;
                else if (right == 0) invalid = true;
                else value = left / right;
            }
            else { path.Remove(expression); return false; }
            if (value < -RuleLimits.MaxStateMagnitude || value > RuleLimits.MaxStateMagnitude) invalid = true;
            path.Remove(expression);
            return true;
        }

        private static StateReferenceV1 ToReference(TypedRuleStateEntryV1 entry) => new StateReferenceV1 { scope = entry.scope, scopeId = entry.scopeId, key = entry.key };
        private static string Identity(RuleStateScope scope, string scopeId, string key) => (int)scope + "|" + (scopeId ?? "") + "|" + (key ?? "");
        private static bool MetadataSafe(string koreanName, string iconToken, string colorHex)
        {
            var probe = new StateDefinitionV1 { scope = RuleStateScope.Run, key = "probe", valueType = RuleStateValueType.Number, koreanName = koreanName, iconToken = iconToken, colorHex = colorHex };
            return RuleExpressionSelectors.IsDefinitionShapeSafe(probe);
        }
    }
}

#pragma warning restore UAC1008
#pragma warning restore UAC1006
#pragma warning restore UAC1005
