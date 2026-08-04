using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using OnlyMyGame.Core;
using OnlyMyGame.Runtime;

namespace OnlyMyGame.Tests
{
    public sealed class CommercialRegressionTests
    {
        [Test]
        public void AwaitingRuleSnapshotKeepsTurnStateStableAcrossRestart()
        {
            var game = WorldGenerator.Create(1701);
            var turnState = new StateDefinitionV1
            {
                scope = RuleStateScope.Turn,
                scopeId = "",
                key = "restart_turn_state",
                valueType = RuleStateValueType.Number,
                koreanName = "재시작 턴 상태",
                iconToken = "restart_turn",
                colorHex = "#33AAFF",
                initialNumber = 4
            };
            game.activeRules.Add(new RuleNodeV1
            {
                id = "restart-turn-state-owner",
                name = "재시작 상태 규칙",
                description = "규칙 수신 재시작 안정성을 검증합니다.",
                trigger = EventType.TurnStart,
                appliedTurn = 1,
                durationTurns = 10,
                stateDefinitions = new List<StateDefinitionV1> { turnState },
                effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } }
            });
            Assert.IsTrue(RuleExpressionRuntime.EnsureActiveDefinitions(game));
            Assert.IsTrue(RuleExpressionRuntime.ApplyStateMutation(new StateMutationV1
            {
                op = StateMutationOp.Set,
                state = new StateReferenceV1 { scope = RuleStateScope.Turn, scopeId = "", key = turnState.key },
                numberValue = new NumberExpressionV1 { op = NumberExpressionOp.Constant, constant = 99 }
            }, game));

            game.turn++;
            game.phase = RunPhase.AwaitingRules;
            game.planningPrepared = false;
            var prepare = typeof(GameController).GetMethod("PrepareTypedStateForCurrentTurn", BindingFlags.Static | BindingFlags.NonPublic);
            var normalize = typeof(GameController).GetMethod("NormalizeSnapshot", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(prepare);
            Assert.IsNotNull(normalize);
            Assert.IsTrue((bool)prepare.Invoke(null, new object[] { game }));
            var beforeRestart = JsonConvert.SerializeObject(game);

            var restored = JsonConvert.DeserializeObject<GameSnapshotV1>(beforeRestart);
            Assert.IsTrue((bool)normalize.Invoke(null, new object[] { restored }));
            var afterRestart = JsonConvert.SerializeObject(restored);

            Assert.AreEqual(beforeRestart, afterRestart, "동일 idempotency key로 재시도할 snapshot payload가 재시작 때문에 바뀌면 안 됩니다.");
            var restoredTurnState = restored.typedRuleState.Single(entry => entry.key == turnState.key);
            Assert.AreEqual(game.turn, restoredTurnState.stateTurn);
            Assert.AreEqual(turnState.initialNumber, restoredTurnState.numberValue);
        }

        [Test]
        public void CommandPresentationMatchesCaptureHuntAndCollisionRules()
        {
            Assert.AreEqual("점령", GameController.CommandKorean(CommandType.Capture));
            Assert.AreEqual(2, GameRules.CommandCost(CommandType.Capture));
            StringAssert.Contains("살아있는 적 유닛·거점이 없으면 점령", GameController.ExpectedRange(CommandType.Capture));
            Assert.AreEqual("초원 식량 2 / 숲 3, 행운 70 이상이면 +1", GameController.ExpectedRange(CommandType.Hunt));
            StringAssert.Contains("행운 보정+시드 난수", GameController.ExpectedRange(CommandType.Move));
        }

        [Test]
        public void RuleWorldCuesTargetVisibleAffectedUnitsWithoutLeakingFog()
        {
            var game = PlayableSnapshot(99);
            var visibleEnemy = new UnitState { id = 2, factionId = 2, position = new HexCoord(1, 0), tags = new List<string> { "징표" } };
            var hiddenEnemy = new UnitState { id = 3, factionId = 2, position = new HexCoord(2, 0), tags = new List<string> { "징표" } };
            game.entities.Add(visibleEnemy);
            game.entities.Add(hiddenEnemy);
            foreach (var tile in game.map) tile.visible = tile.position.Equals(new HexCoord(0, 0)) || tile.position.Equals(visibleEnemy.position);
            var rule = new RuleNodeV1
            {
                id = "marked-enemies",
                name = "징표 추적",
                condition = new ConditionNode { op = CompareOp.HasTag, left = "faction:2", text = "징표" },
                effects = new List<EffectNode> { new EffectNode { type = EffectType.Relation, amount = -5 } }
            };

            var targets = RuleCuePlanner.ResolveVisibleTargets(game, rule, 6);

            CollectionAssert.Contains(targets, visibleEnemy.position, "보이는 실제 영향 대상에는 월드 큐가 있어야 합니다.");
            CollectionAssert.DoesNotContain(targets, hiddenEnemy.position, "시야 밖 대상 위치를 월드 큐로 누설하면 안 됩니다.");
        }

        [Test]
        public void GlobalRuleWorldCueFallsBackToVisiblePlayerCommandAnchor()
        {
            var game = PlayableSnapshot(100);
            foreach (var tile in game.map) tile.visible = tile.position.Equals(new HexCoord(0, 0));
            var rule = new RuleNodeV1
            {
                id = "global-supply",
                name = "원정 보급",
                effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 2 } }
            };

            var targets = RuleCuePlanner.ResolveVisibleTargets(game, rule, 3);

            Assert.AreEqual(1, targets.Count);
            Assert.AreEqual(new HexCoord(0, 0), targets[0], "전역 규칙은 보이는 플레이어 본부에 설명 큐를 고정해야 합니다.");
        }

        [Test]
        public void BeginPlanningRunsOnceAndSpFollowsTurnLifecycle()
        {
            var game = PlayableSnapshot(101);
            var player = game.factions.First(f => f.id == 1);
            game.buildings.Add(new BuildingState { id = 2, factionId = 1, position = new HexCoord(1, 0), type = BuildingType.Market, level = 2 });
            game.activeRules.Add(new RuleNodeV1
            {
                id = "planning-food",
                name = "계획 식량",
                trigger = EventType.TurnStart,
                appliedTurn = 1,
                durationTurns = 30,
                effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } }
            });
            player.sp = 1;
            var initialCoin = player.resources.coin;
            var initialFood = player.resources.food;
            var log = new List<string>();

            TurnResolver.BeginPlanning(game, log);
            Assert.IsTrue(game.planningPrepared);
            Assert.AreEqual(player.maxSp, player.sp, "계획 시작 시 SP가 정확히 한 번 최대치로 회복되어야 합니다.");
            Assert.AreEqual(initialCoin + 2, player.resources.coin, "시장 생산은 계획 시작 시 한 번 적용되어야 합니다.");
            Assert.AreEqual(initialFood + 1, player.resources.food, "TurnStart 규칙은 계획 시작 시 한 번 적용되어야 합니다.");

            TurnResolver.BeginPlanning(game, log);
            Assert.AreEqual(initialCoin + 2, player.resources.coin, "같은 턴의 중복 BeginPlanning이 생산을 반복하면 안 됩니다.");
            Assert.AreEqual(initialFood + 1, player.resources.food, "같은 턴의 중복 BeginPlanning이 규칙을 반복하면 안 됩니다.");

            TurnResolver.Resolve(game, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Hunt, target = new HexCoord(0, 1) }
            }, new DeterministicRandom(101), log);
            Assert.IsFalse(game.planningPrepared, "해결이 끝나면 다음 턴 준비를 위해 planningPrepared가 해제되어야 합니다.");
            Assert.AreEqual(player.maxSp - GameRules.CommandCost(CommandType.Hunt), player.sp, "예약 명령의 SP는 해결 중 한 번만 소비되어야 합니다.");

            game.turn++;
            game.phase = RunPhase.Planning;
            var previousCoin = player.resources.coin;
            TurnResolver.BeginPlanning(game, log);
            Assert.AreEqual(player.maxSp, player.sp, "다음 턴 계획 시작에는 SP가 다시 회복되어야 합니다.");
            Assert.AreEqual(previousCoin + 2, player.resources.coin, "다음 턴에는 생산이 다시 정확히 한 번 적용되어야 합니다.");
        }

        [Test]
        public void AttackDamagesOnlyTheEnemyOnTheRequestedHex()
        {
            var game = PlayableSnapshot(202);
            var decoy = new UnitState { id = 2, factionId = 2, position = new HexCoord(1, 0), hp = 5, tags = new List<string> { "미끼" } };
            var requested = new UnitState { id = 3, factionId = 2, position = new HexCoord(0, -1), hp = 5, tags = new List<string> { "지정 대상" } };
            game.entities.Add(decoy);
            game.entities.Add(requested);
            game.luck = 100;

            TurnResolver.Resolve(game, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Attack, target = requested.position }
            }, new DeterministicRandom(202), new List<string>());

            Assert.AreEqual(5, decoy.hp, "가까운 다른 적이 지정 공격을 대신 맞으면 안 됩니다.");
            Assert.AreEqual(2, requested.hp, "행운 100의 지정 공격은 선택한 대상에게 피해 3을 줘야 합니다.");
        }

        [Test]
        public void AttackDoesNotFallbackWhenTheRequestedHexHasNoTarget()
        {
            var game = PlayableSnapshot(203);
            var nearbyEnemy = new UnitState { id = 2, factionId = 2, position = new HexCoord(1, 0), hp = 5 };
            game.entities.Add(nearbyEnemy);
            var log = new List<string>();

            TurnResolver.Resolve(game, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Attack, target = new HexCoord(-1, 0) }
            }, new DeterministicRandom(203), log);

            Assert.AreEqual(5, nearbyEnemy.hp, "빈 좌표를 지정한 공격이 임의의 인접 적에게 보정되면 안 됩니다.");
            StringAssert.Contains("선택한 대상", string.Join("\n", log));
        }

        [Test]
        public void UpgradeRejectsDistantBuildingAndSucceedsWhenAdjacent()
        {
            var distant = PlayableSnapshot(204);
            var distantPlayer = distant.factions.First(f => f.id == 1);
            var distantBuilding = new BuildingState
            {
                id = 2,
                factionId = 1,
                position = new HexCoord(2, -1),
                type = BuildingType.Workshop
            };
            distant.buildings.Add(distantBuilding);
            var distantStone = distantPlayer.resources.stone;
            var distantSp = distantPlayer.sp;

            TurnResolver.Resolve(distant, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Upgrade, target = distantBuilding.position }
            }, new DeterministicRandom(204), new List<string>());

            Assert.AreEqual(1, distantBuilding.level, "거리 2의 건물은 강화 대상이 될 수 없습니다.");
            Assert.AreEqual(12, distantBuilding.hp);
            Assert.AreEqual(distantStone, distantPlayer.resources.stone, "거리가 잘못된 강화는 석재를 소비하면 안 됩니다.");
            Assert.AreEqual(distantSp, distantPlayer.sp, "거리가 잘못된 강화는 검증 단계에서 거부되어 SP도 소비하면 안 됩니다.");

            var adjacent = PlayableSnapshot(205);
            var adjacentPlayer = adjacent.factions.First(f => f.id == 1);
            var headquarters = adjacent.buildings.Single(b => b.type == BuildingType.Headquarters);
            var adjacentStone = adjacentPlayer.resources.stone;
            var adjacentSp = adjacentPlayer.sp;

            TurnResolver.Resolve(adjacent, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Upgrade, target = headquarters.position }
            }, new DeterministicRandom(205), new List<string>());

            Assert.AreEqual(2, headquarters.level, "거리 1의 소유 건물은 강화되어야 합니다.");
            Assert.AreEqual(15, headquarters.hp);
            Assert.AreEqual(adjacentStone - 3, adjacentPlayer.resources.stone);
            Assert.AreEqual(adjacentSp - GameRules.CommandCost(CommandType.Upgrade), adjacentPlayer.sp);
        }

        [Test]
        public void MoveIsReservedBeforeSelfActionAndDuplicateMoveIsRejected()
        {
            var game = PlayableSnapshot(215);
            var player = game.factions.First(f => f.id == 1);
            var origin = game.map.Single(tile => tile.position.Equals(new HexCoord(0, 1)));
            var destination = game.map.Single(tile => tile.position.Equals(new HexCoord(1, 0)));
            origin.resource = ResourceType.Food;
            origin.amount = 1;
            destination.resource = ResourceType.Wood;
            destination.amount = 1;
            var initialWood = player.resources.wood;
            var log = new List<string>();

            TurnResolver.Resolve(game, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Gather, target = origin.position },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = destination.position },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = new HexCoord(-1, 1) }
            }, new DeterministicRandom(215), log);

            Assert.AreEqual(destination.position, game.entities.Single(unit => unit.id == 1).position);
            Assert.AreEqual(1, origin.amount, "명령 목록에 채집이 먼저 있어도 이동 후 타일을 예약해야 합니다.");
            Assert.AreEqual(0, destination.amount);
            Assert.AreEqual(initialWood + 2, player.resources.wood);
            Assert.AreEqual(player.maxSp - GameRules.CommandCost(CommandType.Move) - GameRules.CommandCost(CommandType.Gather), player.sp, "중복 Move는 SP를 소비하면 안 됩니다.");
            StringAssert.Contains("이동 명령을 하나만", string.Join("\n", log));

            var invalidFirst = PlayableSnapshot(221);
            var invalidFirstPlayer = invalidFirst.factions.First(f => f.id == 1);
            TurnResolver.Resolve(invalidFirst, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = new HexCoord(2, -1) },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = new HexCoord(1, 0) }
            }, new DeterministicRandom(221), new List<string>());

            Assert.AreEqual(new HexCoord(1, 0), invalidFirst.entities.Single(unit => unit.id == 1).position, "거리가 잘못된 첫 Move가 뒤의 유효한 Move 예약을 막으면 안 됩니다.");
            Assert.AreEqual(invalidFirstPlayer.maxSp - GameRules.CommandCost(CommandType.Move), invalidFirstPlayer.sp);
        }

        [Test]
        public void FailedProjectedMoveCancelsGatherHuntAndBuildAtTheLoserOrigin()
        {
            var gather = CollisionProjectionSnapshot(216);
            var gatherPlayer = gather.factions.First(f => f.id == 1);
            var gatherOrigin = gather.map.Single(tile => tile.position.Equals(new HexCoord(2, -1)));
            var gatherDestination = gather.map.Single(tile => tile.position.Equals(new HexCoord(1, 0)));
            gatherOrigin.resource = ResourceType.Food;
            gatherOrigin.amount = 1;
            gatherDestination.resource = ResourceType.Wood;
            gatherDestination.amount = 1;
            var gatherWood = gatherPlayer.resources.wood;
            TurnResolver.Resolve(gather, CollisionCommands(CommandType.Gather), new DeterministicRandom(216), new List<string>());
            Assert.AreEqual(new HexCoord(2, -1), gather.entities.Single(unit => unit.id == 2).position);
            Assert.AreEqual(1, gatherOrigin.amount, "충돌에서 패한 유닛이 원래 타일을 채집하면 안 됩니다.");
            Assert.AreEqual(1, gatherDestination.amount, "도착하지 못한 투영 타일도 채집되면 안 됩니다.");
            Assert.AreEqual(gatherWood, gatherPlayer.resources.wood);
            Assert.AreEqual(0, GameRules.Progress(gather, CommandType.Gather.ToString()));

            var hunt = CollisionProjectionSnapshot(217);
            var huntPlayer = hunt.factions.First(f => f.id == 1);
            hunt.map.Single(tile => tile.position.Equals(new HexCoord(2, -1))).terrain = "숲";
            hunt.luck = 100;
            var initialFood = huntPlayer.resources.food;
            TurnResolver.Resolve(hunt, CollisionCommands(CommandType.Hunt), new DeterministicRandom(217), new List<string>());
            Assert.AreEqual(initialFood, huntPlayer.resources.food, "이동 실패 후 원래 타일에서 수렵 보상을 받으면 안 됩니다.");
            Assert.AreEqual(0, GameRules.Progress(hunt, CommandType.Hunt.ToString()));

            var build = CollisionProjectionSnapshot(218);
            var buildPlayer = build.factions.First(f => f.id == 1);
            buildPlayer.resources.wood = 8;
            var initialBuildings = build.buildings.Count;
            TurnResolver.Resolve(build, CollisionCommands(CommandType.Build), new DeterministicRandom(218), new List<string>());
            Assert.AreEqual(initialBuildings, build.buildings.Count, "충돌에서 패한 유닛이 원래 타일에 건설하면 안 됩니다.");
            Assert.AreEqual(8, buildPlayer.resources.wood, "도착하지 못한 건설은 목재를 소비하면 안 됩니다.");
            Assert.AreEqual(0, GameRules.Progress(build, CommandType.Build.ToString()));
        }

        [Test]
        public void AttackDiplomacyAndUpgradeRangesUseTheProjectedMovePosition()
        {
            var attack = PlayableSnapshot(219);
            var attacker = attack.entities.Single(unit => unit.id == 1);
            attacker.position = new HexCoord(-2, 0);
            attack.luck = 100;
            var target = new UnitState { id = 2, factionId = 2, position = new HexCoord(1, 0), hp = 5 };
            attack.entities.Add(target);
            TurnResolver.Resolve(attack, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Attack, target = target.position },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = new HexCoord(-1, 0) }
            }, new DeterministicRandom(219), new List<string>());
            Assert.AreEqual(new HexCoord(-1, 0), attacker.position);
            Assert.AreEqual(2, target.hp, "기존 위치에서 거리 3이어도 투영 위치에서 거리 2면 공격해야 합니다.");

            var diplomacy = PlayableSnapshot(222);
            var trader = diplomacy.entities.Single(unit => unit.id == 1);
            trader.position = new HexCoord(-2, 0);
            var diplomacyPlayer = diplomacy.factions.First(faction => faction.id == 1);
            var neutral = diplomacy.factions.First(faction => faction.id == 2);
            neutral.relationToPlayer = 0;
            diplomacyPlayer.resources.food = 1;
            var initialCoin = diplomacyPlayer.resources.coin;
            diplomacy.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(1, 0) });
            TurnResolver.Resolve(diplomacy, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Trade, target = new HexCoord(1, 0) },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = new HexCoord(-1, 0) }
            }, new DeterministicRandom(222), new List<string>());
            Assert.AreEqual(0, diplomacyPlayer.resources.food);
            Assert.AreEqual(initialCoin + 2, diplomacyPlayer.resources.coin, "투영 위치에서 거리 2인 거래 대상과 거래해야 합니다.");
            Assert.AreEqual(4, neutral.relationToPlayer);

            var upgrade = PlayableSnapshot(220);
            var building = new BuildingState { id = 2, factionId = 1, position = new HexCoord(2, -1), type = BuildingType.Workshop };
            upgrade.buildings.Add(building);
            var upgradePlayer = upgrade.factions.First(f => f.id == 1);
            upgradePlayer.resources.stone = 3;
            TurnResolver.Resolve(upgrade, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Upgrade, target = building.position },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = new HexCoord(1, 0) }
            }, new DeterministicRandom(220), new List<string>());
            Assert.AreEqual(2, building.level, "투영 위치에서 인접한 건물은 강화해야 합니다.");
            Assert.AreEqual(0, upgradePlayer.resources.stone);
        }

        [Test]
        public void TradeReservationsPreventFoodOverspendAndChargeOnlyAcceptedSp()
        {
            var game = PlayableSnapshot(206);
            var player = game.factions.First(f => f.id == 1);
            var neutral = game.factions.First(f => f.id == 2);
            neutral.relationToPlayer = 0;
            player.resources.food = 1;
            var initialCoin = player.resources.coin;
            game.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(0, -1) });
            game.entities.Add(new UnitState { id = 3, factionId = 2, position = new HexCoord(-1, 0) });

            TurnResolver.Resolve(game, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Trade, target = new HexCoord(0, -1) },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Trade, target = new HexCoord(-1, 0) }
            }, new DeterministicRandom(206), new List<string>());

            Assert.AreEqual(0, player.resources.food, "Food 1로 거래 두 건이 모두 실행되면 안 됩니다.");
            Assert.AreEqual(initialCoin + 2, player.resources.coin, "예약을 통과한 거래 한 건의 보상만 지급되어야 합니다.");
            Assert.AreEqual(player.maxSp - GameRules.CommandCost(CommandType.Trade), player.sp, "자원 예약에서 거부된 거래는 SP를 소비하면 안 됩니다.");
            Assert.AreEqual(1, GameRules.Progress(game, CommandType.Trade.ToString()));
            Assert.AreEqual(4, neutral.relationToPlayer);
        }

        [Test]
        public void BuildReservationsUseSelectedCostsAndRejectDuplicateTiles()
        {
            var projected = PlayableSnapshot(207);
            var projectedPlayer = projected.factions.First(f => f.id == 1);
            projectedPlayer.resources.wood = 7;
            projected.entities.Add(new UnitState { id = 2, factionId = 1, position = new HexCoord(1, 0) });

            TurnResolver.Resolve(projected, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Build, target = new HexCoord(0, 1), buildingType = BuildingType.Warehouse },
                new PlannedCommand { factionId = 1, unitId = 2, type = CommandType.Build, target = new HexCoord(1, 0), buildingType = BuildingType.Workshop }
            }, new DeterministicRandom(207), new List<string>());

            Assert.AreEqual(4, projectedPlayer.resources.wood, "첫 Warehouse 비용 3만 소비되어야 합니다.");
            Assert.AreEqual(1, projected.buildings.Count(b => b.factionId == 1 && b.type == BuildingType.Warehouse));
            Assert.AreEqual(0, projected.buildings.Count(b => b.factionId == 1 && b.type == BuildingType.Workshop), "선택한 Workshop 비용 5를 예약할 목재가 없으면 거부되어야 합니다.");
            Assert.AreEqual(projectedPlayer.maxSp - GameRules.CommandCost(CommandType.Build), projectedPlayer.sp, "목재 예약에서 거부된 건설은 SP를 소비하면 안 됩니다.");

            var fullyFunded = PlayableSnapshot(214);
            var fullyFundedPlayer = fullyFunded.factions.First(f => f.id == 1);
            fullyFundedPlayer.resources.wood = 8;
            fullyFundedPlayer.resources.iron = 2;
            fullyFunded.entities.Add(new UnitState { id = 2, factionId = 1, position = new HexCoord(1, 0) });
            TurnResolver.Resolve(fullyFunded, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Build, target = new HexCoord(0, 1), buildingType = BuildingType.Warehouse },
                new PlannedCommand { factionId = 1, unitId = 2, type = CommandType.Build, target = new HexCoord(1, 0), buildingType = BuildingType.Workshop }
            }, new DeterministicRandom(214), new List<string>());

            Assert.AreEqual(0, fullyFundedPlayer.resources.wood, "충분한 목재가 있으면 Warehouse 3과 Workshop 5가 모두 소비되어야 합니다.");
            Assert.AreEqual(0, fullyFundedPlayer.resources.iron, "Workshop의 명시된 철 비용 2가 소비되어야 합니다.");
            Assert.AreEqual(1, fullyFunded.buildings.Count(b => b.factionId == 1 && b.type == BuildingType.Warehouse));
            Assert.AreEqual(1, fullyFunded.buildings.Count(b => b.factionId == 1 && b.type == BuildingType.Workshop));
            Assert.AreEqual(fullyFundedPlayer.maxSp - GameRules.CommandCost(CommandType.Build) * 2, fullyFundedPlayer.sp);

            var duplicate = PlayableSnapshot(208);
            var duplicatePlayer = duplicate.factions.First(f => f.id == 1);
            duplicatePlayer.resources.wood = 8;
            TurnResolver.Resolve(duplicate, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Build, target = new HexCoord(0, 1), buildingType = BuildingType.Warehouse },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Build, target = new HexCoord(0, 1), buildingType = BuildingType.Warehouse }
            }, new DeterministicRandom(208), new List<string>());

            Assert.AreEqual(5, duplicatePlayer.resources.wood);
            Assert.AreEqual(1, duplicate.buildings.Count(b => b.factionId == 1 && b.type == BuildingType.Warehouse), "같은 타일은 한 턴에 한 번만 건설 예약할 수 있어야 합니다.");
            Assert.AreEqual(duplicatePlayer.maxSp - GameRules.CommandCost(CommandType.Build), duplicatePlayer.sp, "중복 타일 건설은 SP를 소비하면 안 됩니다.");
        }

        [Test]
        public void WorkshopBuildRejectsInsufficientOrReservedIronWithoutPartialSpending()
        {
            var insufficient = PlayableSnapshot(215);
            var insufficientPlayer = insufficient.factions.First(faction => faction.id == 1);
            insufficientPlayer.resources.wood = 10;
            insufficientPlayer.resources.iron = 1;

            TurnResolver.Resolve(insufficient, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Build, target = new HexCoord(0, 1), buildingType = BuildingType.Workshop }
            }, new DeterministicRandom(215), new List<string>());

            Assert.AreEqual(10, insufficientPlayer.resources.wood, "철이 부족한 건설은 목재를 부분 소비하면 안 됩니다.");
            Assert.AreEqual(1, insufficientPlayer.resources.iron);
            Assert.AreEqual(insufficientPlayer.maxSp, insufficientPlayer.sp, "자원 검증에서 거부된 건설은 SP도 소비하면 안 됩니다.");
            Assert.AreEqual(0, insufficient.buildings.Count(building => building.factionId == 1 && building.type == BuildingType.Workshop));

            var reserved = PlayableSnapshot(216);
            var reservedPlayer = reserved.factions.First(faction => faction.id == 1);
            reservedPlayer.resources.wood = 10;
            reservedPlayer.resources.iron = 3;
            reserved.entities.Add(new UnitState { id = 2, factionId = 1, position = new HexCoord(1, 0) });

            TurnResolver.Resolve(reserved, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Build, target = new HexCoord(0, 1), buildingType = BuildingType.Workshop },
                new PlannedCommand { factionId = 1, unitId = 2, type = CommandType.Build, target = new HexCoord(1, 0), buildingType = BuildingType.Workshop }
            }, new DeterministicRandom(216), new List<string>());

            Assert.AreEqual(5, reservedPlayer.resources.wood);
            Assert.AreEqual(1, reservedPlayer.resources.iron, "철 3으로 비용 2인 작업장 두 채를 동시에 예약할 수 없어야 합니다.");
            Assert.AreEqual(1, reserved.buildings.Count(building => building.factionId == 1 && building.type == BuildingType.Workshop));
            Assert.AreEqual(reservedPlayer.maxSp - GameRules.CommandCost(CommandType.Build), reservedPlayer.sp, "철 예약에서 거부된 두 번째 건설은 SP를 소비하면 안 됩니다.");
        }

        [Test]
        public void LegacyBuildCommandWithoutTypeDefaultsToWarehouse()
        {
            var game = PlayableSnapshot(217);
            var player = game.factions.First(faction => faction.id == 1);
            player.resources.wood = 3;
            var legacyJson = JsonConvert.SerializeObject(new
            {
                factionId = 1,
                unitId = 1,
                type = CommandType.Build,
                target = new HexCoord(0, 1)
            });
            var legacyCommand = JsonConvert.DeserializeObject<PlannedCommand>(legacyJson);

            Assert.IsNotNull(legacyCommand);
            Assert.AreEqual(BuildingType.Warehouse, legacyCommand.buildingType, "buildingType 필드가 없는 이전 명령은 안전한 창고로 해석되어야 합니다.");
            TurnResolver.Resolve(game, new List<PlannedCommand> { legacyCommand }, new DeterministicRandom(217), new List<string>());

            Assert.AreEqual(0, player.resources.wood);
            Assert.AreEqual(1, game.buildings.Count(building => building.factionId == 1 && building.type == BuildingType.Warehouse));
        }

        [Test]
        public void BuildReservationHonorsTheGlobalBuildingCapacity()
        {
            var game = PlayableSnapshot(209);
            var player = game.factions.First(f => f.id == 1);
            player.resources.wood = player.resources.maxWood;
            for (var index = game.buildings.Count; index < RuleLimits.MaxBuildings; index++)
            {
                game.buildings.Add(new BuildingState
                {
                    id = index + 1,
                    factionId = 2,
                    position = new HexCoord(index + 10, 0),
                    type = BuildingType.Watchtower
                });
            }
            var initialWood = player.resources.wood;

            TurnResolver.Resolve(game, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Build, target = new HexCoord(0, 1) }
            }, new DeterministicRandom(209), new List<string>());

            Assert.AreEqual(RuleLimits.MaxBuildings, game.buildings.Count);
            Assert.AreEqual(initialWood, player.resources.wood, "건물 정원이 찬 상태의 건설은 목재를 소비하면 안 됩니다.");
            Assert.AreEqual(player.maxSp, player.sp, "건물 정원이 찬 상태의 건설은 SP를 소비하면 안 됩니다.");
        }

        [Test]
        public void UpgradeReservationsPreventStoneOverspendAndDuplicateTargets()
        {
            var overspend = PlayableSnapshot(210);
            var overspendPlayer = overspend.factions.First(f => f.id == 1);
            overspendPlayer.resources.stone = 3;
            var headquarters = overspend.buildings.Single(b => b.type == BuildingType.Headquarters);
            var workshop = new BuildingState { id = 2, factionId = 1, position = new HexCoord(1, 0), type = BuildingType.Workshop };
            overspend.buildings.Add(workshop);

            TurnResolver.Resolve(overspend, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Upgrade, target = headquarters.position },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Upgrade, target = workshop.position }
            }, new DeterministicRandom(210), new List<string>());

            Assert.AreEqual(2, headquarters.level);
            Assert.AreEqual(1, workshop.level, "Stone 3으로 서로 다른 건물 두 채를 모두 강화하면 안 됩니다.");
            Assert.AreEqual(0, overspendPlayer.resources.stone);
            Assert.AreEqual(overspendPlayer.maxSp - GameRules.CommandCost(CommandType.Upgrade), overspendPlayer.sp, "석재 예약에서 거부된 강화는 SP를 소비하면 안 됩니다.");

            var duplicate = PlayableSnapshot(211);
            var duplicatePlayer = duplicate.factions.First(f => f.id == 1);
            duplicatePlayer.resources.stone = 6;
            var duplicateHeadquarters = duplicate.buildings.Single(b => b.type == BuildingType.Headquarters);
            TurnResolver.Resolve(duplicate, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Upgrade, target = duplicateHeadquarters.position },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Upgrade, target = duplicateHeadquarters.position }
            }, new DeterministicRandom(211), new List<string>());

            Assert.AreEqual(2, duplicateHeadquarters.level, "같은 건물은 한 턴에 한 번만 강화 예약할 수 있어야 합니다.");
            Assert.AreEqual(3, duplicatePlayer.resources.stone);
            Assert.AreEqual(duplicatePlayer.maxSp - GameRules.CommandCost(CommandType.Upgrade), duplicatePlayer.sp, "중복 강화는 SP를 소비하면 안 됩니다.");
        }

        [Test]
        public void HireReservationsPreventCoinOverspendAndDuplicateNeutralTargets()
        {
            var overspend = PlayableSnapshot(212);
            var overspendPlayer = overspend.factions.First(f => f.id == 1);
            overspendPlayer.resources.coin = 3;
            overspend.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(0, -1), tags = new List<string>() });
            overspend.entities.Add(new UnitState { id = 3, factionId = 2, position = new HexCoord(-1, 0), tags = new List<string>() });
            var initialEntityCount = overspend.entities.Count;

            TurnResolver.Resolve(overspend, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Hire, target = new HexCoord(0, -1) },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Hire, target = new HexCoord(-1, 0) }
            }, new DeterministicRandom(212), new List<string>());

            Assert.AreEqual(1, overspend.entities.Count(u => u.factionId == 1 && u.id != 1), "Coin 3으로 중립 유닛 두 명을 모두 고용하면 안 됩니다.");
            Assert.AreEqual(initialEntityCount, overspend.entities.Count, "고용은 새 엔티티를 생성하지 않고 기존 중립 유닛의 소속만 바꿔야 합니다.");
            Assert.AreEqual(0, overspendPlayer.resources.coin);
            Assert.AreEqual(overspendPlayer.maxSp - GameRules.CommandCost(CommandType.Hire), overspendPlayer.sp, "코인 예약에서 거부된 고용은 SP를 소비하면 안 됩니다.");

            var duplicate = PlayableSnapshot(213);
            var duplicatePlayer = duplicate.factions.First(f => f.id == 1);
            duplicatePlayer.resources.coin = 6;
            duplicate.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(0, -1), tags = new List<string>() });
            duplicate.entities.Add(new UnitState { id = 3, factionId = 1, position = new HexCoord(-1, 0), tags = new List<string>() });
            TurnResolver.Resolve(duplicate, new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Hire, target = new HexCoord(0, -1) },
                new PlannedCommand { factionId = 1, unitId = 3, type = CommandType.Hire, target = new HexCoord(0, -1) }
            }, new DeterministicRandom(213), new List<string>());

            Assert.AreEqual(1, duplicate.entities.Count(u => u.id == 2 && u.factionId == 1));
            Assert.AreEqual(3, duplicatePlayer.resources.coin, "같은 중립 유닛의 중복 고용은 코인을 한 번만 소비해야 합니다.");
            Assert.AreEqual(duplicatePlayer.maxSp - GameRules.CommandCost(CommandType.Hire), duplicatePlayer.sp, "중복 고용은 SP를 소비하면 안 됩니다.");
        }

        [Test]
        public void ValidatorRejectsAggregateSpawnBudgetAcrossRulesAndActions()
        {
            var game = PlayableSnapshot(303);
            var set = ValidRuleSet(game);
            set.changes[0].effects = new List<EffectNode>
            {
                new EffectNode { type = EffectType.Spawn, target = "2", key = "규칙 소환", amount = 3 }
            };
            set.actions.Add(new DynamicActionV1
            {
                id = "spawn-action",
                name = "추가 소환",
                description = "행동에서도 유닛을 소환합니다.",
                spCost = 1,
                cooldown = 1,
                effects = new List<EffectNode>
                {
                    new EffectNode { type = EffectType.Spawn, target = "2", key = "행동 소환", amount = 2 }
                }
            });

            var result = RuleValidator.Validate(set, game);
            Assert.IsFalse(result.valid);
            CollectionAssert.Contains(result.errors, "SPAWN_BUDGET_EXCEEDED", "개별 효과가 유효해도 선언된 Spawn 총합 5는 턴 예산 4를 넘습니다.");
        }

        [Test]
        public void FirstRealAiTurnRequiresReachableEighteenTurnVictoryContract()
        {
            var game = PlayableSnapshot(305);
            game.turn = 2;

            var missing = RuleValidator.Validate(ValidRuleSet(game), game);
            Assert.IsFalse(missing.valid);
            CollectionAssert.Contains(missing.errors, "VICTORY_CONTRACT_REQUIRED");

            var tooShortSet = ValidRuleSet(game);
            tooShortSet.victoryContracts.Add(NewVictoryContract("short-first-contract", game.turn, 17, game.turn + 6));
            var tooShort = RuleValidator.Validate(tooShortSet, game);
            Assert.IsFalse(tooShort.valid);
            CollectionAssert.Contains(tooShort.errors, "FIRST_VICTORY_TOO_EARLY");

            var validSet = ValidRuleSet(game);
            validSet.victoryContracts.Add(NewVictoryContract("first-contract", game.turn, RuleValidator.MinimumFirstVictoryTurns, game.turn + 6));
            var valid = RuleValidator.Validate(validSet, game);
            Assert.IsTrue(valid.valid, string.Join("\n", valid.errors));
        }

        [Test]
        public void FullActiveRuleCollectionAllowsReplacementButRejectsProjectedAddition()
        {
            var game = PlayableSnapshot(306);
            for (var index = 0; index < RuleLimits.MaxActiveRules; index++)
                game.activeRules.Add(StoredRule("active-rule-" + index));

            var additionSet = ValidRuleSet(game);
            var addition = RuleValidator.Validate(additionSet, game);
            Assert.IsFalse(addition.valid);
            CollectionAssert.Contains(addition.errors, "ACTIVE_RULE_LIMIT");

            var replacementSet = ValidRuleSet(game);
            replacementSet.changes[0].id = game.activeRules[0].id;
            var replacement = RuleValidator.Validate(replacementSet, game);
            Assert.IsTrue(replacement.valid, string.Join("\n", replacement.errors));
        }

        [Test]
        public void FullDynamicActionCollectionAllowsReplacementButRejectsProjectedAddition()
        {
            var game = PlayableSnapshot(307);
            for (var index = 0; index < RuleLimits.MaxDynamicActions; index++)
                game.dynamicActions.Add(ValidDynamicAction("stored-action-" + index, game.turn));

            var additionSet = ValidRuleSet(game);
            additionSet.actions.Add(ValidDynamicAction("overflow-action", game.turn));
            var addition = RuleValidator.Validate(additionSet, game);
            Assert.IsFalse(addition.valid);
            CollectionAssert.Contains(addition.errors, "DYNAMIC_ACTION_LIMIT");

            var replacementSet = ValidRuleSet(game);
            replacementSet.actions.Add(ValidDynamicAction(game.dynamicActions[0].id, game.turn));
            var replacement = RuleValidator.Validate(replacementSet, game);
            Assert.IsTrue(replacement.valid, string.Join("\n", replacement.errors));
        }

        [Test]
        public void FullVictoryContractCollectionAllowsReplacementButRejectsProjectedAddition()
        {
            var game = PlayableSnapshot(308);
            game.turn = 5;
            for (var index = 0; index < RuleLimits.MaxVictoryContracts; index++)
                game.victoryContracts.Add(StoredVictoryContract("stored-contract-" + index, 10));

            var additionSet = ValidRuleSet(game);
            additionSet.victoryContracts.Add(NewVictoryContract("overflow-contract", game.turn, 3, game.turn + 6));
            var addition = RuleValidator.Validate(additionSet, game);
            Assert.IsFalse(addition.valid);
            CollectionAssert.Contains(addition.errors, "VICTORY_LIMIT");

            var replacementSet = ValidRuleSet(game);
            replacementSet.victoryContracts.Add(CloneVictoryContract(game.victoryContracts[0]));
            var replacement = RuleValidator.Validate(replacementSet, game);
            Assert.IsTrue(replacement.valid, string.Join("\n", replacement.errors));
        }

        [Test]
        public void VictoryReplacementRequiresTimelyPriorWarning()
        {
            var earlyWarningGame = PlayableSnapshot(309);
            earlyWarningGame.turn = 5;
            var earlyContract = StoredVictoryContract("warning-contract", 24, RuleValidator.MinimumFirstVictoryTurns);
            earlyWarningGame.victoryContracts.Add(earlyContract);
            var earlyWarningSet = ValidRuleSet(earlyWarningGame);
            var earlyWarning = CloneVictoryContract(earlyContract);
            earlyWarning.replaceWarningTurn = earlyWarningGame.turn;
            earlyWarningSet.victoryContracts.Add(earlyWarning);
            var earlyResult = RuleValidator.Validate(earlyWarningSet, earlyWarningGame);
            Assert.IsFalse(earlyResult.valid);
            CollectionAssert.Contains(earlyResult.errors, "VICTORY_WARNING_TOO_EARLY:warning-contract");

            var missingWarningGame = PlayableSnapshot(310);
            missingWarningGame.turn = 19;
            missingWarningGame.victoryContracts.Add(StoredVictoryContract("warning-contract", 24, RuleValidator.MinimumFirstVictoryTurns));
            var missingWarningSet = ValidRuleSet(missingWarningGame);
            missingWarningSet.victoryContracts.Add(NewVictoryContract("warning-contract", missingWarningGame.turn, RuleValidator.MinimumFirstVictoryTurns, 25));
            var missingWarning = RuleValidator.Validate(missingWarningSet, missingWarningGame);
            Assert.IsFalse(missingWarning.valid);
            CollectionAssert.Contains(missingWarning.errors, "VICTORY_REPLACEMENT_NOT_WARNED:warning-contract");

            var timelyWarningGame = PlayableSnapshot(311);
            timelyWarningGame.turn = 18;
            var timelyContract = StoredVictoryContract("warning-contract", 24, RuleValidator.MinimumFirstVictoryTurns);
            timelyWarningGame.victoryContracts.Add(timelyContract);
            var timelyWarningSet = ValidRuleSet(timelyWarningGame);
            var timelyWarning = CloneVictoryContract(timelyContract);
            timelyWarning.replaceWarningTurn = timelyWarningGame.turn;
            timelyWarningSet.victoryContracts.Add(timelyWarning);
            var timelyResult = RuleValidator.Validate(timelyWarningSet, timelyWarningGame);
            Assert.IsTrue(timelyResult.valid, string.Join("\n", timelyResult.errors));

            var warnedReplacementGame = PlayableSnapshot(312);
            warnedReplacementGame.turn = 19;
            var warnedContract = StoredVictoryContract("warning-contract", 24, RuleValidator.MinimumFirstVictoryTurns);
            warnedContract.replaceWarningTurn = 18;
            warnedReplacementGame.victoryContracts.Add(warnedContract);
            var warnedReplacementSet = ValidRuleSet(warnedReplacementGame);
            warnedReplacementSet.victoryContracts.Add(NewVictoryContract("warning-contract", warnedReplacementGame.turn, RuleValidator.MinimumFirstVictoryTurns, 25));
            var warnedReplacement = RuleValidator.Validate(warnedReplacementSet, warnedReplacementGame);
            Assert.IsTrue(warnedReplacement.valid, string.Join("\n", warnedReplacement.errors));
        }

        [Test]
        public void RuleVmCapsCombinedSpawnEffectsForTheWholeTurn()
        {
            var game = PlayableSnapshot(304);
            game.activeRules.Add(SpawnRule("spawn-a", 3));
            game.activeRules.Add(SpawnRule("spawn-b", 3));
            var initialCount = game.entities.Count;
            var log = new List<string>();
            var vm = new RuleVm();

            vm.Execute(EventType.TurnEnd, game, log);
            vm.Execute(EventType.TurnEnd, game, log);

            Assert.AreEqual(RuleLimits.MaxRuleSpawnsPerTurn, game.entities.Count - initialCount, "여러 규칙과 여러 dispatch를 합쳐도 턴당 Spawn 총량을 넘으면 안 됩니다.");
            Assert.AreEqual(RuleLimits.MaxRuleSpawnsPerTurn, game.ruleBudget.spawnedEntities);
            StringAssert.Contains("생성 엔티티 한도", string.Join("\n", log));
        }

        [Test]
        public void PartialSpawnReturnsNonFullAppliedCountForAtomicDynamicRollback()
        {
            var game = PlayableSnapshot(313);
            game.ruleBudget.turn = game.turn;
            game.ruleBudget.spawnedEntities = RuleLimits.MaxRuleSpawnsPerTurn - 1;
            var initialEntities = game.entities.Count;
            var log = new List<string>();

            var applied = new RuleVm().ApplyValidatedEffects(new[]
            {
                new EffectNode { type = EffectType.Spawn, target = "2", key = "부분 소환", amount = 2 }
            }, game, log, "부분 소환 행동");

            Assert.AreEqual(0, applied, "요청량 일부만 생성된 Spawn 효과는 완전 적용으로 계산하면 안 됩니다.");
            Assert.AreEqual(1, game.entities.Count - initialEntities, "남은 턴 예산만큼의 안전한 부분 생성은 유지되어야 합니다.");
            Assert.AreEqual(RuleLimits.MaxRuleSpawnsPerTurn, game.ruleBudget.spawnedEntities);
            StringAssert.Contains("생성 엔티티 한도", string.Join("\n", log));
        }

        [Test]
        public void ValidatorNeverThrowsForNullOrAdversarialGraphs()
        {
            var game = PlayableSnapshot(404);
            RuleValidationResult result = null;
            Assert.DoesNotThrow(() => result = RuleValidator.Validate(null, game));
            CollectionAssert.Contains(result.errors, "RULESET_NULL");

            Assert.DoesNotThrow(() => result = RuleValidator.Validate(new RuleSetV1(), null));
            CollectionAssert.Contains(result.errors, "SNAPSHOT_NULL");

            var malformedSnapshot = PlayableSnapshot(405);
            malformedSnapshot.map = null;
            malformedSnapshot.entities = null;
            malformedSnapshot.buildings = null;
            malformedSnapshot.factions = new List<FactionState> { null };
            malformedSnapshot.actionStats = null;
            malformedSnapshot.activeRules = null;
            malformedSnapshot.dynamicActions = null;
            malformedSnapshot.victoryContracts = null;
            malformedSnapshot.ruleState = null;
            var malformedSet = new RuleSetV1
            {
                schemaVersion = null,
                requestId = new string('x', RuleLimits.MaxIdentifierLength + 1),
                changes = new List<RuleNodeV1> { null },
                actions = new List<DynamicActionV1> { null },
                victoryContracts = new List<VictoryContractV1> { null }
            };
            Assert.DoesNotThrow(() => result = RuleValidator.Validate(malformedSet, malformedSnapshot));
            Assert.IsFalse(result.valid);
            Assert.IsNotEmpty(result.errors);

            var cyclicCondition = new ConditionNode { op = CompareOp.Always, all = new List<ConditionNode>() };
            cyclicCondition.all.Add(cyclicCondition);
            var cyclicSet = ValidRuleSet(game);
            cyclicSet.changes[0].condition = cyclicCondition;
            Assert.DoesNotThrow(() => result = RuleValidator.Validate(cyclicSet, game));
            Assert.IsTrue(result.errors.Any(error => error.StartsWith("CONDITION_CYCLE", StringComparison.Ordinal)));
        }

        [Test]
        public void OutcomeEvaluationHandlesVictoryDefeatAndLastSurvivor()
        {
            var victory = PlayableSnapshot(505);
            victory.turn = 5;
            victory.victoryContracts.Add(new VictoryContractV1
            {
                id = "turn-five",
                title = "다섯 번째 새벽",
                description = "5턴까지 생존하세요.",
                progressKey = "turn",
                target = 5,
                announcedTurn = 1,
                achievableFromTurn = 2,
                minimumTurns = 3
            });
            victory.outcome = GameRules.EvaluateOutcome(victory);
            Assert.AreEqual(RunOutcome.Victory, victory.outcome);
            Assert.AreEqual("turn-five", victory.completedContractId);
            victory.buildings.First(b => b.type == BuildingType.Headquarters).hp = 0;
            victory.entities.First(u => u.factionId == 1).alive = false;
            Assert.AreEqual(RunOutcome.Victory, GameRules.EvaluateOutcome(victory), "확정된 승리는 이후 상태 변화로 뒤집히면 안 됩니다.");

            var defeat = PlayableSnapshot(506);
            defeat.buildings.First(b => b.type == BuildingType.Headquarters).hp = 0;
            defeat.entities.First(u => u.factionId == 1).alive = false;
            defeat.outcome = GameRules.EvaluateOutcome(defeat);
            Assert.AreEqual(RunOutcome.Defeat, defeat.outcome);
            defeat.buildings.First(b => b.type == BuildingType.Headquarters).hp = 12;
            defeat.entities.First(u => u.factionId == 1).alive = true;
            Assert.AreEqual(RunOutcome.Defeat, GameRules.EvaluateOutcome(defeat), "확정된 패배는 상태를 복구해도 같은 런에서 뒤집히면 안 됩니다.");

            var survivor = PlayableSnapshot(507);
            survivor.buildings.First(b => b.type == BuildingType.Headquarters).hp = 0;
            Assert.AreEqual(RunOutcome.Ongoing, GameRules.EvaluateOutcome(survivor), "본부가 무너져도 생존 아군이 있으면 즉시 패배가 아니어야 합니다.");
        }

        [Test]
        public void ThirtyTurnSimulationIsDeterministic()
        {
            var first = RunThirtyTurns(606);
            var second = RunThirtyTurns(606);
            Assert.AreEqual(first, second, "동일 시드와 동일 명령의 30턴 상태 스냅샷이 달라졌습니다.");
        }

        [Test]
        public void ThirtyTurnRuleLifecycleSoakIsDeterministicAndBounded()
        {
            var first = RunRuleLifecycleSoak(607);
            var second = RunRuleLifecycleSoak(607);
            Assert.AreEqual(first, second, "규칙 추가·교체·만료와 지연 효과를 포함한 30턴 최종 상태가 동일 시드에서 달라졌습니다.");
        }

        [Test]
        public void NewtonsoftRoundTripPreservesNestedConditionSnapshot()
        {
            var game = PlayableSnapshot(707);
            game.ruleState.Add(new RuleStateEntry { key = "omen", value = 2 });
            game.activeRules.Add(new RuleNodeV1
            {
                id = "nested-condition",
                name = "겹친 징조",
                description = "중첩 조건 저장 회귀 테스트",
                trigger = EventType.TurnStart,
                appliedTurn = 1,
                durationTurns = 10,
                condition = new ConditionNode
                {
                    op = CompareOp.GreaterOrEqual,
                    left = "luck",
                    value = 40,
                    all = new List<ConditionNode>
                    {
                        new ConditionNode { op = CompareOp.HasTag, left = "player", text = "탐험가" },
                        new ConditionNode
                        {
                            op = CompareOp.OwnerIs,
                            left = "tile:0,0",
                            value = 1,
                            all = new List<ConditionNode>
                            {
                                new ConditionNode { op = CompareOp.Equal, left = "state:omen", value = 2 }
                            }
                        }
                    }
                },
                effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } }
            });

            var json = JsonConvert.SerializeObject(game);
            var restored = JsonConvert.DeserializeObject<GameSnapshotV1>(json);

            Assert.IsNotNull(restored);
            var condition = restored.activeRules.Single(rule => rule.id == "nested-condition").condition;
            Assert.AreEqual(game.turn, restored.turn);
            Assert.AreEqual(CompareOp.GreaterOrEqual, condition.op);
            Assert.AreEqual(2, condition.all.Count);
            Assert.AreEqual("탐험가", condition.all[0].text);
            Assert.AreEqual(CompareOp.OwnerIs, condition.all[1].op);
            Assert.AreEqual("state:omen", condition.all[1].all.Single().left);
            Assert.AreEqual(2, condition.all[1].all.Single().value);
        }

        private static RuleNodeV1 SpawnRule(string id, int amount)
        {
            return new RuleNodeV1
            {
                id = id,
                name = id,
                trigger = EventType.TurnEnd,
                appliedTurn = 1,
                durationTurns = 30,
                effects = new List<EffectNode>
                {
                    new EffectNode { type = EffectType.Spawn, target = "2", key = "예산 소환", amount = amount }
                }
            };
        }

        private static RuleNodeV1 StoredRule(string id)
        {
            return new RuleNodeV1
            {
                id = id,
                name = "저장 규칙 " + id,
                description = "정원 투영 검증용 저장 규칙입니다.",
                trigger = EventType.TurnStart,
                appliedTurn = 1,
                durationTurns = 30,
                effects = new List<EffectNode>
                {
                    new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 }
                }
            };
        }

        private static DynamicActionV1 ValidDynamicAction(string id, int availableTurn)
        {
            return new DynamicActionV1
            {
                id = id,
                name = "저장 행동 " + id,
                description = "정원 투영 검증용 동적 행동입니다.",
                spCost = 1,
                cooldown = 1,
                availableTurn = availableTurn,
                effects = new List<EffectNode>
                {
                    new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 }
                }
            };
        }

        private static VictoryContractV1 NewVictoryContract(string id, int announcedTurn, int minimumTurns, int target)
        {
            return new VictoryContractV1
            {
                id = id,
                title = "승리 계약 " + id,
                description = "도달 가능한 승리 계약입니다.",
                progressKey = "turn",
                target = target,
                minimumTurns = minimumTurns,
                announcedTurn = announcedTurn,
                achievableFromTurn = announcedTurn + 1
            };
        }

        private static VictoryContractV1 StoredVictoryContract(string id, int target, int minimumTurns = 3)
        {
            return new VictoryContractV1
            {
                id = id,
                title = "저장 계약 " + id,
                description = "시간축과 정원 검증용 저장 계약입니다.",
                progressKey = "turn",
                target = target,
                minimumTurns = minimumTurns,
                announcedTurn = 1,
                achievableFromTurn = 2
            };
        }

        private static VictoryContractV1 CloneVictoryContract(VictoryContractV1 source)
        {
            return new VictoryContractV1
            {
                id = source.id,
                title = source.title,
                description = source.description,
                progressKey = source.progressKey,
                target = source.target,
                minimumTurns = source.minimumTurns,
                announcedTurn = source.announcedTurn,
                achievableFromTurn = source.achievableFromTurn,
                replaceWarningTurn = source.replaceWarningTurn,
                worldCue = source.worldCue
            };
        }

        private static RuleSetV1 ValidRuleSet(GameSnapshotV1 game)
        {
            return new RuleSetV1
            {
                schemaVersion = "v1",
                requestId = "request-" + game.seed,
                applyTurn = game.turn + 1,
                koreanSummary = "안전한 회귀 테스트 규칙",
                changes = new List<RuleNodeV1>
                {
                    new RuleNodeV1
                    {
                        id = "rule-" + game.seed,
                        name = "보급",
                        description = "식량을 보급합니다.",
                        trigger = EventType.TurnStart,
                        durationTurns = 3,
                        effects = new List<EffectNode>
                        {
                            new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 }
                        }
                    }
                },
                actions = new List<DynamicActionV1>(),
                victoryContracts = new List<VictoryContractV1>()
            };
        }

        private static GameSnapshotV1 PlayableSnapshot(int seed)
        {
            var random = new DeterministicRandom(seed);
            var game = new GameSnapshotV1
            {
                runId = "test-run-" + seed,
                seed = seed,
                turn = 1,
                luck = random.Next(1, 101),
                phase = RunPhase.Planning,
                outcome = RunOutcome.Ongoing
            };
            for (var q = -2; q <= 2; q++)
            {
                for (var r = Math.Max(-2, -q - 2); r <= Math.Min(2, -q + 2); r++)
                {
                    game.map.Add(new TileState
                    {
                        position = new HexCoord(q, r),
                        terrain = "초원",
                        resource = ResourceType.Food,
                        amount = 100,
                        owner = q == 0 && r == 0 ? 1 : 0,
                        explored = true,
                        visible = true
                    });
                }
            }
            game.factions.Add(new FactionState { id = 1, name = "테스트 원정대", kind = FactionKind.Player });
            game.factions.Add(new FactionState { id = 2, name = "관전자", kind = FactionKind.Neutral, relationToPlayer = 100 });
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 1), tags = new List<string> { "탐험가" } });
            game.buildings.Add(new BuildingState { id = 1, factionId = 1, position = new HexCoord(0, 0), type = BuildingType.Headquarters });
            return game;
        }

        private static GameSnapshotV1 CollisionProjectionSnapshot(int seed)
        {
            var game = PlayableSnapshot(seed);
            game.entities.Single(unit => unit.id == 1).speed = 3;
            game.entities.Add(new UnitState { id = 2, factionId = 1, position = new HexCoord(2, -1), speed = 1 });
            return game;
        }

        private static List<PlannedCommand> CollisionCommands(CommandType selfAction)
        {
            var destination = new HexCoord(1, 0);
            return new List<PlannedCommand>
            {
                new PlannedCommand { factionId = 1, unitId = 2, type = selfAction, target = new HexCoord(2, -1) },
                new PlannedCommand { factionId = 1, unitId = 2, type = CommandType.Move, target = destination },
                new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Move, target = destination }
            };
        }

        private static string RunThirtyTurns(int seed)
        {
            var game = PlayableSnapshot(seed);
            game.activeRules.Add(new RuleNodeV1
            {
                id = "deterministic-supply",
                name = "결정론 보급",
                trigger = EventType.TurnStart,
                appliedTurn = 1,
                durationTurns = 30,
                effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } }
            });
            var log = new List<string>();
            for (var index = 0; index < 30; index++)
            {
                TurnResolver.Resolve(game, new List<PlannedCommand>
                {
                    new PlannedCommand { factionId = 1, unitId = 1, type = CommandType.Hunt, target = game.entities[0].position }
                }, new DeterministicRandom(seed + game.turn * 7919), log);
                Assert.AreEqual(RunOutcome.Ongoing, game.outcome, "결정론 장기 실행 중 예기치 않게 원정이 종료되었습니다.");
                game.turn++;
                game.phase = RunPhase.Planning;
                game.luck = new DeterministicRandom(seed + game.turn * 7919).Next(1, 101);
            }
            return JsonConvert.SerializeObject(game);
        }

        private static string RunRuleLifecycleSoak(int seed)
        {
            var game = PlayableSnapshot(seed);
            var log = new List<string>();
            var ruleAdditions = 0;
            var ruleReplacements = 0;
            var prunedRules = 0;
            var spawnTurns = 0;
            var saturatedBudgetTurns = 0;
            var sawScheduledRule = false;

            for (var index = 0; index < 30; index++)
            {
                var set = SoakRuleSet(game);
                Assert.That(set.changes.Count, Is.InRange(1, 3));
                var validation = RuleValidator.Validate(set, game);
                Assert.IsTrue(validation.valid, "soak turn " + game.turn + ": " + string.Join("\n", validation.errors));

                ApplySoakRuleSet(game, set, ref ruleAdditions, ref ruleReplacements, ref prunedRules);
                Assert.AreEqual(set.actions.Single().id, "soak-action-" + ((game.turn - 1) % 3));

                TurnResolver.BeginPlanning(game, log);
                sawScheduledRule |= game.activeRules.Any(rule => rule.id != null && rule.id.StartsWith("scheduled-", StringComparison.Ordinal));
                var action = game.dynamicActions.Single(candidate => candidate.id == set.actions[0].id);
                ExecuteSoakDynamicAction(game, action, log);
                TurnResolver.Resolve(game, new List<PlannedCommand>(), new DeterministicRandom(seed + game.turn * 7919), log);

                if (game.turn % 10 == 0)
                {
                    var vm = new RuleVm();
                    for (var dispatch = 0; dispatch < RuleLimits.MaxRuleDispatchesPerTurn + 36; dispatch++)
                        vm.Execute(EventType.TileEntered, game, log);
                    Assert.AreEqual(RuleLimits.MaxRuleDispatchesPerTurn, game.ruleBudget.dispatches, "dispatch 예산이 상한을 넘어서면 안 됩니다.");
                    Assert.AreEqual(RuleLimits.MaxRuleActivationsPerTurn, game.ruleBudget.activations, "연쇄 활성화 예산이 상한에서 멈추어야 합니다.");
                    Assert.AreEqual(RuleLimits.MaxRuleEffectsPerTurn, game.ruleBudget.effects, "효과 예산이 상한에서 멈추어야 합니다.");
                    saturatedBudgetTurns++;
                }

                AssertSoakBounds(game);
                var expectsSpawn = game.activeRules.Any(rule => rule.id == "soak-spawn" && GameRules.IsRuleActive(rule, game.turn));
                Assert.AreEqual(expectsSpawn ? RuleLimits.MaxRuleSpawnsPerTurn : 0, game.ruleBudget.spawnedEntities, "턴당 Spawn 예산이 예상과 다릅니다.");
                if (expectsSpawn) spawnTurns++;
                Assert.AreEqual(RunOutcome.Ongoing, game.outcome, "soak이 승리 계약을 조기 달성하거나 패배하면 안 됩니다.");
                var snapshotValidation = RuleValidator.ValidateSnapshot(game);
                Assert.IsTrue(snapshotValidation.valid, "soak snapshot turn " + game.turn + ": " + string.Join("\n", snapshotValidation.errors));

                game.turn++;
                game.phase = RunPhase.Planning;
                game.luck = new DeterministicRandom(seed + game.turn * 7919).Next(1, 101);
            }

            Assert.Greater(ruleAdditions, 30, "순환 중 신규칙과 지연 규칙이 실제로 추가되어야 합니다.");
            Assert.Greater(ruleReplacements, 20, "동일 id 규칙 교체 경로가 반복 실행되어야 합니다.");
            Assert.Greater(prunedRules, 20, "짧은 duration과 예약 규칙이 만료 후 정리되어야 합니다.");
            Assert.IsTrue(sawScheduledRule, "Schedule 효과가 지연 규칙을 생성해야 합니다.");
            Assert.IsTrue(log.Any(entry => entry.Contains("예약된 TurnEnd")), "지연된 TurnEnd 자원 효과가 실제로 발화해야 합니다.");
            Assert.Greater(spawnTurns, 20);
            Assert.AreEqual(1 + spawnTurns * RuleLimits.MaxRuleSpawnsPerTurn, game.entities.Count, "Spawn 외의 숨은 엔티티 누수가 있어서는 안 됩니다.");
            Assert.AreEqual(3, saturatedBudgetTurns);
            Assert.AreEqual(3, game.dynamicActions.Count);
            Assert.AreEqual(3, game.victoryContracts.Count);
            return JsonConvert.SerializeObject(game);
        }

        private static RuleSetV1 SoakRuleSet(GameSnapshotV1 game)
        {
            var changeCount = 1 + (game.turn - 1) % 3;
            var stressEffects = Enumerable.Range(0, RuleLimits.MaxEffectsPerRule)
                .Select(_ => new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 })
                .ToList();
            var changes = new List<RuleNodeV1>
            {
                new RuleNodeV1
                {
                    id = "soak-ephemeral-" + game.turn,
                    name = "순환 폭증 " + game.turn,
                    description = "만료와 효과 예산을 함께 검증하는 짧은 규칙입니다.",
                    trigger = EventType.TileEntered,
                    appliedTurn = game.turn,
                    durationTurns = 2,
                    effects = stressEffects
                }
            };
            if (changeCount >= 2)
            {
                changes.Add(new RuleNodeV1
                {
                    id = "soak-schedule",
                    name = "순환 예약",
                    description = "2턴 후 TurnEnd에 석재를 지급합니다.",
                    trigger = EventType.TurnStart,
                    appliedTurn = game.turn,
                    durationTurns = 4,
                    effects = new List<EffectNode>
                    {
                        new EffectNode { type = EffectType.Schedule, key = EventType.TurnEnd.ToString(), delay = 2, resource = ResourceType.Stone, amount = 1, value = "soak 지연 보급" }
                    }
                });
            }
            if (changeCount >= 3)
            {
                changes.Add(new RuleNodeV1
                {
                    id = "soak-spawn",
                    name = "순환 소환",
                    description = "턴당 생성 예산 상한까지 중립 유닛을 소환합니다.",
                    trigger = EventType.TurnStart,
                    appliedTurn = game.turn,
                    durationTurns = 4,
                    effects = new List<EffectNode>
                    {
                        new EffectNode { type = EffectType.Spawn, target = "2", key = "soak-neutral", amount = RuleLimits.MaxRuleSpawnsPerTurn }
                    }
                });
            }

            return new RuleSetV1
            {
                schemaVersion = "v1",
                requestId = "soak-" + game.seed + "-" + game.turn,
                applyTurn = game.turn,
                koreanSummary = "30턴 규칙 생명주기 soak " + game.turn,
                changes = changes,
                actions = new List<DynamicActionV1> { SoakDynamicAction(game.turn) },
                victoryContracts = SoakVictoryChanges(game)
            };
        }

        private static DynamicActionV1 SoakDynamicAction(int turn)
        {
            var slot = (turn - 1) % 3;
            return new DynamicActionV1
            {
                id = "soak-action-" + slot,
                name = "순환 행동 " + slot,
                description = "동일 id 교체와 쿨다운을 검증합니다.",
                spCost = 1,
                cooldown = 1,
                availableTurn = turn,
                effects = new List<EffectNode>
                {
                    new EffectNode { type = EffectType.Resource, resource = ResourceType.Wood, amount = 1 }
                }
            };
        }

        private static List<VictoryContractV1> SoakVictoryChanges(GameSnapshotV1 game)
        {
            var changes = new List<VictoryContractV1>();
            if (game.turn >= 1 && game.turn <= 3)
                changes.Add(SoakVictoryContract(game.turn - 1, game.turn, 0));
            if (game.turn == 18)
                changes.Add(SoakWarning(game, 0));
            if (game.turn == 19)
            {
                changes.Add(SoakVictoryContract(0, game.turn, 1));
                changes.Add(SoakWarning(game, 1));
            }
            if (game.turn == 20)
            {
                changes.Add(SoakVictoryContract(1, game.turn, 1));
                changes.Add(SoakWarning(game, 2));
            }
            if (game.turn == 21)
                changes.Add(SoakVictoryContract(2, game.turn, 1));
            if (game.turn >= 22)
            {
                var slot = (game.turn - 22) % 3;
                changes.Add(CloneVictoryContract(game.victoryContracts.Single(contract => contract.id == "soak-contract-" + slot)));
            }
            return changes;
        }

        private static VictoryContractV1 SoakVictoryContract(int slot, int announcedTurn, int version)
        {
            return new VictoryContractV1
            {
                id = "soak-contract-" + slot,
                title = "장기 생존 계약 " + slot + " v" + version,
                description = "충분한 유지 기간과 사전 경고를 갖춘 도달 가능한 계약입니다.",
                // The soak issues no combat or capture commands. A territory target is
                // physically reachable within the validator's six-turn horizon on this
                // 19-tile map, but remains incomplete throughout this lifecycle-only run.
                progressKey = "territory",
                target = 7,
                minimumTurns = RuleValidator.MinimumFirstVictoryTurns,
                announcedTurn = announcedTurn,
                achievableFromTurn = announcedTurn + 1
            };
        }

        private static VictoryContractV1 SoakWarning(GameSnapshotV1 game, int slot)
        {
            var warning = CloneVictoryContract(game.victoryContracts.Single(contract => contract.id == "soak-contract-" + slot));
            warning.replaceWarningTurn = game.turn;
            return warning;
        }

        private static void ApplySoakRuleSet(GameSnapshotV1 game, RuleSetV1 set, ref int additions, ref int replacements, ref int pruned)
        {
            foreach (var rule in set.changes)
            {
                var replacing = game.activeRules.Any(existing => existing.id == rule.id);
                game.activeRules.RemoveAll(existing => existing.id == rule.id);
                game.activeRules.Add(rule);
                if (replacing) replacements++;
                else additions++;
            }
            var countBeforePrune = game.activeRules.Count;
            GameRules.PruneExpiredRules(game);
            pruned += countBeforePrune - game.activeRules.Count;

            foreach (var action in set.actions)
            {
                game.dynamicActions.RemoveAll(existing => existing.id == action.id);
                game.dynamicActions.Add(action);
            }
            foreach (var contract in set.victoryContracts)
            {
                game.victoryContracts.RemoveAll(existing => existing.id == contract.id);
                game.victoryContracts.Add(contract);
            }
        }

        private static void ExecuteSoakDynamicAction(GameSnapshotV1 game, DynamicActionV1 action, List<string> log)
        {
            var validation = RuleValidator.ValidateDynamicActionForRuntime(action, game);
            Assert.IsTrue(validation.valid, "dynamic action turn " + game.turn + ": " + string.Join("\n", validation.errors));
            Assert.LessOrEqual(action.availableTurn, game.turn);
            var player = game.factions.Single(faction => faction.id == 1);
            Assert.GreaterOrEqual(player.sp, action.spCost);
            if (action.resourceAmount > 0)
                Assert.IsTrue(player.resources.Spend(action.resourceCost, action.resourceAmount));
            player.sp -= action.spCost;
            var applied = new RuleVm().ApplyValidatedEffects(action.effects, game, log, action.name);
            Assert.AreEqual(action.effects.Count, applied);
            action.availableTurn = game.turn + Math.Max(1, action.cooldown);
            GameRules.CountAction(game, CommandType.Dynamic);
        }

        private static void AssertSoakBounds(GameSnapshotV1 game)
        {
            Assert.LessOrEqual(game.activeRules.Count(rule => GameRules.IsRuleActive(rule, game.turn)), RuleLimits.MaxActiveRules);
            Assert.LessOrEqual(game.activeRules.Count, RuleLimits.MaxStoredRules);
            Assert.AreEqual(game.activeRules.Count, game.activeRules.Select(rule => rule.id).Distinct(StringComparer.Ordinal).Count());
            Assert.LessOrEqual(game.dynamicActions.Count, RuleLimits.MaxDynamicActions);
            Assert.AreEqual(game.dynamicActions.Count, game.dynamicActions.Select(action => action.id).Distinct(StringComparer.Ordinal).Count());
            Assert.LessOrEqual(game.victoryContracts.Count, RuleLimits.MaxVictoryContracts);
            Assert.AreEqual(game.victoryContracts.Count, game.victoryContracts.Select(contract => contract.id).Distinct(StringComparer.Ordinal).Count());
            Assert.LessOrEqual(game.entities.Count, RuleLimits.MaxEntities);
            Assert.LessOrEqual(game.buildings.Count, RuleLimits.MaxBuildings);
            Assert.LessOrEqual(game.ruleState.Count, RuleLimits.MaxStateVariables);
            Assert.LessOrEqual(game.journal.Count, RuleLimits.MaxJournalEntries);
            Assert.LessOrEqual(game.ruleBudget.dispatches, RuleLimits.MaxRuleDispatchesPerTurn);
            Assert.LessOrEqual(game.ruleBudget.activations, RuleLimits.MaxRuleActivationsPerTurn);
            Assert.LessOrEqual(game.ruleBudget.effects, RuleLimits.MaxRuleEffectsPerTurn);
            Assert.LessOrEqual(game.ruleBudget.spawnedEntities, RuleLimits.MaxRuleSpawnsPerTurn);
            Assert.IsFalse(game.activeRules.Any(rule => !GameRules.IsRuleActive(rule, game.turn) && (long)game.turn >= (long)rule.appliedTurn + Math.Max(1, rule.durationTurns)), "만료된 규칙이 prune 후에도 남아 있습니다.");

            foreach (var faction in game.factions)
            {
                Assert.That(faction.resources.food, Is.InRange(0, faction.resources.maxFood));
                Assert.That(faction.resources.wood, Is.InRange(0, faction.resources.maxWood));
                Assert.That(faction.resources.stone, Is.InRange(0, faction.resources.maxStone));
                Assert.That(faction.resources.iron, Is.InRange(0, faction.resources.maxIron));
                Assert.That(faction.resources.coin, Is.InRange(0, faction.resources.maxCoin));
                Assert.That(faction.sp, Is.InRange(0, faction.maxSp));
            }
        }
    }
}
