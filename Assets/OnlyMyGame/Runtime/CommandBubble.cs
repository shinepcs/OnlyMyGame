using System;
using System.Collections.Generic;
using UnityEngine;

namespace OnlyMyGame.Runtime
{
    /// <summary>
    /// 유닛/건물 위에 떠 있는 명령 버블 UI.
    /// 클릭한 대상이 사용 가능한 명령을 원형 버블로 표시한다.
    /// </summary>
    public sealed class CommandBubble : MonoBehaviour
    {
        [Serializable]
        public sealed class BubbleEntry
        {
            public string label;
            public string tooltip;
            public Color color = Color.white;
            public Action onClick;
        }

        private readonly List<BubbleEntry> entries = new List<BubbleEntry>();
        private readonly List<GameObject> bubbleObjects = new List<GameObject>();
        private Transform anchor;
        private float radius = 1.1f;
        private bool visible;

        public void Show(Transform anchorTransform, float bubbleRadius, List<BubbleEntry> items)
        {
            anchor = anchorTransform;
            radius = bubbleRadius;
            entries.Clear();
            entries.AddRange(items);
            visible = true;
            Rebuild();
        }

        public void Hide()
        {
            visible = false;
            foreach (var go in bubbleObjects) Destroy(go);
            bubbleObjects.Clear();
        }

        public bool IsVisible => visible;

        private void Update()
        {
            if (!visible || anchor == null) return;
            transform.position = anchor.position + Vector3.up * 1.4f;
            if (Camera.main != null) transform.rotation = Camera.main.transform.rotation;
        }

        private void Rebuild()
        {
            foreach (var go in bubbleObjects) Destroy(go);
            bubbleObjects.Clear();
            if (!visible || entries.Count == 0) return;

            var count = entries.Count;
            for (var i = 0; i < count; i++)
            {
                var entry = entries[i];
                var angle = (i / (float)count) * 360f;
                var rad = angle * Mathf.Deg2Rad;
                var pos = new Vector3(Mathf.Cos(rad) * radius, 0f, Mathf.Sin(rad) * radius);

                var bubble = CreateBubble(entry, pos);
                bubbleObjects.Add(bubble);
            }
        }

        private GameObject CreateBubble(BubbleEntry entry, Vector3 localPos)
        {
            var go = new GameObject("Bubble_" + entry.label);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPos;

            // 배경 원판
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "Disc";
            disc.transform.SetParent(go.transform, false);
            disc.transform.localScale = new Vector3(0.42f, 0.05f, 0.42f);
            disc.transform.localPosition = Vector3.zero;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader != null)
            {
                foreach (var renderer in disc.GetComponentsInChildren<Renderer>())
                {
                    renderer.material = new Material(shader) { color = entry.color };
                }
            }
            else
            {
                foreach (var renderer in disc.GetComponentsInChildren<Renderer>())
                {
                    renderer.material.color = entry.color;
                }
            }

            // 라벨 텍스트
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            labelGo.transform.localPosition = Vector3.up * 0.12f;
            var text = labelGo.AddComponent<TextMesh>();
            text.text = entry.label;
            text.characterSize = 0.16f;
            text.fontSize = 48;
            text.anchor = TextAnchor.MiddleCenter;
            text.color = Color.white;
            var font = Resources.Load<Font>("Fonts/NanumGothic-Regular");
            if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null) text.font = font;
            if (Camera.main != null) labelGo.transform.rotation = Camera.main.transform.rotation;

            // 클릭 감지
            var clicker = go.AddComponent<BubbleClicker>();
            clicker.Init(entry);

            return go;
        }
    }

    /// <summary>
    /// 버블 클릭 감지용 헬퍼. Physics.Raycast로 클릭을 감지한다.
    /// </summary>
    public sealed class BubbleClicker : MonoBehaviour
    {
        private CommandBubble.BubbleEntry entry;

        public void Init(CommandBubble.BubbleEntry e) => entry = e;

        public void Trigger()
        {
            entry?.onClick?.Invoke();
        }

        private void OnMouseDown()
        {
            Trigger();
        }
    }
}