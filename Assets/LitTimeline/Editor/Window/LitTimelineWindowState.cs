using System.Collections.Generic;
using LitMotion;
using UnityEditor;
using UnityEngine;

namespace LitTimeline.Editor
{
    public enum WindowMode
    {
        NoSelection,
        NoComponent,
        NoClip,
        HasController
    }

    public class LitTimelineWindowState
    {
        public WindowMode Mode { get; private set; } = WindowMode.NoSelection;
        public LitTimelineController Controller { get; private set; }
        public TimelineEntryData SelectedEntry { get; set; }
        public float CurrentTime { get; private set; }
        public bool IsPreviewPlaying { get; private set; }
        public float PreviewSpeed { get; set; } = 1f;
        public bool PreviewLoop { get; set; } = true;

        private Dictionary<string, PropertyValueUnion> _snapshot;
        private Dictionary<string, PropertyValueUnion> _useCurrentStartCache = new Dictionary<string, PropertyValueUnion>();
        private bool _snapshotTaken;
        private double _lastEditorTime;

        // ─── Preview mode ─────────────────────────────────────────────────────

        public bool IsPreviewEnabled { get; private set; }

        public void EnterPreviewMode()
        {
            if (IsPreviewEnabled || Controller == null) return;
            IsPreviewEnabled = true;
            _useCurrentStartCache.Clear();
            EnsureSnapshot();
            GotoTime(0f);
        }

        public void ExitPreviewMode()
        {
            if (!IsPreviewEnabled) return;
            if (IsPreviewPlaying)
            {
                EditorApplication.update -= OnEditorUpdate;
                IsPreviewPlaying = false;
            }

            CurrentTime = 0f;
            _lastAppliedTime = -1f;
            _useCurrentStartCache.Clear();
            RestoreSnapshot();
            IsPreviewEnabled = false;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        // ─── Selection ────────────────────────────────────────────────────────

        public void Evaluate()
        {
            var go = Selection.activeGameObject;
            if (go == null)
            {
                Set(WindowMode.NoSelection, null);
                return;
            }

            var ctrl = go.GetComponentInParent<LitTimelineController>();
            if (ctrl == null)
            {
                Set(WindowMode.NoComponent, null);
                return;
            }

            if (ctrl.Clip == null)
            {
                Set(WindowMode.NoClip, ctrl);
                return;
            }

            Set(WindowMode.HasController, ctrl);
        }

        private void Set(WindowMode mode, LitTimelineController ctrl)
        {
            if (mode != WindowMode.HasController) ExitPreviewMode();
            Mode = mode;
            Controller = ctrl;
            if (SelectedEntry != null && (Controller?.Sequence == null || !Controller.Sequence.entries.Contains(SelectedEntry)))
                SelectedEntry = null;
        }

        // ─── Scrubbing (manual interpolation — no LitMotion sequence needed) ───

        public void GotoTime(float time)
        {
            if (!IsPreviewEnabled || Controller == null || Controller.Sequence == null) return;
            EnsureSnapshot();

            float total = Mathf.Max(0.001f, Controller.Sequence.TotalDuration);
            CurrentTime = Mathf.Clamp(time, 0f, total);
            ApplyAtTime(CurrentTime);
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        // Directly interpolate and apply all entry values at the given time.
        // Bypasses LitMotion so it works reliably in Edit Mode without an active sequence.
        private float _lastAppliedTime = -1f;

        private void ApplyAtTime(float time)
        {
            if (Controller == null || Controller.Sequence == null) return;

            bool scrubbingBackward = time < _lastAppliedTime;
            if (scrubbingBackward)
                _useCurrentStartCache.Clear();
            _lastAppliedTime = time;

            // Pass 1: apply pre-tween start values for entries that haven't started yet.
            foreach (var entry in Controller.Sequence.entries)
            {
                if (!entry.isEnabled || entry.binding == null) continue;
                if (time >= entry.delay) continue;

                _useCurrentStartCache.Remove(entry.entryId);
                if (entry.useCurrentAsStart) continue;

                var accessor = PropertyAccessorRegistry.Get(entry.binding.componentTypeName, entry.binding.propertyName);
                var component = ResolveComponent(Controller, entry.binding);
                if (accessor == null || component == null) continue;

                PropertyValueUnion preStart;
                if (!string.IsNullOrEmpty(entry.linkedStartEntryId))
                {
                    var linked = Controller.Sequence.entries.Find(e => e.entryId == entry.linkedStartEntryId);
                    preStart = linked != null ? linked.endValue : entry.startValue;
                }
                else
                    preStart = entry.startValue;

                accessor.ApplyValue(component, preStart);
            }

            // Pass 2: apply interpolated values for active and completed entries.
            foreach (var entry in Controller.Sequence.entries)
            {
                if (!entry.isEnabled || entry.binding == null) continue;
                if (time < entry.delay) continue;

                var accessor = PropertyAccessorRegistry.Get(entry.binding.componentTypeName, entry.binding.propertyName);
                var component = ResolveComponent(Controller, entry.binding);
                if (accessor == null || component == null) continue;

                float localT;
                if (entry.EffectiveDuration <= 0f || time >= entry.delay + entry.EffectiveDuration)
                    localT = 1f;
                else
                    localT = (time - entry.delay) / entry.EffectiveDuration;

                float easedT = entry.useCustomCurve && entry.customEaseCurve != null
                    ? (localT <= 0f ? 0f : localT >= 1f ? 1f : entry.customEaseCurve.Evaluate(localT))
                    : EaseUtility.Evaluate(localT, entry.ease);

                PropertyValueUnion from;
                if (entry.useCurrentAsStart)
                {
                    if (!_useCurrentStartCache.TryGetValue(entry.entryId, out from))
                    {
                        from = accessor.ReadValue(component);
                        _useCurrentStartCache[entry.entryId] = from;
                    }
                }
                else if (!string.IsNullOrEmpty(entry.linkedStartEntryId))
                {
                    var linked = Controller.Sequence.entries.Find(e => e.entryId == entry.linkedStartEntryId);
                    from = linked != null ? linked.endValue : entry.startValue;
                }
                else
                    from = entry.startValue;

                accessor.ApplyValue(component, LerpValue(from, entry.endValue, easedT));
            }
        }

        private static PropertyValueUnion LerpValue(PropertyValueUnion a, PropertyValueUnion b, float t)
        {
            var r = new PropertyValueUnion { type = a.type };
            switch (a.type)
            {
                case PropertyType.Float: r.floatValue = Mathf.Lerp(a.floatValue, b.floatValue, t); break;
                case PropertyType.Vector2: r.vector2Value = Vector2.Lerp(a.vector2Value, b.vector2Value, t); break;
                case PropertyType.Vector3: r.vector3Value = Vector3.Lerp(a.vector3Value, b.vector3Value, t); break;
                case PropertyType.Color: r.colorValue = Color.Lerp(a.colorValue, b.colorValue, t); break;
            }

            return r;
        }

        // ─── Playback (editor update loop) ────────────────────────────────────

        public void StartPreview()
        {
            if (Controller == null) return;
            EnsureSnapshot();
            _lastEditorTime = EditorApplication.timeSinceStartup;
            if (!IsPreviewPlaying)
            {
                EditorApplication.update += OnEditorUpdate;
                IsPreviewPlaying = true;
            }
        }

        public void PausePreview()
        {
            if (!IsPreviewPlaying) return;
            EditorApplication.update -= OnEditorUpdate;
            IsPreviewPlaying = false;
        }

        public void StopPreview()
        {
            if (!IsPreviewEnabled) return;
            if (IsPreviewPlaying)
            {
                EditorApplication.update -= OnEditorUpdate;
                IsPreviewPlaying = false;
            }

            GotoTime(0f);
        }

        public void RewindPreview()
        {
            bool wasPlaying = IsPreviewPlaying;
            PausePreview();
            GotoTime(0f);
            if (wasPlaying) StartPreview();
        }

        private void OnEditorUpdate()
        {
            if (Controller == null || Controller.Sequence == null)
            {
                IsPreviewPlaying = false;
                EditorApplication.update -= OnEditorUpdate;
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            float delta = (float)(now - _lastEditorTime);
            _lastEditorTime = now;

            float total = Mathf.Max(0.001f, Controller.Sequence.TotalDuration);
            float newTime = CurrentTime + delta * Controller.Sequence.timeScale * PreviewSpeed;
            if (newTime >= total)
            {
                if (PreviewLoop)
                    newTime %= total;
                else
                {
                    GotoTime(total);
                    PausePreview();
                    return;
                }
            }

            GotoTime(newTime);
        }

        // ─── Snapshot ─────────────────────────────────────────────────────────

        private void EnsureSnapshot()
        {
            if (_snapshotTaken) return;
            TakeSnapshot();
            _snapshotTaken = true;
        }

        private void TakeSnapshot()
        {
            _snapshot = new Dictionary<string, PropertyValueUnion>();
            if (Controller == null || Controller.Sequence == null) return;

            foreach (var entry in Controller.Sequence.entries)
            {
                if (!entry.isEnabled || entry.binding == null) continue;
                string key = SnapshotKey(entry);
                if (_snapshot.ContainsKey(key)) continue;

                var accessor = PropertyAccessorRegistry.Get(entry.binding.componentTypeName, entry.binding.propertyName);
                var component = ResolveComponent(Controller, entry.binding);
                if (accessor != null && component != null)
                    _snapshot[key] = accessor.ReadValue(component);
            }
        }

        private void RestoreSnapshot()
        {
            if (_snapshot == null || Controller == null || Controller.Sequence == null) return;

            foreach (var entry in Controller.Sequence.entries)
            {
                if (!entry.isEnabled || entry.binding == null) continue;
                string key = SnapshotKey(entry);
                if (!_snapshot.TryGetValue(key, out var saved)) continue;

                var accessor = PropertyAccessorRegistry.Get(entry.binding.componentTypeName, entry.binding.propertyName);
                var component = ResolveComponent(Controller, entry.binding);
                if (accessor != null && component != null)
                    accessor.ApplyValue(component, saved);
            }

            _snapshot = null;
            _snapshotTaken = false;
        }

        private static string SnapshotKey(TimelineEntryData e) =>
            $"{e.binding.hierarchyPath}|{e.binding.componentTypeName}|{e.binding.propertyName}";

        internal static UnityEngine.Component ResolveComponent(LitTimelineController ctrl, PropertyBinding binding)
        {
            Transform t = string.IsNullOrEmpty(binding.hierarchyPath)
                ? ctrl.transform
                : ctrl.transform.Find(binding.hierarchyPath);
            if (t == null) return null;

            var type = System.Type.GetType(binding.componentTypeName);
            if (type == null)
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType(binding.componentTypeName);
                    if (type != null) break;
                }

            return type != null ? t.GetComponent(type) : null;
        }
    }
}
