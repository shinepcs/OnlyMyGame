using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

#nullable disable

#pragma warning disable UAC1005
#pragma warning disable UAC1006
#pragma warning disable UAC1008

namespace OnlyMyGame.Core
{
    /// <summary>
    /// A resolved dynamic-action target. This is runtime context only and is not
    /// part of the AI wire response. The selector remains declarative while this
    /// value records the player actor and the observable object they clicked.
    /// </summary>
    [Serializable]
    public sealed class DynamicActionTargetV1
    {
        public int actorUnitId;
        public DynamicTargetKind kind;
        public int targetId;
        public HexCoord tile;
        public int ownerFactionId;
    }

    public static class DynamicActionTargeting
    {
        public const string ActorToken = "$actor";
        public const string TargetToken = "$target";
        public const string TileToken = "$tile";
        public const string OwnerToken = "$owner";

        public static bool RequiresTarget(DynamicActionV1 action) =>
            action?.targetSelector != null && action.targetSelector.kind != DynamicTargetKind.None;

        public static bool IsSelectorShapeSafe(DynamicTargetSelectorV1 selector)
        {
            if (selector == null ||
                !Enum.IsDefined(typeof(DynamicTargetKind), selector.kind) ||
                !Enum.IsDefined(typeof(DynamicTargetOwnership), selector.ownership) ||
                !Enum.IsDefined(typeof(DynamicTargetVisibility), selector.visibility) ||
                selector.minDistance < 0 || selector.maxDistance < selector.minDistance ||
                selector.maxDistance > RuleLimits.MaxDynamicTargetDistance ||
                selector.maxCandidates < 1 || selector.maxCandidates > RuleLimits.MaxDynamicTargetCandidates)
                return false;

            if (selector.kind == DynamicTargetKind.None)
                return selector.ownership == DynamicTargetOwnership.Any &&
                       selector.visibility == DynamicTargetVisibility.Visible &&
                       selector.minDistance == 0 && selector.maxDistance == 0 &&
                       selector.maxCandidates == 16;

            // Unit/building pools already require the object's current tile to
            // be visible. Treating "explored" as a distinct wire value would be
            // semantically inert and would inflate evaluator diversity scores.
            return (selector.kind != DynamicTargetKind.Unit && selector.kind != DynamicTargetKind.Building) ||
                   selector.visibility == DynamicTargetVisibility.Visible;
        }

        public static void ValidateSelectorAndBindings(
            DynamicActionV1 action,
            GameSnapshotV1 game,
            IList<string> errors,
            string source,
            bool requireCandidate)
        {
            if (action == null || errors == null) return;
            source = string.IsNullOrEmpty(source) ? "ACTION" : source;
            var selector = action.targetSelector;
            if (!IsSelectorShapeSafe(selector))
            {
                Add(errors, "DYNAMIC_TARGET_SELECTOR_INVALID:" + source);
                return;
            }

            var walker = new BindingValidationWalker(selector, errors, source);
            walker.VisitCondition(action.condition, 1);
            foreach (var effect in (action.effects ?? new List<EffectNode>()).Take(RuleLimits.MaxEffectsPerRule + 1))
                walker.VisitEffect(effect, 1);
            if (selector.kind != DynamicTargetKind.None && !walker.UsesSelectedTarget)
                Add(errors, "DYNAMIC_TARGET_UNUSED:" + source);

            if (requireCandidate && selector.kind != DynamicTargetKind.None && !HasPotentialTarget(action, game))
                Add(errors, "DYNAMIC_TARGET_UNAVAILABLE:" + source);
        }

        /// <summary>
        /// Applies one indexed availability budget across every action in a
        /// received rule set. This keeps the response-level cost bounded instead
        /// of multiplying the full actor scan by the number of actions.
        /// </summary>
        public static void ValidateTargetAvailability(
            IEnumerable<DynamicActionV1> actions,
            GameSnapshotV1 game,
            IList<string> errors,
            string sourcePrefix)
        {
            if (game == null || errors == null) return;
            var targeted = (actions ?? Enumerable.Empty<DynamicActionV1>())
                .Take(RuleLimits.MaxDynamicActions + 1)
                .Where(action => action != null && RequiresTarget(action) && IsSelectorShapeSafe(action.targetSelector))
                .ToList();
            if (targeted.Count == 0) return;

            var remainingValidationWork = RuleLimits.MaxDynamicTargetValidationWork;
            var indexed = TryCreateResolutionIndex(game, true, ref remainingValidationWork, out var index);
            foreach (var action in targeted)
            {
                var id = string.IsNullOrEmpty(action.id) ? "unknown" : action.id;
                var source = string.IsNullOrEmpty(sourcePrefix) ? id : sourcePrefix + ":" + id;
                if (!indexed || !HasPotentialTarget(action, game, index, ref remainingValidationWork))
                    Add(errors, "DYNAMIC_TARGET_UNAVAILABLE:" + source);
            }
        }

        public static bool IsTagBindingSelector(string value, DynamicTargetSelectorV1 selector)
        {
            if (string.Equals(value, ActorToken, StringComparison.Ordinal)) return selector != null && selector.kind != DynamicTargetKind.None;
            return string.Equals(value, TargetToken, StringComparison.Ordinal) && selector?.kind == DynamicTargetKind.Unit;
        }

        public static bool IsOwnerBindingSelector(string value, DynamicTargetSelectorV1 selector) =>
            string.Equals(value, TileToken, StringComparison.Ordinal) && selector != null && selector.kind != DynamicTargetKind.None;

        public static bool TryValidateNumberSelectorBinding(
            NumberExpressionOp op,
            string value,
            DynamicTargetSelectorV1 selector,
            out bool isBinding)
        {
            isBinding = LooksLikeBinding(value);
            if (!isBinding) return true;
            if (selector == null || selector.kind == DynamicTargetKind.None || !IsBindingToken(value)) return false;
            if (string.Equals(value, ActorToken, StringComparison.Ordinal))
                return op == NumberExpressionOp.CountUnits || op == NumberExpressionOp.Distance;
            if (string.Equals(value, TileToken, StringComparison.Ordinal))
                return op == NumberExpressionOp.CountTiles || op == NumberExpressionOp.Distance;
            if (string.Equals(value, OwnerToken, StringComparison.Ordinal))
                return selector.visibility == DynamicTargetVisibility.Visible &&
                       (op == NumberExpressionOp.CountUnits || op == NumberExpressionOp.CountBuildings || op == NumberExpressionOp.CountTiles);
            if (!string.Equals(value, TargetToken, StringComparison.Ordinal)) return false;
            if (op == NumberExpressionOp.Distance) return true;
            if (op == NumberExpressionOp.CountUnits) return selector.kind == DynamicTargetKind.Unit;
            if (op == NumberExpressionOp.CountBuildings) return selector.kind == DynamicTargetKind.Building;
            return op == NumberExpressionOp.CountTiles && selector.kind == DynamicTargetKind.Tile;
        }

        public static bool TryResolveCandidates(
            DynamicActionV1 action,
            GameSnapshotV1 game,
            int actorUnitId,
            out List<DynamicActionTargetV1> candidates)
        {
            candidates = new List<DynamicActionTargetV1>();
            var maximum = action?.targetSelector?.maxCandidates ?? 0;
            var remainingScanWork = RuleLimits.MaxDynamicTargetResolutionWork;
            return TryCreateResolutionIndex(game, false, ref remainingScanWork, out var index) &&
                   TryResolveCandidatesBounded(action, game, index, actorUnitId, maximum, ref remainingScanWork, out candidates);
        }

        /// <summary>
        /// Resolves the globally bounded observable pool before target-dependent
        /// conditions are evaluated. The selector's maxCandidates cap is applied
        /// after binding and condition filtering by the runtime.
        /// </summary>
        public static bool TryResolveCandidatePool(
            DynamicActionV1 action,
            GameSnapshotV1 game,
            int actorUnitId,
            out List<DynamicActionTargetV1> candidates)
        {
            var remainingScanWork = RuleLimits.MaxDynamicTargetResolutionWork;
            return TryResolveCandidatePoolWithinBudget(action, game, actorUnitId, ref remainingScanWork, out candidates);
        }

        /// <summary>
        /// Resolves a candidate pool while charging index, scan, and ordering work.
        /// A failed budget check returns no partial candidate page.
        /// </summary>
        public static bool TryResolveCandidatePoolWithinBudget(
            DynamicActionV1 action,
            GameSnapshotV1 game,
            int actorUnitId,
            ref int remainingScanWork,
            out List<DynamicActionTargetV1> candidates)
        {
            candidates = new List<DynamicActionTargetV1>();
            return TryCreateResolutionIndex(game, false, ref remainingScanWork, out var index) &&
                   TryResolveCandidatesBounded(
                       action,
                       game,
                       index,
                       actorUnitId,
                       RuleLimits.MaxDynamicTargetScanCandidates,
                       ref remainingScanWork,
                       out candidates);
        }

        /// <summary>
        /// Resolves the first deterministic page of condition-matching targets.
        /// Binding allocations and condition selector scans share explicit budgets;
        /// an incomplete scan never exposes a misleading partial page.
        /// </summary>
        public static bool TryResolveExecutableCandidates(
            DynamicActionV1 action,
            GameSnapshotV1 game,
            int actorUnitId,
            out List<DynamicActionTargetV1> executable)
        {
            executable = new List<DynamicActionTargetV1>();
            var remainingScanWork = RuleLimits.MaxDynamicTargetResolutionWork;
            return TryCreateResolutionIndex(game, false, ref remainingScanWork, out var index) &&
                   TryResolveExecutableCandidatesIndexed(action, game, index, actorUnitId, remainingScanWork, out executable);
        }

        /// <summary>
        /// Builds one immutable lookup view for one short-lived group of HUD queries.
        /// Each action still receives the same independent post-index work budget as
        /// the single-action resolver, so card order cannot change availability. The
        /// index never escapes this call and therefore cannot survive a world change.
        /// </summary>
        public static bool TryResolveExecutableCandidatesBatch(
            IReadOnlyList<DynamicActionV1> actions,
            GameSnapshotV1 game,
            int actorUnitId,
            out List<List<DynamicActionTargetV1>> executableByAction)
        {
            executableByAction = new List<List<DynamicActionTargetV1>>();
            if (actions == null || actions.Count < 1 || actions.Count > RuleLimits.MaxDynamicTargetBatchActions) return false;
            var remainingScanWork = RuleLimits.MaxDynamicTargetResolutionWork;
            if (!TryCreateResolutionIndex(game, false, ref remainingScanWork, out var index)) return false;
            foreach (var action in actions)
            {
                if (!TryResolveExecutableCandidatesIndexed(
                        action,
                        game,
                        index,
                        actorUnitId,
                        remainingScanWork,
                        out var executable)) executable = new List<DynamicActionTargetV1>();
                executableByAction.Add(executable);
            }
            return true;
        }

        private static bool TryResolveExecutableCandidatesIndexed(
            DynamicActionV1 action,
            GameSnapshotV1 game,
            ResolutionIndex index,
            int actorUnitId,
            int initialScanWork,
            out List<DynamicActionTargetV1> executable)
        {
            executable = new List<DynamicActionTargetV1>();
            var remainingScanWork = initialScanWork;
            if (!TryResolveCandidatesBounded(
                    action,
                    game,
                    index,
                    actorUnitId,
                    RuleLimits.MaxDynamicTargetScanCandidates,
                    ref remainingScanWork,
                    out var candidates)) return false;
            var remainingBindingWork = RuleLimits.MaxDynamicTargetBindingWork;
            var remainingConditionWork = RuleLimits.MaxDynamicTargetConditionWork;
            foreach (var candidate in candidates)
            {
                if (!TryBindExecutionWithinBudget(
                        action,
                        candidate,
                        ref remainingBindingWork,
                        out var condition,
                        out _,
                        out var bindingWorkExhausted))
                {
                    if (bindingWorkExhausted)
                    {
                        executable.Clear();
                        return false;
                    }
                    continue;
                }
                if (!RuleVm.TryConditionMatchesWithinBudget(condition, game, remainingConditionWork, out var matches, out var usedWork))
                {
                    executable.Clear();
                    return false;
                }
                remainingConditionWork -= usedWork;
                if (!matches) continue;
                executable.Add(candidate);
                if (executable.Count >= action.targetSelector.maxCandidates) return true;
            }
            return true;
        }

        private static bool TryResolveCandidatesBounded(
            DynamicActionV1 action,
            GameSnapshotV1 game,
            ResolutionIndex index,
            int actorUnitId,
            int maximum,
            ref int remainingScanWork,
            out List<DynamicActionTargetV1> candidates)
        {
            candidates = new List<DynamicActionTargetV1>();
            if (action == null || game == null || index == null || !IsSelectorShapeSafe(action.targetSelector) || !CollectionsAreBounded(game) ||
                maximum < 1 || maximum > RuleLimits.MaxDynamicTargetScanCandidates) return false;
            var selector = action.targetSelector;
            if (selector.kind == DynamicTargetKind.None) return true;

            if (!index.UnitsById.TryGetValue(actorUnitId, out var actor) ||
                actor == null || actor.factionId != 1 || !actor.alive || !index.Tiles.ContainsKey(actor.position)) return false;

            if (selector.kind == DynamicTargetKind.Tile)
            {
                if (!TrySpendWork(ref remainingScanWork, game.map.Count)) return false;
                var selectable = new List<TileState>();
                foreach (var tile in game.map)
                {
                    if (tile == null || !TileIsSelectable(index, tile, selector) ||
                        !DistanceMatches(actor.position, tile.position, selector)) continue;
                    selectable.Add(tile);
                }
                if (!TrySpendWork(ref remainingScanWork, EstimateSortWork(selectable.Count))) return false;
                selectable.Sort((left, right) =>
                {
                    var comparison = actor.position.Distance(left.position).CompareTo(actor.position.Distance(right.position));
                    if (comparison != 0) return comparison;
                    comparison = left.position.q.CompareTo(right.position.q);
                    return comparison != 0 ? comparison : left.position.r.CompareTo(right.position.r);
                });
                foreach (var tile in selectable)
                {
                    candidates.Add(new DynamicActionTargetV1
                    {
                        actorUnitId = actor.id,
                        kind = DynamicTargetKind.Tile,
                        tile = tile.position,
                        ownerFactionId = tile.visible ? tile.owner : 0
                    });
                    if (candidates.Count >= maximum) break;
                }
                return true;
            }

            if (selector.kind == DynamicTargetKind.Unit)
            {
                if (!TrySpendWork(ref remainingScanWork, game.entities.Count)) return false;
                var selectable = new List<UnitState>();
                foreach (var unit in game.entities)
                {
                    if (unit == null || !unit.alive || !index.Tiles.TryGetValue(unit.position, out var tile) ||
                        tile == null || !tile.visible || !OwnershipMatches(index, unit.factionId, selector.ownership) ||
                        !DistanceMatches(actor.position, unit.position, selector)) continue;
                    selectable.Add(unit);
                }
                if (!TrySpendWork(ref remainingScanWork, EstimateSortWork(selectable.Count))) return false;
                selectable.Sort((left, right) =>
                {
                    var comparison = actor.position.Distance(left.position).CompareTo(actor.position.Distance(right.position));
                    return comparison != 0 ? comparison : left.id.CompareTo(right.id);
                });
                foreach (var unit in selectable)
                {
                    candidates.Add(new DynamicActionTargetV1
                    {
                        actorUnitId = actor.id,
                        kind = DynamicTargetKind.Unit,
                        targetId = unit.id,
                        tile = unit.position,
                        ownerFactionId = unit.factionId
                    });
                    if (candidates.Count >= maximum) break;
                }
                return true;
            }

            if (!TrySpendWork(ref remainingScanWork, game.buildings.Count)) return false;
            var selectableBuildings = new List<BuildingState>();
            foreach (var building in game.buildings)
            {
                if (building == null || building.hp <= 0 || !index.Tiles.TryGetValue(building.position, out var tile) ||
                    tile == null || !tile.visible || !OwnershipMatches(index, building.factionId, selector.ownership) ||
                    !DistanceMatches(actor.position, building.position, selector)) continue;
                selectableBuildings.Add(building);
            }
            if (!TrySpendWork(ref remainingScanWork, EstimateSortWork(selectableBuildings.Count))) return false;
            selectableBuildings.Sort((left, right) =>
            {
                var comparison = actor.position.Distance(left.position).CompareTo(actor.position.Distance(right.position));
                return comparison != 0 ? comparison : left.id.CompareTo(right.id);
            });
            foreach (var building in selectableBuildings)
            {
                candidates.Add(new DynamicActionTargetV1
                {
                    actorUnitId = actor.id,
                    kind = DynamicTargetKind.Building,
                    targetId = building.id,
                    tile = building.position,
                    ownerFactionId = building.factionId
                });
                if (candidates.Count >= maximum) break;
            }
            return true;
        }

        public static bool TryFindCandidate(
            DynamicActionV1 action,
            GameSnapshotV1 game,
            int actorUnitId,
            DynamicTargetKind kind,
            int targetId,
            HexCoord tile,
            out DynamicActionTargetV1 candidate)
        {
            candidate = null;
            if (!TryResolveCandidates(action, game, actorUnitId, out var candidates)) return false;
            candidate = candidates.FirstOrDefault(value => SameTarget(value, kind, targetId, tile));
            return candidate != null;
        }

        public static bool SameTarget(DynamicActionTargetV1 candidate, DynamicActionTargetV1 other) =>
            candidate != null && other != null && SameTarget(candidate, other.kind, other.targetId, other.tile) &&
            candidate.actorUnitId == other.actorUnitId;

        public static DynamicActionTargetV1 FindClickedCandidate(
            IEnumerable<DynamicActionTargetV1> candidates,
            DynamicTargetKind kind,
            int targetId,
            HexCoord tile) =>
            (candidates ?? Enumerable.Empty<DynamicActionTargetV1>())
                .FirstOrDefault(candidate => SameTarget(candidate, kind, targetId, tile));

        public static bool TryBindExecution(
            DynamicActionV1 action,
            DynamicActionTargetV1 target,
            out ConditionNode condition,
            out List<EffectNode> effects)
        {
            return TryBindExecutionBounded(
                action,
                target,
                MaximumBindingWorkPerCandidate,
                out condition,
                out effects,
                out _,
                out _);
        }

        public static bool TryBindExecutionWithinBudget(
            DynamicActionV1 action,
            DynamicActionTargetV1 target,
            ref int remainingWork,
            out ConditionNode condition,
            out List<EffectNode> effects,
            out bool workExhausted)
        {
            var maximum = Math.Max(0, Math.Min(
                remainingWork,
                MaximumBindingWorkPerCandidate));
            var bound = TryBindExecutionBounded(
                action,
                target,
                maximum,
                out condition,
                out effects,
                out var usedWork,
                out workExhausted);
            remainingWork = Math.Max(0, remainingWork - usedWork);
            return bound;
        }

        private static int MaximumBindingWorkPerCandidate =>
            RuleLimits.MaxConditionNodes + RuleLimits.MaxEffectsPerRule * RuleLimits.MaxStateSetElements;

        private static bool TryBindExecutionBounded(
            DynamicActionV1 action,
            DynamicActionTargetV1 target,
            int maximumWork,
            out ConditionNode condition,
            out List<EffectNode> effects,
            out int usedWork,
            out bool workExhausted)
        {
            condition = null;
            effects = null;
            usedWork = 0;
            workExhausted = false;
            if (action == null || target == null || !IsSelectorShapeSafe(action.targetSelector) ||
                action.targetSelector.kind == DynamicTargetKind.None || target.kind != action.targetSelector.kind ||
                target.actorUnitId <= 0)
                return false;

            var budget = new CloneBudget(maximumWork);
            try
            {
                if (!TryCloneCondition(action.condition, target, budget, 1, out condition)) return false;
                var sourceEffects = action.effects ?? new List<EffectNode>();
                if (sourceEffects.Count < 1 || sourceEffects.Count > RuleLimits.MaxEffectsPerRule) return false;
                effects = new List<EffectNode>(sourceEffects.Count);
                foreach (var effect in sourceEffects)
                {
                    if (!TryCloneEffect(effect, target, budget, out var copy)) return false;
                    effects.Add(copy);
                }
                return true;
            }
            finally
            {
                usedWork = budget.Nodes;
                workExhausted = budget.WorkLimitExceeded;
            }
        }

        public static string DescribeSelector(DynamicTargetSelectorV1 selector)
        {
            if (selector == null || selector.kind == DynamicTargetKind.None) return "대상 없음";
            var kind = selector.kind == DynamicTargetKind.Tile ? "타일" : selector.kind == DynamicTargetKind.Unit ? "유닛" : "건물";
            var owner = selector.ownership == DynamicTargetOwnership.Player ? "아군 " :
                selector.ownership == DynamicTargetOwnership.NonPlayer ? "비아군 " :
                selector.ownership == DynamicTargetOwnership.Neutral ? "중립 " : string.Empty;
            return owner + kind + " · 거리 " + selector.minDistance + "–" + selector.maxDistance;
        }

        private static bool HasPotentialTarget(DynamicActionV1 action, GameSnapshotV1 game)
        {
            if (game == null || !CollectionsAreBounded(game)) return false;
            var remainingValidationWork = RuleLimits.MaxDynamicTargetValidationWork;
            if (!TryCreateResolutionIndex(game, true, ref remainingValidationWork, out var index)) return false;
            return HasPotentialTarget(action, game, index, ref remainingValidationWork);
        }

        private static bool HasPotentialTarget(
            DynamicActionV1 action,
            GameSnapshotV1 game,
            ResolutionIndex index,
            ref int remainingValidationWork)
        {
            if (action == null || game == null || index == null || remainingValidationWork <= 0) return false;
            foreach (var actor in index.PlayerActors)
            {
                if (!TrySpendWork(ref remainingValidationWork, 1) ||
                    !TryResolveCandidatesBounded(
                        action,
                        game,
                        index,
                        actor.id,
                        RuleLimits.MaxDynamicTargetScanCandidates,
                        ref remainingValidationWork,
                        out var candidates)) return false;
                var remainingBindingWork = RuleLimits.MaxDynamicTargetBindingWork;
                var remainingConditionWork = RuleLimits.MaxDynamicTargetConditionWork;
                var hasBindableCandidate = false;
                var completeScan = true;
                foreach (var candidate in candidates)
                {
                    var bindingAllowance = Math.Min(remainingBindingWork, remainingValidationWork);
                    if (bindingAllowance < 1)
                    {
                        completeScan = false;
                        break;
                    }
                    var bindingBefore = bindingAllowance;
                    if (!TryBindExecutionWithinBudget(
                            action,
                            candidate,
                            ref bindingAllowance,
                            out var condition,
                            out _,
                            out var bindingWorkExhausted))
                    {
                        var usedBindingWork = bindingBefore - bindingAllowance;
                        remainingBindingWork -= usedBindingWork;
                        if (!TrySpendWork(ref remainingValidationWork, usedBindingWork) || bindingWorkExhausted)
                        {
                            completeScan = false;
                            break;
                        }
                        continue;
                    }
                    var boundWork = bindingBefore - bindingAllowance;
                    remainingBindingWork -= boundWork;
                    if (!TrySpendWork(ref remainingValidationWork, boundWork))
                    {
                        completeScan = false;
                        break;
                    }
                    var conditionAllowance = Math.Min(remainingConditionWork, remainingValidationWork);
                    if (conditionAllowance < 1)
                    {
                        completeScan = false;
                        break;
                    }
                    var evaluated = RuleVm.TryConditionMatchesWithinBudget(
                        condition,
                        game,
                        conditionAllowance,
                        out var matches,
                        out var usedConditionWork);
                    if (!TrySpendWork(ref remainingValidationWork, Math.Max(1, usedConditionWork)) || !evaluated)
                    {
                        completeScan = false;
                        break;
                    }
                    remainingConditionWork -= usedConditionWork;
                    if (matches) hasBindableCandidate = true;
                }
                if (completeScan && hasBindableCandidate) return true;
                if (remainingValidationWork <= 0) return false;
            }
            return false;
        }

        private static bool SameTarget(DynamicActionTargetV1 candidate, DynamicTargetKind kind, int targetId, HexCoord tile)
        {
            if (candidate == null || candidate.kind != kind || !candidate.tile.Equals(tile)) return false;
            return kind == DynamicTargetKind.Tile || candidate.targetId == targetId;
        }

        private static bool CollectionsAreBounded(GameSnapshotV1 game) =>
            game.map != null && game.map.Count <= RuleLimits.MaxMapTiles &&
            game.entities != null && game.entities.Count <= RuleLimits.MaxEntities &&
            game.buildings != null && game.buildings.Count <= RuleLimits.MaxBuildings &&
            game.factions != null && game.factions.Count <= RuleLimits.MaxFactions;

        private static bool TileIsSelectable(ResolutionIndex index, TileState tile, DynamicTargetSelectorV1 selector)
        {
            var observable = selector.visibility == DynamicTargetVisibility.Visible ? tile.visible : tile.explored;
            if (!observable) return false;
            // An explored-but-currently-hidden foreign owner is intentionally not
            // used as a filter. Doing so would turn target highlighting into a fog
            // of war oracle when ownership changed outside player vision.
            if (!tile.visible && selector.ownership != DynamicTargetOwnership.Any &&
                !(selector.ownership == DynamicTargetOwnership.Player && tile.owner == 1)) return false;
            return OwnershipMatches(index, tile.owner, selector.ownership);
        }

        private static bool OwnershipMatches(ResolutionIndex index, int factionId, DynamicTargetOwnership ownership)
        {
            if (ownership == DynamicTargetOwnership.Any) return true;
            if (ownership == DynamicTargetOwnership.Player) return factionId == 1;
            if (ownership == DynamicTargetOwnership.NonPlayer) return factionId > 1;
            if (factionId == 0) return true;
            return index.FactionKinds.TryGetValue(factionId, out var kind) && kind == FactionKind.Neutral;
        }

        private static bool TryCreateResolutionIndex(
            GameSnapshotV1 game,
            bool collectPlayerActors,
            ref int remainingWork,
            out ResolutionIndex index)
        {
            index = null;
            if (game == null || !CollectionsAreBounded(game)) return false;
            var buildWork = (long)game.map.Count + game.entities.Count + game.buildings.Count + game.factions.Count;
            if (!TrySpendWork(ref remainingWork, buildWork)) return false;

            var created = new ResolutionIndex(game.map.Count, game.entities.Count, game.buildings.Count, game.factions.Count, collectPlayerActors);
            foreach (var tile in game.map)
            {
                if (tile == null || created.Tiles.ContainsKey(tile.position)) return false;
                created.Tiles.Add(tile.position, tile);
            }
            foreach (var unit in game.entities)
            {
                if (unit == null || created.UnitsById.ContainsKey(unit.id)) return false;
                created.UnitsById.Add(unit.id, unit);
                if (collectPlayerActors && unit.factionId == 1 && unit.alive) created.PlayerActors.Add(unit);
            }
            foreach (var building in game.buildings)
                if (building == null || !created.BuildingIds.Add(building.id)) return false;
            foreach (var faction in game.factions)
            {
                if (faction == null || created.FactionKinds.ContainsKey(faction.id)) return false;
                created.FactionKinds.Add(faction.id, faction.kind);
            }

            if (collectPlayerActors)
            {
                if (!TrySpendWork(ref remainingWork, EstimateSortWork(created.PlayerActors.Count))) return false;
                created.PlayerActors.Sort((left, right) => left.id.CompareTo(right.id));
            }
            index = created;
            return true;
        }

        private static bool TrySpendWork(ref int remainingWork, long amount)
        {
            if (amount < 0 || amount > remainingWork)
            {
                remainingWork = 0;
                return false;
            }
            remainingWork -= (int)amount;
            return true;
        }

        private static long EstimateSortWork(int count)
        {
            if (count < 2) return count;
            var levels = 0;
            for (var remaining = count; remaining > 1; remaining = (remaining + 1) / 2) levels++;
            return (long)count * levels;
        }

        private sealed class ResolutionIndex
        {
            public readonly Dictionary<HexCoord, TileState> Tiles;
            public readonly Dictionary<int, UnitState> UnitsById;
            public readonly HashSet<int> BuildingIds;
            public readonly Dictionary<int, FactionKind> FactionKinds;
            public readonly List<UnitState> PlayerActors;

            public ResolutionIndex(int tileCapacity, int unitCapacity, int buildingCapacity, int factionCapacity, bool collectPlayerActors)
            {
                Tiles = new Dictionary<HexCoord, TileState>(Math.Max(0, tileCapacity));
                UnitsById = new Dictionary<int, UnitState>(Math.Max(0, unitCapacity));
                BuildingIds = new HashSet<int>();
                FactionKinds = new Dictionary<int, FactionKind>(Math.Max(0, factionCapacity));
                PlayerActors = new List<UnitState>(collectPlayerActors ? Math.Max(0, unitCapacity) : 0);
            }
        }

        private static bool DistanceMatches(HexCoord actor, HexCoord target, DynamicTargetSelectorV1 selector)
        {
            var distance = actor.Distance(target);
            return distance >= selector.minDistance && distance <= selector.maxDistance;
        }

        private static bool IsBindingToken(string value) =>
            string.Equals(value, ActorToken, StringComparison.Ordinal) ||
            string.Equals(value, TargetToken, StringComparison.Ordinal) ||
            string.Equals(value, TileToken, StringComparison.Ordinal) ||
            string.Equals(value, OwnerToken, StringComparison.Ordinal);

        private static bool LooksLikeBinding(string value) => !string.IsNullOrEmpty(value) && value[0] == '$';

        private static bool TryCloneCondition(ConditionNode source, DynamicActionTargetV1 target, CloneBudget budget, int depth, out ConditionNode copy)
        {
            copy = null;
            if (!budget.Enter(source, depth)) return false;
            try
            {
                var left = source.left;
                var text = source.text;
                if (source.op == CompareOp.HasTag && LooksLikeBinding(left))
                {
                    if (!TryBindTagSelector(left, target, out left)) return false;
                }
                else if (source.op == CompareOp.OwnerIs)
                {
                    if (LooksLikeBinding(left) && !TryBindTileSelector(left, target, out left)) return false;
                    if (LooksLikeBinding(text) && !TryBindTileSelector(text, target, out text)) return false;
                }
                copy = new ConditionNode
                {
                    op = source.op,
                    left = left,
                    value = source.value,
                    text = text,
                    all = new List<ConditionNode>()
                };
                if (source.predicate != null && !TryClonePredicate(source.predicate, target, budget, depth + 1, out copy.predicate)) return false;
                foreach (var child in source.all ?? new List<ConditionNode>())
                {
                    if (!TryCloneCondition(child, target, budget, depth + 1, out var childCopy)) return false;
                    copy.all.Add(childCopy);
                }
                return true;
            }
            finally { budget.Exit(source); }
        }

        private static bool TryClonePredicate(PredicateExpressionV1 source, DynamicActionTargetV1 target, CloneBudget budget, int depth, out PredicateExpressionV1 copy)
        {
            copy = null;
            if (!budget.Enter(source, depth)) return false;
            try
            {
                copy = new PredicateExpressionV1
                {
                    op = source.op,
                    children = new List<PredicateExpressionV1>(),
                    state = CloneStateReference(source.state),
                    element = source.element
                };
                if (source.child != null && !TryClonePredicate(source.child, target, budget, depth + 1, out copy.child)) return false;
                if (source.left != null && !TryCloneNumber(source.left, target, budget, depth + 1, out copy.left)) return false;
                if (source.right != null && !TryCloneNumber(source.right, target, budget, depth + 1, out copy.right)) return false;
                foreach (var child in source.children ?? new List<PredicateExpressionV1>())
                {
                    if (!TryClonePredicate(child, target, budget, depth + 1, out var childCopy)) return false;
                    copy.children.Add(childCopy);
                }
                return true;
            }
            finally { budget.Exit(source); }
        }

        private static bool TryCloneNumber(NumberExpressionV1 source, DynamicActionTargetV1 target, CloneBudget budget, int depth, out NumberExpressionV1 copy)
        {
            copy = null;
            if (!budget.Enter(source, depth)) return false;
            try
            {
                var selector = source.selector;
                var secondSelector = source.secondSelector;
                if (LooksLikeBinding(selector) && !TryBindNumberSelector(source.op, selector, target, out selector)) return false;
                if (LooksLikeBinding(secondSelector) && !TryBindNumberSelector(source.op, secondSelector, target, out secondSelector)) return false;
                copy = new NumberExpressionV1
                {
                    op = source.op,
                    constant = source.constant,
                    state = CloneStateReference(source.state),
                    selector = selector,
                    secondSelector = secondSelector,
                    action = source.action,
                    recentTurns = source.recentTurns
                };
                if (source.left != null && !TryCloneNumber(source.left, target, budget, depth + 1, out copy.left)) return false;
                if (source.right != null && !TryCloneNumber(source.right, target, budget, depth + 1, out copy.right)) return false;
                return true;
            }
            finally { budget.Exit(source); }
        }

        private static bool TryCloneEffect(EffectNode source, DynamicActionTargetV1 target, CloneBudget budget, out EffectNode copy)
        {
            copy = null;
            if (!budget.Enter(source, 1)) return false;
            try
            {
                if (source.type == EffectType.FactionSwitch && string.Equals(source.target, TargetToken, StringComparison.Ordinal) &&
                    int.TryParse(source.key ?? string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture, out var newFactionId) &&
                    target.ownerFactionId == newFactionId)
                    return false;
                var boundTarget = source.target;
                if (LooksLikeBinding(boundTarget) && !TryBindEffectTarget(source.type, boundTarget, target, out boundTarget)) return false;
                copy = new EffectNode
                {
                    type = source.type,
                    resource = source.resource,
                    amount = source.amount,
                    target = boundTarget,
                    key = source.key,
                    value = source.value,
                    delay = source.delay
                };
                if (source.stateMutation != null && !TryCloneMutation(source.stateMutation, target, budget, 2, out copy.stateMutation)) return false;
                return true;
            }
            finally { budget.Exit(source); }
        }

        private static bool TryCloneMutation(StateMutationV1 source, DynamicActionTargetV1 target, CloneBudget budget, int depth, out StateMutationV1 copy)
        {
            copy = null;
            if (!budget.Enter(source, depth)) return false;
            try
            {
                var sourceSetValues = source.setValues ?? new List<string>();
                if (!budget.TrySpend(sourceSetValues.Count)) return false;
                copy = new StateMutationV1
                {
                    op = source.op,
                    state = CloneStateReference(source.state),
                    boolValue = source.boolValue,
                    setValues = new List<string>(sourceSetValues),
                    element = source.element
                };
                if (source.numberValue != null && !TryCloneNumber(source.numberValue, target, budget, depth + 1, out copy.numberValue)) return false;
                return true;
            }
            finally { budget.Exit(source); }
        }

        private static StateReferenceV1 CloneStateReference(StateReferenceV1 source) => source == null ? null : new StateReferenceV1
        {
            scope = source.scope,
            scopeId = source.scopeId,
            key = source.key
        };

        private static bool TryBindTagSelector(string token, DynamicActionTargetV1 target, out string value)
        {
            value = null;
            if (string.Equals(token, ActorToken, StringComparison.Ordinal))
            {
                value = "unit:" + target.actorUnitId.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            if (string.Equals(token, TargetToken, StringComparison.Ordinal) && target.kind == DynamicTargetKind.Unit)
            {
                value = "unit:" + target.targetId.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            return false;
        }

        private static bool TryBindTileSelector(string token, DynamicActionTargetV1 target, out string value)
        {
            value = null;
            if (!string.Equals(token, TileToken, StringComparison.Ordinal)) return false;
            value = TileSelector(target.tile);
            return true;
        }

        private static bool TryBindNumberSelector(NumberExpressionOp op, string token, DynamicActionTargetV1 target, out string value)
        {
            value = null;
            if (string.Equals(token, ActorToken, StringComparison.Ordinal))
            {
                value = "unit:" + target.actorUnitId.ToString(CultureInfo.InvariantCulture);
                return op == NumberExpressionOp.CountUnits || op == NumberExpressionOp.Distance;
            }
            if (string.Equals(token, TileToken, StringComparison.Ordinal))
            {
                value = TileSelector(target.tile);
                return op == NumberExpressionOp.CountTiles || op == NumberExpressionOp.Distance;
            }
            if (string.Equals(token, OwnerToken, StringComparison.Ordinal))
            {
                if (target.ownerFactionId < 0) return false;
                if (target.ownerFactionId == 0 && op != NumberExpressionOp.CountTiles) return false;
                value = op == NumberExpressionOp.CountTiles
                    ? "owner:" + target.ownerFactionId.ToString(CultureInfo.InvariantCulture)
                    : "faction:" + target.ownerFactionId.ToString(CultureInfo.InvariantCulture);
                return op == NumberExpressionOp.CountUnits || op == NumberExpressionOp.CountBuildings || op == NumberExpressionOp.CountTiles;
            }
            if (!string.Equals(token, TargetToken, StringComparison.Ordinal)) return false;
            if (target.kind == DynamicTargetKind.Unit) value = "unit:" + target.targetId.ToString(CultureInfo.InvariantCulture);
            else if (target.kind == DynamicTargetKind.Building) value = "building:" + target.targetId.ToString(CultureInfo.InvariantCulture);
            else value = TileSelector(target.tile);
            return op == NumberExpressionOp.Distance ||
                   op == NumberExpressionOp.CountUnits && target.kind == DynamicTargetKind.Unit ||
                   op == NumberExpressionOp.CountBuildings && target.kind == DynamicTargetKind.Building ||
                   op == NumberExpressionOp.CountTiles && target.kind == DynamicTargetKind.Tile;
        }

        private static bool TryBindEffectTarget(EffectType type, string token, DynamicActionTargetV1 target, out string value)
        {
            value = null;
            if (type == EffectType.FactionSwitch && string.Equals(token, TargetToken, StringComparison.Ordinal) &&
                target.kind == DynamicTargetKind.Unit && target.ownerFactionId != 1)
            {
                value = target.targetId.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            if (type == EffectType.Spawn && string.Equals(token, OwnerToken, StringComparison.Ordinal) && target.ownerFactionId > 0)
            {
                value = target.ownerFactionId.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            if (type == EffectType.Relation && string.Equals(token, OwnerToken, StringComparison.Ordinal) && target.ownerFactionId > 1)
            {
                value = "faction:" + target.ownerFactionId.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            return false;
        }

        private static string TileSelector(HexCoord tile) => "tile:" + tile.q.ToString(CultureInfo.InvariantCulture) + "," + tile.r.ToString(CultureInfo.InvariantCulture);

        private static void Add(IList<string> errors, string value)
        {
            if (!errors.Contains(value)) errors.Add(value);
        }

        private sealed class CloneBudget
        {
            private readonly HashSet<object> path = new HashSet<object>();
            private readonly int maximumWork;
            private int nodes;
            private int work;
            public int Nodes => work;
            public bool WorkLimitExceeded { get; private set; }

            public CloneBudget(int maximumWork) { this.maximumWork = Math.Max(0, maximumWork); }

            public bool Enter(object value, int depth)
            {
                if (value == null || depth > RuleLimits.MaxConditionDepth || nodes >= RuleLimits.MaxConditionNodes || !path.Add(value)) return false;
                if (!TrySpend(1))
                {
                    path.Remove(value);
                    return false;
                }
                nodes++;
                return true;
            }

            public bool TrySpend(int amount)
            {
                if (amount < 0 || amount > maximumWork - work)
                {
                    WorkLimitExceeded = true;
                    work = maximumWork;
                    return false;
                }
                work += amount;
                return true;
            }

            public void Exit(object value) { if (value != null) path.Remove(value); }
        }

        private sealed class BindingValidationWalker
        {
            private readonly DynamicTargetSelectorV1 selector;
            private readonly IList<string> errors;
            private readonly string source;
            private readonly HashSet<object> path = new HashSet<object>();
            private int nodes;
            public bool UsesSelectedTarget { get; private set; }

            public BindingValidationWalker(DynamicTargetSelectorV1 selector, IList<string> errors, string source)
            {
                this.selector = selector;
                this.errors = errors;
                this.source = source;
            }

            public void VisitCondition(ConditionNode condition, int depth)
            {
                if (!Enter(condition, depth)) return;
                try
                {
                    if (condition.op == CompareOp.HasTag)
                    {
                        if (LooksLikeBinding(condition.left))
                        {
                            if (!IsTagBindingSelector(condition.left, selector)) Invalid();
                            else MarkTargetUse(condition.left);
                        }
                        if (LooksLikeBinding(condition.text)) Invalid();
                    }
                    else if (condition.op == CompareOp.OwnerIs)
                    {
                        if (LooksLikeBinding(condition.left))
                        {
                            if (!IsOwnerBindingSelector(condition.left, selector)) Invalid();
                            else MarkTargetUse(condition.left);
                        }
                        if (LooksLikeBinding(condition.text))
                        {
                            if (!IsOwnerBindingSelector(condition.text, selector)) Invalid();
                            else MarkTargetUse(condition.text);
                        }
                    }
                    else if (LooksLikeBinding(condition.left) || LooksLikeBinding(condition.text)) Invalid();
                    if (condition.predicate != null) VisitPredicate(condition.predicate, depth + 1);
                    foreach (var child in condition.all ?? new List<ConditionNode>()) VisitCondition(child, depth + 1);
                }
                finally { Exit(condition); }
            }

            public void VisitEffect(EffectNode effect, int depth)
            {
                if (effect == null) return;
                if (LooksLikeBinding(effect.target))
                {
                    if (!EffectBindingIsAllowed(effect.type, effect.target)) Invalid();
                    else MarkTargetUse(effect.target);
                }
                if (LooksLikeBinding(effect.key) || LooksLikeBinding(effect.value)) Invalid();
                if (effect.stateMutation != null) VisitMutation(effect.stateMutation, depth + 1);
            }

            private void VisitPredicate(PredicateExpressionV1 predicate, int depth)
            {
                if (!Enter(predicate, depth)) return;
                try
                {
                    if (LooksLikeBinding(predicate.element) || HasStateBinding(predicate.state)) Invalid();
                    if (predicate.left != null) VisitNumber(predicate.left, depth + 1);
                    if (predicate.right != null) VisitNumber(predicate.right, depth + 1);
                    if (predicate.child != null) VisitPredicate(predicate.child, depth + 1);
                    foreach (var child in predicate.children ?? new List<PredicateExpressionV1>()) VisitPredicate(child, depth + 1);
                }
                finally { Exit(predicate); }
            }

            private void VisitNumber(NumberExpressionV1 expression, int depth)
            {
                if (!Enter(expression, depth)) return;
                try
                {
                    if (HasStateBinding(expression.state)) Invalid();
                    if (!TryValidateNumberSelectorBinding(expression.op, expression.selector, selector, out var firstBinding)) Invalid();
                    else if (firstBinding) MarkTargetUse(expression.selector);
                    if (LooksLikeBinding(expression.secondSelector) && expression.op != NumberExpressionOp.Distance ||
                        !TryValidateNumberSelectorBinding(expression.op, expression.secondSelector, selector, out var secondBinding)) Invalid();
                    else if (secondBinding) MarkTargetUse(expression.secondSelector);
                    if (expression.left != null) VisitNumber(expression.left, depth + 1);
                    if (expression.right != null) VisitNumber(expression.right, depth + 1);
                }
                finally { Exit(expression); }
            }

            private void VisitMutation(StateMutationV1 mutation, int depth)
            {
                if (!Enter(mutation, depth)) return;
                try
                {
                    if (HasStateBinding(mutation.state) || LooksLikeBinding(mutation.element) ||
                        (mutation.setValues ?? new List<string>()).Any(LooksLikeBinding)) Invalid();
                    if (mutation.numberValue != null) VisitNumber(mutation.numberValue, depth + 1);
                }
                finally { Exit(mutation); }
            }

            private bool EffectBindingIsAllowed(EffectType type, string token)
            {
                if (selector.kind == DynamicTargetKind.None || !IsBindingToken(token)) return false;
                if (type == EffectType.FactionSwitch) return string.Equals(token, TargetToken, StringComparison.Ordinal) && selector.kind == DynamicTargetKind.Unit;
                if (type == EffectType.Spawn || type == EffectType.Relation)
                    return string.Equals(token, OwnerToken, StringComparison.Ordinal) && selector.visibility == DynamicTargetVisibility.Visible;
                return false;
            }

            private static bool HasStateBinding(StateReferenceV1 state) => state != null && (LooksLikeBinding(state.scopeId) || LooksLikeBinding(state.key));

            private bool Enter(object value, int depth)
            {
                if (value == null) return false;
                if (depth > RuleLimits.MaxConditionDepth || nodes >= RuleLimits.MaxConditionNodes || !path.Add(value)) return false;
                nodes++;
                return true;
            }

            private void Exit(object value) { if (value != null) path.Remove(value); }
            private void Invalid() => Add(errors, "DYNAMIC_BINDING_POSITION_INVALID:" + source);
            private void MarkTargetUse(string token)
            {
                if (string.Equals(token, TargetToken, StringComparison.Ordinal) ||
                    string.Equals(token, TileToken, StringComparison.Ordinal) ||
                    string.Equals(token, OwnerToken, StringComparison.Ordinal)) UsesSelectedTarget = true;
            }
        }
    }
}
