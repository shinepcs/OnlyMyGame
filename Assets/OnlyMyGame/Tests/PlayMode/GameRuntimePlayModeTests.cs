using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using NUnit.Framework;
using OnlyMyGame.Core;
using OnlyMyGame.Runtime;
using UnityEngine;

namespace OnlyMyGame.Tests
{
    public sealed class GameRuntimePlayModeTests
    {
        private const string SaveKey = "onlymygame.autosave.v1";
        private const string BackupKey = "onlymygame.autosave.v1.backup";
        private const string TempKey = "onlymygame.autosave.v1.pending";
        private readonly Dictionary<string, string> preservedPreferences = new Dictionary<string, string>();

        [Serializable]
        private sealed class TestSaveEnvelope
        {
            public int schemaVersion = 2;
            public string payload;
            public string checksum;
        }

        [SetUp]
        public void PreserveGameSaves()
        {
            preservedPreferences.Clear();
            foreach (var key in SaveKeys())
            {
                if (PlayerPrefs.HasKey(key)) preservedPreferences[key] = PlayerPrefs.GetString(key, "");
                PlayerPrefs.DeleteKey(key);
            }
            PlayerPrefs.Save();
        }

        [TearDown]
        public void RestoreGameSaves()
        {
            foreach (var key in SaveKeys())
            {
                if (preservedPreferences.TryGetValue(key, out var value)) PlayerPrefs.SetString(key, value);
                else PlayerPrefs.DeleteKey(key);
            }
            PlayerPrefs.Save();
        }

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
        public void GeneratedWorldStartsPlayerBesideRatherThanInsideHeadquarters()
        {
            var world = WorldGenerator.Create(20260804);
            var player = world.entities.Find(unit => unit.factionId == 1 && unit.alive);
            var headquarters = world.buildings.Find(building => building.factionId == 1 && building.type == BuildingType.Headquarters && building.hp > 0);

            Assert.IsNotNull(player);
            Assert.IsNotNull(headquarters);
            Assert.AreNotEqual(headquarters.position, player.position, "시작 유닛이 본부와 겹치면 선택과 가시성이 깨집니다.");
            Assert.AreEqual(1, player.position.Distance(headquarters.position), "시작 유닛은 본부와 인접한 타일에서 시작해야 합니다.");
        }

        [Test]
        public void TargetVisibilityRespectsFogOfWar()
        {
            var world = WorldGenerator.Create(20260805);
            var hidden = world.entities.Find(unit => unit.factionId != 1 && unit.alive).position;
            var tile = world.map.Find(candidate => candidate.position.Equals(hidden));
            var method = typeof(GameController).GetMethod("IsVisibleTile", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "대상 시야 검사 진입점을 찾지 못했습니다.");

            tile.visible = false;
            Assert.IsFalse((bool)method.Invoke(null, new object[] { world, hidden }), "안개 밖 대상 좌표를 명령 UI가 노출하면 안 됩니다.");
            tile.visible = true;
            Assert.IsTrue((bool)method.Invoke(null, new object[] { world, hidden }), "현재 보이는 대상 좌표는 명령 UI에서 선택할 수 있어야 합니다.");
        }

        [Test]
        public void BeginPlanningRestoresSpAndAppliesBuildingProductionOnce()
        {
            var world = WorldGenerator.Create(7);
            var player = world.factions.Find(f => f.id == 1);
            world.turn++;
            world.phase = RunPhase.Planning;
            world.planningPrepared = false;
            player.sp = 0;
            world.buildings.Add(new BuildingState { id = 99, factionId = 1, type = BuildingType.Market });
            var before = player.resources.coin;
            TurnResolver.BeginPlanning(world, world.journal);
            Assert.AreEqual(player.maxSp, player.sp);
            Assert.AreEqual(before + 1, player.resources.coin);

            TurnResolver.BeginPlanning(world, world.journal);
            Assert.AreEqual(before + 1, player.resources.coin, "같은 턴에 계획 준비를 다시 호출해도 생산이 누적되면 안 됩니다.");
        }

        [Test]
        public void InterruptedPendingSaveIsPromotedAndPrimaryBecomesBackup()
        {
            var primary = WorldGenerator.Create(101);
            primary.runId = "older-primary";
            var pending = WorldGenerator.Create(202);
            pending.runId = "newer-pending";
            var primaryRaw = Envelope(primary);
            PlayerPrefs.SetString(SaveKey, primaryRaw);
            PlayerPrefs.SetString(TempKey, Envelope(pending));
            PlayerPrefs.Save();

            var recovered = RecoverBestSave();

            Assert.AreEqual("newer-pending", recovered.runId);
            Assert.IsFalse(PlayerPrefs.HasKey(TempKey), "복구된 임시 세대는 승격 후 제거되어야 합니다.");
            Assert.AreEqual(primaryRaw, PlayerPrefs.GetString(BackupKey), "기존 주 세대는 백업으로 보존되어야 합니다.");
            Assert.AreEqual("newer-pending", RecoverBestSave().runId, "승격된 주 세대를 다음 시작에서도 읽어야 합니다.");
        }

        [Test]
        public void CorruptPendingAndPrimaryFallBackToBackupAndSelfHeal()
        {
            var backup = WorldGenerator.Create(303);
            backup.runId = "valid-backup";
            var backupRaw = Envelope(backup);
            PlayerPrefs.SetString(TempKey, "{broken-pending");
            PlayerPrefs.SetString(SaveKey, "{broken-primary");
            PlayerPrefs.SetString(BackupKey, backupRaw);
            PlayerPrefs.Save();

            var recovered = RecoverBestSave();

            Assert.AreEqual("valid-backup", recovered.runId);
            Assert.IsFalse(PlayerPrefs.HasKey(TempKey), "손상된 임시 세대는 반복 복구를 막기 위해 제거해야 합니다.");
            Assert.AreEqual(backupRaw, PlayerPrefs.GetString(SaveKey), "유효한 백업으로 손상된 주 세대를 자가 복구해야 합니다.");
        }

        [Test]
        public void SemanticallyInvalidPrimaryFallsBackToBackupAndSelfHeals()
        {
            var invalidPrimary = WorldGenerator.Create(304);
            invalidPrimary.runId = "checksum-valid-but-invalid";
            invalidPrimary.entities[1].id = invalidPrimary.entities[0].id;
            var backup = WorldGenerator.Create(305);
            backup.runId = "semantic-backup";
            var backupRaw = Envelope(backup);
            PlayerPrefs.SetString(SaveKey, Envelope(invalidPrimary));
            PlayerPrefs.SetString(BackupKey, backupRaw);
            PlayerPrefs.Save();

            var recovered = RecoverBestSave();

            Assert.AreEqual("semantic-backup", recovered.runId, "체크섬이 맞아도 중복 엔티티 ID가 있는 저장은 실행하면 안 됩니다.");
            Assert.AreEqual(backupRaw, PlayerPrefs.GetString(SaveKey), "심층 검증을 통과한 백업으로 주 세대를 자가 복구해야 합니다.");
        }

        [Test]
        public void LegacySchemaOneSaveNormalizesMissingCollectionsAndInterruptedResolution()
        {
            var legacy = WorldGenerator.Create(404);
            legacy.runId = "legacy-schema-one";
            legacy.turn = 7;
            legacy.phase = RunPhase.Resolving;
            legacy.planningPrepared = true;
            legacy.dynamicActions = null;
            legacy.ruleState = null;
            legacy.journal = null;
            PlayerPrefs.SetString(SaveKey, Envelope(legacy, 1));
            PlayerPrefs.Save();

            var recovered = RecoverBestSave();

            Assert.AreEqual(8, recovered.turn, "해결 도중 저장된 이전 세대는 이미 해결된 턴 다음 규칙 요청으로 복구해야 합니다.");
            Assert.AreEqual(RunPhase.AwaitingRules, recovered.phase);
            Assert.IsFalse(recovered.planningPrepared);
            Assert.IsNotNull(recovered.dynamicActions);
            Assert.IsNotNull(recovered.ruleState);
            Assert.IsNotNull(recovered.journal);
        }

        private static GameSnapshotV1 RecoverBestSave()
        {
            var method = typeof(GameController).GetMethod("RecoverBestSave", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "저장 복구 진입점을 찾지 못했습니다.");
            return (GameSnapshotV1)method.Invoke(null, null);
        }

        private static string Envelope(GameSnapshotV1 snapshot, int schemaVersion = 2)
        {
            var payload = JsonConvert.SerializeObject(snapshot);
            return JsonConvert.SerializeObject(new TestSaveEnvelope { schemaVersion = schemaVersion, payload = payload, checksum = Hash(payload) });
        }

        private static string Hash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (var character in value)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return hash.ToString("X8");
            }
        }

        private static IEnumerable<string> SaveKeys()
        {
            yield return SaveKey;
            yield return BackupKey;
            yield return TempKey;
        }
    }
}
