using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using OnlyMyGame.Core;
using OnlyMyGame.Runtime;

namespace OnlyMyGame.Tests
{
    public sealed class LedgerPresentationTests
    {
        [Test]
        public void LedgerConditionTextPreservesNestedAndSemantics()
        {
            var condition = new ConditionNode
            {
                op = CompareOp.GreaterOrEqual,
                left = "luck",
                value = 70,
                all = new List<ConditionNode>
                {
                    new ConditionNode { op = CompareOp.HasTag, left = "faction:2", text = "징표" },
                    new ConditionNode { op = CompareOp.OwnerIs, left = "tile:1,0", value = 1 }
                }
            };

            var text = InvokeConditionText(condition);

            StringAssert.Contains("luck ≥ 70", text);
            StringAssert.Contains("세력 2 유닛", text);
            StringAssert.Contains("징표", text);
            StringAssert.Contains("tile:1,0", text);
            Assert.AreEqual(2, CountOccurrences(text, " AND "), "세 조건의 AND 결합이 장부에서 사라지면 안 됩니다.");
        }

        [Test]
        public void LedgerNamesVictoryContractDirectlyAffectedByRule()
        {
            var rule = new RuleNodeV1
            {
                trigger = EventType.Trade,
                effects = new List<EffectNode>
                {
                    new EffectNode { type = EffectType.Resource, resource = ResourceType.Coin, amount = 2 }
                }
            };
            var contracts = new List<VictoryContractV1>
            {
                new VictoryContractV1 { title = "황금 교역로", progressKey = "coin" },
                new VictoryContractV1 { title = "요새 건설", progressKey = "buildings" }
            };

            var method = typeof(CommercialGameHud).GetMethod("VictoryImpactText", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var text = (string)method.Invoke(null, new object[] { rule, contracts });

            StringAssert.Contains("황금 교역로", text);
            StringAssert.DoesNotContain("요새 건설", text);
        }

        [Test]
        public void LedgerExplainsTypedPredicateWithoutHidingItsAst()
        {
            var score = new StateReferenceV1 { scope = RuleStateScope.Run, key = "momentum" };
            var condition = new ConditionNode
            {
                op = CompareOp.Always,
                predicate = new PredicateExpressionV1
                {
                    op = PredicateExpressionOp.All,
                    children = new List<PredicateExpressionV1>
                    {
                        new PredicateExpressionV1
                        {
                            op = PredicateExpressionOp.NumberGreaterOrEqual,
                            left = new NumberExpressionV1 { op = NumberExpressionOp.State, state = score },
                            right = new NumberExpressionV1 { op = NumberExpressionOp.Constant, constant = 5 }
                        },
                        new PredicateExpressionV1
                        {
                            op = PredicateExpressionOp.NumberLess,
                            left = new NumberExpressionV1 { op = NumberExpressionOp.RecentActionRatio, action = CommandType.Move, recentTurns = 3 },
                            right = new NumberExpressionV1 { op = NumberExpressionOp.Constant, constant = 80 }
                        }
                    }
                }
            };

            var text = InvokeConditionText(condition);

            StringAssert.Contains("원정의 momentum ≥ 5", text);
            StringAssert.Contains("최근 3턴 이동 비율 < 80", text);
            StringAssert.Contains(" AND ", text);
        }

        [Test]
        public void LedgerRendersTypedStateTokenColorNameScopeAndCurrentValue()
        {
            var definition = new StateDefinitionV1
            {
                scope = RuleStateScope.Faction,
                scopeId = "player",
                key = "resolve",
                valueType = RuleStateValueType.Number,
                koreanName = "원정대 결의",
                iconToken = "resolve-token",
                colorHex = "#33AAFF",
                initialNumber = 2
            };
            var game = new GameSnapshotV1();
            game.typedRuleState.Add(new TypedRuleStateEntryV1
            {
                scope = RuleStateScope.Faction,
                scopeId = "faction:1",
                key = "resolve",
                valueType = RuleStateValueType.Number,
                numberValue = 7
            });
            var method = typeof(CommercialGameHud).GetMethod("StateDefinitionsText", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(method);
            var text = (string)method.Invoke(null, new object[] { new[] { definition }, game });

            StringAssert.Contains("<color=#33AAFF>", text);
            StringAssert.Contains("◈ 원정대 결의 [resolve-token] = 7", text);
            StringAssert.Contains("세력 player", text);
        }

        private static string InvokeConditionText(ConditionNode condition)
        {
            var method = typeof(CommercialGameHud).GetMethod(
                "ConditionText",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(ConditionNode) },
                null);
            Assert.NotNull(method);
            return (string)method.Invoke(null, new object[] { condition });
        }

        private static int CountOccurrences(string value, string token)
        {
            var count = 0;
            var index = 0;
            while ((index = value.IndexOf(token, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += token.Length;
            }
            return count;
        }
    }
}
