using System;
using System.Collections.Generic;
using System.Linq;
using OnlyMyGame.Core;

namespace OnlyMyGame.Runtime
{
    /// <summary>
    /// Maps declarative rules to existing world positions without revealing fogged units.
    /// The ledger remains the complete explanation; these targets provide an immediate,
    /// deterministic world-space answer to "what did that rule affect?".
    /// </summary>
    public static class RuleCuePlanner
    {
        private const int MaximumTargets = 12;

        public static IReadOnlyList<HexCoord> ResolveVisibleTargets(GameSnapshotV1 game, RuleNodeV1 rule, int limit = 3)
        {
            if (game == null || rule == null) return Array.Empty<HexCoord>();
            var map = game.map ?? new List<TileState>();
            var visible = new HashSet<HexCoord>(map.Where(tile => tile != null && tile.visible).Select(tile => tile.position));
            if (visible.Count == 0) return Array.Empty<HexCoord>();

            limit = Math.Max(1, Math.Min(MaximumTargets, limit));
            var candidates = new Dictionary<HexCoord, int>();
            AddConditionTargets(game, rule.condition, candidates, 0);
            foreach (var effect in rule.effects ?? new List<EffectNode>()) AddEffectTargets(game, effect, candidates, 1);

            var reference = PlayerReference(game);
            var resolved = OrderedVisible(candidates, visible, reference, limit);
            if (resolved.Count > 0) return resolved;

            // Global rules and rules whose explicit target is still hidden get a cue at
            // the player's visible command anchors. This explains the change without
            // leaking an unexplored enemy position.
            AddPlayerAnchors(game, candidates, 4);
            return OrderedVisible(candidates, visible, reference, limit);
        }

        private static List<HexCoord> OrderedVisible(
            Dictionary<HexCoord, int> candidates,
            HashSet<HexCoord> visible,
            HexCoord reference,
            int limit)
        {
            return candidates
                .Where(pair => visible.Contains(pair.Key))
                .OrderBy(pair => pair.Value)
                .ThenBy(pair => pair.Key.Distance(reference))
                .ThenBy(pair => pair.Key.q)
                .ThenBy(pair => pair.Key.r)
                .Select(pair => pair.Key)
                .Take(limit)
                .ToList();
        }

        private static void AddConditionTargets(
            GameSnapshotV1 game,
            ConditionNode condition,
            Dictionary<HexCoord, int> candidates,
            int priority)
        {
            if (condition == null) return;
            if (condition.op == CompareOp.HasTag)
            {
                var units = (game.entities ?? new List<UnitState>())
                    .Where(unit => unit != null && unit.alive && (unit.tags ?? new List<string>())
                        .Any(tag => string.Equals(tag, condition.text, StringComparison.OrdinalIgnoreCase)));
                units = SelectUnits(units, condition.left);
                foreach (var unit in units) Add(candidates, unit.position, priority);
            }
            else if (condition.op == CompareOp.OwnerIs)
            {
                var selector = string.IsNullOrWhiteSpace(condition.left) ? condition.text : condition.left;
                if (string.IsNullOrWhiteSpace(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var tile in (game.map ?? new List<TileState>()).Where(tile => tile != null && tile.owner == condition.value))
                        Add(candidates, tile.position, priority);
                }
                else if (string.Equals(selector, "player_tile", StringComparison.OrdinalIgnoreCase))
                {
                    var player = (game.entities ?? new List<UnitState>()).FirstOrDefault(unit => unit != null && unit.alive && unit.factionId == 1);
                    if (player != null) Add(candidates, player.position, priority);
                }
                else if (TryParseHex(selector, out var position)) Add(candidates, position, priority);
            }

            foreach (var child in condition.all ?? new List<ConditionNode>())
                AddConditionTargets(game, child, candidates, priority);
        }

        private static void AddEffectTargets(
            GameSnapshotV1 game,
            EffectNode effect,
            Dictionary<HexCoord, int> candidates,
            int priority)
        {
            if (effect == null) return;
            if (effect.type == EffectType.FactionSwitch)
            {
                if (TryParseSelectorId(effect.target, "unit:", out var selectedUnitId) || int.TryParse(effect.target, out selectedUnitId))
                {
                    var selected = (game.entities ?? new List<UnitState>()).FirstOrDefault(unit => unit != null && unit.id == selectedUnitId);
                    if (selected != null) Add(candidates, selected.position, priority);
                }
                return;
            }

            if (effect.type == EffectType.Spawn)
            {
                if (TryResolveFaction(game, effect.target, out var factionId)) AddFactionAnchors(game, factionId, candidates, priority);
                return;
            }

            if (effect.type == EffectType.Relation)
            {
                foreach (var faction in (game.factions ?? new List<FactionState>()).Where(faction => faction != null && faction.id != 1))
                    AddFactionAnchors(game, faction.id, candidates, priority);
                return;
            }

            if (TryParseSelectorId(effect.target, "unit:", out var unitId))
            {
                var unit = (game.entities ?? new List<UnitState>()).FirstOrDefault(candidate => candidate != null && candidate.id == unitId);
                if (unit != null) Add(candidates, unit.position, priority);
            }
            else if (TryParseSelectorId(effect.target, "faction:", out var selectedFaction))
            {
                AddFactionAnchors(game, selectedFaction, candidates, priority);
            }
            else AddPlayerAnchors(game, candidates, priority + 1);
        }

        private static IEnumerable<UnitState> SelectUnits(IEnumerable<UnitState> units, string selector)
        {
            if (string.IsNullOrWhiteSpace(selector) || string.Equals(selector, "any", StringComparison.OrdinalIgnoreCase)) return units;
            if (string.Equals(selector, "player", StringComparison.OrdinalIgnoreCase)) return units.Where(unit => unit.factionId == 1);
            if (TryParseSelectorId(selector, "unit:", out var unitId)) return units.Where(unit => unit.id == unitId);
            if (TryParseSelectorId(selector, "faction:", out var factionId)) return units.Where(unit => unit.factionId == factionId);
            return Enumerable.Empty<UnitState>();
        }

        private static void AddFactionAnchors(GameSnapshotV1 game, int factionId, Dictionary<HexCoord, int> candidates, int priority)
        {
            foreach (var building in (game.buildings ?? new List<BuildingState>())
                         .Where(building => building != null && building.hp > 0 && building.factionId == factionId))
                Add(candidates, building.position, priority);
            foreach (var unit in (game.entities ?? new List<UnitState>())
                         .Where(unit => unit != null && unit.alive && unit.factionId == factionId))
                Add(candidates, unit.position, priority + 1);
        }

        private static void AddPlayerAnchors(GameSnapshotV1 game, Dictionary<HexCoord, int> candidates, int priority)
        {
            AddFactionAnchors(game, 1, candidates, priority);
        }

        private static HexCoord PlayerReference(GameSnapshotV1 game)
        {
            var headquarters = (game.buildings ?? new List<BuildingState>())
                .FirstOrDefault(building => building != null && building.hp > 0 && building.factionId == 1 && building.type == BuildingType.Headquarters);
            if (headquarters != null) return headquarters.position;
            var player = (game.entities ?? new List<UnitState>()).FirstOrDefault(unit => unit != null && unit.alive && unit.factionId == 1);
            return player?.position ?? default;
        }

        private static bool TryResolveFaction(GameSnapshotV1 game, string selector, out int factionId)
        {
            factionId = 0;
            if (string.Equals(selector, "player", StringComparison.OrdinalIgnoreCase)) factionId = 1;
            else if (!TryParseSelectorId(selector, "faction:", out factionId) && !int.TryParse(selector, out factionId)) return false;
            var resolvedFactionId = factionId;
            return (game.factions ?? new List<FactionState>()).Any(faction => faction != null && faction.id == resolvedFactionId);
        }

        private static bool TryParseSelectorId(string value, string prefix, out int id)
        {
            id = 0;
            return value != null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(value.Substring(prefix.Length), out id);
        }

        private static bool TryParseHex(string selector, out HexCoord position)
        {
            position = default;
            if (string.IsNullOrWhiteSpace(selector)) return false;
            var raw = selector.StartsWith("tile:", StringComparison.OrdinalIgnoreCase) ? selector.Substring(5) : selector;
            var parts = raw.Split(',');
            return parts.Length == 2 && int.TryParse(parts[0], out position.q) && int.TryParse(parts[1], out position.r);
        }

        private static void Add(Dictionary<HexCoord, int> candidates, HexCoord position, int priority)
        {
            if (!candidates.TryGetValue(position, out var existing) || priority < existing) candidates[position] = priority;
        }
    }
}
