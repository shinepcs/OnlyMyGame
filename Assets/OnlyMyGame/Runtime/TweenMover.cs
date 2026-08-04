using System;
using System.Collections;
using UnityEngine;

namespace OnlyMyGame.Runtime
{
    /// <summary>
    /// 유닛/건물을 부드러운 트윈(보간)으로 이동시키는 컴포넌트.
    /// 점프 이동 대신 ease-in-out 곡선으로 자연스럽게 움직인다.
    /// </summary>
    public sealed class TweenMover : MonoBehaviour
    {
        [SerializeField] private float moveDuration = 0.45f;
        [SerializeField] private float hopHeight = 0.35f;
        [SerializeField] private AnimationCurve easeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        private Coroutine moveRoutine;

        public bool IsMoving => moveRoutine != null;

        public void MoveTo(Vector3 target, Action onComplete = null)
        {
            if (moveRoutine != null) StopCoroutine(moveRoutine);
            moveRoutine = StartCoroutine(MoveRoutine(target, onComplete));
        }

        public void SnapTo(Vector3 target)
        {
            if (moveRoutine != null) StopCoroutine(moveRoutine);
            moveRoutine = null;
            transform.position = target;
        }

        private IEnumerator MoveRoutine(Vector3 target, Action onComplete)
        {
            var start = transform.position;
            var elapsed = 0f;
            while (elapsed < moveDuration)
            {
                elapsed += Time.deltaTime;
                var t = Mathf.Clamp01(elapsed / moveDuration);
                var eased = easeCurve.Evaluate(t);
                var flat = Vector3.Lerp(start, target, eased);
                var hop = Mathf.Sin(t * Mathf.PI) * hopHeight;
                transform.position = flat + Vector3.up * hop;
                yield return null;
            }
            transform.position = target;
            moveRoutine = null;
            onComplete?.Invoke();
        }
    }
}