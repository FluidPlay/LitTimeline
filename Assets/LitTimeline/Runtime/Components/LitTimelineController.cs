using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LitMotion;
using UnityEngine;
using UnityEngine.Events;

[assembly: InternalsVisibleTo("LitTimeline.Editor")]

namespace LitTimeline
{
    [Serializable] public class TimelineLoopUnityEvent : UnityEvent<int>
    {
    }

    [Serializable] public class TimelineStringUnityEvent : UnityEvent<string>
    {
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("LitTimeline/Lit Timeline Controller")]
    public class LitTimelineController : MonoBehaviour
    {
        [SerializeField] private LitTimelineClip clip;

        //[Header("Events")] --Dup
        [SerializeField] private UnityEvent _onPlay = new UnityEvent();
        [SerializeField] private UnityEvent _onPause = new UnityEvent();
        [SerializeField] private UnityEvent _onStop = new UnityEvent();
        [SerializeField] private UnityEvent _onComplete = new UnityEvent();
        [SerializeField] private TimelineLoopUnityEvent _onLoop = new TimelineLoopUnityEvent();
        [SerializeField] private TimelineStringUnityEvent _onSpineEvent = new TimelineStringUnityEvent();

        // C# events for code subscribers
        public event Action OnPlay;
        public event Action OnPause;
        public event Action OnStop;
        public event Action OnComplete;
        public event Action<int> OnLoop;
        public event Action<string> OnSpineEvent;

        // LitMotion does not expose Pause or reverse playback, so the controller advances the
        // sequence handle's Time manually. The built handle is held paused (PlaybackSpeed = 0)
        // and driven from Update so Play / Pause / backward / Goto all behave predictably.
        private MotionHandle _seqHandle;
        private double _time;
        private int _direction = 1;
        private bool _isPlaying;
        private bool _isComplete;

        // Spine layers are driven outside LitMotion, so the Update loop must run even
        // when the built LitMotion sequence is empty. Recomputed in Rebuild().
        private bool _hasSpineLayers;

        // Callback polling
        private double _lastPollTime;
        private bool _polling;

        private Dictionary<string, TimelineEntryData> _entryCache;
        private Dictionary<string, EventMarkerData> _markerCache;

        // ── Clip ─────────────────────────────────────────────────────────────
        public LitTimelineClip Clip => clip;
        public TimelineSequenceData Sequence => clip != null ? clip.Data : null;

        // ── State ─────────────────────────────────────────────────────────────

        // Sequence is "live" if either a LitMotion handle is active OR the clip has
        // Spine layers (which we drive ourselves, with no backing MotionHandle).
        private bool IsBuilt => _seqHandle.IsActive() || _hasSpineLayers;

        /// <summary>Sequence is built and actively playing.</summary>
        public bool IsPlaying => IsBuilt && _isPlaying;

        /// <summary>Sequence is built but paused mid-playback.</summary>
        public bool IsPaused => IsBuilt && !_isPlaying && !_isComplete;

        /// <summary>True after the sequence played to its end (reset on next Play).</summary>
        public bool IsComplete => _isComplete;

        /// <summary>Total duration in seconds (read from clip data).</summary>
        public float Duration => Sequence?.TotalDuration ?? 0f;

        /// <summary>Elapsed playback time in seconds.</summary>
        public float CurrentTime => IsBuilt ? (float)_time : 0f;

        /// <summary>Elapsed time as 0–1 normalized value.</summary>
        public float NormalizedTime => Duration > 0f ? Mathf.Clamp01(CurrentTime / Duration) : 0f;

        /// <summary>Playback speed multiplier. Stored on the clip data and applied while playing.</summary>
        public float TimeScale
        {
            get => Sequence?.timeScale ?? 1f;
            set
            {
                if (Sequence != null) Sequence.timeScale = value;
            }
        }

        // ── Entry lookup ──────────────────────────────────────────────────────

        /// <summary>
        /// Access an entry by its display name. Returns null if not found.
        /// Example: ctrl["Cube - Fade"].OnComplete += OnFadeDone;
        /// </summary>
        public TimelineEntryData this[string entryName]
        {
            get
            {
                EnsureEntryCache();
                return _entryCache.TryGetValue(entryName, out var e) ? e : null;
            }
        }

        /// <summary>Rebuild the name→entry lookup table. Call after modifying entries at runtime.</summary>
        public void RebuildEntryCache()
        {
            _entryCache = new Dictionary<string, TimelineEntryData>();
            if (Sequence == null) return;
            foreach (var entry in Sequence.entries)
            {
                if (!string.IsNullOrEmpty(entry.displayName) && !_entryCache.ContainsKey(entry.displayName))
                    _entryCache[entry.displayName] = entry;
                if (!_entryCache.ContainsKey(entry.entryId))
                    _entryCache[entry.entryId] = entry;
            }
        }

        /// <summary>Same as the indexer. Returns null if not found.</summary>
        public TimelineEntryData GetEntry(string entryName)
        {
            EnsureEntryCache();
            return _entryCache.TryGetValue(entryName, out var e) ? e : null;
        }

        private void EnsureEntryCache()
        {
            if (_entryCache == null) RebuildEntryCache();
        }

        // ── Marker lookup ─────────────────────────────────────────────────────

        /// <summary>
        /// Find a marker by display name or markerId. Returns null if not found.
        /// Example: ctrl.GetMarker("OnJump").OnTrigger += HandleJump;
        /// </summary>
        public EventMarkerData GetMarker(string nameOrId)
        {
            EnsureMarkerCache();
            return _markerCache.TryGetValue(nameOrId, out var m) ? m : null;
        }

        private void RebuildMarkerCache()
        {
            _markerCache = new Dictionary<string, EventMarkerData>();
            if (Sequence?.markers == null) return;
            foreach (var marker in Sequence.markers)
            {
                if (!string.IsNullOrEmpty(marker.displayName) && !_markerCache.ContainsKey(marker.displayName))
                    _markerCache[marker.displayName] = marker;
                if (!_markerCache.ContainsKey(marker.markerId))
                    _markerCache[marker.markerId] = marker;
            }
        }

        private void EnsureMarkerCache()
        {
            if (_markerCache == null) RebuildMarkerCache();
        }

        /// <summary>
        /// Add a marker at runtime. Call Play() after to include it in the running sequence.
        /// </summary>
        public EventMarkerData AddMarker(string displayName, float time)
        {
            if (Sequence == null) return null;
            if (Sequence.markers == null) Sequence.markers = new List<EventMarkerData>();

            var marker = new EventMarkerData { displayName = displayName, time = time };
            Sequence.markers.Add(marker);
            _markerCache = null;
            return marker;
        }

        /// <summary>
        /// Remove a marker by display name or markerId. Returns true if found and removed.
        /// </summary>
        public bool RemoveMarker(string nameOrId)
        {
            if (Sequence?.markers == null) return false;

            EnsureMarkerCache();
            if (!_markerCache.TryGetValue(nameOrId, out var marker)) return false;

            Sequence.markers.Remove(marker);
            _markerCache = null;
            return true;
        }

        // ── Inspector event accessors ─────────────────────────────────────────
        public UnityEvent OnPlayEvent => _onPlay;
        public UnityEvent OnPauseEvent => _onPause;
        public UnityEvent OnStopEvent => _onStop;
        public UnityEvent OnCompleteEvent => _onComplete;
        public TimelineLoopUnityEvent OnLoopEvent => _onLoop;
        public TimelineStringUnityEvent OnSpineEventEvent => _onSpineEvent;

        internal void RaiseSpineEvent(string eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return;
            _onSpineEvent.Invoke(eventName);
            OnSpineEvent?.Invoke(eventName);
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────
        private void Awake()
        {
            if (Sequence != null && Sequence.playOnAwake)
                Play();
        }

        private void OnDestroy() => KillHandle();

        private void Update()
        {
            if (!_isPlaying) return;
            if (!_seqHandle.IsActive() && !_hasSpineLayers) return;

            float total = Duration;
            _time += Time.deltaTime * Mathf.Max(0f, TimeScale) * _direction;

            bool ended = false;
            if (_direction > 0 && _time >= total)
            {
                _time = total;
                ended = true;
            }
            else if (_direction < 0 && _time <= 0d)
            {
                _time = 0d;
                ended = true;
            }

            ApplyTime(_time);
            PollCallbacks();

            if (ended)
            {
                _isPlaying = false;
                FireComplete();
            }
        }

        // ── Playback ──────────────────────────────────────────────────────────

        /// <summary>Build and play the current clip from the beginning.</summary>
        public void Play()
        {
            if (Sequence == null) return;
            _isComplete = false;
            _direction = 1;
            _time = 0d;
            _lastPollTime = 0d;
            Rebuild();
            ApplyTime(0d);
            _isPlaying = true;
            FirePlay();
        }

        /// <summary>Play and return a Task that completes when the sequence finishes.</summary>
        public Task PlayAsync(CancellationToken cancellationToken = default)
        {
            if (Sequence == null) return Task.CompletedTask;

            var tcs = new TaskCompletionSource<bool>();

            void OnDone()
            {
                OnComplete -= OnDone;
                tcs.TrySetResult(true);
            }

            OnComplete += OnDone;
            Play();

            if (cancellationToken.CanBeCanceled)
                cancellationToken.Register(() =>
                {
                    OnComplete -= OnDone;
                    Stop();
                    tcs.TrySetCanceled(cancellationToken);
                });

            return tcs.Task;
        }

        /// <summary>Assign a new clip then play it from the beginning.</summary>
        public void Play(LitTimelineClip newClip)
        {
            SetClip(newClip);
            Play();
        }

        /// <summary>Play from a specific time in seconds.</summary>
        public void PlayFromTime(float time)
        {
            if (Sequence == null) return;
            _isComplete = false;
            _direction = 1;
            Rebuild();
            _time = Mathf.Clamp(time, 0f, Duration);
            _lastPollTime = _time;
            ApplyTime(_time);
            _isPlaying = true;
            FirePlay();
        }

        /// <summary>Play from a normalized position (0 = start, 1 = end).</summary>
        public void PlayFromNormalizedTime(float normalizedTime) =>
            PlayFromTime(normalizedTime * Duration);

        /// <summary>Build and play the clip backwards from the end.</summary>
        public void PlayBackward()
        {
            if (Sequence == null) return;
            _isComplete = false;
            _direction = -1;
            Rebuild();
            _time = Duration;
            _lastPollTime = _time;
            ApplyTime(_time);
            _isPlaying = true;
            FirePlay();
        }

        /// <summary>Play backwards from a specific time in seconds.</summary>
        public void PlayBackwardFromTime(float time)
        {
            if (Sequence == null) return;
            _isComplete = false;
            _direction = -1;
            Rebuild();
            _time = Mathf.Clamp(time, 0f, Duration);
            _lastPollTime = _time;
            ApplyTime(_time);
            _isPlaying = true;
            FirePlay();
        }

        /// <summary>Play backwards from a normalized position (0 = start, 1 = end).</summary>
        public void PlayBackwardFromNormalizedTime(float normalizedTime) =>
            PlayBackwardFromTime(normalizedTime * Duration);

        /// <summary>Pause playback. Resume with <see cref="Resume"/> or <see cref="Play"/>.</summary>
        public void Pause()
        {
            if (!IsPlaying) return;
            _isPlaying = false;
            _onPause.Invoke();
            OnPause?.Invoke();
        }

        /// <summary>Resume a paused sequence.</summary>
        public void Resume()
        {
            if (!IsPaused) return;
            _isPlaying = true;
            FirePlay();
        }

        /// <summary>Kill the running sequence without restoring values.</summary>
        public void Stop()
        {
#if USING_SPINE
            if (_hasSpineLayers)
                SpineLayerDriver.Reset(this, Sequence);
#endif
            KillHandle();
            _hasSpineLayers = false;
            _isPlaying = false;
            _onStop.Invoke();
            OnStop?.Invoke();
        }

        /// <summary>Jump to the beginning and pause.</summary>
        public void Rewind()
        {
            EnsureBuilt();
            _direction = 1;
            _time = 0d;
            _lastPollTime = 0d;
            ApplyTime(0d);
            _isPlaying = false;
        }

        /// <summary>Seek to a time in seconds without changing play/pause state.</summary>
        public void GotoTime(float time)
        {
            EnsureBuilt();
            _time = Mathf.Clamp(time, 0f, Duration);
            _lastPollTime = _time;
            ApplyTime(_time);
        }

        /// <summary>Seek to a normalized position (0–1) without changing play/pause state.</summary>
        public void GotoNormalizedTime(float normalizedTime) =>
            GotoTime(normalizedTime * Duration);

        /// <summary>Replace the clip. Stops any running playback.</summary>
        public void SetClip(LitTimelineClip newClip)
        {
            Stop();
            clip = newClip;
            _entryCache = null;
            _markerCache = null;
        }

        // ── Internal helpers ─────────────────────────────────────────────────

        private void Rebuild()
        {
            KillHandle();
            _seqHandle = LitSequenceBuilder.Build(this, Sequence);
            // Hold the handle still; Update drives Time manually.
            if (_seqHandle.IsActive()) _seqHandle.PlaybackSpeed = 0f;
            _hasSpineLayers = ContainsSpineLayer(Sequence);
        }

        private static bool ContainsSpineLayer(TimelineSequenceData data)
        {
            if (data?.entries == null) return false;
            foreach (var entry in data.entries)
                if (entry != null && entry.layerType == LayerType.Spine && entry.isEnabled)
                    return true;
            return false;
        }

        private void EnsureBuilt()
        {
            if (IsBuilt) return;
            if (Sequence == null) return;
            _isComplete = false;
            Rebuild();
            _isPlaying = false;
        }

        private void ApplyTime(double time)
        {
            if (_seqHandle.IsActive()) _seqHandle.Time = time;
#if USING_SPINE
            if (_hasSpineLayers)
                SpineLayerDriver.Pose(this, Sequence, (float)time);
#endif
        }

        private void KillHandle()
        {
            if (_seqHandle.IsActive()) _seqHandle.Cancel();
            _seqHandle = MotionHandle.None;
        }

        // Fires entry OnStart / OnComplete and marker OnTrigger when the playhead crosses
        // their times moving forward. Replaces DOTween's InsertCallback.
        private void PollCallbacks()
        {
            if (_polling) return;
            double from = _lastPollTime;
            double to = _time;
            _lastPollTime = _time;

            if (to <= from) return; // only fire on forward motion

            _polling = true;
            try
            {
                foreach (var entry in Sequence.entries)
                {
                    if (!entry.isEnabled) continue;

                    if (Crossed(entry.delay, from, to))
                        entry.InvokeOnStart();

                    int loopCount = Mathf.Max(1, entry.loops);
                    float single = entry.EffectiveDuration;
                    if (loopCount > 1 && single > 0f)
                    {
                        for (int i = 1; i < loopCount; i++)
                        {
                            if (Crossed(entry.delay + single * i, from, to))
                            {
                                _onLoop.Invoke(i);
                                OnLoop?.Invoke(i);
                            }
                        }
                    }

                    if (entry.loops >= 0)
                    {
                        float completeAt = entry.delay + single * loopCount;
                        if (Crossed(completeAt, from, to))
                            entry.InvokeOnComplete();
                    }
                }

                if (Sequence.markers != null)
                {
                    foreach (var marker in Sequence.markers)
                    {
                        if (!marker.isEnabled || marker.time < 0f) continue;
                        if (Crossed(marker.time, from, to))
                            marker.InvokeTrigger();
                    }
                }

#if USING_SPINE
                if (_hasSpineLayers)
                    SpineLayerDriver.PollEvents(this, Sequence, from, to, RaiseSpineEvent);
#endif
            }
            finally
            {
                _polling = false;
            }
        }

        private static bool Crossed(double threshold, double from, double to) =>
            threshold > from && threshold <= to;

        private void FirePlay()
        {
            _onPlay.Invoke();
            OnPlay?.Invoke();
        }

        private void FireComplete()
        {
            _isComplete = true;
            _onComplete.Invoke();
            OnComplete?.Invoke();
        }

        internal TimelineSequenceData GetSequenceData() => Sequence;

#if UNITY_EDITOR
        private void Reset()
        {
            string assetName = gameObject.name + "_TimelineClip";
            string path = UnityEditor.EditorUtility.SaveFilePanelInProject(
                "Create Timeline Clip", assetName, "asset", "Choose where to save the Timeline Clip asset");
            if (string.IsNullOrEmpty(path)) return;

            var newClip = ScriptableObject.CreateInstance<LitTimelineClip>();
            UnityEditor.AssetDatabase.CreateAsset(newClip, path);
            UnityEditor.AssetDatabase.SaveAssets();
            clip = newClip;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
