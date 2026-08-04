using NUnit.Framework;
using Newtonsoft.Json;
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

        [Test] public void NegativeSpRulePreservesAPlayableFloorWithoutRefundingSpentSp()
        {
            var game = WorldLike(10);
            var player = game.factions[0];
            game.activeRules.Add(new RuleNodeV1
            {
                id = "sp-floor",
                name = "행동 보장",
                trigger = EventType.TurnStart,
                effects = new System.Collections.Generic.List<EffectNode> { new EffectNode { type = EffectType.Sp, amount = -10 } }
            });

            player.sp = player.maxSp;
            new RuleVm().Execute(EventType.TurnStart, game, new System.Collections.Generic.List<string>());
            Assert.AreEqual(3, player.sp, "규칙이 준비된 세력의 모든 기본 행동을 제거하면 안 됩니다.");

            player.sp = 2;
            game.ruleBudget = new RuleRuntimeBudget();
            new RuleVm().Execute(EventType.TurnStart, game, new System.Collections.Generic.List<string>());
            Assert.AreEqual(2, player.sp, "이미 소비한 SP를 안전 하한이 되돌려 주면 안 됩니다.");
        }

        [Test] public void SnapshotValidatorRejectsMalformedWorldTopology()
        {
            var duplicate = WorldLike(901);
            duplicate.map.Add(new TileState { position = duplicate.map[0].position, terrain = "초원" });
            CollectionAssert.Contains(RuleValidator.ValidateSnapshot(duplicate).errors, "TILE_STATE_INVALID");

            var impossibleOwner = WorldLike(902);
            impossibleOwner.map[0].owner = 999;
            CollectionAssert.Contains(RuleValidator.ValidateSnapshot(impossibleOwner).errors, "TILE_STATE_INVALID");

            var visibleSecret = WorldLike(903);
            visibleSecret.map[0].visible = true;
            visibleSecret.map[0].explored = false;
            CollectionAssert.Contains(RuleValidator.ValidateSnapshot(visibleSecret).errors, "TILE_STATE_INVALID");

            var offMapUnit = WorldLike(904);
            offMapUnit.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(99, 99) });
            CollectionAssert.Contains(RuleValidator.ValidateSnapshot(offMapUnit).errors, "UNIT_STATE_INVALID");

            var invalidBuildingKind = WorldLike(905);
            invalidBuildingKind.buildings.Add(new BuildingState { id = 1, factionId = 1, position = new HexCoord(0, 0), type = (BuildingType)999 });
            CollectionAssert.Contains(RuleValidator.ValidateSnapshot(invalidBuildingKind).errors, "BUILDING_STATE_INVALID");
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

        [Test] public void MovementDependencyChainStopsBeforeFixedOccupantWithoutOverlap()
        {
            var blocked = WorldLike(6);
            blocked.entities.Clear();
            blocked.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0), speed = 2 });
            blocked.entities.Add(new UnitState { id = 2, factionId = 1, position = new HexCoord(1, 0), speed = 2 });
            blocked.entities.Add(new UnitState { id = 3, factionId = 1, position = new HexCoord(2, 0), speed = 2 });
            var log = new List<string>();

            TurnResolver.Resolve(blocked, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = new HexCoord(1, 0) },
                new PlannedCommand { factionId = 1, unitId = 2, type = CommandType.Move, target = new HexCoord(2, 0) }
            }, new DeterministicRandom(6), log);

            Assert.AreEqual(new HexCoord(0, 0), blocked.entities.Single(unit => unit.id == 1).position, "뒤 이동이 막히면 선행 이동도 원래 칸에 남아야 합니다.");
            Assert.AreEqual(new HexCoord(1, 0), blocked.entities.Single(unit => unit.id == 2).position);
            Assert.AreEqual(3, blocked.entities.Where(unit => unit.alive).Select(unit => unit.position).Distinct().Count(), "연쇄 이동 실패로 두 유닛이 한 칸에 겹치면 안 됩니다.");
            StringAssert.Contains("점유된 타일", string.Join("\n", log));

            var open = WorldLike(6);
            open.entities.Clear();
            open.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0), speed = 2 });
            open.entities.Add(new UnitState { id = 2, factionId = 1, position = new HexCoord(1, 0), speed = 2 });
            TurnResolver.Resolve(open, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = new HexCoord(1, 0) },
                new PlannedCommand { factionId = 1, unitId = 2, type = CommandType.Move, target = new HexCoord(2, 0) }
            }, new DeterministicRandom(6), new List<string>());

            Assert.AreEqual(new HexCoord(1, 0), open.entities.Single(unit => unit.id == 1).position, "빈 칸으로 끝나는 정상 이동 체인은 허용해야 합니다.");
            Assert.AreEqual(new HexCoord(2, 0), open.entities.Single(unit => unit.id == 2).position);
        }

        [Test] public void MovementEventsObserveTheFullyCommittedSimultaneousState()
        {
            var game = WorldLike(8);
            game.entities.Clear();
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0), speed = 2 });
            game.entities.Add(new UnitState { id = 2, factionId = 1, position = new HexCoord(1, 0), speed = 2 });
            game.activeRules.Add(new RuleNodeV1
            {
                id = "observe-committed-movement",
                name = "동시 이동 관찰",
                trigger = EventType.Move,
                appliedTurn = game.turn,
                durationTurns = 3,
                effects = new List<EffectNode>
                {
                    new EffectNode { type = EffectType.Spawn, target = "player", key = "atomic-observer", amount = 1 }
                }
            });

            TurnResolver.Resolve(game, new List<PlannedCommand>
            {
                // Resolve B first so the old per-unit event dispatch would observe A at its origin.
                new PlannedCommand { factionId = 1, unitId = 2, type = CommandType.Move, target = new HexCoord(2, 0) },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = new HexCoord(1, 0) }
            }, new DeterministicRandom(8), new List<string>());

            var observers = game.entities
                .Where(unit => (unit.tags ?? new List<string>()).Contains("atomic-observer"))
                .ToList();
            Assert.AreEqual(2, observers.Count, "두 Move 이벤트가 각각 한 번씩 실행되어야 합니다.");
            Assert.IsTrue(observers.All(unit => unit.position.Equals(new HexCoord(1, 0))), "첫 Move 이벤트도 중간 상태가 아니라 전체 이동의 최종 위치를 관찰해야 합니다.");
            Assert.AreEqual(2, GameRules.Progress(game, CommandType.Move.ToString()), "Move 진행도도 이벤트 발행 전에 전체 커밋되어야 합니다.");
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

        [Test] public void CaptureWireValuesAreAppendedWithoutRenumberingExistingEnums()
        {
            Assert.AreEqual(9, (int)CommandType.Dynamic, "저장된 명령 ordinal을 바꾸면 기존 런이 다른 행동으로 복원됩니다.");
            Assert.AreEqual(10, (int)CommandType.Capture);
            Assert.AreEqual(9, (int)EventType.TileEntered, "저장된 VM 트리거 ordinal을 보존해야 합니다.");
            Assert.AreEqual(10, (int)EventType.Capture);
        }

        [Test] public void DisplayedLuckChangesTheDeterministicEqualSpeedCollisionTieBreak()
        {
            var highLuck = CollisionLuckWorld(100);
            var lowLuck = CollisionLuckWorld(1);
            var target = new HexCoord(0, 0);
            var commands = new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = target },
                new PlannedCommand { factionId = 2, unitId = 2, type = CommandType.Move, target = target }
            };

            TurnResolver.Resolve(highLuck, commands, new DeterministicRandom(1), new List<string>());
            TurnResolver.Resolve(lowLuck, commands.Select(command => new PlannedCommand { factionId = command.factionId, unitId = command.unitId, type = command.type, target = command.target }).ToList(), new DeterministicRandom(1), new List<string>());

            Assert.AreEqual(target, highLuck.entities.Single(unit => unit.id == 1).position, "높은 행운은 동속 충돌의 시드 난수 판정에 아군 보정으로 반영되어야 합니다.");
            Assert.AreEqual(target, lowLuck.entities.Single(unit => unit.id == 2).position, "동일 시드에서 표시 행운을 바꾸었을 때 충돌 판정이 실제로 달라져야 합니다.");
        }

        [Test] public void CaptureAfterMovementCommitsTerritoryStatLogAndRuleEvent()
        {
            var game = WorldLike(12);
            var origin = new HexCoord(0, 1);
            var target = new HexCoord(1, 0);
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = origin });
            game.buildings.Add(new BuildingState { id = 1, factionId = 1, position = new HexCoord(0, 0), type = BuildingType.Headquarters });
            game.map.Single(tile => tile.position.Equals(new HexCoord(0, 0))).owner = 1;
            game.map.Single(tile => tile.position.Equals(target)).owner = 2;
            game.activeRules.Add(new RuleNodeV1
            {
                id = "capture-event",
                name = "점령 보급",
                trigger = EventType.Capture,
                appliedTurn = game.turn,
                durationTurns = 3,
                effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Coin, amount = 1 } }
            });
            var player = game.factions.Single(faction => faction.id == 1);
            var initialCoin = player.resources.coin;
            var log = new List<string>();

            TurnResolver.Resolve(game, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = target },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Capture, target = target }
            }, new DeterministicRandom(12), log);

            Assert.AreEqual(target, game.entities.Single(unit => unit.id == 1).position);
            Assert.AreEqual(1, game.map.Single(tile => tile.position.Equals(target)).owner);
            Assert.AreEqual(player.maxSp - GameRules.CommandCost(CommandType.Move) - GameRules.CaptureSpCost, player.sp);
            Assert.AreEqual(1, GameRules.Progress(game, "capture"));
            Assert.AreEqual(initialCoin + 1, player.resources.coin, "Capture 트리거는 소유권과 누적 통계가 커밋된 뒤 한 번 발행되어야 합니다.");
            StringAssert.Contains("영토로 점령", string.Join("\n", log));
        }

        [Test] public void CaptureCannotPassLivingEnemyUnitOrEnemyStronghold()
        {
            var blockedByUnit = CaptureBlockerWorld(false);
            var blockedByStronghold = CaptureBlockerWorld(true);
            var target = new HexCoord(0, 1);

            TurnResolver.Resolve(blockedByUnit, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Capture, target = target }
            }, new DeterministicRandom(13), new List<string>());
            TurnResolver.Resolve(blockedByStronghold, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Capture, target = target }
            }, new DeterministicRandom(14), new List<string>());

            Assert.AreEqual(2, blockedByUnit.map.Single(tile => tile.position.Equals(target)).owner);
            Assert.AreEqual(2, blockedByStronghold.map.Single(tile => tile.position.Equals(target)).owner);
            Assert.AreEqual(0, GameRules.Progress(blockedByUnit, "capture"));
            Assert.AreEqual(0, GameRules.Progress(blockedByStronghold, "capture"));
        }

        [Test] public void EnemyCaptureCommandIsRejectedAndAiNeverInventsCapturePlans()
        {
            var game = WorldLike(15);
            var enemyPosition = new HexCoord(1, 0);
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 1) });
            game.entities.Add(new UnitState { id = 2, factionId = 2, position = enemyPosition });
            game.buildings.Add(new BuildingState { id = 1, factionId = 1, position = new HexCoord(0, 0), type = BuildingType.Headquarters });
            var tile = game.map.Single(candidate => candidate.position.Equals(enemyPosition));
            tile.owner = 0;

            TurnResolver.Resolve(game, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 2, unitId = 2, type = CommandType.Capture, target = enemyPosition }
            }, new DeterministicRandom(15), new List<string>());

            Assert.AreEqual(0, tile.owner);
            Assert.AreEqual(0, GameRules.Progress(game, "capture"));
        }

        [Test] public void TerritoryAllianceAndCaptureProgressCompleteContracts()
        {
            var game = WorldLike(16);
            game.turn = 5;
            foreach (var tile in game.map.Take(3)) tile.owner = 1;
            game.factions.Single(faction => faction.id == 2).relationToPlayer = 60;
            game.factions.Add(new FactionState { id = 3, kind = FactionKind.Neutral, relationToPlayer = 59 });
            game.factions.Add(new FactionState { id = 4, kind = FactionKind.Neutral, relationToPlayer = 80 });
            GameRules.CountAction(game, CommandType.Capture);
            GameRules.CountAction(game, CommandType.Capture);

            Assert.AreEqual(3, GameRules.Progress(game, "territory"));
            Assert.AreEqual(2, GameRules.Progress(game, "alliances"), "동맹은 플레이어를 제외한 관계 60 이상 세력만 세어야 합니다.");
            Assert.AreEqual(2, GameRules.Progress(game, "capture"));
            Assert.IsTrue(GameRules.IsVictoryComplete(game, ProgressContract("territory", 3)));
            Assert.IsTrue(GameRules.IsVictoryComplete(game, ProgressContract("alliances", 2)));
            Assert.IsTrue(GameRules.IsVictoryComplete(game, ProgressContract("capture", 2)));
        }

        [Test] public void SixTurnValidationRejectsKillGoalWhenNoLivingEnemyExists()
        {
            var game = WorldLike(17);
            var result = RuleValidator.Validate(ReachabilityRuleSet(game, "kills", 1), game);

            Assert.IsFalse(result.valid);
            CollectionAssert.Contains(result.errors, "SIX_TURN_SIMULATION_FAILED");
        }

        [Test] public void RuleSetMustPreserveAtLeastOneReachableStoredVictoryContract()
        {
            var game = WorldLike(1701);
            game.turn = 10;
            game.victoryContracts.Add(ProgressContract("kills", 1));
            var withoutRepair = ReachabilityRuleSet(game, "turn", game.turn + 1);
            withoutRepair.victoryContracts.Clear();

            var blocked = RuleValidator.Validate(withoutRepair, game);
            CollectionAssert.Contains(blocked.errors, "SIX_TURN_SIMULATION_FAILED", "도달 불가능한 기존 유일 계약을 그대로 방치하면 안 됩니다.");

            game.victoryContracts.Add(ProgressContract("turn", game.turn + 1));
            var preserved = RuleValidator.Validate(withoutRepair, game);
            Assert.IsTrue(preserved.valid, "도달 가능한 기존 계약이 하나라도 유지되면 규칙 응답을 허용해야 합니다: " + string.Join(", ", preserved.errors));
        }

        [Test] public void SixTurnValidationAppliesPhysicalProgressCeilings()
        {
            var game = WorldLike(18);
            var player = game.factions.Single(faction => faction.id == 1);
            player.maxSp = 1000;
            player.sp = 1000;

            var territory = RuleValidator.Validate(ReachabilityRuleSet(game, "territory", game.map.Count + 1), game);
            var capture = RuleValidator.Validate(ReachabilityRuleSet(game, "capture", game.map.Count + 1), game);
            var coin = RuleValidator.Validate(ReachabilityRuleSet(game, "coin", player.resources.maxCoin + 1), game);
            var buildings = RuleValidator.Validate(ReachabilityRuleSet(game, "buildings", game.map.Count + 1), game);

            Assert.IsTrue(RuleValidator.Validate(ReachabilityRuleSet(game, "territory", 1), game).valid, "territory는 알려진 승리 진행 키여야 합니다.");
            Assert.IsTrue(RuleValidator.Validate(ReachabilityRuleSet(game, "capture", 1), game).valid, "capture는 누적 행동 통계 승리 키여야 합니다.");
            CollectionAssert.Contains(territory.errors, "SIX_TURN_SIMULATION_FAILED");
            CollectionAssert.Contains(capture.errors, "SIX_TURN_SIMULATION_FAILED");
            CollectionAssert.Contains(coin.errors, "SIX_TURN_SIMULATION_FAILED");
            CollectionAssert.Contains(buildings.errors, "SIX_TURN_SIMULATION_FAILED");
        }

        [Test] public void AllianceReachabilityRequiresARecruitableLivingFactionTarget()
        {
            var reachable = WorldLike(19);
            reachable.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 1) });
            reachable.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(1, 0) });
            reachable.factions.Single(faction => faction.id == 2).relationToPlayer = 52;
            var withoutTarget = WorldLike(20);
            withoutTarget.factions.Single(faction => faction.id == 2).relationToPlayer = 52;

            Assert.IsTrue(RuleValidator.Validate(ReachabilityRuleSet(reachable, "alliances", 1), reachable).valid, "관계 52인 생존 세력은 설득 1회로 6턴 안에 동맹이 될 수 있습니다.");
            CollectionAssert.Contains(RuleValidator.Validate(ReachabilityRuleSet(withoutTarget, "alliances", 1), withoutTarget).errors, "SIX_TURN_SIMULATION_FAILED");
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

        [Test]
        public void LegacyDynamicActionDeserializationKeepsImmediateNoTargetDefault()
        {
            var restored = JsonConvert.DeserializeObject<DynamicActionV1>("{\"id\":\"legacy-action\"}");

            Assert.IsNotNull(restored);
            Assert.IsNotNull(restored.targetSelector, "이전 저장 JSON에 새 필드가 없어도 기본 selector 객체가 살아 있어야 합니다.");
            Assert.AreEqual(DynamicTargetKind.None, restored.targetSelector.kind);
            Assert.AreEqual(DynamicTargetOwnership.Any, restored.targetSelector.ownership);
            Assert.AreEqual(DynamicTargetVisibility.Visible, restored.targetSelector.visibility);
            Assert.AreEqual(0, restored.targetSelector.minDistance);
            Assert.AreEqual(0, restored.targetSelector.maxDistance);
            Assert.AreEqual(16, restored.targetSelector.maxCandidates);
            Assert.IsFalse(DynamicActionTargeting.RequiresTarget(restored));
        }

        [Test]
        public void TargetResolverHidesFogEntitiesOrdersByDistanceAndBindsOnlyClickedId()
        {
            var game = DynamicTargetWorld();
            game.entities.Add(new UnitState { id = 8, factionId = 2, position = new HexCoord(1, 0), tags = new List<string> { "marked" } });
            var action = TargetedFactionSwitchAction();
            var validation = RuleValidator.ValidateDynamicActionForRuntime(action, game);
            Assert.IsTrue(validation.valid, string.Join("\n", validation.errors));

            Assert.IsTrue(DynamicActionTargeting.TryResolveCandidates(action, game, 1, out var candidates));
            CollectionAssert.AreEqual(new[] { 8, 9, 2 }, candidates.Select(candidate => candidate.targetId).ToArray(),
                "가까운 공개 대상부터 안정 ID 순으로 제시하고 안개 속 id=3은 포함하지 않아야 합니다.");

            var clicked = DynamicActionTargeting.FindClickedCandidate(candidates, DynamicTargetKind.Unit, 9, new HexCoord(1, 0));
            Assert.IsNotNull(clicked, "같은 타일에 같은 종류 유닛이 겹쳐도 실제로 클릭한 ID를 보존해야 합니다.");
            Assert.IsTrue(DynamicActionTargeting.TryBindExecution(action, clicked, out var condition, out var effects));
            Assert.AreEqual("$target", action.condition.left, "실행 바인딩이 AI 원본 action을 변형하면 안 됩니다.");
            Assert.AreEqual("$target", action.effects[0].target, "effect 원본도 deep clone 경계 밖에서 유지되어야 합니다.");
            Assert.AreEqual("unit:9", condition.left);
            Assert.AreEqual("9", effects[0].target, "position만이 아니라 클릭한 exact unit ID가 effect.target에 바인딩되어야 합니다.");

            var applied = new RuleVm().ApplyValidatedEffects(effects, game, new List<string>(), "선택 대상 테스트");
            Assert.AreEqual(1, applied);
            Assert.AreEqual(1, game.entities.Single(unit => unit.id == 9).factionId);
            Assert.AreEqual(2, game.entities.Single(unit => unit.id == 8).factionId, "같은 타일의 첫 정렬 후보를 클릭 ID 대신 바꾸면 안 됩니다.");
            Assert.AreEqual(2, game.entities.Single(unit => unit.id == 2).factionId, "같은 selector의 다른 공개 후보를 임의로 바꾸면 안 됩니다.");
            Assert.AreEqual(2, game.entities.Single(unit => unit.id == 3).factionId, "숨은 후보는 어떤 경로로도 효과 대상이 되면 안 됩니다.");
        }

        [Test]
        public void TargetValidatorRejectsCandidatesWhoseEffectsCannotBind()
        {
            var game = DynamicTargetWorld();
            var sameFactionSwitch = TargetedFactionSwitchAction();
            sameFactionSwitch.effects[0].key = "2";

            var validation = RuleValidator.ValidateDynamicActionForRuntime(sameFactionSwitch, game);

            Assert.IsFalse(validation.valid);
            Assert.IsTrue(validation.errors.Any(error => error.StartsWith("DYNAMIC_TARGET_UNAVAILABLE", System.StringComparison.Ordinal)),
                "raw 후보가 있어도 모든 FactionSwitch가 동일 세력 no-op이면 실행 가능한 행동으로 승인하면 안 됩니다.");
            Assert.IsTrue(DynamicActionTargeting.TryResolveCandidatePool(sameFactionSwitch, game, 1, out var candidates));
            Assert.IsFalse(candidates.Any(candidate => DynamicActionTargeting.TryBindExecution(sameFactionSwitch, candidate, out _, out _)));
        }

        [Test]
        public void ReceiptRejectsTargetedActionWhenEveryBoundConditionIsFalse()
        {
            var game = DynamicTargetWorld();
            var action = TargetedFactionSwitchAction();
            action.condition = new ConditionNode
            {
                op = CompareOp.Always,
                predicate = new PredicateExpressionV1
                {
                    op = PredicateExpressionOp.NumberGreater,
                    left = new NumberExpressionV1 { op = NumberExpressionOp.Constant, constant = 0 },
                    right = new NumberExpressionV1 { op = NumberExpressionOp.Constant, constant = 1 }
                }
            };

            Assert.IsTrue(DynamicActionTargeting.TryResolveExecutableCandidates(action, game, 1, out var executable));
            Assert.AreEqual(0, executable.Count);
            var receipt = RuleValidator.ValidateDynamicActionForRuntime(action, game);
            Assert.IsFalse(receipt.valid);
            Assert.IsTrue(receipt.errors.Any(error => error.StartsWith("DYNAMIC_TARGET_UNAVAILABLE", System.StringComparison.Ordinal)),
                "수신 검증은 bind/evaluate 성공이 아니라 실제 true 조건 후보를 요구해야 합니다.");
        }

        [Test]
        public void RuleSetRejectsMoreActionsThanCommercialHudCanExpose()
        {
            var game = DynamicTargetWorld();
            var set = new RuleSetV1
            {
                requestId = "commercial-action-cap",
                applyTurn = game.turn,
                koreanSummary = "상용 행동 슬롯 상한을 검증합니다.",
                changes = new List<RuleNodeV1>
                {
                    new RuleNodeV1
                    {
                        id = "safe-rule",
                        name = "안전 규칙",
                        description = "안전한 보급 규칙입니다.",
                        trigger = EventType.TurnStart,
                        appliedTurn = game.turn,
                        effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } }
                    }
                },
                actions = Enumerable.Range(0, RuleLimits.MaxDynamicActionsPerRuleSet + 1)
                    .Select(index =>
                    {
                        var action = TargetedFactionSwitchAction();
                        action.id = "over-cap-" + index;
                        return action;
                    }).ToList()
            };

            var validation = RuleValidator.Validate(set, game);

            CollectionAssert.Contains(validation.errors, "RULESET_ACTION_LIMIT");
        }

        [Test]
        public void StructureValidationAllowsTemporarilyUnavailableTarget()
        {
            var game = DynamicTargetWorld();
            foreach (var tile in game.map) tile.visible = false;
            var action = TargetedFactionSwitchAction();

            var structure = RuleValidator.ValidateDynamicActionStructureForRuntime(action, game);
            var receipt = RuleValidator.ValidateDynamicActionForRuntime(action, game);

            Assert.IsTrue(structure.valid, string.Join("\n", structure.errors));
            Assert.IsFalse(receipt.valid);
            Assert.IsTrue(receipt.errors.Any(error => error.StartsWith("DYNAMIC_TARGET_UNAVAILABLE", System.StringComparison.Ordinal)));
        }

        [Test]
        public void StoredActionSurvivesWhenReceiptOnlyDistanceTargetBecomesHidden()
        {
            var game = DynamicTargetWorld();
            var action = TargetedFactionSwitchAction();
            action.condition = new ConditionNode
            {
                op = CompareOp.Always,
                predicate = new PredicateExpressionV1
                {
                    op = PredicateExpressionOp.NumberGreaterOrEqual,
                    left = new NumberExpressionV1
                    {
                        op = NumberExpressionOp.Distance,
                        selector = "unit:9",
                        secondSelector = "player_unit"
                    },
                    right = new NumberExpressionV1 { op = NumberExpressionOp.Constant, constant = 0 }
                }
            };

            var receipt = RuleValidator.ValidateDynamicActionForRuntime(action, game);
            Assert.IsTrue(receipt.valid, string.Join("\n", receipt.errors));
            game.dynamicActions.Add(action);
            var hiddenTile = game.map.Single(tile => tile.position.Equals(new HexCoord(1, 0)));
            hiddenTile.visible = false;
            hiddenTile.explored = true;

            var structure = RuleValidator.ValidateDynamicActionStructureForRuntime(action, game);
            var currentWorld = RuleValidator.ValidateDynamicActionCurrentWorldForRuntime(action, game);
            var storedSnapshot = RuleValidator.ValidateSnapshot(game);

            Assert.IsTrue(structure.valid, string.Join("\n", structure.errors));
            Assert.IsFalse(currentWorld.valid, "현재 안개 속 exact Distance 참조는 실행 시점에 fail-closed 해야 합니다.");
            Assert.IsTrue(currentWorld.errors.Any(error => error.StartsWith("EXPR_SELECTOR_INVALID", System.StringComparison.Ordinal)));
            Assert.IsTrue(storedSnapshot.valid, string.Join("\n", storedSnapshot.errors));
            Assert.IsFalse(RuleVm.ConditionMatches(action.condition, game));
        }

        [Test]
        public void StoredRuleSurvivesWhenReceiptOnlyDistanceTargetBecomesHidden()
        {
            var game = DynamicTargetWorld();
            var rule = new RuleNodeV1
            {
                id = "stored-distance-rule",
                name = "추적 규칙",
                description = "공개된 표적의 거리를 추적합니다.",
                trigger = EventType.TurnStart,
                appliedTurn = game.turn,
                durationTurns = 3,
                condition = new ConditionNode
                {
                    op = CompareOp.Always,
                    predicate = new PredicateExpressionV1
                    {
                        op = PredicateExpressionOp.NumberGreaterOrEqual,
                        left = new NumberExpressionV1 { op = NumberExpressionOp.Distance, selector = "unit:9", secondSelector = "player_unit" },
                        right = new NumberExpressionV1 { op = NumberExpressionOp.Constant, constant = 0 }
                    }
                },
                effects = new List<EffectNode>
                {
                    new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 }
                }
            };
            var receiptErrors = new List<string>();
            RuleExpressionValidator.ValidateRule(rule, game, "RULE:stored-distance-rule", receiptErrors);
            Assert.AreEqual(0, receiptErrors.Count, string.Join("\n", receiptErrors));
            game.activeRules.Add(rule);
            var hiddenTile = game.map.Single(tile => tile.position.Equals(new HexCoord(1, 0)));
            hiddenTile.visible = false;
            hiddenTile.explored = true;

            var currentErrors = new List<string>();
            RuleExpressionValidator.ValidateRule(rule, game, "RULE:stored-distance-rule", currentErrors);
            var structureErrors = new List<string>();
            RuleExpressionValidator.ValidateRule(rule, game, "SNAPSHOT_RULE:stored-distance-rule", structureErrors, false);

            Assert.IsTrue(currentErrors.Any(error => error.StartsWith("EXPR_SELECTOR_INVALID", System.StringComparison.Ordinal)));
            Assert.AreEqual(0, structureErrors.Count, string.Join("\n", structureErrors));
            var storedSnapshot = RuleValidator.ValidateSnapshot(game);
            Assert.IsTrue(storedSnapshot.valid, string.Join("\n", storedSnapshot.errors));
        }

        [Test]
        public void BindingBudgetChargesSetMutationPayloadBeforeCopying()
        {
            var game = DynamicTargetWorld();
            var action = TargetedFactionSwitchAction();
            var setValues = Enumerable.Range(0, RuleLimits.MaxStateSetElements).Select(index => "value-" + index).ToList();
            action.effects.Add(new EffectNode
            {
                type = EffectType.TypedState,
                stateMutation = new StateMutationV1
                {
                    op = StateMutationOp.Set,
                    state = new StateReferenceV1 { scope = RuleStateScope.Run, key = "payload" },
                    setValues = setValues
                }
            });
            Assert.IsTrue(DynamicActionTargeting.TryResolveCandidatePool(action, game, 1, out var candidates));
            var target = candidates.First(candidate => candidate.targetId == 9);

            var nodeOnlyBudget = 4;
            Assert.IsFalse(DynamicActionTargeting.TryBindExecutionWithinBudget(
                action, target, ref nodeOnlyBudget, out _, out _, out var exhausted));
            Assert.IsTrue(exhausted, "setValues payload를 복사하기 전에 공유 binding 예산을 소진해야 합니다.");
            Assert.AreEqual(0, nodeOnlyBudget);

            var completeBudget = 4 + RuleLimits.MaxStateSetElements;
            Assert.IsTrue(DynamicActionTargeting.TryBindExecutionWithinBudget(
                action, target, ref completeBudget, out _, out var effects, out exhausted));
            Assert.IsFalse(exhausted);
            Assert.AreEqual(0, completeBudget);
            Assert.AreEqual(RuleLimits.MaxStateSetElements, effects[1].stateMutation.setValues.Count);
            effects[1].stateMutation.setValues[0] = "changed";
            Assert.AreEqual("value-0", action.effects[1].stateMutation.setValues[0], "payload clone이 원본 action과 리스트를 공유하면 안 됩니다.");
        }

        [Test]
        public void ExecutableBatchMatchesIndependentResolutionAndRejectsStaleIdentity()
        {
            var game = DynamicTargetWorld();
            var first = TargetedFactionSwitchAction();
            first.id = "batch-first";
            var second = TargetedFactionSwitchAction();
            second.id = "batch-second";
            second.targetSelector.maxCandidates = 1;

            Assert.IsTrue(DynamicActionTargeting.TryResolveExecutableCandidates(first, game, 1, out var independentFirst));
            Assert.IsTrue(DynamicActionTargeting.TryResolveExecutableCandidates(second, game, 1, out var independentSecond));
            Assert.IsTrue(DynamicActionTargeting.TryResolveExecutableCandidatesBatch(
                new[] { first, second }, game, 1, out var batched));
            var batchedFirst = batched[0];
            var batchedSecond = batched[1];
            CollectionAssert.AreEqual(independentFirst.Select(candidate => candidate.targetId), batchedFirst.Select(candidate => candidate.targetId));
            CollectionAssert.AreEqual(independentSecond.Select(candidate => candidate.targetId), batchedSecond.Select(candidate => candidate.targetId));

            game.entities.Add(new UnitState { id = 9, factionId = 2, position = new HexCoord(1, 0), tags = new List<string> { "marked" } });
            Assert.IsFalse(DynamicActionTargeting.TryResolveExecutableCandidatesBatch(
                    new[] { first, second }, game, 1, out _),
                "다음 HUD render는 현재 월드에서 새 index를 만들며 duplicate identity를 fail-closed 해야 합니다.");
        }

        [Test]
        public void ConditionEstimatorStopsAtCallerAllowance()
        {
            var game = DynamicTargetWorld();
            var condition = new ConditionNode { op = CompareOp.HasTag, left = "any", text = "marked" };

            Assert.IsFalse(RuleVm.TryConditionMatchesWithinBudget(condition, game, 3, out _, out var usedWork));
            Assert.AreEqual(4, usedWork, "estimator는 caller allowance를 한 단위 넘는 즉시 중단 신호를 반환해야 합니다.");
        }

        [Test]
        public void CurrentWorldValidationStopsRepeatedSelectorScansAtItsHardBudget()
        {
            var game = DynamicTargetWorld();
            for (var id = 10; id <= 4001; id++)
                game.entities.Add(new UnitState { id = id, factionId = 2, position = new HexCoord(1, 0) });
            var comparisons = Enumerable.Range(0, 8).Select(_ => new PredicateExpressionV1
            {
                op = PredicateExpressionOp.NumberGreaterOrEqual,
                left = new NumberExpressionV1 { op = NumberExpressionOp.CountUnits, selector = "any" },
                right = new NumberExpressionV1 { op = NumberExpressionOp.Constant, constant = 0 }
            }).ToList();
            var action = new DynamicActionV1
            {
                id = "bounded-current-world",
                name = "전장 조사",
                description = "현재 전장의 유닛 수를 제한된 비용으로 조사합니다.",
                spCost = 1,
                cooldown = 1,
                availableTurn = game.turn,
                condition = new ConditionNode
                {
                    op = CompareOp.Always,
                    predicate = new PredicateExpressionV1 { op = PredicateExpressionOp.All, children = comparisons }
                },
                effects = new List<EffectNode>
                {
                    new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 }
                }
            };

            var structure = RuleValidator.ValidateDynamicActionStructureForRuntime(action, game);
            var current = RuleValidator.ValidateDynamicActionCurrentWorldForRuntime(action, game);

            Assert.IsTrue(structure.valid, string.Join("\n", structure.errors));
            Assert.IsFalse(current.valid);
            Assert.IsTrue(current.errors.Any(error => error.StartsWith("EXPR_WORK_LIMIT", System.StringComparison.Ordinal)),
                "current-world 참조 검증은 조건 VM과 같은 caller-bounded scan 예산을 가져야 합니다.");
        }

        [Test]
        public void CurrentWorldValidationBudgetAlsoCoversLegacyOwnerScans()
        {
            var game = new GameSnapshotV1 { turn = 1 };
            game.map = Enumerable.Range(0, RuleLimits.MaxMapTiles)
                .Select(index => new TileState
                {
                    position = index == RuleLimits.MaxMapTiles - 1 ? new HexCoord(0, 0) : new HexCoord(1, 0)
                })
                .ToList();
            game.factions.Add(new FactionState { id = 1, kind = FactionKind.Player });
            const string lastTile = "tile:0,0";
            var action = new DynamicActionV1
            {
                id = "bounded-owner-scan",
                name = "영토 조사",
                description = "영토 참조 비용을 제한합니다.",
                spCost = 1,
                cooldown = 1,
                availableTurn = game.turn,
                condition = new ConditionNode
                {
                    op = CompareOp.Always,
                    all = Enumerable.Range(0, RuleLimits.MaxConditionWorkPerEvaluation / RuleLimits.MaxMapTiles + 1)
                        .Select(_ => new ConditionNode { op = CompareOp.OwnerIs, left = lastTile, value = 0 })
                        .ToList()
                },
                effects = new List<EffectNode>
                {
                    new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 }
                }
            };

            var structure = RuleValidator.ValidateDynamicActionStructureForRuntime(action, game);
            Assert.IsTrue(structure.valid, string.Join("\n", structure.errors));
            var current = RuleValidator.ValidateDynamicActionCurrentWorldForRuntime(action, game);
            Assert.IsFalse(current.valid);
            Assert.IsTrue(current.errors.Any(error => error.StartsWith("CURRENT_WORLD_WORK_LIMIT", System.StringComparison.Ordinal)));
        }

        [Test]
        public void CurrentWorldValidationBudgetAlsoCoversEffectReferenceScans()
        {
            var game = new GameSnapshotV1 { turn = 1 };
            game.map.Add(new TileState { position = new HexCoord(0, 0) });
            game.factions.Add(new FactionState { id = 1, kind = FactionKind.Player });
            game.factions.Add(new FactionState { id = 2, kind = FactionKind.Skeleton });
            for (var id = 1; id <= RuleLimits.MaxEntities; id++)
                game.entities.Add(new UnitState { id = id, factionId = id == 1 ? 1 : 2, position = new HexCoord(0, 0) });
            var action = new DynamicActionV1
            {
                id = "bounded-effect-scan",
                name = "대규모 회유",
                description = "효과 참조 비용을 제한합니다.",
                spCost = 1,
                cooldown = 1,
                availableTurn = game.turn,
                effects = Enumerable.Range(0, RuleLimits.MaxEffectsPerRule)
                    .Select(_ => new EffectNode
                    {
                        type = EffectType.FactionSwitch,
                        target = RuleLimits.MaxEntities.ToString(),
                        key = "1"
                    }).ToList()
            };

            Assert.IsTrue(RuleValidator.ValidateDynamicActionStructureForRuntime(action, game).valid);
            var current = RuleValidator.ValidateDynamicActionCurrentWorldForRuntime(action, game);
            Assert.IsFalse(current.valid);
            Assert.IsTrue(current.errors.Any(error => error.StartsWith("CURRENT_WORLD_WORK_LIMIT", System.StringComparison.Ordinal)));
        }

        [Test]
        public void CandidateLimitIsAppliedAfterTargetConditionFiltering()
        {
            var game = DynamicTargetWorld();
            game.entities.Single(unit => unit.id == 9).tags = new List<string> { "unmarked" };
            for (var id = 10; id <= 40; id++)
                game.entities.Add(new UnitState { id = id, factionId = 2, position = new HexCoord(1, 0), tags = new List<string> { "unmarked" } });
            var action = TargetedFactionSwitchAction();
            action.targetSelector.maxCandidates = 1;

            Assert.IsTrue(DynamicActionTargeting.TryResolveCandidates(action, game, 1, out var rawLimited));
            CollectionAssert.AreEqual(new[] { 9 }, rawLimited.Select(candidate => candidate.targetId).ToArray());
            Assert.IsTrue(DynamicActionTargeting.TryResolveCandidatePool(action, game, 1, out var pool));
            Assert.AreEqual(2, pool[32].targetId, "조건을 통과하는 33번째 raw 후보까지 bounded scan에 남아야 합니다.");

            var executable = pool.Where(candidate =>
            {
                return DynamicActionTargeting.TryBindExecution(action, candidate, out var condition, out _) &&
                       RuleVm.ConditionMatches(condition, game);
            }).Take(action.targetSelector.maxCandidates).ToList();
            CollectionAssert.AreEqual(new[] { 2 }, executable.Select(candidate => candidate.targetId).ToArray(),
                "가장 가까운 raw 후보가 조건에 실패해도 다음 공개 후보를 검사해야 합니다.");
            Assert.IsTrue(DynamicActionTargeting.TryResolveExecutableCandidates(action, game, 1, out var boundedExecutable));
            CollectionAssert.AreEqual(new[] { 2 }, boundedExecutable.Select(candidate => candidate.targetId).ToArray());
        }

        [Test]
        public void CandidatePoolChargesIndexScanAndOnlySortsObservableMatches()
        {
            var game = DynamicTargetWorld();
            for (var id = 10; id < 1010; id++)
                game.entities.Add(new UnitState
                {
                    id = id,
                    factionId = 2,
                    position = new HexCoord(3, 0),
                    tags = new List<string> { "marked" }
                });
            var action = TargetedFactionSwitchAction();
            var indexAndSourceScan = game.map.Count + game.entities.Count + game.buildings.Count + game.factions.Count + game.entities.Count;

            var insufficient = indexAndSourceScan - 1;
            Assert.IsFalse(DynamicActionTargeting.TryResolveCandidatePoolWithinBudget(
                action, game, 1, ref insufficient, out var partial));
            Assert.AreEqual(0, partial.Count, "원본 collection scan을 예약할 수 없으면 partial target page를 노출하면 안 됩니다.");

            var filteredSortBudget = indexAndSourceScan + 16;
            Assert.IsTrue(DynamicActionTargeting.TryResolveCandidatePoolWithinBudget(
                action, game, 1, ref filteredSortBudget, out var candidates));
            CollectionAssert.AreEqual(new[] { 9, 2 }, candidates.Select(candidate => candidate.targetId).ToArray(),
                "숨은 대량 후보는 가시성 필터 뒤의 정렬 예산을 소비하거나 대상 목록에 나타나면 안 됩니다.");
            Assert.GreaterOrEqual(filteredSortBudget, 0);
        }

        [Test]
        public void CandidateIndexFailsClosedOnDuplicateWorldIdentity()
        {
            var game = DynamicTargetWorld();
            var duplicate = game.map[0];
            game.map.Add(new TileState
            {
                position = duplicate.position,
                terrain = duplicate.terrain,
                explored = duplicate.explored,
                visible = duplicate.visible
            });
            var budget = RuleLimits.MaxDynamicTargetResolutionWork;

            Assert.IsFalse(DynamicActionTargeting.TryResolveCandidatePoolWithinBudget(
                TargetedFactionSwitchAction(), game, 1, ref budget, out var candidates));
            Assert.AreEqual(0, candidates.Count);
        }

        [Test]
        public void ReceiptAvailabilitySharesOneBudgetAcrossIncomingActions()
        {
            var game = WorldLike(813);
            foreach (var tile in game.map)
            {
                tile.explored = true;
                tile.visible = true;
            }
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0) });
            for (var id = 2; id <= 4041; id++)
                game.entities.Add(new UnitState { id = id, factionId = 2, position = new HexCoord(0, 0) });

            var actions = new List<DynamicActionV1>();
            for (var actionIndex = 0; actionIndex < 3; actionIndex++)
            {
                var action = TargetedFactionSwitchAction();
                action.id = "shared-budget-" + actionIndex;
                action.condition = new ConditionNode { op = CompareOp.Always };
                action.targetSelector.minDistance = 0;
                action.targetSelector.maxDistance = 0;
                action.effects = Enumerable.Range(0, 15)
                    .Select(_ => new EffectNode { type = EffectType.FactionSwitch, target = "$target", key = "1" })
                    .ToList();
                Assert.IsTrue(RuleValidator.ValidateDynamicActionForRuntime(action, game).valid,
                    "각 action은 독립 수신 예산 안에서는 유효해야 shared response budget 회귀를 검증할 수 있습니다.");
                actions.Add(action);
            }

            var errors = new List<string>();
            DynamicActionTargeting.ValidateTargetAvailability(actions, game, errors, "ACTION");

            Assert.IsTrue(errors.Any(error => error == "DYNAMIC_TARGET_UNAVAILABLE:ACTION:shared-budget-2"),
                "한 응답의 후속 action이 앞 action들이 소비한 전역 후보·binding 예산을 새로 받아서는 안 됩니다.");
        }

        [Test]
        public void TargetValidationRejectsScansThatCannotReachEveryConditionCandidateWithinBudget()
        {
            var game = DynamicTargetWorld();
            foreach (var unit in game.entities.Where(unit => unit.factionId != 1))
                unit.tags = new List<string> { "unmarked" };
            for (var id = 10; id < 210; id++)
                game.entities.Add(new UnitState { id = id, factionId = 2, position = new HexCoord(1, 0), tags = new List<string> { "unmarked" } });
            game.entities.Add(new UnitState { id = 999, factionId = 2, position = new HexCoord(1, 0), tags = new List<string> { "marked" } });
            var action = TargetedFactionSwitchAction();
            action.targetSelector.maxCandidates = 1;

            Assert.IsFalse(DynamicActionTargeting.TryResolveExecutableCandidates(action, game, 1, out var partial));
            Assert.AreEqual(0, partial.Count, "예산으로 전체 후보를 확인하지 못하면 앞쪽 partial target page를 노출하면 안 됩니다.");
            var validation = RuleValidator.ValidateDynamicActionForRuntime(action, game);
            Assert.IsFalse(validation.valid);
            Assert.IsTrue(validation.errors.Any(error => error.StartsWith("DYNAMIC_TARGET_UNAVAILABLE", System.StringComparison.Ordinal)),
                "수신 검증이 런타임과 같은 누적 조건·binding 예산을 적용해야 합니다.");
        }

        [Test]
        public void TargetValidationSearchesPastThirtyTwoPlayerActorsWithinItsOwnBudget()
        {
            var game = WorldLike(812);
            foreach (var tile in game.map)
            {
                tile.explored = true;
                tile.visible = true;
            }
            for (var id = 1; id <= 32; id++)
                game.entities.Add(new UnitState { id = id, factionId = 1, position = new HexCoord(2, 0) });
            game.entities.Add(new UnitState { id = 33, factionId = 1, position = new HexCoord(0, 0) });
            game.entities.Add(new UnitState { id = 100, factionId = 2, position = new HexCoord(0, 0), tags = new List<string> { "marked" } });
            var action = TargetedFactionSwitchAction();
            action.targetSelector.minDistance = 0;
            action.targetSelector.maxDistance = 0;
            action.targetSelector.maxCandidates = 1;

            var validation = RuleValidator.ValidateDynamicActionForRuntime(action, game);

            Assert.IsTrue(validation.valid, string.Join("\n", validation.errors));
            Assert.IsTrue(DynamicActionTargeting.TryResolveExecutableCandidates(action, game, 33, out var candidates));
            CollectionAssert.AreEqual(new[] { 100 }, candidates.Select(candidate => candidate.targetId).ToArray());
        }

        [Test]
        public void UnownedTileOwnerBindingAllowsTileCountsButRejectsFactionCounts()
        {
            var game = DynamicTargetWorld();
            var action = new DynamicActionV1
            {
                id = "unowned-owner",
                name = "미소유지 조사",
                description = "선택한 미소유 타일을 조사합니다.",
                spCost = 1,
                cooldown = 1,
                targetSelector = new DynamicTargetSelectorV1
                {
                    kind = DynamicTargetKind.Tile,
                    ownership = DynamicTargetOwnership.Neutral,
                    visibility = DynamicTargetVisibility.Visible,
                    minDistance = 0,
                    maxDistance = 2,
                    maxCandidates = 8
                },
                condition = new ConditionNode
                {
                    op = CompareOp.Always,
                    predicate = new PredicateExpressionV1
                    {
                        op = PredicateExpressionOp.NumberGreaterOrEqual,
                        left = new NumberExpressionV1 { op = NumberExpressionOp.CountUnits, selector = "$owner" },
                        right = new NumberExpressionV1 { op = NumberExpressionOp.Constant, constant = 0 }
                    }
                },
                effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } }
            };

            Assert.IsTrue(DynamicActionTargeting.TryResolveCandidatePool(action, game, 1, out var candidates));
            Assert.IsTrue(candidates.Count > 0 && candidates.All(candidate => candidate.ownerFactionId == 0));
            Assert.IsFalse(candidates.Any(candidate => DynamicActionTargeting.TryBindExecution(action, candidate, out _, out _)),
                "owner=0은 존재하지 않는 faction:0 CountUnits selector로 바인딩되면 안 됩니다.");

            action.condition.predicate.left.op = NumberExpressionOp.CountTiles;
            Assert.IsTrue(candidates.Any(candidate =>
                    DynamicActionTargeting.TryBindExecution(action, candidate, out var boundCondition, out _) &&
                    RuleVm.ConditionMatches(boundCondition, game)),
                "미소유 타일의 owner:0 CountTiles는 바인딩 후 실제 평가에서도 유효해야 합니다.");
        }

        [Test]
        public void SameFactionSwitchIsANoOpAndNonCanonicalNoneSelectorIsRejected()
        {
            var game = DynamicTargetWorld();
            var effects = new List<EffectNode>
            {
                new EffectNode { type = EffectType.FactionSwitch, target = "9", key = "2" }
            };

            Assert.AreEqual(0, new RuleVm().ApplyValidatedEffects(effects, game, new List<string>(), "동일 세력"));
            Assert.AreEqual(2, game.entities.Single(unit => unit.id == 9).factionId);

            var selector = new DynamicTargetSelectorV1 { maxCandidates = 1 };
            Assert.AreEqual(DynamicTargetKind.None, selector.kind);
            Assert.IsFalse(DynamicActionTargeting.IsSelectorShapeSafe(selector),
                "대상 없음 selector는 의미 없는 필드까지 canonical 기본값이어야 합니다.");

            var exploredUnitSelector = new DynamicTargetSelectorV1
            {
                kind = DynamicTargetKind.Unit,
                ownership = DynamicTargetOwnership.Any,
                visibility = DynamicTargetVisibility.Explored,
                minDistance = 0,
                maxDistance = 4,
                maxCandidates = 16
            };
            Assert.IsFalse(DynamicActionTargeting.IsSelectorShapeSafe(exploredUnitSelector),
                "유닛·건물은 현재 보이는 타일만 후보가 되므로 explored selector를 별도 의미로 허용하면 안 됩니다.");
        }

        [Test]
        public void ExploredHiddenTileRedactsOwnerAndForbidsOwnerBinding()
        {
            var game = DynamicTargetWorld();
            var hiddenTile = game.map.Single(tile => tile.position.Equals(new HexCoord(2, 0)));
            hiddenTile.visible = false;
            hiddenTile.explored = true;
            hiddenTile.owner = 2;
            var action = new DynamicActionV1
            {
                id = "explored-tile",
                name = "기억의 타일",
                description = "탐사한 타일을 다시 선택합니다.",
                spCost = 1,
                cooldown = 1,
                targetSelector = new DynamicTargetSelectorV1
                {
                    kind = DynamicTargetKind.Tile,
                    ownership = DynamicTargetOwnership.Any,
                    visibility = DynamicTargetVisibility.Explored,
                    minDistance = 2,
                    maxDistance = 2,
                    maxCandidates = RuleLimits.MaxDynamicTargetCandidates
                },
                condition = new ConditionNode { op = CompareOp.OwnerIs, left = "$tile", value = 0 },
                effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } }
            };

            Assert.IsTrue(DynamicActionTargeting.TryResolveCandidates(action, game, 1, out var candidates));
            var hidden = candidates.Single(candidate => candidate.tile.Equals(hiddenTile.position));
            Assert.AreEqual(0, hidden.ownerFactionId, "현재 안개 속 타일의 실제 owner=2를 실행 컨텍스트에 노출하면 안 됩니다.");

            action.condition = new ConditionNode
            {
                op = CompareOp.Always,
                predicate = new PredicateExpressionV1
                {
                    op = PredicateExpressionOp.NumberGreaterOrEqual,
                    left = new NumberExpressionV1 { op = NumberExpressionOp.CountUnits, selector = "$owner" },
                    right = new NumberExpressionV1 { op = NumberExpressionOp.Constant, constant = 0 }
                }
            };
            var validation = RuleValidator.ValidateDynamicActionForRuntime(action, game);
            Assert.IsFalse(validation.valid);
            Assert.IsTrue(validation.errors.Any(error => error.StartsWith("DYNAMIC_BINDING_POSITION_INVALID", System.StringComparison.Ordinal)),
                "visibility:explored action에서 $owner는 현재 숨은 소유자의 side channel이므로 금지해야 합니다.");
        }

        [Test]
        public void TargetValidatorRejectsUnusedSelectorsAndStateReferenceBindings()
        {
            var game = DynamicTargetWorld();
            var unused = TargetedFactionSwitchAction();
            unused.condition = new ConditionNode { op = CompareOp.Always };
            unused.effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } };
            var unusedValidation = RuleValidator.ValidateDynamicActionForRuntime(unused, game);
            Assert.IsTrue(unusedValidation.errors.Any(error => error.StartsWith("DYNAMIC_TARGET_UNUSED", System.StringComparison.Ordinal)));

            var stateBound = TargetedFactionSwitchAction();
            stateBound.condition = new ConditionNode
            {
                op = CompareOp.Always,
                predicate = new PredicateExpressionV1
                {
                    op = PredicateExpressionOp.NumberGreater,
                    left = new NumberExpressionV1
                    {
                        op = NumberExpressionOp.State,
                        state = new StateReferenceV1 { scope = RuleStateScope.Unit, scopeId = "$target", key = "mark" }
                    },
                    right = new NumberExpressionV1 { op = NumberExpressionOp.Constant, constant = 0 }
                }
            };
            var stateValidation = RuleValidator.ValidateDynamicActionForRuntime(stateBound, game);
            Assert.IsTrue(stateValidation.errors.Any(error => error.StartsWith("DYNAMIC_BINDING_POSITION_INVALID", System.StringComparison.Ordinal)),
                "StateReference.scopeId 동적 바인딩은 이번 bounded 계약에서 명시적으로 지원하지 않습니다.");
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

        private static GameSnapshotV1 CollisionLuckWorld(int luck)
        {
            var game = WorldLike(1);
            game.luck = luck;
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(-1, 0), speed = 2 });
            game.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(0, -1), speed = 2 });
            game.buildings.Add(new BuildingState { id = 1, factionId = 1, position = new HexCoord(-2, 0), type = BuildingType.Headquarters });
            return game;
        }

        private static GameSnapshotV1 CaptureBlockerWorld(bool useStronghold)
        {
            var game = WorldLike(useStronghold ? 14 : 13);
            var target = new HexCoord(0, 1);
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = target });
            game.buildings.Add(new BuildingState { id = 1, factionId = 1, position = new HexCoord(0, 0), type = BuildingType.Headquarters });
            game.map.Single(tile => tile.position.Equals(target)).owner = 2;
            if (useStronghold) game.buildings.Add(new BuildingState { id = 2, factionId = 2, position = target, type = BuildingType.Headquarters });
            else game.entities.Add(new UnitState { id = 2, factionId = 2, position = target });
            return game;
        }

        private static VictoryContractV1 ProgressContract(string key, int target)
        {
            return new VictoryContractV1
            {
                id = "progress-" + key,
                title = "누적 목표 " + key,
                description = "현재 런의 누적 진행도를 확인합니다.",
                progressKey = key,
                target = target,
                announcedTurn = 1,
                achievableFromTurn = 2,
                minimumTurns = 3
            };
        }

        private static RuleSetV1 ReachabilityRuleSet(GameSnapshotV1 game, string progressKey, int target)
        {
            return new RuleSetV1
            {
                requestId = "reachability-" + progressKey,
                applyTurn = game.turn + 1,
                koreanSummary = "6턴 도달성 검증",
                changes = new List<RuleNodeV1>
                {
                    new RuleNodeV1
                    {
                        id = "supply-" + progressKey,
                        name = "보급 규칙",
                        description = "안전한 식량 보급 규칙입니다.",
                        trigger = EventType.TurnStart,
                        durationTurns = 3,
                        effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } }
                    }
                },
                actions = new List<DynamicActionV1>(),
                victoryContracts = new List<VictoryContractV1>
                {
                    new VictoryContractV1
                    {
                        id = "goal-" + progressKey,
                        title = "도달성 목표",
                        description = "6턴 안에 달성 가능해야 합니다.",
                        progressKey = progressKey,
                        target = target,
                        minimumTurns = 3,
                        announcedTurn = game.turn,
                        achievableFromTurn = game.turn + 1
                    }
                }
            };
        }

        private static GameSnapshotV1 DynamicTargetWorld()
        {
            var game = WorldLike(811);
            foreach (var tile in game.map)
            {
                tile.explored = true;
                tile.visible = tile.position.Distance(new HexCoord(0, 0)) <= 2;
            }
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0), tags = new List<string> { "actor" } });
            game.entities.Add(new UnitState { id = 9, factionId = 2, position = new HexCoord(1, 0), tags = new List<string> { "marked" } });
            game.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(0, 2), tags = new List<string> { "marked" } });
            game.entities.Add(new UnitState { id = 3, factionId = 2, position = new HexCoord(3, 0), tags = new List<string> { "marked" } });
            return game;
        }

        private static DynamicActionV1 TargetedFactionSwitchAction()
        {
            return new DynamicActionV1
            {
                id = "targeted-switch",
                name = "선택 회유",
                description = "선택한 공개 유닛만 아군으로 전환합니다.",
                spCost = 2,
                cooldown = 2,
                availableTurn = 1,
                targetSelector = new DynamicTargetSelectorV1
                {
                    kind = DynamicTargetKind.Unit,
                    ownership = DynamicTargetOwnership.NonPlayer,
                    visibility = DynamicTargetVisibility.Visible,
                    minDistance = 1,
                    maxDistance = 4,
                    maxCandidates = 8
                },
                condition = new ConditionNode { op = CompareOp.HasTag, left = "$target", text = "marked" },
                effects = new List<EffectNode>
                {
                    new EffectNode { type = EffectType.FactionSwitch, target = "$target", key = "1" }
                }
            };
        }

        private static GameSnapshotV1 WorldLike(int seed)
        {
            var random = new DeterministicRandom(seed); var game = new GameSnapshotV1 { seed = seed, turn = 1 }; for (var q = -8; q <= 8; q++) for (var r = System.Math.Max(-8, -q - 8); r <= System.Math.Min(8, -q + 8); r++) game.map.Add(new TileState { position = new HexCoord(q, r), terrain = random.Percent() < 50 ? "숲" : "초원", amount = random.Next(2, 7) }); game.factions.Add(new FactionState { id = 1, kind = FactionKind.Player }); game.factions.Add(new FactionState { id = 2, kind = FactionKind.Skeleton, relationToPlayer = -60 }); return game;
        }
        private static RuleNodeV1 Rule() => new RuleNodeV1 { id = System.Guid.NewGuid().ToString(), name = "규칙", trigger = EventType.TurnStart, effects = new System.Collections.Generic.List<EffectNode> { new EffectNode { type = EffectType.Resource, amount = 1 } } };
    }
}
