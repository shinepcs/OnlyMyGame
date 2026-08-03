using NUnit.Framework;
using OnlyMyGame.Core;
using OnlyMyGame.Runtime;

namespace OnlyMyGame.Tests
{
    public sealed class GameRuntimePlayModeTests
    {
        [Test]
        public void GeneratedWorldHasPlayableFactionsAndVisibility()
        {
            var world = WorldGenerator.Create(20260803);
            Assert.AreEqual(217, world.map.Count);
            Assert.AreEqual(3, world.factions.Count);
            Assert.IsTrue(world.map.Exists(t => t.visible));
            Assert.IsTrue(GameRules.HeadquartersAlive(world));
        }

        [Test]
        public void TurnStartRestoresSpAndAppliesBuildingProduction()
        {
            var world = WorldGenerator.Create(7);
            var player = world.factions.Find(f => f.id == 1);
            player.sp = 0;
            world.buildings.Add(new BuildingState { id = 99, factionId = 1, type = BuildingType.Market });
            var before = player.resources.coin;
            GameRules.StartTurn(world);
            Assert.GreaterOrEqual(player.sp, 3);
            Assert.AreEqual(before + 1, player.resources.coin);
        }
    }
}
