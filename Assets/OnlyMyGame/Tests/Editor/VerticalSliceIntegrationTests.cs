using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using NUnit.Framework;
using OnlyMyGame.Core;
using OnlyMyGame.Runtime;

namespace OnlyMyGame.Tests
{
    public sealed class VerticalSliceIntegrationTests
    {
        private const int Seed = 21724;
        private const int ExpectedMapTiles = 217;
        private const int ExpectedEntities = 9;
        private static readonly HexCoord MoveDestination = new HexCoord(2, 0);
        private static readonly HexCoord BuilderPosition = new HexCoord(2, -2);
        private static readonly HexCoord DiplomacyTarget = new HexCoord(-4, 3);
        private static readonly HexCoord CapturePosition = new HexCoord(-2, 0);
        private static readonly HexCoord CombatTarget = new HexCoord(3, 0);

        [Test]
        public void DeterministicTwentyFourTurnVerticalSliceSurvivesSaveRestoreAndReachesAiVictory()
        {
            var first = RunVerticalSlice(Seed);
            var second = RunVerticalSlice(Seed);

            Assert.AreEqual(first.finalJson, second.finalJson, "같은 시드의 최종 전체 스냅샷은 바이트 단위로 결정론적이어야 합니다.");
            Assert.AreEqual(RunOutcome.Victory, first.outcome);
            Assert.That(first.victoryTurn, Is.InRange(20, 30));
            Assert.AreEqual(24, first.victoryTurn, "로컬 AI 계약의 공개 유지 기간이 끝나는 24턴에 승리해야 합니다.");
            Assert.AreEqual("local-ai-expedition", first.completedContractId);
            Assert.IsTrue(first.roundTripCompleted, "중간 턴의 전체 스냅샷 저장/복원이 실행되어야 합니다.");
            Assert.AreEqual(10 - GameRules.BuildingIronCost(BuildingType.Workshop), first.ironAfterWorkshopBuild);
            Assert.Greater(first.finalIron, first.ironAfterWorkshopBuild, "Workshop은 후속 턴마다 철을 생산해야 합니다.");
            Assert.AreEqual(30, first.finalIron, "레벨 2 Workshop의 철 생산은 자원 상한에서 안전하게 멈춰야 합니다.");
        }

        private static VerticalSliceResult RunVerticalSlice(int seed)
        {
            var game = WorldGenerator.Create(seed);
            ConfigureDeterministicScenario(game, seed);
            var provider = new LocalDeterministicAiRuleProvider();
            var roundTripCompleted = false;
            var ironAfterWorkshopBuild = -1;

            while (game.outcome == RunOutcome.Ongoing && game.turn <= 30)
            {
                PrepareScenarioTurn(game);
                var ruleSet = provider.Create(game);
                Assert.AreEqual(1 + (game.turn - 1) % 3, ruleSet.changes.Count, "AI는 매 턴 1~3개 규칙을 제안해야 합니다.");

                var validation = RuleValidator.Validate(ruleSet, game);
                Assert.IsTrue(validation.valid, "turn " + game.turn + " local AI rules: " + string.Join("\n", validation.errors));
                ApplyRuleSet(game, ruleSet);

                TurnResolver.Resolve(
                    game,
                    CommandsForTurn(game),
                    new DeterministicRandom(seed + game.turn * 7919),
                    game.journal);

                if (game.turn == 6)
                    ironAfterWorkshopBuild = Player(game).resources.iron;

                AssertVerticalSliceBounds(game);

                if (game.turn == 12)
                {
                    game = RoundTripCompleteSnapshot(game);
                    roundTripCompleted = true;
                }

                if (game.outcome == RunOutcome.Ongoing)
                {
                    game.turn++;
                    game.phase = RunPhase.Planning;
                    game.luck = new DeterministicRandom(seed + game.turn * 7919).Next(1, 101);
                }
                else
                {
                    game.phase = RunPhase.Terminal;
                }
            }

            Assert.IsTrue(roundTripCompleted);
            Assert.AreEqual(RunOutcome.Victory, game.outcome);
            Assert.That(game.turn, Is.InRange(20, 30));
            Assert.AreEqual("local-ai-expedition", game.completedContractId);
            Assert.AreEqual(ExpectedMapTiles, game.map.Count);
            Assert.AreEqual(ExpectedEntities, game.entities.Count, "규칙 적용 중 숨은 Spawn/엔티티 누수가 없어야 합니다.");
            Assert.AreEqual(4, game.factions.Count);
            Assert.AreEqual(3, game.buildings.Count);
            Assert.AreEqual(0, game.dynamicActions.Count);
            Assert.AreEqual(1, game.victoryContracts.Count);
            Assert.AreEqual(0, game.ruleState.Count);
            Assert.AreEqual(0, game.typedRuleState.Count);
            Assert.AreEqual(6, game.recentActionStats.Count, "최근 행동 창에는 마지막 6턴의 수렵만 남아야 합니다.");

            AssertCommandExecuted(game, CommandType.Move);
            AssertCommandExecuted(game, CommandType.Gather);
            AssertCommandExecuted(game, CommandType.Hunt);
            AssertCommandExecuted(game, CommandType.Attack);
            AssertCommandExecuted(game, CommandType.Trade);
            AssertCommandExecuted(game, CommandType.Persuade);
            AssertCommandExecuted(game, CommandType.Hire);
            AssertCommandExecuted(game, CommandType.Build);
            AssertCommandExecuted(game, CommandType.Upgrade);
            AssertCommandExecuted(game, CommandType.Capture);

            Assert.AreEqual(MoveDestination, game.entities.Single(unit => unit.id == 1).position);
            Assert.AreEqual(1, game.entities.Single(unit => unit.id == 3).factionId, "중립 상인이 고용되어 플레이어 세력으로 전환되어야 합니다.");
            Assert.IsTrue(game.entities.Single(unit => unit.id == 3).tags.Contains("고용병"));
            Assert.IsFalse(game.entities.Single(unit => unit.id == 90).alive);
            Assert.AreEqual(1, game.playerKills);
            Assert.AreEqual(1, game.map.Single(tile => tile.position.Equals(CapturePosition)).owner);

            var workshop = game.buildings.Single(building => building.factionId == 1 && building.position.Equals(BuilderPosition));
            Assert.AreEqual(BuildingType.Workshop, workshop.type, "명령에서 선택한 건물 종류가 보존되어야 합니다.");
            Assert.AreEqual(2, workshop.level);

            var finalValidation = RuleValidator.ValidateSnapshot(game);
            Assert.IsTrue(finalValidation.valid, "final snapshot: " + string.Join("\n", finalValidation.errors));
            return new VerticalSliceResult
            {
                finalJson = JsonConvert.SerializeObject(game),
                outcome = game.outcome,
                victoryTurn = game.turn,
                completedContractId = game.completedContractId,
                roundTripCompleted = roundTripCompleted,
                ironAfterWorkshopBuild = ironAfterWorkshopBuild,
                finalIron = Player(game).resources.iron
            };
        }

        private static void ConfigureDeterministicScenario(GameSnapshotV1 game, int seed)
        {
            Assert.AreEqual(ExpectedMapTiles, game.map.Count, "radius-8 WorldGenerator 맵은 217개 타일이어야 합니다.");
            game.runId = "vertical-slice-" + seed;
            game.phase = RunPhase.Planning;
            game.outcome = RunOutcome.Ongoing;

            var player = Player(game);
            player.resources.food = 20;
            player.resources.wood = 20;
            player.resources.stone = 15;
            player.resources.iron = 10;
            player.resources.coin = 10;

            var generatedSkeletons = game.factions.Single(faction => faction.id == 2);
            generatedSkeletons.kind = FactionKind.Neutral;
            generatedSkeletons.relationToPlayer = 100;
            game.factions.Single(faction => faction.id == 3).relationToPlayer = 10;
            game.factions.Add(new FactionState
            {
                id = 4,
                name = "결투 검증대",
                kind = FactionKind.Skeleton,
                relationToPlayer = -100
            });

            ConfigureTile(game, MoveDestination, ResourceType.Food, 4, 0);
            ConfigureTile(game, BuilderPosition, ResourceType.Iron, 4, 0);
            ConfigureTile(game, CapturePosition, ResourceType.Stone, 4, 0);
            ConfigureTile(game, CombatTarget, ResourceType.Food, 4, 0);

            game.entities.Add(new UnitState { id = 10, factionId = 1, position = new HexCoord(-3, 3), tags = new List<string> { "외교관" } });
            game.entities.Add(new UnitState { id = 11, factionId = 1, position = BuilderPosition, tags = new List<string> { "건축가" } });
            game.entities.Add(new UnitState { id = 12, factionId = 1, position = CapturePosition, tags = new List<string> { "개척자" } });
            game.entities.Add(new UnitState { id = 90, factionId = 4, position = CombatTarget, hp = 0, alive = false, tags = new List<string> { "결투 상대" } });
            Assert.AreEqual(ExpectedEntities, game.entities.Count);

            WorldGenerator.Reveal(game);
            var validation = RuleValidator.ValidateSnapshot(game);
            Assert.IsTrue(validation.valid, "configured snapshot: " + string.Join("\n", validation.errors));
        }

        private static void ConfigureTile(GameSnapshotV1 game, HexCoord position, ResourceType resource, int amount, int owner)
        {
            var tile = game.map.Single(candidate => candidate.position.Equals(position));
            tile.terrain = "초원";
            tile.resource = resource;
            tile.amount = amount;
            tile.owner = owner;
        }

        private static void PrepareScenarioTurn(GameSnapshotV1 game)
        {
            if (game.turn != 8) return;
            var combatant = game.entities.Single(unit => unit.id == 90);
            combatant.hp = 2;
            combatant.alive = true;
        }

        private static List<PlannedCommand> CommandsForTurn(GameSnapshotV1 game)
        {
            if (game.turn == 1)
                return One(Command(1, CommandType.Gather, new HexCoord(1, 0)));
            if (game.turn == 2)
                return new List<PlannedCommand>
                {
                    Command(1, CommandType.Move, MoveDestination),
                    Command(1, CommandType.Hunt, MoveDestination)
                };
            if (game.turn == 3)
                return One(Command(10, CommandType.Trade, DiplomacyTarget));
            if (game.turn == 4)
                return One(Command(10, CommandType.Persuade, DiplomacyTarget));
            if (game.turn == 5)
                return One(Command(10, CommandType.Hire, DiplomacyTarget));
            if (game.turn == 6)
            {
                var build = Command(11, CommandType.Build, BuilderPosition);
                build.buildingType = BuildingType.Workshop;
                return One(build);
            }
            if (game.turn == 7)
                return One(Command(11, CommandType.Upgrade, BuilderPosition));
            if (game.turn == 8)
                return One(Command(1, CommandType.Attack, CombatTarget));
            if (game.turn == 9)
                return One(Command(12, CommandType.Capture, CapturePosition));
            return One(Command(1, CommandType.Hunt, game.entities.Single(unit => unit.id == 1).position));
        }

        private static PlannedCommand Command(int unitId, CommandType type, HexCoord target)
        {
            return new PlannedCommand { factionId = 1, unitId = unitId, type = type, target = target };
        }

        private static List<PlannedCommand> One(PlannedCommand command)
        {
            return new List<PlannedCommand> { command };
        }

        private static void ApplyRuleSet(GameSnapshotV1 game, RuleSetV1 ruleSet)
        {
            foreach (var rule in ruleSet.changes)
            {
                game.activeRules.RemoveAll(existing => string.Equals(existing.id, rule.id, StringComparison.Ordinal));
                game.activeRules.Add(rule);
            }

            foreach (var action in ruleSet.actions)
            {
                game.dynamicActions.RemoveAll(existing => string.Equals(existing.id, action.id, StringComparison.Ordinal));
                game.dynamicActions.Add(action);
            }

            foreach (var contract in ruleSet.victoryContracts)
            {
                game.victoryContracts.RemoveAll(existing => string.Equals(existing.id, contract.id, StringComparison.Ordinal));
                game.victoryContracts.Add(contract);
            }

            GameRules.PruneExpiredRules(game);
            foreach (var rule in ruleSet.changes)
                Assert.AreEqual(game.turn, game.activeRules.Single(existing => existing.id == rule.id).appliedTurn);
        }

        private static GameSnapshotV1 RoundTripCompleteSnapshot(GameSnapshotV1 game)
        {
            var before = JsonConvert.SerializeObject(game);
            var restored = JsonConvert.DeserializeObject<GameSnapshotV1>(before);
            Assert.IsNotNull(restored);
            Assert.AreEqual(before, JsonConvert.SerializeObject(restored), "전체 DTO 그래프는 손실 없이 직렬화 왕복되어야 합니다.");
            Assert.AreEqual(ExpectedMapTiles, restored.map.Count);
            Assert.AreEqual(game.entities.Count, restored.entities.Count);
            Assert.AreEqual(game.buildings.Count, restored.buildings.Count);
            Assert.AreEqual(game.factions.Count, restored.factions.Count);
            Assert.AreEqual(game.actionStats.Count, restored.actionStats.Count);
            Assert.AreEqual(game.activeRules.Count, restored.activeRules.Count);
            Assert.AreEqual(game.victoryContracts.Count, restored.victoryContracts.Count);
            Assert.AreEqual(game.dynamicActions.Count, restored.dynamicActions.Count);
            Assert.AreEqual(game.ruleState.Count, restored.ruleState.Count);
            Assert.AreEqual(game.typedRuleState.Count, restored.typedRuleState.Count);
            Assert.AreEqual(game.recentActionStats.Count, restored.recentActionStats.Count);
            Assert.AreEqual(game.journal.Count, restored.journal.Count);
            Assert.IsNotNull(restored.ruleBudget);

            var validation = RuleValidator.ValidateSnapshot(restored);
            Assert.IsTrue(validation.valid, "round-tripped snapshot: " + string.Join("\n", validation.errors));
            return restored;
        }

        private static void AssertVerticalSliceBounds(GameSnapshotV1 game)
        {
            Assert.AreEqual(ExpectedMapTiles, game.map.Count);
            Assert.AreEqual(ExpectedEntities, game.entities.Count);
            Assert.LessOrEqual(game.activeRules.Count(rule => GameRules.IsRuleActive(rule, game.turn)), RuleLimits.MaxActiveRules);
            Assert.LessOrEqual(game.activeRules.Count, RuleLimits.MaxStoredRules);
            Assert.AreEqual(game.activeRules.Count, game.activeRules.Select(rule => rule.id).Distinct(StringComparer.Ordinal).Count());
            Assert.LessOrEqual(game.dynamicActions.Count, RuleLimits.MaxDynamicActions);
            Assert.LessOrEqual(game.victoryContracts.Count, RuleLimits.MaxVictoryContracts);
            Assert.LessOrEqual(game.ruleState.Count, RuleLimits.MaxStateVariables);
            Assert.LessOrEqual(game.typedRuleState.Count, RuleLimits.MaxStateVariables);
            Assert.LessOrEqual(game.recentActionStats.Count, RuleLimits.MaxRecentActionEntries);
            Assert.Less(game.journal.Count, RuleLimits.MaxJournalEntries);
            Assert.IsFalse(game.activeRules.Any(rule => !GameRules.IsRuleActive(rule, game.turn) && (long)game.turn >= (long)rule.appliedTurn + Math.Max(1, rule.durationTurns)), "만료 규칙이 저장 컬렉션에 누적되면 안 됩니다.");

            Assert.IsNotNull(game.ruleBudget);
            Assert.AreEqual(game.turn, game.ruleBudget.turn);
            Assert.That(game.ruleBudget.dispatches, Is.InRange(0, RuleLimits.MaxRuleDispatchesPerTurn));
            Assert.That(game.ruleBudget.conditionWork, Is.InRange(0, RuleLimits.MaxRuleConditionWorkPerTurn));
            Assert.That(game.ruleBudget.activations, Is.InRange(0, RuleLimits.MaxRuleActivationsPerTurn));
            Assert.That(game.ruleBudget.effects, Is.InRange(0, RuleLimits.MaxRuleEffectsPerTurn));
            Assert.AreEqual(0, game.ruleBudget.spawnedEntities);
            Assert.AreEqual(0, game.ruleBudget.loggedLimits, "정상 vertical slice가 어느 런타임 예산도 포화시키면 안 됩니다.");

            foreach (var faction in game.factions)
            {
                Assert.That(faction.resources.food, Is.InRange(0, faction.resources.maxFood));
                Assert.That(faction.resources.wood, Is.InRange(0, faction.resources.maxWood));
                Assert.That(faction.resources.stone, Is.InRange(0, faction.resources.maxStone));
                Assert.That(faction.resources.iron, Is.InRange(0, faction.resources.maxIron));
                Assert.That(faction.resources.coin, Is.InRange(0, faction.resources.maxCoin));
                Assert.That(faction.sp, Is.InRange(0, faction.maxSp));
            }

            var validation = RuleValidator.ValidateSnapshot(game);
            Assert.IsTrue(validation.valid, "turn " + game.turn + " snapshot: " + string.Join("\n", validation.errors));
        }

        private static void AssertCommandExecuted(GameSnapshotV1 game, CommandType type)
        {
            Assert.GreaterOrEqual(game.actionStats.Single(stat => stat.type == type).count, 1, type + " 명령이 실제 해결 단계를 통과해야 합니다.");
        }

        private static FactionState Player(GameSnapshotV1 game)
        {
            return game.factions.Single(faction => faction.id == 1);
        }

        private sealed class LocalDeterministicAiRuleProvider
        {
            public RuleSetV1 Create(GameSnapshotV1 game)
            {
                var changeCount = 1 + (game.turn - 1) % 3;
                var changes = new List<RuleNodeV1>();
                for (var slot = 0; slot < changeCount; slot++)
                {
                    changes.Add(new RuleNodeV1
                    {
                        id = "local-ai-rule-" + slot,
                        name = "로컬 AI 보급 " + slot,
                        description = "네트워크 없이 결정론적으로 검증하는 짧은 생명주기 규칙입니다.",
                        trigger = EventType.TurnEnd,
                        appliedTurn = game.turn,
                        durationTurns = 2,
                        effects = new List<EffectNode>
                        {
                            new EffectNode
                            {
                                type = EffectType.Resource,
                                resource = slot == 0 ? ResourceType.Food : slot == 1 ? ResourceType.Wood : ResourceType.Stone,
                                amount = 1
                            }
                        },
                        worldCue = "로컬 AI " + slot
                    });
                }

                var contracts = new List<VictoryContractV1>();
                if (game.turn == 1)
                {
                    contracts.Add(new VictoryContractV1
                    {
                        id = "local-ai-expedition",
                        title = "24턴 원정 완주",
                        description = "6턴 안에 목표 수치에 닿을 수 있지만 충분한 공개 기간 뒤에만 완성되는 AI 승리 계약입니다.",
                        progressKey = "turn",
                        target = 7,
                        minimumTurns = 23,
                        announcedTurn = 1,
                        achievableFromTurn = 2,
                        worldCue = "승리의 깃발"
                    });
                }

                return new RuleSetV1
                {
                    schemaVersion = "v1",
                    requestId = "local-ai-" + game.seed + "-" + game.turn,
                    applyTurn = game.turn,
                    koreanSummary = "로컬 결정론 규칙 " + game.turn,
                    changes = changes,
                    actions = new List<DynamicActionV1>(),
                    victoryContracts = contracts
                };
            }
        }

        private sealed class VerticalSliceResult
        {
            public string finalJson;
            public RunOutcome outcome;
            public int victoryTurn;
            public string completedContractId;
            public bool roundTripCompleted;
            public int ironAfterWorkshopBuild;
            public int finalIron;
        }
    }
}
