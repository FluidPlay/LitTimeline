#if USING_SPINE
using System;
using System.Collections.Generic;
using Spine;
using Spine.Unity;
using UnityEngine;

namespace LitTimeline
{
    /// <summary>
    /// Drives Spine SkeletonAnimation components from LitTimeline entries.
    ///
    /// LitTimeline owns the playback clock, so Spine's AnimationState must NOT
    /// auto-advance: we set <see cref="SkeletonAnimation.timeScale"/> to 0 and
    /// drive each TrackEntry's TrackTime directly. Events are scanned manually
    /// from the animation's EventTimeline (Spine's own event dispatch is unreliable
    /// when AnimationState is fed delta=0).
    /// </summary>
    public static class SpineLayerDriver
    {
        // Resolve a Spine.Unity.SkeletonAnimation from a binding (hierarchyPath relative
        // to the controller; component type is whatever the binding stored — we just look
        // for SkeletonAnimation on the resolved Transform).
        public static SkeletonAnimation ResolveSkeleton(LitTimelineController ctrl, PropertyBinding binding)
        {
            if (ctrl == null || binding == null) return null;
            Transform t = string.IsNullOrEmpty(binding.hierarchyPath)
                ? ctrl.transform
                : ctrl.transform.Find(binding.hierarchyPath);
            return t != null ? t.GetComponent<SkeletonAnimation>() : null;
        }

        // Distinct Spine trackIds in entry order. The topmost row (first encountered)
        // gets the HIGHEST AnimationState track index so it renders on top of lower rows.
        public static Dictionary<string, int> BuildSpineTrackMap(TimelineSequenceData data)
        {
            var ordered = new List<string>();
            if (data?.entries != null)
            {
                foreach (var e in data.entries)
                {
                    if (e == null || e.layerType != LayerType.Spine) continue;
                    string id = string.IsNullOrEmpty(e.trackId) ? e.entryId : e.trackId;
                    if (!ordered.Contains(id)) ordered.Add(id);
                }
            }

            var map = new Dictionary<string, int>(ordered.Count);
            int k = ordered.Count;
            for (int i = 0; i < k; i++)
                map[ordered[i]] = k - 1 - i; // first (topmost) gets K-1
            return map;
        }

        /// <summary>
        /// Pose every SkeletonAnimation driven by Spine layers in <paramref name="data"/>
        /// at the given timeline time.
        /// </summary>
        public static void Pose(LitTimelineController ctrl, TimelineSequenceData data, float time)
        {
            if (ctrl == null || data == null) return;

            var trackMap = BuildSpineTrackMap(data);

            // Group entries by their resolved SkeletonAnimation so we can clear/apply once per skeleton.
            var bySkeleton = new Dictionary<SkeletonAnimation, List<TimelineEntryData>>();
            foreach (var entry in data.entries)
            {
                if (entry == null || !entry.isEnabled || entry.layerType != LayerType.Spine) continue;

                var skel = ResolveSkeleton(ctrl, entry.binding);
                if (skel == null) continue;

                if (!bySkeleton.TryGetValue(skel, out var list))
                {
                    list = new List<TimelineEntryData>();
                    bySkeleton[skel] = list;
                }
                list.Add(entry);
            }

            foreach (var kvp in bySkeleton)
            {
                var skel = kvp.Key;
                if (!skel.IsValid) continue;

                skel.timeScale = 0f;

                var state = skel.AnimationState;
                var skeleton = skel.Skeleton;

                // Reset to setup pose so layers below the highest-index track don't leave
                // ghosting from a previous frame's animation.
                skeleton.SetToSetupPose();

                // Track which AnimationState indices we use this frame so we can clear
                // any leftovers from previous frames (e.g. if a Spine layer was deleted).
                var usedIndices = new HashSet<int>();

                foreach (var entry in kvp.Value)
                {
                    string id = string.IsNullOrEmpty(entry.trackId) ? entry.entryId : entry.trackId;
                    if (!trackMap.TryGetValue(id, out int trackIndex)) continue;

                    if (time < entry.delay) continue;
                    if (string.IsNullOrEmpty(entry.spine.animationName)) continue;

                    int loopCount = Mathf.Max(1, entry.loops);
                    float animLen = Mathf.Max(0.0001f, entry.spine.animationLength);
                    float speed = Mathf.Max(0.001f, entry.speed);
                    float localTime = (time - entry.delay) * speed;
                    float totalLocal = animLen * loopCount;
                    float clamped = Mathf.Clamp(localTime, 0f, totalLocal);
                    bool loop = entry.spine.loopMode == SpineLoopMode.Loopable;

                    // Only call SetAnimation when the desired animation actually changes,
                    // otherwise we'd re-trigger start mixing every Pose() call.
                    TrackEntry te = state.GetCurrent(trackIndex);
                    if (te == null || te.Animation == null
                        || te.Animation.Name != entry.spine.animationName
                        || te.Loop != loop)
                    {
                        te = state.SetAnimation(trackIndex, entry.spine.animationName, loop);
                    }

                    if (te != null)
                    {
                        te.TimeScale = 0f; // we drive TrackTime; don't let state.Update advance it.
                        te.TrackTime = clamped;
                        usedIndices.Add(trackIndex);
                    }
                }

                // Clear leftover tracks (deleted/disabled Spine layers).
                int tracksCount = state.Tracks.Count;
                for (int i = 0; i < tracksCount; i++)
                {
                    if (usedIndices.Contains(i)) continue;
                    if (state.GetCurrent(i) != null) state.ClearTrack(i);
                }

                state.Apply(skeleton);
                skeleton.UpdateWorldTransform();
            }
        }

        /// <summary>
        /// Reset all Spine skeletons referenced by <paramref name="data"/> to their setup pose
        /// and clear any active tracks. Used when exiting preview or stopping playback.
        /// </summary>
        public static void Reset(LitTimelineController ctrl, TimelineSequenceData data)
        {
            if (ctrl == null || data == null) return;

            var seen = new HashSet<SkeletonAnimation>();
            foreach (var entry in data.entries)
            {
                if (entry == null || entry.layerType != LayerType.Spine) continue;
                var skel = ResolveSkeleton(ctrl, entry.binding);
                if (skel == null || !skel.IsValid) continue;
                if (!seen.Add(skel)) continue;

                skel.AnimationState.ClearTracks();
                skel.Skeleton.SetToSetupPose();
                skel.Skeleton.UpdateWorldTransform();
            }
        }

        /// <summary>
        /// Poll Spine event timelines for forward crossings in (from, to] and invoke
        /// <paramref name="raise"/> with the event name for each entry whose
        /// <c>bridgeSpineEvents</c> flag is set.
        /// </summary>
        public static void PollEvents(LitTimelineController ctrl, TimelineSequenceData data,
            double from, double to, Action<string> raise)
        {
            if (ctrl == null || data == null || raise == null) return;
            if (to <= from) return;

            foreach (var entry in data.entries)
            {
                if (entry == null || !entry.isEnabled) continue;
                if (entry.layerType != LayerType.Spine) continue;
                if (entry.spine == null || !entry.spine.bridgeSpineEvents) continue;
                if (string.IsNullOrEmpty(entry.spine.animationName)) continue;

                var skel = ResolveSkeleton(ctrl, entry.binding);
                if (skel == null || !skel.IsValid) continue;

                var skeletonData = skel.Skeleton?.Data;
                if (skeletonData == null) continue;

                var animation = skeletonData.FindAnimation(entry.spine.animationName);
                if (animation == null) continue;

                float speed = Mathf.Max(0.001f, entry.speed);
                float animLen = Mathf.Max(0.0001f, entry.spine.animationLength);
                int loopCount = Mathf.Max(1, entry.loops);
                float totalLocal = animLen * loopCount;

                double localFrom = (from - entry.delay) * speed;
                double localTo = (to - entry.delay) * speed;

                if (localTo <= 0d) continue; // entry hasn't started yet
                if (localFrom >= totalLocal) continue; // entry already finished

                double clampedFrom = Math.Max(0d, localFrom);
                double clampedTo = Math.Min(totalLocal, localTo);
                if (clampedTo <= clampedFrom) continue;

                bool loop = entry.spine.loopMode == SpineLoopMode.Loopable;
                ScanEventsInRange(animation, clampedFrom, clampedTo, animLen, loop, raise);
            }
        }

        private static void ScanEventsInRange(Animation animation, double localFrom, double localTo,
            float animLen, bool loop, Action<string> raise)
        {
            var timelines = animation.Timelines;
            for (int ti = 0; ti < timelines.Count; ti++)
            {
                if (!(timelines.Items[ti] is EventTimeline et)) continue;

                float[] frames = et.Frames;
                if (frames == null || frames.Length == 0) continue;

                if (!loop)
                {
                    for (int i = 0; i < frames.Length; i++)
                    {
                        float ft = frames[i];
                        if (ft > localFrom && ft <= localTo)
                            raise.Invoke(et.Events[i]?.Data?.Name);
                    }
                    continue;
                }

                // Looping animation: each cycle of length animLen replays all events.
                int firstCycle = (int)Math.Floor(localFrom / animLen);
                int lastCycle = (int)Math.Floor((localTo - 1e-6) / animLen);
                for (int c = firstCycle; c <= lastCycle; c++)
                {
                    double cycleStart = c * animLen;
                    for (int i = 0; i < frames.Length; i++)
                    {
                        double ft = cycleStart + frames[i];
                        if (ft > localFrom && ft <= localTo)
                            raise.Invoke(et.Events[i]?.Data?.Name);
                    }
                }
            }
        }
    }
}
#endif
