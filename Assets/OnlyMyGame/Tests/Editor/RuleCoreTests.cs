using NUnit.Framework;
using OnlyMyGame.Core;

namespace OnlyMyGame.Tests
{
    public class RuleCoreTests
    {
        [Test] public void SameSeedCreatesSame217TileWorld()
        {
            var a = WorldLike(73); var b = WorldLike(73);
            Assert.AreEqual(217, a.map.Count); Assert.AreEqual(a.map[30].terrain, b.map[30].terrain); Assert.AreEqual(a.map[30].amount, b.map[30].amount);
        }
        [Test] public void ValidatorRejectsImmediateVictoryAndTooManyRules()
        {
            var game = WorldLike(1); var set = new RuleSetV1 { changes = new System.Collections.Generic.List<RuleNodeV1> { Rule(), Rule(), Rule(), Rule() }, victoryContracts = new System.Collections.Generic.List<VictoryContractV1> { new VictoryContractV1 { id = "win", target = 1, minimumTurns = 1, achievableFromTurn = game.turn } } };
            Assert.IsFalse(RuleValidator.Validate(set, game).valid);
        }
        [Test] public void VmNeverLetsResourceGoNegative()
        {
            var game = WorldLike(9); game.activeRules.Clear(); game.activeRules.Add(new RuleNodeV1 { id = "safe", name = "안전", trigger = EventType.TurnStart, effects = new System.Collections.Generic.List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = -999 } } });
            new RuleVm().Execute(EventType.TurnStart, game, new System.Collections.Generic.List<string>()); Assert.AreEqual(0, game.factions[0].resources.food);
        }
        private static GameSnapshotV1 WorldLike(int seed)
        {
            var random = new DeterministicRandom(seed); var game = new GameSnapshotV1 { seed = seed, turn = 1 }; for (var q = -8; q <= 8; q++) for (var r = System.Math.Max(-8, -q - 8); r <= System.Math.Min(8, -q + 8); r++) game.map.Add(new TileState { position = new HexCoord(q, r), terrain = random.Percent() < 50 ? "숲" : "초원", amount = random.Next(2, 7) }); game.factions.Add(new FactionState { id = 1, kind = FactionKind.Player }); return game;
        }
        private static RuleNodeV1 Rule() => new RuleNodeV1 { id = System.Guid.NewGuid().ToString(), name = "규칙", trigger = EventType.TurnStart, effects = new System.Collections.Generic.List<EffectNode> { new EffectNode { type = EffectType.Resource, amount = 1 } } };
    }
}
