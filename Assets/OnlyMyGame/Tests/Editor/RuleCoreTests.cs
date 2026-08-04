using NUnit.Framework;
using OnlyMyGame.Core;
using System.Collections.Generic;
using System.Linq;

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

        // 1단계: 결정론 테스트 — 동일 시드·명령·규칙으로 100회 실행 시 상태 해시가 동일해야 한다.
        [Test] public void SameSeedCommandsAndRulesProduceSameStateHash100Times()
        {
            var first = RunDeterministic(42);
            for (var i = 0; i < 100; i++)
            {
                var next = RunDeterministic(42);
                Assert.AreEqual(first, next, "결정론 위반: " + i + "번째 실행에서 상태 해시가 달라졌습니다.");
            }
        }

        [Test] public void MovementCollisionKeepsOnlyOneUnitOnTargetTile()
        {
            var game = WorldLike(5);
            game.entities.Clear();
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(-1, 0), speed = 2 });
            game.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(0, -1), speed = 2 });
            var target = new HexCoord(0, 0);
            var commands = new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = target },
                new PlannedCommand { factionId = 2, unitId = 2, type = CommandType.Move, target = target }
            };
            var log = new List<string>();
            TurnResolver.Resolve(game, commands, new DeterministicRandom(5), log);
            var onTarget = game.entities.Count(u => u.alive && u.position.Equals(target));
            Assert.AreEqual(1, onTarget, "같은 타일에 두 유닛이 동시에 도착하면 한 유닛만 이동해야 합니다.");
        }

        [Test] public void SimultaneousKillResolvesBothAttacks()
        {
            var game = WorldLike(7);
            game.entities.Clear();
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0), hp = 2 });
            game.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(1, 0), hp = 2 });
            var commands = new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Attack, target = new HexCoord(1, 0) }
            };
            var log = new List<string>();
            TurnResolver.Resolve(game, commands, new DeterministicRandom(7), log);
            Assert.IsFalse(game.entities.First(u => u.id == 1).alive, "동시 공격에서 양쪽 모두 처치되어야 합니다.");
            Assert.IsFalse(game.entities.First(u => u.id == 2).alive, "동시 공격에서 양쪽 모두 처치되어야 합니다.");
        }

        [Test] public void TradeAndAttackConflictResolvesInFixedOrder()
        {
            var game = WorldLike(11);
            game.entities.Clear();
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0) });
            game.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(1, 0), hp = 2 });
            var player = game.factions.First(f => f.id == 1);
            player.resources.food = 5;
            var commands = new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Trade, target = new HexCoord(1, 0) },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Attack, target = new HexCoord(1, 0) }
            };
            var log = new List<string>();
            TurnResolver.Resolve(game, commands, new DeterministicRandom(11), log);
            // 거래·외교가 전투보다 먼저 해결되므로 거래는 성사되고 공격도 실행된다.
            Assert.GreaterOrEqual(player.resources.coin, 7, "거래가 먼저 해결되어 화폐가 증가해야 합니다.");
            Assert.IsFalse(game.entities.First(u => u.id == 2).alive, "전투가 거래 이후 해결되어 적이 처치되어야 합니다.");
        }

        // 2단계: 생활·성장 루프 — 창고·작업장·감시탑·병영 효과
        [Test] public void WarehouseRaisesResourceCap()
        {
            var game = WorldLike(20);
            var player = game.factions.First(f => f.id == 1);
            player.resources.food = 28; // 기본 상한 30에 근접
            game.buildings.Add(new BuildingState { id = 50, factionId = 1, type = BuildingType.Warehouse, level = 2 });
            TurnResolver.BeginPlanning(game, new List<string>());
            Assert.AreEqual(50, player.resources.maxFood, "창고 레벨 합계 2이면 상한이 30+20=50이어야 합니다.");
            Assert.AreEqual(50, player.resources.maxCoin);
        }

        [Test] public void WorkshopProducesIronPerLevel()
        {
            var game = WorldLike(21);
            var player = game.factions.First(f => f.id == 1);
            var before = player.resources.iron;
            game.buildings.Add(new BuildingState { id = 51, factionId = 1, type = BuildingType.Workshop, level = 3 });
            TurnResolver.BeginPlanning(game, new List<string>());
            Assert.AreEqual(before + 3, player.resources.iron, "작업장 레벨 3이면 철 3을 생산해야 합니다.");
        }

        [Test] public void WatchtowerExtendsVisibility()
        {
            var game = WorldLike(22);
            Assert.AreEqual(2, GameRules.VisibilityRange(game, 1));
            game.buildings.Add(new BuildingState { id = 52, factionId = 1, type = BuildingType.Watchtower, level = 2 });
            Assert.AreEqual(4, GameRules.VisibilityRange(game, 1), "감시탑 레벨 합계 2이면 시야 반경이 2+2=4가 되어야 합니다.");
        }

        [Test] public void BarracksIncreasesMaxSp()
        {
            var game = WorldLike(23);
            var player = game.factions.First(f => f.id == 1);
            Assert.AreEqual(10, player.maxSp);
            game.buildings.Add(new BuildingState { id = 53, factionId = 1, type = BuildingType.Barracks, level = 1 });
            TurnResolver.BeginPlanning(game, new List<string>());
            Assert.GreaterOrEqual(player.maxSp, 11, "병영이 있으면 최대 SP가 증가해야 합니다.");
        }

        // 3단계: 규칙 VM 확장 — Spawn, UnlockAction, Schedule, FactionSwitch, 검증기 강화
        [Test] public void VmSpawnsUnitForFaction()
        {
            var game = WorldLike(30);
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0) });
            game.activeRules.Add(new RuleNodeV1 { id = "spawn", name = "소환", trigger = EventType.TurnStart, durationTurns = 30, appliedTurn = 1, effects = new List<EffectNode> { new EffectNode { type = EffectType.Spawn, target = "2", key = "소환병", amount = 1 } } });
            var before = game.entities.Count;
            new RuleVm().Execute(EventType.TurnStart, game, new List<string>());
            Assert.AreEqual(before + 1, game.entities.Count, "Spawn 효과로 유닛이 생성되어야 합니다.");
            Assert.AreEqual(2, game.entities.Last().factionId);
        }

        [Test] public void VmUnlocksDynamicAction()
        {
            var game = WorldLike(31);
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0) });
            game.activeRules.Add(new RuleNodeV1 { id = "unlock", name = "행동 해제", trigger = EventType.TurnStart, durationTurns = 30, appliedTurn = 1, effects = new List<EffectNode> { new EffectNode { type = EffectType.UnlockAction, key = "특별 수렵", amount = 2 } } });
            new RuleVm().Execute(EventType.TurnStart, game, new List<string>());
            Assert.IsTrue(game.dynamicActions.Any(a => a.name == "특별 수렵"), "UnlockAction 효과로 새 행동이 추가되어야 합니다.");
        }

        [Test] public void VmSchedulesDelayedEvent()
        {
            var game = WorldLike(32);
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0) });
            game.activeRules.Add(new RuleNodeV1 { id = "sched", name = "예약", trigger = EventType.TurnStart, durationTurns = 30, appliedTurn = 1, effects = new List<EffectNode> { new EffectNode { type = EffectType.Schedule, key = "TurnEnd", delay = 2, resource = ResourceType.Food, amount = 3 } } });
            new RuleVm().Execute(EventType.TurnStart, game, new List<string>());
            Assert.IsTrue(game.activeRules.Any(r => r.id.StartsWith("scheduled-")), "Schedule 효과로 예약 규칙이 추가되어야 합니다.");
        }

        [Test] public void VmSwitchesUnitFaction()
        {
            var game = WorldLike(33);
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0) });
            game.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(1, 0) });
            game.activeRules.Add(new RuleNodeV1 { id = "switch", name = "전환", trigger = EventType.TurnStart, durationTurns = 30, appliedTurn = 1, effects = new List<EffectNode> { new EffectNode { type = EffectType.FactionSwitch, target = "2", key = "1" } } });
            new RuleVm().Execute(EventType.TurnStart, game, new List<string>());
            Assert.AreEqual(1, game.entities.First(u => u.id == 2).factionId, "FactionSwitch 효과로 유닛 세력이 전환되어야 합니다.");
        }

        [Test] public void ValidatorRejectsExcessiveSpawns()
        {
            var game = WorldLike(34);
            var set = new RuleSetV1 { changes = new List<RuleNodeV1> { new RuleNodeV1 { id = "spam", name = "과잉", trigger = EventType.TurnStart, effects = new List<EffectNode> { new EffectNode { type = EffectType.Spawn, amount = 5 } } } } };
            Assert.IsFalse(RuleValidator.Validate(set, game).valid);
        }

        [Test] public void ValidatorRejectsPlayerFactionSwitch()
        {
            var game = WorldLike(35);
            var set = new RuleSetV1 { changes = new List<RuleNodeV1> { new RuleNodeV1 { id = "bad", name = "나쁜 규칙", trigger = EventType.TurnStart, effects = new List<EffectNode> { new EffectNode { type = EffectType.FactionSwitch, target = "player", key = "2" } } } } };
            Assert.IsFalse(RuleValidator.Validate(set, game).valid);
        }

        [Test] public void InsufficientSpCommandIsRejected()
        {
            var game = WorldLike(13);
            var player = game.factions.First(f => f.id == 1);
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0) });
            game.buildings.Add(new BuildingState { id = 1, factionId = 1, position = new HexCoord(0, 1), type = BuildingType.Headquarters });
            game.planningPrepared = true;
            player.sp = 1;
            player.resources.wood = player.resources.maxWood;
            var commands = new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Build, target = new HexCoord(0, 0) }
            };
            var log = new List<string>();
            TurnResolver.Resolve(game, commands, new DeterministicRandom(13), log);
            Assert.AreEqual(0, game.buildings.Count(b => b.factionId == 1 && b.type != BuildingType.Headquarters), "SP가 부족하면 건설이 실행되지 않아야 합니다.");
        }

        private static string RunDeterministic(int seed)
        {
            var game = WorldLike(seed);
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 1) });
            game.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(3, -1) });
            game.entities.Add(new UnitState { id = 3, factionId = 2, position = new HexCoord(4, -1) });
            game.buildings.Add(new BuildingState { id = 1, factionId = 1, position = new HexCoord(0, 0), type = BuildingType.Headquarters });
            game.activeRules.Add(new RuleNodeV1 { id = "test-rule", name = "테스트 규칙", trigger = EventType.TurnStart, durationTurns = 30, appliedTurn = 1, effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } } });
            var commands = new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Hunt, target = new HexCoord(0, 1) }
            };
            var log = new List<string>();
            for (var turn = 0; turn < 5; turn++)
            {
                TurnResolver.Resolve(game, commands, new DeterministicRandom(seed + turn * 7919), log);
                game.turn++;
                game.luck = new DeterministicRandom(seed + game.turn * 7919).Next(1, 101);
            }
            return StateHash(game);
        }

        private static string StateHash(GameSnapshotV1 game)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var c in JsonLike(game)) { hash ^= c; hash *= 16777619; }
                return hash.ToString("X8");
            }
        }

        private static string JsonLike(GameSnapshotV1 game)
        {
            var parts = new List<string> { game.turn.ToString(), game.luck.ToString() };
            parts.AddRange(game.factions.Select(f => f.id + ":" + f.sp + ":" + f.resources.food + ":" + f.resources.wood + ":" + f.resources.stone + ":" + f.resources.iron + ":" + f.resources.coin + ":" + f.relationToPlayer));
            parts.AddRange(game.entities.Select(u => u.id + ":" + u.factionId + ":" + u.position + ":" + u.hp + ":" + u.alive));
            parts.AddRange(game.buildings.Select(b => b.id + ":" + b.factionId + ":" + b.type + ":" + b.level + ":" + b.hp));
            parts.AddRange(game.map.Select(t => t.position + ":" + t.amount + ":" + t.owner));
            return string.Join("|", parts);
        }

        private static GameSnapshotV1 WorldLike(int seed)
        {
            var random = new DeterministicRandom(seed); var game = new GameSnapshotV1 { seed = seed, turn = 1 }; for (var q = -8; q <= 8; q++) for (var r = System.Math.Max(-8, -q - 8); r <= System.Math.Min(8, -q + 8); r++) game.map.Add(new TileState { position = new HexCoord(q, r), terrain = random.Percent() < 50 ? "숲" : "초원", amount = random.Next(2, 7) }); game.factions.Add(new FactionState { id = 1, kind = FactionKind.Player }); game.factions.Add(new FactionState { id = 2, kind = FactionKind.Skeleton, relationToPlayer = -60 }); return game;
        }
        private static RuleNodeV1 Rule() => new RuleNodeV1 { id = System.Guid.NewGuid().ToString(), name = "규칙", trigger = EventType.TurnStart, effects = new System.Collections.Generic.List<EffectNode> { new EffectNode { type = EffectType.Resource, amount = 1 } } };
    }
}
