using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OnlyMyGame.Core;

namespace OnlyMyGame.Tests
{
    public sealed class RuleExpressionTests
    {
        [Test]
        public void LegacyConditionFieldsRemainBackwardCompatible()
        {
            var game = Snapshot();
            game.luck = 70;
            var condition = new ConditionNode
            {
                op = CompareOp.GreaterOrEqual,
                left = "luck",
                value = 70,
                all = new List<ConditionNode> { new ConditionNode { op = CompareOp.LessOrEqual, left = "turn", value = 1 } }
            };
            var rule = BaseRule("legacy");
            rule.condition = condition;

            Assert.IsTrue(RuleVm.ConditionMatches(condition, game));
            Assert.IsTrue(RuleValidator.Validate(RuleSet(game, rule), game).valid, "식 필드가 없는 기존 ConditionNode는 기존 의미를 그대로 유지해야 합니다.");
        }

        [Test]
        public void TypedStateDefinitionsCoverEveryScopeAndTurnStateResets()
        {
            var game = Snapshot();
            var definitions = new List<StateDefinitionV1>
            {
                NumberDefinition(RuleStateScope.Run, null, "run_score", 1),
                NumberDefinition(RuleStateScope.Turn, null, "turn_score", 2),
                NumberDefinition(RuleStateScope.Faction, "player", "faction_score", 3),
                NumberDefinition(RuleStateScope.Unit, "unit:1", "unit_score", 4),
                NumberDefinition(RuleStateScope.Building, "building:1", "building_score", 5),
                NumberDefinition(RuleStateScope.Tile, "tile:0,0", "tile_score", 6)
            };

            Assert.IsTrue(RuleExpressionRuntime.EnsureDefinitions(definitions, game));
            Assert.AreEqual(6, game.typedRuleState.Count);
            for (var index = 0; index < definitions.Count; index++)
            {
                Assert.IsTrue(RuleExpressionRuntime.TryReadNumber(game, Reference(definitions[index]), out var value));
                Assert.AreEqual(index + 1, value);
            }
            Assert.IsTrue(game.typedRuleState.All(entry => entry.koreanName.Contains("상태") && entry.iconToken.StartsWith("state_", StringComparison.Ordinal) && entry.colorHex == "#33AAFF"));

            Assert.IsTrue(RuleExpressionRuntime.ApplyStateMutation(Add(Reference(definitions[0]), 10), game));
            Assert.IsTrue(RuleExpressionRuntime.ApplyStateMutation(Add(Reference(definitions[1]), 10), game));
            game.turn = 2;
            Assert.IsTrue(RuleExpressionRuntime.EnsureDefinitions(definitions, game));
            Assert.IsTrue(RuleExpressionRuntime.TryReadNumber(game, Reference(definitions[0]), out var persisted));
            Assert.IsTrue(RuleExpressionRuntime.TryReadNumber(game, Reference(definitions[1]), out var reset));
            Assert.AreEqual(11, persisted, "run 상태는 턴이 바뀌어도 보존되어야 합니다.");
            Assert.AreEqual(2, reset, "turn 상태는 새 턴의 초기값으로 재설정되어야 합니다.");
        }

        [Test]
        public void RuleVmAppliesNumberBoolAndSetMutationsSafely()
        {
            var game = Snapshot();
            var number = NumberDefinition(RuleStateScope.Run, null, "momentum", 2);
            var boolean = Definition(RuleStateScope.Run, null, "gate_open", RuleStateValueType.Boolean);
            boolean.initialBool = false;
            var set = Definition(RuleStateScope.Run, null, "badges", RuleStateValueType.Set);
            set.initialSet = new List<string> { "alpha" };
            var rule = BaseRule("mutations");
            rule.stateDefinitions = new List<StateDefinitionV1> { number, boolean, set };
            rule.effects = new List<EffectNode>
            {
                Typed(new StateMutationV1 { op = StateMutationOp.Set, state = Reference(number), numberValue = Constant(5) }),
                Typed(Add(Reference(number), 3)),
                Typed(new StateMutationV1 { op = StateMutationOp.Toggle, state = Reference(boolean) }),
                Typed(new StateMutationV1 { op = StateMutationOp.SetAdd, state = Reference(set), element = "beta" }),
                Typed(new StateMutationV1 { op = StateMutationOp.SetRemove, state = Reference(set), element = "alpha" })
            };
            game.activeRules.Add(rule);

            new RuleVm().Execute(EventType.TurnStart, game, new List<string>());

            Assert.IsTrue(RuleExpressionRuntime.TryReadNumber(game, Reference(number), out var numberValue));
            Assert.IsTrue(RuleExpressionRuntime.TryReadBool(game, Reference(boolean), out var boolValue));
            Assert.IsTrue(RuleExpressionRuntime.TryReadSet(game, Reference(set), out var setValue));
            Assert.AreEqual(8, numberValue);
            Assert.IsTrue(boolValue);
            CollectionAssert.AreEqual(new[] { "beta" }, setValue);
        }

        [Test]
        public void NumberExpressionsEvaluateArithmeticCountsDistanceAndRecentRatio()
        {
            var game = Snapshot();
            var score = NumberDefinition(RuleStateScope.Run, null, "score", 10);
            Assert.IsTrue(RuleExpressionRuntime.EnsureDefinitions(new[] { score }, game));
            GameRules.CountAction(game, CommandType.Move);
            GameRules.CountAction(game, CommandType.Attack);
            game.turn = 2;
            GameRules.CountAction(game, CommandType.Move);

            var arithmetic = Binary(NumberExpressionOp.Divide,
                Binary(NumberExpressionOp.Multiply,
                    Binary(NumberExpressionOp.Add, State(Reference(score)), Constant(2)),
                    Constant(5)),
                Constant(3));

            AssertNumber(game, arithmetic, 20);
            AssertNumber(game, new NumberExpressionV1 { op = NumberExpressionOp.CountUnits, selector = "any" }, 2);
            AssertNumber(game, new NumberExpressionV1 { op = NumberExpressionOp.CountBuildings, selector = "player" }, 1);
            AssertNumber(game, new NumberExpressionV1 { op = NumberExpressionOp.CountTiles, selector = "player_owned" }, 1);
            AssertNumber(game, new NumberExpressionV1 { op = NumberExpressionOp.Distance, selector = "unit:1", secondSelector = "unit:2" }, 2);
            AssertNumber(game, new NumberExpressionV1 { op = NumberExpressionOp.RecentActionRatio, action = CommandType.Move, recentTurns = 2 }, 66);
            Assert.AreEqual(3, game.recentActionStats.Sum(entry => entry.count), "CountAction은 누적 통계와 별개로 최근 턴 기록을 결정론적으로 보존해야 합니다.");
        }

        [Test]
        public void PredicateAllAnyNotNumericBoolAndSetContainsWorkTogether()
        {
            var game = Snapshot();
            var score = NumberDefinition(RuleStateScope.Run, null, "score", 5);
            var ready = Definition(RuleStateScope.Run, null, "ready", RuleStateValueType.Boolean);
            ready.initialBool = true;
            var badges = Definition(RuleStateScope.Run, null, "badges", RuleStateValueType.Set);
            badges.initialSet = new List<string> { "alpha" };
            Assert.IsTrue(RuleExpressionRuntime.EnsureDefinitions(new[] { score, ready, badges }, game));
            var predicate = new PredicateExpressionV1
            {
                op = PredicateExpressionOp.All,
                children = new List<PredicateExpressionV1>
                {
                    Compare(PredicateExpressionOp.NumberGreaterOrEqual, State(Reference(score)), Constant(5)),
                    new PredicateExpressionV1
                    {
                        op = PredicateExpressionOp.Any,
                        children = new List<PredicateExpressionV1>
                        {
                            new PredicateExpressionV1 { op = PredicateExpressionOp.BoolState, state = Reference(ready) },
                            new PredicateExpressionV1 { op = PredicateExpressionOp.SetContains, state = Reference(badges), element = "beta" }
                        }
                    },
                    new PredicateExpressionV1
                    {
                        op = PredicateExpressionOp.Not,
                        child = new PredicateExpressionV1 { op = PredicateExpressionOp.SetContains, state = Reference(badges), element = "beta" }
                    },
                    new PredicateExpressionV1 { op = PredicateExpressionOp.SetContains, state = Reference(badges), element = "alpha" }
                }
            };
            var condition = new ConditionNode { op = CompareOp.Always, predicate = predicate };

            Assert.IsTrue(RuleExpressionRuntime.TryEvaluatePredicate(predicate, game, out var result));
            Assert.IsTrue(result);
            Assert.IsTrue(RuleVm.ConditionMatches(condition, game));
            var rule = BaseRule("predicate");
            rule.stateDefinitions = new List<StateDefinitionV1> { score, ready, badges };
            rule.condition = condition;
            Assert.IsTrue(RuleValidator.Validate(RuleSet(game, rule), game).valid);
        }

        [Test]
        public void ValidatorRejectsOverflowZeroDivideCyclesDepthNodesSetsStateLimitAndSelectors()
        {
            var game = Snapshot();
            var number = NumberDefinition(RuleStateScope.Run, null, "number", 1);

            AssertError(game, MutationRule("overflow", number, Binary(NumberExpressionOp.Multiply, Constant(RuleLimits.MaxStateMagnitude), Constant(2))), "EXPR_ARITHMETIC_INVALID");
            AssertError(game, MutationRule("zero-divide", number, Binary(NumberExpressionOp.Divide, Constant(5), Constant(0))), "EXPR_ARITHMETIC_INVALID");

            var cycle = new NumberExpressionV1 { op = NumberExpressionOp.Add, right = Constant(1) };
            cycle.left = cycle;
            AssertError(game, MutationRule("cycle", number, cycle), "EXPR_CYCLE");

            var ready = Definition(RuleStateScope.Run, null, "ready", RuleStateValueType.Boolean);
            var depthPredicate = new PredicateExpressionV1 { op = PredicateExpressionOp.Not };
            depthPredicate.child = new PredicateExpressionV1 { op = PredicateExpressionOp.Not, child = new PredicateExpressionV1 { op = PredicateExpressionOp.Not, child = new PredicateExpressionV1 { op = PredicateExpressionOp.BoolState, state = Reference(ready) } } };
            var depthRule = BaseRule("depth");
            depthRule.stateDefinitions.Add(ready);
            depthRule.condition.predicate = depthPredicate;
            AssertError(game, depthRule, "AST_DEPTH_LIMIT");

            var nodeRule = BaseRule("nodes");
            nodeRule.stateDefinitions.Add(ready);
            nodeRule.condition.predicate = new PredicateExpressionV1 { op = PredicateExpressionOp.All };
            for (var index = 0; index < RuleLimits.MaxConditionNodes; index++) nodeRule.condition.predicate.children.Add(new PredicateExpressionV1 { op = PredicateExpressionOp.BoolState, state = Reference(ready) });
            AssertError(game, nodeRule, "AST_NODE_LIMIT");

            var oversizedSet = Definition(RuleStateScope.Run, null, "oversized", RuleStateValueType.Set);
            oversizedSet.initialSet = Enumerable.Range(0, RuleLimits.MaxStateSetElements + 1).Select(index => "item_" + index).ToList();
            var setRule = BaseRule("set-limit");
            setRule.stateDefinitions.Add(oversizedSet);
            AssertError(game, setRule, "STATE_SET_LIMIT");

            var stateLimitRule = BaseRule("state-limit");
            for (var index = 0; index < RuleLimits.MaxStateVariables + 1; index++) stateLimitRule.stateDefinitions.Add(NumberDefinition(RuleStateScope.Run, null, "state_" + index, index));
            AssertError(game, stateLimitRule, "STATE_VARIABLE_LIMIT");

            var selectorRule = BaseRule("selector");
            selectorRule.condition.predicate = Compare(PredicateExpressionOp.NumberGreaterOrEqual, new NumberExpressionV1 { op = NumberExpressionOp.CountUnits, selector = "faction:999" }, Constant(0));
            AssertError(game, selectorRule, "EXPR_SELECTOR_INVALID");

            var scopedSelectorRule = BaseRule("scope-selector");
            scopedSelectorRule.stateDefinitions.Add(NumberDefinition(RuleStateScope.Unit, "unit:999", "missing_unit", 0));
            AssertError(game, scopedSelectorRule, "STATE_DEFINITION_INVALID");
        }

        [Test]
        public void CurrentWorldDefinitionValidationSharesTheExpressionScanBudget()
        {
            var game = Snapshot();
            FillMaximumVisibleWorld(game);
            var rule = BaseRule("definition-budget");
            rule.stateDefinitions = Enumerable.Range(0, RuleLimits.MaxStateDefinitionsPerRule)
                .Select(index => NumberDefinition(
                    RuleStateScope.Unit,
                    "unit:" + RuleLimits.MaxEntities,
                    "bounded_definition_" + index,
                    index))
                .ToList();
            var errors = new List<string>();

            RuleExpressionValidator.ValidateRule(rule, game, "RULE:" + rule.id, errors, true);

            Assert.IsTrue(errors.Any(error => error.StartsWith("EXPR_WORK_LIMIT", StringComparison.Ordinal)),
                "state definition의 존재·가시성 scan도 조건/효과와 같은 current-world 예산을 공유해야 합니다.");
        }

        [Test]
        public void StoredDefinitionValidationIsStructuralWhenItsTargetIsTemporarilyMissing()
        {
            var game = Snapshot();
            var rule = BaseRule("stored-definition-shape");
            rule.appliedTurn = game.turn;
            rule.stateDefinitions.Add(NumberDefinition(RuleStateScope.Unit, "unit:999", "missing_later", 0));

            AssertError(game, rule, "STATE_DEFINITION_INVALID");
            game.activeRules.Add(rule);
            var stored = RuleValidator.ValidateSnapshot(game);

            Assert.IsTrue(stored.valid, string.Join("\n", stored.errors));
        }

        [Test]
        public void SnapshotTypedReferencesUseOneBoundedWorldIndex()
        {
            var game = Snapshot();
            FillMaximumVisibleWorld(game);
            game.typedRuleState = Enumerable.Range(0, RuleLimits.MaxStateVariables)
                .Select(index => new TypedRuleStateEntryV1
                {
                    scope = RuleStateScope.Tile,
                    scopeId = "tile:" + (RuleLimits.MaxMapTiles - 1) + ",0",
                    key = "indexed_snapshot_state_" + index,
                    valueType = RuleStateValueType.Number,
                    koreanName = "인덱스 저장 상태 " + index,
                    iconToken = "indexed_state_" + index,
                    colorHex = "#33AAFF"
                })
                .ToList();
            var errors = new List<string>();

            RuleExpressionValidator.ValidateSnapshot(game, errors);

            Assert.IsEmpty(errors, string.Join("\n", errors));
        }

        [Test]
        public void DefinitionMaterializationRejectsMalformedWorldIdentityIndexes()
        {
            var game = Snapshot();
            game.map.Add(new TileState { position = game.map[0].position, terrain = "중복 지형", explored = true, visible = true });
            var definition = NumberDefinition(RuleStateScope.Run, null, "malformed_world_guard", 1);

            Assert.IsFalse(RuleExpressionRuntime.EnsureDefinitions(new[] { definition }, game),
                "전체 snapshot 검사 전 materialization도 duplicate/null world identity를 fail closed 해야 합니다.");
            Assert.IsEmpty(game.typedRuleState);
        }

        [Test]
        public void DefinitionRegistryCacheRefreshesWhenRulesChangeWithinTheSameTurn()
        {
            var game = Snapshot();
            var first = BaseRule("cached-definition");
            first.appliedTurn = game.turn;
            first.trigger = EventType.Move;
            first.stateDefinitions.Add(NumberDefinition(RuleStateScope.Run, null, "cache_first", 1));
            game.activeRules.Add(first);
            var vm = new RuleVm();

            vm.Execute(EventType.Move, game, new List<string>());
            Assert.IsTrue(RuleExpressionRuntime.TryReadNumber(game, Reference(first.stateDefinitions[0]), out var firstValue));
            Assert.AreEqual(1, firstValue);

            var replacement = BaseRule("cached-definition");
            replacement.appliedTurn = game.turn;
            replacement.trigger = EventType.Move;
            replacement.stateDefinitions.Add(NumberDefinition(RuleStateScope.Run, null, "cache_second", 2));
            game.activeRules[0] = replacement;

            vm.Execute(EventType.Move, game, new List<string>());

            Assert.IsTrue(RuleExpressionRuntime.TryReadNumber(game, Reference(replacement.stateDefinitions[0]), out var secondValue));
            Assert.AreEqual(2, secondValue);
        }

        [Test]
        public void SameTypedRuleInputProducesSameStateOneHundredTimes()
        {
            var expected = RunDeterministicTypedRule();
            for (var iteration = 0; iteration < 100; iteration++) Assert.AreEqual(expected, RunDeterministicTypedRule(), "타입 상태·표현식 결과가 " + iteration + "번째 실행에서 달라졌습니다.");
        }

        [Test]
        public void IdleTurnsDoNotMakeStaleRecentActionHistoryInvalidateSnapshot()
        {
            var game = Snapshot();
            GameRules.CountAction(game, CommandType.Move);
            game.turn = 7;

            Assert.IsTrue(RuleValidator.ValidateSnapshot(game).valid, "최근 행동이 없던 턴 때문에 저장·규칙 요청이 차단되면 안 됩니다.");
            Assert.AreEqual(0, RuleExpressionRuntime.RecentActionRatio(game, CommandType.Move, RuleLimits.MaxRecentActionTurns));

            GameRules.PruneRecentActionStats(game);
            Assert.IsEmpty(game.recentActionStats, "턴 경계 정리는 6턴 창 밖의 통계를 결정론적으로 제거해야 합니다.");
        }

        [Test]
        public void ProjectedDefinitionsRejectCrossRuleTypeAndSignatureConflicts()
        {
            var game = Snapshot();
            var owner = BaseRule("state-owner");
            owner.appliedTurn = game.turn;
            owner.stateDefinitions = new List<StateDefinitionV1> { NumberDefinition(RuleStateScope.Run, null, "shared_state", 1) };
            game.activeRules.Add(owner);

            var typeConflict = BaseRule("type-conflict");
            typeConflict.stateDefinitions = new List<StateDefinitionV1> { Definition(RuleStateScope.Run, null, "shared_state", RuleStateValueType.Boolean) };
            var typeErrors = new List<string>();
            RuleExpressionValidator.ValidateProjectedDefinitions(new[] { typeConflict }, game, typeErrors);
            Assert.IsNotEmpty(typeErrors, "서로 다른 규칙이 같은 상태 identity를 다른 타입으로 정의하면 거부해야 합니다.");

            var signatureConflict = BaseRule("signature-conflict");
            signatureConflict.stateDefinitions = new List<StateDefinitionV1> { NumberDefinition(RuleStateScope.Run, null, "shared_state", 2) };
            var signatureErrors = new List<string>();
            RuleExpressionValidator.ValidateProjectedDefinitions(new[] { signatureConflict }, game, signatureErrors);
            Assert.IsNotEmpty(signatureErrors, "타입이 같아도 초기값·표시 메타데이터를 포함한 정의 signature가 다르면 거부해야 합니다.");

            var replacement = BaseRule(owner.id);
            replacement.stateDefinitions = new List<StateDefinitionV1> { NumberDefinition(RuleStateScope.Run, null, "shared_state", 2) };
            var replacementErrors = new List<string>();
            RuleExpressionValidator.ValidateProjectedDefinitions(new[] { replacement }, game, replacementErrors);
            Assert.IsEmpty(replacementErrors, "동일 rule id 교체에서는 교체될 이전 정의를 동시에 투영해 자기 자신과 충돌시키면 안 됩니다.");

            var otherOwner = BaseRule("other-owner");
            otherOwner.appliedTurn = game.turn;
            otherOwner.stateDefinitions = new List<StateDefinitionV1> { NumberDefinition(RuleStateScope.Run, null, "other_shared_state", 7) };
            game.activeRules.Add(otherOwner);
            replacement.stateDefinitions = new List<StateDefinitionV1> { NumberDefinition(RuleStateScope.Run, null, "other_shared_state", 7) };
            replacementErrors.Clear();
            RuleExpressionValidator.ValidateProjectedDefinitions(new[] { replacement }, game, replacementErrors);
            Assert.IsNotEmpty(replacementErrors, "교체 규칙도 다른 활성 규칙이 소유한 상태 identity를 가로채면 안 됩니다.");
        }

        [Test]
        public void StoredScopeAliasesCanonicalizeWithoutDuplicateState()
        {
            var game = Snapshot();
            game.typedRuleState.Add(new TypedRuleStateEntryV1
            {
                scope = RuleStateScope.Faction,
                scopeId = "player",
                key = "morale",
                valueType = RuleStateValueType.Number,
                koreanName = "테스트 상태 morale",
                iconToken = "state_morale",
                colorHex = "#33AAFF",
                numberValue = 42
            });
            var canonicalDefinition = NumberDefinition(RuleStateScope.Faction, "faction:1", "morale", 3);

            Assert.IsTrue(RuleExpressionRuntime.EnsureDefinitions(new[] { canonicalDefinition }, game));
            Assert.AreEqual(1, game.typedRuleState.Count, "player와 faction:1 별칭이 서로 다른 상태 슬롯을 만들면 안 됩니다.");
            Assert.AreEqual("faction:1", game.typedRuleState[0].scopeId, "저장된 별칭은 정규 scope id로 마이그레이션되어야 합니다.");
            Assert.IsTrue(RuleExpressionRuntime.TryReadNumber(game, new StateReferenceV1 { scope = RuleStateScope.Faction, scopeId = "player", key = "morale" }, out var aliasValue));
            Assert.IsTrue(RuleExpressionRuntime.TryReadNumber(game, Reference(canonicalDefinition), out var canonicalValue));
            Assert.AreEqual(42, aliasValue, "별칭 정규화는 기존 런 상태 값을 보존해야 합니다.");
            Assert.AreEqual(aliasValue, canonicalValue);
        }

        [Test]
        public void ValidatorRejectsStateOverZeroDivide()
        {
            var game = Snapshot();
            var number = NumberDefinition(RuleStateScope.Run, null, "numerator", 9);
            var divideByZero = Binary(NumberExpressionOp.Divide, State(Reference(number)), Constant(0));

            AssertError(game, MutationRule("state-over-zero", number, divideByZero), "EXPR_ARITHMETIC_INVALID");
        }

        [Test]
        public void WorldRuleEffectsRollbackWhenTypedMutationFails()
        {
            var game = Snapshot();
            var player = game.factions.Single(faction => faction.kind == FactionKind.Player);
            var initialFood = player.resources.food;
            var capped = NumberDefinition(RuleStateScope.Run, null, "capped", RuleLimits.MaxStateMagnitude);
            var rule = BaseRule("atomic-world-rule");
            rule.appliedTurn = game.turn;
            rule.stateDefinitions = new List<StateDefinitionV1> { capped };
            rule.effects = new List<EffectNode>
            {
                new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 },
                Typed(Add(Reference(capped), 1))
            };
            game.activeRules.Add(rule);

            new RuleVm().Execute(EventType.TurnStart, game, new List<string>());

            Assert.AreEqual(initialFood, player.resources.food, "뒤의 타입 상태 mutation이 실패하면 앞서 적용된 월드 효과도 남아서는 안 됩니다.");
            Assert.IsTrue(RuleExpressionRuntime.TryReadNumber(game, Reference(capped), out var value));
            Assert.AreEqual(RuleLimits.MaxStateMagnitude, value);
        }

        [Test]
        public void WorldRuleAtomicPreflightObservesEarlierSpawnBeforeTypedCountMutation()
        {
            var game = Snapshot();
            var initialEntityCount = game.entities.Count;
            var cappedAfterOriginalCount = NumberDefinition(
                RuleStateScope.Run,
                null,
                "spawn_sensitive_cap",
                RuleLimits.MaxStateMagnitude - initialEntityCount);
            var rule = BaseRule("atomic-spawn-count");
            rule.appliedTurn = game.turn;
            rule.stateDefinitions = new List<StateDefinitionV1> { cappedAfterOriginalCount };
            rule.effects = new List<EffectNode>
            {
                new EffectNode { type = EffectType.Spawn, target = "player", key = "원자성", amount = 1 },
                Typed(new StateMutationV1
                {
                    op = StateMutationOp.Add,
                    state = Reference(cappedAfterOriginalCount),
                    numberValue = new NumberExpressionV1 { op = NumberExpressionOp.CountUnits, selector = "any" }
                })
            };
            game.activeRules.Add(rule);

            new RuleVm().Execute(EventType.TurnStart, game, new List<string>());

            Assert.AreEqual(initialEntityCount, game.entities.Count, "Spawn 뒤의 world-dependent typed mutation이 실패하면 생성 유닛도 남아서는 안 됩니다.");
            Assert.AreEqual(0, game.ruleBudget.spawnedEntities, "실패한 사전 검사는 live spawn 예산도 소비하면 안 됩니다.");
            Assert.IsTrue(RuleExpressionRuntime.TryReadNumber(game, Reference(cappedAfterOriginalCount), out var value));
            Assert.AreEqual(RuleLimits.MaxStateMagnitude - initialEntityCount, value);
        }

        [Test]
        public void TurnDefinitionsExistAndResetBeforeFirstTrigger()
        {
            var game = Snapshot();
            var turnState = NumberDefinition(RuleStateScope.Turn, null, "move_charge", 4);
            var moveRule = BaseRule("move-only-state-owner");
            moveRule.appliedTurn = game.turn;
            moveRule.trigger = EventType.Move;
            moveRule.stateDefinitions = new List<StateDefinitionV1> { turnState };
            game.activeRules.Add(moveRule);

            TurnResolver.BeginPlanning(game, new List<string>());

            Assert.IsTrue(RuleExpressionRuntime.TryReadNumber(game, Reference(turnState), out var initial), "상태 소유 규칙의 첫 trigger 전에도 모든 활성 정의가 materialize되어야 합니다.");
            Assert.AreEqual(4, initial);
            Assert.IsTrue(RuleExpressionRuntime.ApplyStateMutation(Add(Reference(turnState), 7), game));

            game.turn = 2;
            game.planningPrepared = false;
            TurnResolver.BeginPlanning(game, new List<string>());

            Assert.IsTrue(RuleExpressionRuntime.TryReadNumber(game, Reference(turnState), out var reset));
            Assert.AreEqual(4, reset, "Turn 상태는 해당 규칙 trigger를 기다리지 않고 턴 시작 경계에서 초기화되어야 합니다.");
        }

        [Test]
        public void HiddenWorldSelectorsCannotObserveFoggedEntities()
        {
            var game = Snapshot();
            var hiddenPosition = new HexCoord(2, -1);
            var hiddenTile = game.map.Single(tile => tile.position.Equals(hiddenPosition));
            hiddenTile.visible = false;
            hiddenTile.explored = false;
            game.entities.Single(unit => unit.id == 2).tags.Add("secret_tag");
            game.buildings.Add(new BuildingState { id = 2, factionId = 2, position = hiddenPosition, type = BuildingType.Barracks });

            AssertNumber(game, new NumberExpressionV1 { op = NumberExpressionOp.CountUnits, selector = "any" }, 1);
            AssertNumber(game, new NumberExpressionV1 { op = NumberExpressionOp.CountUnits, selector = "faction:2" }, 0);
            AssertNumber(game, new NumberExpressionV1 { op = NumberExpressionOp.CountUnits, selector = "unit:2" }, 0);
            AssertNumber(game, new NumberExpressionV1 { op = NumberExpressionOp.CountBuildings, selector = "any" }, 1);
            AssertNumber(game, new NumberExpressionV1 { op = NumberExpressionOp.CountBuildings, selector = "faction:2" }, 0);
            AssertNumber(game, new NumberExpressionV1 { op = NumberExpressionOp.CountBuildings, selector = "building:2" }, 0);
            Assert.IsFalse(RuleExpressionRuntime.TryEvaluateNumber(new NumberExpressionV1 { op = NumberExpressionOp.Distance, selector = "player_unit", secondSelector = "unit:2" }, game, out _), "미탐색 적 좌표를 distance로 역추적할 수 없어야 합니다.");
            Assert.IsFalse(RuleVm.ConditionMatches(new ConditionNode { op = CompareOp.HasTag, left = "any", text = "secret_tag" }, game), "legacy tag 조건도 안개 속 적 태그를 읽으면 안 됩니다.");
            Assert.IsFalse(RuleVm.ConditionMatches(new ConditionNode { op = CompareOp.HasTag, left = "unit:2", text = "secret_tag" }, game), "exact legacy tag 조건으로 안개 속 적을 추적하면 안 됩니다.");
            Assert.IsFalse(RuleVm.ConditionMatches(new ConditionNode { op = CompareOp.OwnerIs, left = "any", value = 2 }, game), "legacy owner 조건도 미가시 적 영토를 집계하면 안 됩니다.");
            Assert.IsFalse(RuleVm.ConditionMatches(new ConditionNode { op = CompareOp.OwnerIs, left = "tile:2,-1", value = 2 }, game), "exact legacy owner 조건으로 미탐사 타일 소유권을 읽으면 안 됩니다.");

            var hiddenScopeRule = BaseRule("hidden-scope");
            hiddenScopeRule.stateDefinitions.Add(NumberDefinition(RuleStateScope.Unit, "unit:2", "secret_counter", 0));
            AssertError(game, hiddenScopeRule, "STATE_DEFINITION_INVALID");
        }

        [Test]
        public void ThirtyTurnStateLifecycleDoesNotExhaustCapacity()
        {
            var game = Snapshot();
            for (var turn = 1; turn <= 30; turn++)
            {
                game.turn = turn;
                var first = BaseRule("state-batch-" + turn + "-a");
                first.stateDefinitions = Enumerable.Range(0, 2)
                    .Select(index => NumberDefinition(RuleStateScope.Run, null, "turn_" + turn + "_state_" + index, turn))
                    .ToList();
                var second = BaseRule("state-batch-" + turn + "-b");
                second.stateDefinitions = Enumerable.Range(2, 2)
                    .Select(index => NumberDefinition(RuleStateScope.Run, null, "turn_" + turn + "_state_" + index, turn))
                    .ToList();
                var errors = new List<string>();

                RuleExpressionValidator.ValidateProjectedDefinitions(new[] { first, second }, game, errors);

                Assert.IsEmpty(errors, turn + "턴의 신규 상태 4개 응답은 30턴 상용 런 예산 안에서 허용되어야 합니다: " + string.Join(", ", errors));
                Assert.IsTrue(RuleExpressionRuntime.EnsureDefinitions(first.stateDefinitions, game));
                Assert.IsTrue(RuleExpressionRuntime.EnsureDefinitions(second.stateDefinitions, game));
            }
            Assert.AreEqual(120, game.typedRuleState.Count);
            Assert.LessOrEqual(game.typedRuleState.Count, RuleLimits.MaxStateVariables);

            game.turn = 31;
            var four = BaseRule("oversized-response-a");
            four.stateDefinitions = Enumerable.Range(0, 4).Select(index => NumberDefinition(RuleStateScope.Run, null, "overflow_state_" + index, 0)).ToList();
            var fifth = BaseRule("oversized-response-b");
            fifth.stateDefinitions = new List<StateDefinitionV1> { NumberDefinition(RuleStateScope.Run, null, "overflow_state_4", 0) };
            var oversizedErrors = new List<string>();

            RuleExpressionValidator.ValidateProjectedDefinitions(new[] { four, fifth }, game, oversizedErrors);

            Assert.IsNotEmpty(oversizedErrors, "응답 하나가 신규 상태 identity를 5개 예약하면 남은 전체 용량과 무관하게 거부해야 합니다.");
        }

        [Test]
        public void ThirtyTurnTypedAndDynamicActionStatusRunwayDoesNotExhaustCapacity()
        {
            var game = Snapshot();
            var vm = new RuleVm();
            for (var turn = 1; turn <= 30; turn++)
            {
                game.turn = turn;
                var rule = BaseRule("mixed-state-batch-" + turn);
                rule.stateDefinitions = Enumerable.Range(0, 2)
                    .Select(index => NumberDefinition(RuleStateScope.Run, null, "mixed_typed_" + turn + "_" + index, turn))
                    .ToList();
                var action = StatusAction("rotating-state-action", "mixed_legacy_" + turn + "_", 2);
                var errors = new List<string>();

                RuleExpressionValidator.ValidateProjectedDefinitions(new[] { rule }, new[] { action }, game, errors);

                Assert.IsEmpty(errors, turn + "턴의 typed 2개 + 동적 행동 Status 2개는 같은 4-identity runway에서 허용되어야 합니다: " + string.Join(", ", errors));
                Assert.IsTrue(RuleExpressionRuntime.EnsureDefinitions(rule.stateDefinitions, game));
                Assert.AreEqual(2, vm.ApplyValidatedEffects(action.effects, game, new List<string>(), action.name));
                game.dynamicActions.RemoveAll(existing => existing != null && existing.id == action.id);
                game.dynamicActions.Add(action);
            }

            Assert.AreEqual(60, game.typedRuleState.Count);
            Assert.AreEqual(60, game.ruleState.Count);
            Assert.AreEqual(120, game.typedRuleState.Count + game.ruleState.Count, "30턴 동안 액션 Status까지 runway에 포함해도 128 슬롯을 조기에 소진하면 안 됩니다.");

            game.turn = 31;
            var overflowRule = BaseRule("mixed-overflow");
            overflowRule.stateDefinitions = Enumerable.Range(0, 2)
                .Select(index => NumberDefinition(RuleStateScope.Run, null, "mixed_overflow_typed_" + index, 0))
                .ToList();
            var overflowAction = StatusAction("rotating-state-action", "mixed_overflow_legacy_", 3);
            var overflowErrors = new List<string>();

            RuleExpressionValidator.ValidateProjectedDefinitions(new[] { overflowRule }, new[] { overflowAction }, game, overflowErrors);

            Assert.IsTrue(overflowErrors.Contains("NEW_STATE_IDENTITY_LIMIT"), "typed와 action Status를 합쳐 신규 identity 5개인 응답은 거부해야 합니다: " + string.Join(", ", overflowErrors));
            Assert.IsFalse(overflowErrors.Contains("STATE_VARIABLE_LIMIT"), "125개 투영은 전역 128 한도가 아니라 응답별 runway 한도로 거부되어야 합니다.");
        }

        [Test]
        public void DynamicActionReplacementReleasesOnlyItsUnmaterializedStatusReservation()
        {
            var game = Snapshot();
            game.ruleState = Enumerable.Range(0, RuleLimits.MaxStateVariables - 1)
                .Select(index => new RuleStateEntry { key = "persisted_legacy_" + index, value = 0 })
                .ToList();
            game.dynamicActions.Add(StatusAction("replace-status-action", "outgoing_unmaterialized_", 1));
            var replacement = StatusAction("replace-status-action", "incoming_replacement_", 1);
            var replacementErrors = new List<string>();

            RuleExpressionValidator.ValidateProjectedDefinitions(Array.Empty<RuleNodeV1>(), new[] { replacement }, game, replacementErrors);

            Assert.IsEmpty(replacementErrors, "같은 action id 교체는 아직 materialize되지 않은 이전 Status 예약만 투영에서 해제해야 합니다: " + string.Join(", ", replacementErrors));

            var additional = StatusAction("additional-status-action", "incoming_additional_", 1);
            var additionalErrors = new List<string>();
            RuleExpressionValidator.ValidateProjectedDefinitions(Array.Empty<RuleNodeV1>(), new[] { additional }, game, additionalErrors);

            Assert.IsTrue(additionalErrors.Contains("STATE_VARIABLE_LIMIT"), "다른 action id는 기존 Status 예약과 함께 계산되어 128 슬롯 초과를 막아야 합니다.");
        }

        [Test]
        public void FalseRuleConditionsConsumeAttemptBudgetBeforeEvaluation()
        {
            var game = Snapshot();
            var player = game.factions.Single(faction => faction.kind == FactionKind.Player);
            var initialFood = player.resources.food;
            var rule = BaseRule("always-false-attempt");
            rule.appliedTurn = game.turn;
            rule.condition = new ConditionNode { op = CompareOp.GreaterOrEqual, left = "luck", value = RuleLimits.MaxStateMagnitude };
            game.activeRules.Add(rule);
            var vm = new RuleVm();
            var log = new List<string>();

            for (var attempt = 0; attempt < RuleLimits.MaxRuleDispatchesPerTurn + 10; attempt++)
                vm.Execute(EventType.TurnStart, game, log);

            Assert.AreEqual(RuleLimits.MaxRuleDispatchesPerTurn, game.ruleBudget.dispatches, "false 조건도 실제 평가 전에 attempt 예산을 소비해야 합니다.");
            Assert.Greater(game.ruleBudget.conditionWork, 0);
            Assert.LessOrEqual(game.ruleBudget.conditionWork, RuleLimits.MaxRuleConditionWorkPerTurn);
            Assert.AreEqual(0, game.ruleBudget.activations);
            Assert.AreEqual(0, game.ruleBudget.effects);
            Assert.AreEqual(initialFood, player.resources.food);
        }

        [Test]
        public void LargeWorldSelectorConditionsStayWithinThirtyTurnWebGlWorkBudget()
        {
            var game = Snapshot();
            FillMaximumVisibleWorld(game);
            var countIsNonNegative = Compare(
                PredicateExpressionOp.NumberGreaterOrEqual,
                new NumberExpressionV1 { op = NumberExpressionOp.CountUnits, selector = "any" },
                Constant(0));
            var overweightCondition = new ConditionNode
            {
                op = CompareOp.Always,
                all = Enumerable.Range(0, 5)
                    .Select(_ => new ConditionNode { op = CompareOp.Always, predicate = countIsNonNegative })
                    .ToList()
            };
            Assert.IsFalse(RuleVm.ConditionMatches(overweightCondition, game), "의미상 true여도 단일 평가가 WebGL 비용 한도를 넘으면 실제 selector 순회를 시작하기 전에 fail closed 해야 합니다.");

            var selectorRule = BaseRule("large-world-selector-soak");
            selectorRule.appliedTurn = 1;
            selectorRule.durationTurns = 30;
            selectorRule.condition = new ConditionNode
            {
                op = CompareOp.Always,
                predicate = Compare(
                    PredicateExpressionOp.NumberGreater,
                    new NumberExpressionV1 { op = NumberExpressionOp.CountUnits, selector = "any" },
                    Constant(RuleLimits.MaxStateMagnitude))
            };
            game.activeRules.Add(selectorRule);
            var vm = new RuleVm();
            var log = new List<string>();

            for (var turn = 1; turn <= 30; turn++)
            {
                game.turn = turn;
                for (var dispatch = 0; dispatch < 100; dispatch++) vm.Execute(EventType.TurnStart, game, log);

                Assert.AreEqual(RuleLimits.MaxRuleConditionWorkPerTurn, game.ruleBudget.conditionWork, turn + "턴 selector 계산이 턴 예산에서 멈추지 않았습니다.");
                Assert.Greater(game.ruleBudget.dispatches, 0);
                Assert.LessOrEqual(game.ruleBudget.dispatches, RuleLimits.MaxRuleDispatchesPerTurn);
                Assert.AreEqual(0, game.ruleBudget.activations);
                Assert.AreEqual(0, game.ruleBudget.effects);
            }
        }

        private static string RunDeterministicTypedRule()
        {
            var game = Snapshot();
            var number = NumberDefinition(RuleStateScope.Run, null, "score", 1);
            var set = Definition(RuleStateScope.Run, null, "tokens", RuleStateValueType.Set);
            set.initialSet = new List<string> { "alpha" };
            var rule = BaseRule("deterministic");
            rule.stateDefinitions = new List<StateDefinitionV1> { number, set };
            rule.effects = new List<EffectNode>
            {
                Typed(Add(Reference(number), 2)),
                Typed(new StateMutationV1 { op = StateMutationOp.SetAdd, state = Reference(set), element = "beta" })
            };
            game.activeRules.Add(rule);
            for (var turn = 1; turn <= 6; turn++)
            {
                game.turn = turn;
                GameRules.CountAction(game, turn % 2 == 0 ? CommandType.Move : CommandType.Gather);
                new RuleVm().Execute(EventType.TurnStart, game, new List<string>());
            }
            var states = string.Join(";", game.typedRuleState.OrderBy(entry => entry.key, StringComparer.Ordinal).Select(entry => entry.key + ":" + entry.numberValue + ":" + entry.boolValue + ":" + string.Join(",", entry.setValue ?? new List<string>())));
            var history = string.Join(";", game.recentActionStats.Select(entry => entry.turn + ":" + entry.type + ":" + entry.count));
            return states + "|" + history + "|" + RuleExpressionRuntime.RecentActionRatio(game, CommandType.Move, 6);
        }

        private static GameSnapshotV1 Snapshot()
        {
            var game = new GameSnapshotV1 { runId = "expression-test", seed = 77, turn = 1, luck = 50, phase = RunPhase.Planning, outcome = RunOutcome.Ongoing };
            game.factions.Add(new FactionState { id = 1, name = "테스트 원정대", kind = FactionKind.Player });
            game.factions.Add(new FactionState { id = 2, name = "테스트 적", kind = FactionKind.Skeleton, relationToPlayer = -60 });
            game.map.Add(new TileState { position = new HexCoord(0, 0), terrain = "초원", owner = 1, visible = true, explored = true });
            game.map.Add(new TileState { position = new HexCoord(1, 0), terrain = "숲", owner = 0, visible = true, explored = true });
            game.map.Add(new TileState { position = new HexCoord(2, -1), terrain = "언덕", owner = 2, visible = true, explored = true });
            game.entities.Add(new UnitState { id = 1, factionId = 1, position = new HexCoord(0, 0) });
            game.entities.Add(new UnitState { id = 2, factionId = 2, position = new HexCoord(2, -1) });
            game.buildings.Add(new BuildingState { id = 1, factionId = 1, position = new HexCoord(0, 0), type = BuildingType.Headquarters });
            return game;
        }

        private static void FillMaximumVisibleWorld(GameSnapshotV1 game)
        {
            game.map.Clear();
            game.entities.Clear();
            game.buildings.Clear();
            for (var index = 0; index < RuleLimits.MaxMapTiles; index++)
                game.map.Add(new TileState { position = new HexCoord(index, 0), terrain = "초원", owner = index == 0 ? 1 : 2, visible = true, explored = true });
            for (var index = 0; index < RuleLimits.MaxEntities; index++)
                game.entities.Add(new UnitState { id = index + 1, factionId = index == 0 ? 1 : 2, position = new HexCoord(index, 0) });
        }

        private static DynamicActionV1 StatusAction(string id, string keyPrefix, int count)
        {
            return new DynamicActionV1
            {
                id = id,
                name = "상태 행동 " + id,
                description = "legacy Status identity runway를 검증합니다.",
                spCost = 1,
                cooldown = 1,
                availableTurn = 1,
                effects = Enumerable.Range(0, count)
                    .Select(index => new EffectNode { type = EffectType.Status, key = keyPrefix + index, amount = 1 })
                    .ToList()
            };
        }

        private static RuleNodeV1 BaseRule(string id)
        {
            return new RuleNodeV1
            {
                id = id,
                name = "표현식 규칙 " + id,
                description = "범용 타입 상태와 표현식을 검증합니다.",
                trigger = EventType.TurnStart,
                // Incoming rules use 0 and are stamped to RuleSet.applyTurn by the
                // validator/application path. A hard-coded old turn makes an otherwise
                // valid expression fixture fail for an unrelated timing mismatch.
                appliedTurn = 0,
                durationTurns = 30,
                effects = new List<EffectNode> { new EffectNode { type = EffectType.Resource, resource = ResourceType.Food, amount = 1 } }
            };
        }

        private static RuleSetV1 RuleSet(GameSnapshotV1 game, RuleNodeV1 rule)
        {
            return new RuleSetV1
            {
                requestId = "expression-request-" + rule.id,
                applyTurn = game.turn + 1,
                koreanSummary = "표현식 검증 규칙",
                changes = new List<RuleNodeV1> { rule },
                actions = new List<DynamicActionV1>(),
                victoryContracts = new List<VictoryContractV1>()
            };
        }

        private static StateDefinitionV1 Definition(RuleStateScope scope, string scopeId, string key, RuleStateValueType type)
        {
            return new StateDefinitionV1
            {
                scope = scope,
                scopeId = scopeId,
                key = key,
                valueType = type,
                koreanName = "테스트 상태 " + key,
                iconToken = "state_" + key,
                colorHex = "#33AAFF"
            };
        }

        private static StateDefinitionV1 NumberDefinition(RuleStateScope scope, string scopeId, string key, int initial)
        {
            var definition = Definition(scope, scopeId, key, RuleStateValueType.Number);
            definition.initialNumber = initial;
            return definition;
        }

        private static StateReferenceV1 Reference(StateDefinitionV1 definition) => new StateReferenceV1 { scope = definition.scope, scopeId = definition.scopeId, key = definition.key };
        private static NumberExpressionV1 Constant(int value) => new NumberExpressionV1 { op = NumberExpressionOp.Constant, constant = value };
        private static NumberExpressionV1 State(StateReferenceV1 reference) => new NumberExpressionV1 { op = NumberExpressionOp.State, state = reference };
        private static NumberExpressionV1 Binary(NumberExpressionOp op, NumberExpressionV1 left, NumberExpressionV1 right) => new NumberExpressionV1 { op = op, left = left, right = right };
        private static StateMutationV1 Add(StateReferenceV1 reference, int amount) => new StateMutationV1 { op = StateMutationOp.Add, state = reference, numberValue = Constant(amount) };
        private static EffectNode Typed(StateMutationV1 mutation) => new EffectNode { type = EffectType.TypedState, stateMutation = mutation };
        private static PredicateExpressionV1 Compare(PredicateExpressionOp op, NumberExpressionV1 left, NumberExpressionV1 right) => new PredicateExpressionV1 { op = op, left = left, right = right };

        private static RuleNodeV1 MutationRule(string id, StateDefinitionV1 definition, NumberExpressionV1 expression)
        {
            var rule = BaseRule(id);
            rule.stateDefinitions.Add(definition);
            rule.effects = new List<EffectNode> { Typed(new StateMutationV1 { op = StateMutationOp.Set, state = Reference(definition), numberValue = expression }) };
            return rule;
        }

        private static void AssertNumber(GameSnapshotV1 game, NumberExpressionV1 expression, int expected)
        {
            Assert.IsTrue(RuleExpressionRuntime.TryEvaluateNumber(expression, game, out var actual));
            Assert.AreEqual(expected, actual);
        }

        private static void AssertError(GameSnapshotV1 game, RuleNodeV1 rule, string code)
        {
            var validation = RuleValidator.Validate(RuleSet(game, rule), game);
            Assert.IsFalse(validation.valid, code + " 검증 사례가 통과했습니다.");
            Assert.IsTrue(validation.errors.Any(error => error.StartsWith(code, StringComparison.Ordinal)), code + " 오류가 없습니다: " + string.Join(", ", validation.errors));
        }
    }
}
