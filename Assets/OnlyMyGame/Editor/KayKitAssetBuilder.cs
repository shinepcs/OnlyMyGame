using System.IO;
using OnlyMyGame.Runtime;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OnlyMyGame.EditorTools
{
    /// <summary>
    /// KayKit FBX 에셋을 프리팹으로 변환하고, 게임에 사용되는 모든 리소스를
    /// 한눈에 배치한 편집용 쇼케이스 씬을 생성한다.
    ///
    /// 사용법:
    ///   1. 메뉴 OnlyMyGame/Build KayKit Prefabs → 프리팹 + 카탈로그 생성
    ///   2. 메뉴 OnlyMyGame/Open Resource Showcase Scene → 편집용 씬 열기
    /// </summary>
    public static class KayKitAssetBuilder
    {
        private const string OutputRoot = "Assets/OnlyMyGame/Resources/KayKit";
        private const string CatalogPath = "Assets/OnlyMyGame/Resources/OnlyMyGamePresentation.asset";
        private const string ShowcaseScenePath = "Assets/Scenes/OnlyMyGame_ResourceShowcase.unity";

        [MenuItem("OnlyMyGame/Build KayKit Prefabs")]
        public static void BuildAll()
        {
            EnsureFolder(OutputRoot);
            var catalog = AssetDatabase.LoadAssetAtPath<GamePresentationCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<GamePresentationCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            // ==================== 지형 타일 ====================
            catalog.grassTile = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/tiles/base/hex_grass.fbx", "hex_grass", 1f);
            catalog.waterTile = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/tiles/base/hex_water.fbx", "hex_water", 1f);
            catalog.forestTile = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/tiles/base/hex_grass.fbx", "hex_forest", 1f);
            catalog.hillTile = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/tiles/base/hex_grass_sloped_low.fbx", "hex_hill", 1f);

            // ---- 지형 변형 ----
            catalog.riverTileA = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/tiles/rivers/hex_river_A.fbx", "hex_river_A", 1f);
            catalog.riverTileB = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/tiles/rivers/hex_river_B.fbx", "hex_river_B", 1f);
            catalog.riverTileC = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/tiles/rivers/hex_river_C.fbx", "hex_river_C", 1f);
            catalog.coastTileA = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/tiles/coast/hex_coast_A.fbx", "hex_coast_A", 1f);
            catalog.coastTileB = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/tiles/coast/hex_coast_B.fbx", "hex_coast_B", 1f);
            catalog.roadTileA = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/tiles/roads/hex_road_A.fbx", "hex_road_A", 1f);
            catalog.roadTileB = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/tiles/roads/hex_road_B.fbx", "hex_road_B", 1f);

            // ==================== 유닛 ====================
            catalog.playerUnit = BuildPrefab("Assets/KayKit/KayKit_Adventurers/Characters/fbx/Knight.fbx", "unit_knight", 0.5f);
            catalog.skeletonUnit = BuildPrefab("Assets/KayKit/KayKit_Skeletons/characters/fbx/Skeleton_Warrior.fbx", "unit_skeleton", 0.5f);
            catalog.neutralUnit = BuildPrefab("Assets/KayKit/KayKit_Adventurers/Characters/fbx/Mage.fbx", "unit_mage", 0.5f);
            catalog.rangerUnit = BuildPrefab("Assets/KayKit/KayKit_Adventurers/Characters/fbx/Ranger.fbx", "unit_ranger", 0.5f);
            catalog.rogueUnit = BuildPrefab("Assets/KayKit/KayKit_Adventurers/Characters/fbx/Rogue.fbx", "unit_rogue", 0.5f);
            catalog.barbarianUnit = BuildPrefab("Assets/KayKit/KayKit_Adventurers/Characters/fbx/Barbarian.fbx", "unit_barbarian", 0.5f);
            catalog.skeletonMageUnit = BuildPrefab("Assets/KayKit/KayKit_Skeletons/characters/fbx/Skeleton_Mage.fbx", "unit_skeleton_mage", 0.5f);
            catalog.skeletonRogueUnit = BuildPrefab("Assets/KayKit/KayKit_Skeletons/characters/fbx/Skeleton_Rogue.fbx", "unit_skeleton_rogue", 0.5f);

            // ==================== 건물 ====================
            // 주의: KayKit 팩에서 파란 성은 buildings/yellow 폴더에 있다 (색상 폴더명이 실제 색과 다름).
            catalog.playerHeadquarters = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/buildings/yellow/building_castle_blue.fbx", "building_castle_blue", 1f);
            catalog.enemyHeadquarters = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/buildings/red/building_castle_red.fbx", "building_castle_red", 1f);
            catalog.settlement = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/buildings/neutral/building_stage_A.fbx", "building_stage", 1f);
            catalog.lumbermill = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/buildings/green/building_lumbermill_green.fbx", "building_lumbermill", 1f);
            catalog.market = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/buildings/green/building_market_green.fbx", "building_market", 1f);
            catalog.blacksmith = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/buildings/green/building_blacksmith_green.fbx", "building_blacksmith", 1f);
            catalog.barracks = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/buildings/green/building_barracks_green.fbx", "building_barracks", 1f);
            catalog.tower = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/buildings/green/building_tower_A_green.fbx", "building_tower", 1f);
            catalog.church = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/buildings/green/building_church_green.fbx", "building_church", 1f);
            catalog.home = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/buildings/green/building_home_A_green.fbx", "building_home", 1f);
            catalog.well = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/buildings/green/building_well_green.fbx", "building_well", 1f);
            catalog.windmill = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/buildings/green/building_windmill_green.fbx", "building_windmill", 1f);

            // ==================== 장식 ====================
            catalog.treeDecoration = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/nature/tree_single_A.fbx", "tree_single", 1f);
            catalog.rockDecoration = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/nature/rock_single_A.fbx", "rock_single", 1f);
            catalog.mountainDecoration = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/nature/mountain_A.fbx", "mountain", 1f);
            catalog.hillDecoration = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/nature/hill_single_A.fbx", "hill_single", 1f);
            catalog.waterlilyDecoration = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/nature/waterlily_A.fbx", "waterlily", 1f);
            catalog.waterplantDecoration = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/nature/waterplant_A.fbx", "waterplant", 1f);
            catalog.cloudDecoration = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/nature/cloud_big.fbx", "cloud_big", 1f);

            // ==================== 자원 ====================
            catalog.resourceWood = BuildPrefab("Assets/KayKit/KayKit_ResourceBits/Assets/fbx(unity)/Wood_Log_Stack.fbx", "resource_wood", 1f);
            catalog.resourceStone = BuildPrefab("Assets/KayKit/KayKit_ResourceBits/Assets/fbx(unity)/Stone_Bricks_Stack_Large.fbx", "resource_stone", 1f);
            catalog.resourceIron = BuildPrefab("Assets/KayKit/KayKit_ResourceBits/Assets/fbx(unity)/Iron_Bars_Stack_Large.fbx", "resource_iron", 1f);
            catalog.resourceFood = BuildPrefab("Assets/KayKit/KayKit_ResourceBits/Assets/fbx(unity)/Pallet_Wood_Covered_A.fbx", "resource_food", 1f);
            catalog.resourceGold = BuildPrefab("Assets/KayKit/KayKit_ResourceBits/Assets/fbx(unity)/Gold_Bars_Stack_Large.fbx", "resource_gold", 1f);
            catalog.resourceCopper = BuildPrefab("Assets/KayKit/KayKit_ResourceBits/Assets/fbx(unity)/Copper_Bars_Stack_Large.fbx", "resource_copper", 1f);
            catalog.resourceTextile = BuildPrefab("Assets/KayKit/KayKit_ResourceBits/Assets/fbx(unity)/Textiles_Stack_Large.fbx", "resource_textile", 1f);

            // ==================== 깃발 ====================
            catalog.flagPlayer = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/props/flag_blue.fbx", "flag_blue", 1f);
            catalog.flagEnemy = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/props/flag_red.fbx", "flag_red", 1f);
            catalog.flagNeutral = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/props/flag_green.fbx", "flag_green", 1f);

            // ==================== 소품 ====================
            catalog.propBarrel = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/props/barrel.fbx", "prop_barrel", 1f);
            catalog.propCrate = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/props/crate_A_big.fbx", "prop_crate", 1f);
            catalog.propSack = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/props/sack.fbx", "prop_sack", 1f);
            catalog.propTent = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/props/tent.fbx", "prop_tent", 1f);
            catalog.propLadder = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/props/ladder.fbx", "prop_ladder", 1f);
            catalog.propWheelbarrow = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/props/wheelbarrow.fbx", "prop_wheelbarrow", 1f);
            catalog.propWeaponrack = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/props/weaponrack.fbx", "prop_weaponrack", 1f);
            catalog.propTarget = BuildPrefab("Assets/KayKit/KayKit_Medieval_Hexagon_Pack/Assets/fbx(unity)/decoration/props/target.fbx", "prop_target", 1f);

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[KayKit] 프리팹 변환 완료: " + OutputRoot);
        }

        [MenuItem("OnlyMyGame/Open Resource Showcase Scene")]
        public static void OpenShowcaseScene()
        {
            BuildAll();
            BuildShowcaseScene();
            EditorSceneManager.OpenScene(ShowcaseScenePath);
        }

        /// <summary>
        /// 카탈로그에 등록된 모든 리소스를 그리드로 배치한 편집용 씬을 생성한다.
        /// 각 항목은 이름 라벨과 함께 배치되어 어떤 프리셋이 어떤 모습인지
        /// 한눈에 확인하고 스케일/회전을 조정할 수 있다.
        /// </summary>
        [MenuItem("OnlyMyGame/Build Resource Showcase Scene")]
        public static void BuildShowcaseScene()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<GamePresentationCatalog>(CatalogPath);
            if (catalog == null)
            {
                Debug.LogError("[KayKit] 카탈로그가 없습니다. 먼저 OnlyMyGame/Build KayKit Prefabs를 실행하세요.");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "OnlyMyGame_ResourceShowcase";

            // 카메라
            var camGo = new GameObject("Showcase Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.transform.position = new Vector3(0, 14, -12);
            cam.transform.rotation = Quaternion.Euler(50, 0, 0);
            cam.orthographic = true;
            cam.orthographicSize = 7f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.08f, .1f, .14f);

            // 조명
            var lightGo = new GameObject("Showcase Sun");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, .91f, .73f);
            light.intensity = 1.3f;
            light.transform.rotation = Quaternion.Euler(50, -35, 0);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(.3f, .36f, .45f);

            // 바닥 그리드
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Showcase Floor";
            floor.transform.position = new Vector3(0, -0.01f, 0);
            floor.transform.localScale = new Vector3(30, 1, 30);
            var floorRenderer = floor.GetComponent<Renderer>();
            if (floorRenderer != null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader != null) floorRenderer.material = new Material(shader) { color = new Color(.12f, .14f, .18f) };
            }

            // 카탈로그의 모든 필드를 섹션별로 배치
            var sections = new (string title, (string label, Object asset)[] items)[]
            {
                ("지형 타일", new (string, Object)[]
                {
                    ("grassTile", catalog.grassTile), ("waterTile", catalog.waterTile),
                    ("forestTile", catalog.forestTile), ("hillTile", catalog.hillTile),
                    ("riverTileA", catalog.riverTileA), ("riverTileB", catalog.riverTileB),
                    ("riverTileC", catalog.riverTileC), ("coastTileA", catalog.coastTileA),
                    ("coastTileB", catalog.coastTileB), ("roadTileA", catalog.roadTileA),
                    ("roadTileB", catalog.roadTileB),
                }),
                ("유닛", new (string, Object)[]
                {
                    ("playerUnit", catalog.playerUnit), ("skeletonUnit", catalog.skeletonUnit),
                    ("neutralUnit", catalog.neutralUnit), ("rangerUnit", catalog.rangerUnit),
                    ("rogueUnit", catalog.rogueUnit), ("barbarianUnit", catalog.barbarianUnit),
                    ("skeletonMageUnit", catalog.skeletonMageUnit), ("skeletonRogueUnit", catalog.skeletonRogueUnit),
                }),
                ("건물", new (string, Object)[]
                {
                    ("playerHeadquarters", catalog.playerHeadquarters), ("enemyHeadquarters", catalog.enemyHeadquarters),
                    ("settlement", catalog.settlement), ("lumbermill", catalog.lumbermill),
                    ("market", catalog.market), ("blacksmith", catalog.blacksmith),
                    ("barracks", catalog.barracks), ("tower", catalog.tower),
                    ("church", catalog.church), ("home", catalog.home),
                    ("well", catalog.well), ("windmill", catalog.windmill),
                }),
                ("장식", new (string, Object)[]
                {
                    ("treeDecoration", catalog.treeDecoration), ("rockDecoration", catalog.rockDecoration),
                    ("mountainDecoration", catalog.mountainDecoration), ("hillDecoration", catalog.hillDecoration),
                    ("waterlilyDecoration", catalog.waterlilyDecoration), ("waterplantDecoration", catalog.waterplantDecoration),
                    ("cloudDecoration", catalog.cloudDecoration),
                }),
                ("자원", new (string, Object)[]
                {
                    ("resourceWood", catalog.resourceWood), ("resourceStone", catalog.resourceStone),
                    ("resourceIron", catalog.resourceIron), ("resourceFood", catalog.resourceFood),
                    ("resourceGold", catalog.resourceGold), ("resourceCopper", catalog.resourceCopper),
                    ("resourceTextile", catalog.resourceTextile),
                }),
                ("깃발", new (string, Object)[]
                {
                    ("flagPlayer", catalog.flagPlayer), ("flagEnemy", catalog.flagEnemy),
                    ("flagNeutral", catalog.flagNeutral),
                }),
                ("소품", new (string, Object)[]
                {
                    ("propBarrel", catalog.propBarrel), ("propCrate", catalog.propCrate),
                    ("propSack", catalog.propSack), ("propTent", catalog.propTent),
                    ("propLadder", catalog.propLadder), ("propWheelbarrow", catalog.propWheelbarrow),
                    ("propWeaponrack", catalog.propWeaponrack), ("propTarget", catalog.propTarget),
                }),
            };

            const float spacing = 2.2f;
            const float sectionGap = 3.5f;
            var z = 0f;

            foreach (var section in sections)
            {
                // 섹션 제목
                var title = CreateTextObject(section.title, new Vector3(0, 0.1f, z), 0.35f, new Color(1f, .85f, .3f));
                title.transform.SetParent(null, true);

                var x = -(section.items.Length - 1) * spacing * 0.5f;
                foreach (var (fieldName, asset) in section.items)
                {
                    if (asset == null)
                    {
                        Debug.LogWarning("[OnlyMyGame] 쇼케이스 누락: " + fieldName);
                        x += spacing;
                        continue;
                    }

                    var prefab = asset as GameObject;
                    if (prefab == null)
                    {
                        Debug.LogWarning("[OnlyMyGame] 쇼케이스 스킵 (GameObject 아님): " + fieldName);
                        x += spacing;
                        continue;
                    }

                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    instance.name = fieldName;
                    instance.transform.position = new Vector3(x, 0, z);
                    instance.transform.rotation = Quaternion.identity;

                    // 이름 라벨
                    var label = CreateTextObject(fieldName, new Vector3(x, 0.1f, z + 1.1f), 0.22f, Color.white);
                    label.transform.SetParent(instance.transform, true);

                    x += spacing;
                }

                z -= sectionGap;
            }

            EditorSceneManager.SaveScene(scene, ShowcaseScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[OnlyMyGame] 리소스 쇼케이스 씬 생성 완료: " + ShowcaseScenePath);
        }

        private const string TmpFontPath = "Assets/OnlyMyGame/Resources/Fonts/NanumGothic-Regular SDF.asset";

        private static GameObject CreateTextObject(string text, Vector3 position, float characterSize, Color color)
        {
            var go = new GameObject("Label_" + text);
            go.transform.position = position;

            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text;
            // 레거시 TextMesh(characterSize=0.22/0.35, fontSize=48)의 실제 렌더 높이 ≈ characterSize × 48 / 10.
            // TMP의 fontSize는 월드 단위 높이를 직접 제어하므로 동일한 시각적 크기로 환산한다.
            tmp.fontSize = characterSize * 4.8f;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = color;

            var font = LoadOrCreateTmpFont();
            if (font != null) tmp.font = font;

            return go;
        }

        /// <summary>
        /// NanumGothic으로 만든 TMP SDF 폰트 에셋을 반환한다.
        /// 아직 없으면 TTF에서 생성해 저장하고, 생성할 수 없으면 기본 TMP 폰트로 폴백한다.
        /// </summary>
        private static TMP_FontAsset LoadOrCreateTmpFont()
        {
            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(TmpFontPath);
            if (existing != null) return existing;

            var ttf = AssetDatabase.LoadAssetAtPath<Font>("Assets/OnlyMyGame/Resources/Fonts/NanumGothic-Regular.ttf");
            var fallback = TMP_Settings.instance != null ? TMP_Settings.defaultFontAsset : null;
            if (ttf == null) return fallback;

            // 명시적 파라미터로 생성: TMP Settings가 초기화되지 않은 배치모드에서도 동작한다.
            var created = TMP_FontAsset.CreateFontAsset(ttf, 90, 9, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic);
            if (created == null) return fallback;

            AssetDatabase.CreateAsset(created, TmpFontPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return created;
        }

        private static Object BuildPrefab(string fbxPath, string name, float scale)
        {
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (fbx == null)
            {
                Debug.LogWarning("[KayKit] FBX를 찾을 수 없음: " + fbxPath);
                return null;
            }
            var outputPath = OutputRoot + "/" + name + ".prefab";
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(outputPath);
            if (existing != null) AssetDatabase.DeleteAsset(outputPath);
            var instance = Object.Instantiate(fbx);
            instance.name = name;
            instance.transform.localScale = Vector3.one * scale;
            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, outputPath);
            Object.DestroyImmediate(instance);
            return prefab;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            var leaf = Path.GetFileName(path);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}