using UnityEngine;

namespace OnlyMyGame.Runtime
{
    /// <summary>Lightweight world-cue animation and camera-facing label.</summary>
    public sealed class RuleCuePulse : MonoBehaviour
    {
        private Transform pulseTarget;
        private Transform billboard;
        private Transform billboardCamera;
        private Vector3 baseScale;
        private Vector3 basePosition;
        private float phase;

        public void Configure(Transform target, Transform label, float phaseOffset, Transform cameraTransform = null)
        {
            pulseTarget = target;
            billboard = label;
            billboardCamera = cameraTransform;
            phase = phaseOffset;
            if (pulseTarget != null)
            {
                baseScale = pulseTarget.localScale;
                basePosition = pulseTarget.localPosition;
            }
        }

        private void LateUpdate()
        {
            var wave = (Mathf.Sin(Time.unscaledTime * 2.4f + phase) + 1f) * 0.5f;
            if (pulseTarget != null)
            {
                pulseTarget.localScale = baseScale * Mathf.Lerp(0.92f, 1.08f, wave);
                pulseTarget.localPosition = basePosition + Vector3.up * Mathf.Lerp(0f, 0.08f, wave);
            }
            if (billboard != null && billboardCamera != null) billboard.rotation = billboardCamera.rotation;
        }
    }
}
