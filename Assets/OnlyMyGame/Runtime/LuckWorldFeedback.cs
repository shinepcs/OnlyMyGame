using UnityEngine;

namespace OnlyMyGame.Runtime
{
    /// <summary>
    /// Keeps luck visible in the world and turns each luck change into a short,
    /// readable character reaction without interfering with positional movement.
    /// </summary>
    public sealed class LuckWorldFeedback : MonoBehaviour
    {
        private const float CelebrationDuration = 1.25f;
        private static readonly int BaseColorProperty = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorProperty = Shader.PropertyToID("_Color");

        private Transform marker;
        private Transform label;
        private Transform billboardCamera;
        private TextMesh labelText;
        private Transform character;
        private Vector3 markerBasePosition;
        private Vector3 markerBaseScale;
        private Vector3 labelBasePosition;
        private Vector3 labelBaseScale;
        private Vector3 characterBaseScale;
        private Quaternion characterBaseRotation;
        private float celebrationStartedAt = float.NegativeInfinity;
        private int currentLuck = -1;

        public int CurrentLuck => currentLuck;
        public bool IsCelebrating => Time.unscaledTime - celebrationStartedAt < CelebrationDuration;

        public void Configure(Transform signMarker, Transform signLabel, TextMesh text, Transform cameraTransform = null)
        {
            marker = signMarker;
            label = signLabel;
            labelText = text;
            billboardCamera = cameraTransform;
            if (marker != null)
            {
                markerBasePosition = marker.localPosition;
                markerBaseScale = marker.localScale;
            }
            if (label != null)
            {
                labelBasePosition = label.localPosition;
                labelBaseScale = label.localScale;
            }
        }

        public void BindCharacter(Transform nextCharacter)
        {
            if (character == nextCharacter) return;
            RestoreCharacter();
            character = nextCharacter;
            if (character == null) return;
            characterBaseScale = character.localScale;
            characterBaseRotation = character.localRotation;
        }

        public void SetLuck(int luck, bool celebrate)
        {
            var clamped = Mathf.Clamp(luck, 1, 100);
            var changed = currentLuck >= 0 && currentLuck != clamped;
            currentLuck = clamped;
            var color = LuckColor(clamped);
            if (labelText != null)
            {
                labelText.text = "☀  행운 " + clamped + "\n" + LuckMessage(clamped);
                labelText.color = color;
            }
            if (marker != null)
            {
                foreach (var renderer in marker.GetComponentsInChildren<Renderer>(true))
                {
                    var properties = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(properties);
                    properties.SetColor(BaseColorProperty, color);
                    properties.SetColor(ColorProperty, color);
                    renderer.SetPropertyBlock(properties);
                }
            }
            if (celebrate && changed) celebrationStartedAt = Time.unscaledTime;
        }

        private void Update()
        {
            var time = Time.unscaledTime;
            if (marker != null)
            {
                marker.localPosition = markerBasePosition + Vector3.up * (Mathf.Sin(time * 2.6f) * .045f);
                marker.localScale = markerBaseScale;
            }
            if (label != null)
            {
                label.localPosition = labelBasePosition + Vector3.up * (Mathf.Sin(time * 2.6f + .45f) * .055f);
                label.localScale = labelBaseScale;
                if (billboardCamera != null) label.rotation = billboardCamera.rotation;
            }

            if (!IsCelebrating)
            {
                RestoreCharacter();
                return;
            }

            var progress = Mathf.Clamp01((time - celebrationStartedAt) / CelebrationDuration);
            var envelope = Mathf.Sin(progress * Mathf.PI);
            if (marker != null) marker.localScale = markerBaseScale * (1f + envelope * .22f);
            if (label != null) label.localScale = labelBaseScale * (1f + envelope * .14f);
            if (character != null)
            {
                character.localScale = characterBaseScale * (1f + envelope * .13f);
                character.localRotation = characterBaseRotation * Quaternion.Euler(0f, Mathf.Sin(progress * Mathf.PI * 4f) * 14f * envelope, 0f);
            }
        }

        private void OnDisable()
        {
            RestoreCharacter();
        }

        private void OnDestroy()
        {
            RestoreCharacter();
        }

        private void RestoreCharacter()
        {
            if (character == null) return;
            character.localScale = characterBaseScale;
            character.localRotation = characterBaseRotation;
        }

        private static Color LuckColor(int luck)
        {
            if (luck >= 70) return new Color(.3f, .95f, 1f, 1f);
            if (luck >= 35) return new Color(1f, .78f, .28f, 1f);
            return new Color(1f, .38f, .34f, 1f);
        }

        private static string LuckMessage(int luck)
        {
            if (luck >= 70) return "호운 · 전투와 수렵 보너스";
            if (luck >= 35) return "평운 · 기본 확률 적용";
            return "흉운 · 충돌 변수 주의";
        }
    }
}
