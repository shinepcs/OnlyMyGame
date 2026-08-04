using UnityEngine;
using UnityEngine.EventSystems;

namespace OnlyMyGame.Runtime
{
    /// <summary>Keyboard, wheel and middle-drag controls for the quarter-view map.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class QuarterViewCameraController : MonoBehaviour
    {
        private Camera controlledCamera;
        private Vector3 targetPosition;
        private float targetZoom;
        private Vector3 dragOrigin;
        private bool dragging;
        private const float MinZoom = 4.4f;
        private const float MaxZoom = 10.5f;
        private const float BoundsX = 13.5f;
        private const float BoundsZ = 11.5f;

        private void Awake()
        {
            controlledCamera = GetComponent<Camera>();
            targetPosition = transform.position;
            targetZoom = controlledCamera.orthographic ? controlledCamera.orthographicSize : 6.5f;
        }

        public void Configure(float initialZoom)
        {
            controlledCamera = GetComponent<Camera>();
            controlledCamera.orthographic = true;
            targetZoom = Mathf.Clamp(initialZoom, MinZoom, MaxZoom);
            controlledCamera.orthographicSize = targetZoom;
            targetPosition = transform.position;
        }

        public void Focus(Vector3 worldPosition, bool immediate = false)
        {
            // Camera is pitched on X only, so keep its height and viewing offset.
            targetPosition.x = Mathf.Clamp(worldPosition.x, -BoundsX, BoundsX);
            targetPosition.z = Mathf.Clamp(worldPosition.z - 10f, -BoundsZ - 10f, BoundsZ - 10f);
            if (immediate) transform.position = targetPosition;
        }

        private void Update()
        {
            var dt = Mathf.Min(0.05f, Time.unscaledDeltaTime);
            var overUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            var input = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
            if (input.sqrMagnitude > 1f) input.Normalize();
            if (input.sqrMagnitude > 0.01f)
            {
                var speed = Mathf.Lerp(5.5f, 10f, Mathf.InverseLerp(MinZoom, MaxZoom, targetZoom));
                targetPosition += new Vector3(input.x, 0, input.y) * speed * dt;
            }

            if (!overUi)
            {
                var wheel = Input.mouseScrollDelta.y;
                if (Mathf.Abs(wheel) > 0.01f) targetZoom = Mathf.Clamp(targetZoom - wheel * 0.75f, MinZoom, MaxZoom);
                if (Input.GetMouseButtonDown(2))
                {
                    dragOrigin = Input.mousePosition;
                    dragging = true;
                }
                if (dragging && Input.GetMouseButton(2))
                {
                    var delta = Input.mousePosition - dragOrigin;
                    dragOrigin = Input.mousePosition;
                    var scale = targetZoom / Mathf.Max(320f, Screen.height) * 2f;
                    targetPosition += new Vector3(-delta.x * scale, 0, -delta.y * scale);
                }
            }
            if (Input.GetMouseButtonUp(2)) dragging = false;

            targetPosition.x = Mathf.Clamp(targetPosition.x, -BoundsX, BoundsX);
            targetPosition.z = Mathf.Clamp(targetPosition.z, -BoundsZ - 10f, BoundsZ - 10f);
            transform.position = Vector3.Lerp(transform.position, targetPosition, 1f - Mathf.Exp(-10f * dt));
            controlledCamera.orthographicSize = Mathf.Lerp(controlledCamera.orthographicSize, targetZoom, 1f - Mathf.Exp(-12f * dt));
        }
    }
}
