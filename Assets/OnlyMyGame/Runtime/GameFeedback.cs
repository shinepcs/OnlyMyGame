using System.Collections;
using UnityEngine;

namespace OnlyMyGame.Runtime
{
    /// <summary>Small, allocation-light feedback layer for selections, commands and combat.</summary>
    public sealed class GameFeedback : MonoBehaviour
    {
        private AudioSource source;
        private AudioClip selectClip;
        private AudioClip commandClip;
        private AudioClip hitClip;
        private Camera worldCamera;
        private Coroutine shakeRoutine;

        public void Initialize(Camera camera)
        {
            worldCamera = camera;
            source = gameObject.GetComponent<AudioSource>();
            if (source == null) source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.volume = 0.22f;
            selectClip = Tone("Select", 620f, 0.055f, 0.16f);
            commandClip = Tone("Command", 410f, 0.09f, 0.2f);
            hitClip = Tone("Hit", 145f, 0.12f, 0.28f);
        }

        public void Selection(Vector3 position)
        {
            Play(selectClip, 0.8f);
            Burst(position + Vector3.up * 0.25f, new Color(0.2f, 0.82f, 1f), 8, 0.08f);
        }

        public void CommandQueued(Vector3 position)
        {
            Play(commandClip, 0.9f);
            Burst(position + Vector3.up * 0.25f, new Color(1f, 0.78f, 0.28f), 10, 0.1f);
        }

        public void Hit(Vector3 position, int damage, bool defeated)
        {
            Play(hitClip, defeated ? 0.72f : 1f);
            FloatingText(position + Vector3.up * 1.1f, "-" + damage, defeated ? new Color(1f, 0.72f, 0.2f) : new Color(1f, 0.3f, 0.32f));
            Burst(position + Vector3.up * 0.55f, defeated ? new Color(1f, 0.72f, 0.2f) : new Color(1f, 0.28f, 0.3f), defeated ? 24 : 14, defeated ? 0.18f : 0.12f);
            if (shakeRoutine != null) StopCoroutine(shakeRoutine);
            shakeRoutine = StartCoroutine(Shake(defeated ? 0.16f : 0.09f, defeated ? 0.18f : 0.1f));
        }

        public void Reward(Vector3 position, string text)
        {
            Play(commandClip, 1.25f);
            FloatingText(position + Vector3.up * 1.1f, text, new Color(0.45f, 1f, 0.56f));
            Burst(position + Vector3.up * 0.4f, new Color(0.45f, 1f, 0.56f), 12, 0.1f);
        }

        private void Play(AudioClip clip, float pitch)
        {
            if (source == null || clip == null) return;
            source.pitch = pitch;
            source.PlayOneShot(clip);
        }

        private void FloatingText(Vector3 position, string value, Color color)
        {
            var go = new GameObject("Feedback_" + value);
            go.transform.position = position;
            var text = go.AddComponent<TextMesh>();
            text.text = value;
            text.fontSize = 54;
            text.characterSize = 0.18f;
            text.anchor = TextAnchor.MiddleCenter;
            text.fontStyle = FontStyle.Bold;
            text.color = color;
            var font = Resources.Load<Font>("Fonts/NanumGothic-Regular") ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null) text.font = font;
            if (worldCamera != null) go.transform.rotation = worldCamera.transform.rotation;
            var mover = go.AddComponent<TweenMover>();
            mover.MoveTo(position + Vector3.up * 0.8f);
            Destroy(go, 1.1f);
        }

        private void Burst(Vector3 position, Color color, int count, float size)
        {
            var go = new GameObject("FeedbackBurst");
            go.transform.position = position;
            var particles = go.AddComponent<ParticleSystem>();
            // A newly added ParticleSystem starts immediately because playOnAwake is
            // enabled by default. Duration and other structural settings may only be
            // changed while fully stopped, otherwise Unity raises a runtime Assert.
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var particleRenderer = go.GetComponent<ParticleSystemRenderer>();
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Particles/Standard Unlit");
            if (particleRenderer != null && shader != null) particleRenderer.material = new Material(shader);
            var main = particles.main;
            main.playOnAwake = false;
            main.duration = 0.2f;
            main.loop = false;
            main.startLifetime = 0.45f;
            main.startSpeed = 1.25f;
            main.startSize = size;
            main.startColor = color;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 32;
            var emission = particles.emission;
            emission.enabled = false;
            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.16f;
            particles.Play(true);
            particles.Emit(count);
            Destroy(go, 1.2f);
        }

        private IEnumerator Shake(float duration, float strength)
        {
            if (worldCamera == null) yield break;
            var origin = worldCamera.transform.localPosition;
            var elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                worldCamera.transform.localPosition = origin + Random.insideUnitSphere * strength * (1f - elapsed / duration);
                yield return null;
            }
            worldCamera.transform.localPosition = origin;
            shakeRoutine = null;
        }

        private static AudioClip Tone(string name, float frequency, float duration, float amplitude)
        {
            const int sampleRate = 22050;
            var samples = Mathf.Max(1, Mathf.CeilToInt(sampleRate * duration));
            var data = new float[samples];
            for (var i = 0; i < samples; i++)
            {
                var t = i / (float)sampleRate;
                var envelope = 1f - i / (float)samples;
                data[i] = Mathf.Sin(2f * Mathf.PI * frequency * t) * amplitude * envelope * envelope;
            }
            var clip = AudioClip.Create(name, samples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
