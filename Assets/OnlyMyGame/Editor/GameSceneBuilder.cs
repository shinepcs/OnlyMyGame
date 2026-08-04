using OnlyMyGame.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace OnlyMyGame.EditorTools
{
    /// <summary>
    /// 게임 실행용 씬(OnlyMyGame)에 GameController, HUD, CommandBubble, SelectionRing,
    /// 카메라/조명을 배치하고 GameController의 SerializeField에 자동 연결한다.
    ///
    /// 사용법:
    ///   메뉴 OnlyMyGame/Build Game Scene → Assets/Scenes/OnlyMyGame.unity 에 전체 구성
    ///
    /// 이후 에디터에서 GameController 인스펙터의 필드를 직접 교체하면
    /// 런타임 Resources.Load 없이 씬 직렬화를 통해 프리팹/UI를 교체할 수 있다.
    /// </summary>
    public static class GameSceneBuilder
    {
        private const string ScenePath = "Assets/Scenes/OnlyMyGame.unity";
        private const string CatalogPath = "Assets/OnlyMyGame/Resources/OnlyMyGamePresentation.asset";

        [MenuItem("OnlyMyGame/Build Game Scene")]
        public static void BuildGameScene()
        {
            // 프리팹 카탈로그가 없으면 먼저 생성
            KayKitAssetBuilder.BuildAll();

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // 기존 구성 제거 (다시 빌드할 때 중복 방지)
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name.StartsWith("OnlyMyGame") || root.name == "GameHud" ||
                    root.name == "CommandBubble" || root.name == "SelectionRing" ||
                    root.name == "Quarter Camera" || root.name == "World Sun" ||
                    root.name == "EventSystem")
                {
                    Object.DestroyImmediate(root);
                }
            }

            // ==================== 카메라 ====================
            var camGo = new GameObject("Quarter Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.transform.position = new Vector3(0, 12, -10);
            cam.transform.rotation = Quaternion.Euler(52, 0, 0);
            cam.orthographic = true;
            cam.orthographicSize = 5.4f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(.035f, .07f, .12f);

            // ==================== 조명 ====================
            var sunGo = new GameObject("World Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, .91f, .73f);
            sun.intensity = 1.35f;
            sun.transform.rotation = Quaternion.Euler(50, -35, 0);

            // ==================== GameController ====================
            var controllerGo = new GameObject("OnlyMyGame");
            var controller = controllerGo.AddComponent<GameController>();

            // 카탈로그 연결
            var catalog = AssetDatabase.LoadAssetAtPath<GamePresentationCatalog>(CatalogPath);
            SetField(controller, "presentation", catalog);

            // 카메라/조명 연결
            SetField(controller, "mainCamera", cam);
            SetField(controller, "worldSun", sun);

            // ==================== 선택 링 ====================
            var ringGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ringGo.name = "SelectionRing";
            ringGo.transform.localScale = new Vector3(1.1f, 0.02f, 1.1f);
            var ringShader = Shader.Find("Universal Render Pipeline/Lit");
            if (ringShader != null)
            {
                foreach (var renderer in ringGo.GetComponentsInChildren<Renderer>())
                {
                    renderer.material = new Material(ringShader) { color = new Color(1f, .9f, .3f, .8f) };
                }
            }
            ringGo.SetActive(false);
            SetField(controller, "selectionRing", ringGo);

            // ==================== CommandBubble ====================
            var bubbleGo = new GameObject("CommandBubble");
            var bubble = bubbleGo.AddComponent<CommandBubble>();
            SetField(controller, "commandBubble", bubble);

            // ==================== HUD Canvas ====================
            var font = AssetDatabase.LoadAssetAtPath<Font>("Assets/OnlyMyGame/Resources/Fonts/NanumGothic-Regular.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var canvasGo = new GameObject("GameHud", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            SetField(controller, "hudCanvas", canvas);

            // ---- 상단 리소스 바 ----
            var topBar = CreatePanel("TopBar", canvasGo.transform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(0, 0), new Vector2(1920, 90), new Color(0f, 0f, 0f, 0.75f));
            var hudResources = CreateText("Resources", topBar.transform, new Vector2(20, -10), new Vector2(0, 1), new Vector2(0, 1), 22, Color.white, font, TextAnchor.UpperLeft);
            var hudGoal = CreateText("Goal", topBar.transform, new Vector2(20, -52), new Vector2(0, 1), new Vector2(0, 1), 16, new Color(1f, .9f, .4f), font, TextAnchor.UpperLeft);
            SetField(controller, "hudResources", hudResources);
            SetField(controller, "hudGoal", hudGoal);

            // ---- 하단 로그 ----
            var logPanel = CreatePanel("LogPanel", canvasGo.transform, new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(768, 180), new Color(0f, 0f, 0f, 0.6f));
            var hudLog = CreateText("Log", logPanel.transform, new Vector2(12, -8), new Vector2(0, 1), new Vector2(0, 1), 14, new Color(.85f, .85f, .85f), font, TextAnchor.UpperLeft);
            SetField(controller, "hudLog", hudLog);

            // ---- 우하단 턴 종료 버튼 ----
            var endTurnGo = new GameObject("EndTurnButton", typeof(RectTransform), typeof(Image), typeof(Button));
            endTurnGo.transform.SetParent(canvasGo.transform, false);
            var endRect = endTurnGo.GetComponent<RectTransform>();
            endRect.anchorMin = new Vector2(1, 0);
            endRect.anchorMax = new Vector2(1, 0);
            endRect.pivot = new Vector2(1, 0);
            endRect.anchoredPosition = new Vector2(-24, 24);
            endRect.sizeDelta = new Vector2(200, 56);
            endTurnGo.GetComponent<Image>().color = new Color(.2f, .5f, .9f, .95f);
            var endTurnButton = endTurnGo.GetComponent<Button>();
            SetField(controller, "endTurnButton", endTurnButton);
            var endTurnLabel = CreateText("EndTurnLabel", endTurnGo.transform, Vector2.zero, new Vector2(.5f, .5f), new Vector2(.5f, .5f), 20, Color.white, font, TextAnchor.MiddleCenter);
            endTurnLabel.text = "명령 확정 · 턴 종료";
            endTurnLabel.rectTransform.sizeDelta = new Vector2(200, 56);
            SetField(controller, "endTurnButtonText", endTurnLabel);

            // ---- AI 차단 패널 ----
            var blockPanel = CreatePanel("BlockPanel", canvasGo.transform, new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(420, 180), new Color(0f, 0f, 0f, 0.9f));
            var blockText = CreateText("BlockText", blockPanel.transform, new Vector2(0, 20), new Vector2(.5f, .5f), new Vector2(.5f, .5f), 16, Color.white, font, TextAnchor.MiddleCenter);
            blockText.rectTransform.sizeDelta = new Vector2(380, 100);
            SetField(controller, "blockPanel", blockPanel);
            SetField(controller, "blockText", blockText);
            var retryBtn = CreateButton("Retry", blockPanel.transform, new Vector2(-90, -60), new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(140, 36), "재시도", font);
            var quitBtn = CreateButton("Quit", blockPanel.transform, new Vector2(90, -60), new Vector2(.5f, .5f), new Vector2(.5f, .5f), new Vector2(140, 36), "저장 후 나가기", font);
            blockPanel.SetActive(false);

            // ==================== EventSystem (UI 클릭용) ====================
            if (Object.FindAnyObjectByType<EventSystem>() == null)
            {
                var esGo = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            }

            // ==================== 저장 ====================
            EditorUtility.SetDirty(controllerGo);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[OnlyMyGame] 게임 씬 구성 완료: " + ScenePath + "\nGameController 인스펙터에서 프리팹/UI를 교체할 수 있습니다.");
        }

        [MenuItem("OnlyMyGame/Open Game Scene")]
        public static void OpenGameScene()
        {
            BuildGameScene();
            EditorSceneManager.OpenScene(ScenePath);
        }

        // ==================== 헬퍼 ====================

        private static void SetField(Object target, string fieldName, Object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(fieldName);
            if (prop != null)
            {
                prop.objectReferenceValue = value;
                so.ApplyModifiedProperties();
            }
            else
            {
                Debug.LogWarning("[OnlyMyGame] 필드 없음: " + target.name + "." + fieldName);
            }
        }

        private static GameObject CreatePanel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            go.GetComponent<Image>().color = color;
            return go;
        }

        private static Text CreateText(string name, Transform parent, Vector2 pos, Vector2 anchorMin, Vector2 anchorMax, int size, Color color, Font font, TextAnchor alignment)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(600, 40);
            var text = go.GetComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        private static Button CreateButton(string name, Transform parent, Vector2 pos, Vector2 anchorMin, Vector2 anchorMax, Vector2 size, string label, Font font)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = size;
            go.GetComponent<Image>().color = new Color(.3f, .3f, .3f, .95f);
            var text = CreateText("Label", go.transform, Vector2.zero, new Vector2(.5f, .5f), new Vector2(.5f, .5f), 16, Color.white, font, TextAnchor.MiddleCenter);
            text.text = label;
            text.rectTransform.sizeDelta = size;
            return go.GetComponent<Button>();
        }
    }
}