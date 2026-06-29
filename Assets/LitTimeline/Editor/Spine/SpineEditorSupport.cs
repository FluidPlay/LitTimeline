#if USING_SPINE
using System.Collections.Generic;
using Spine;
using Spine.Unity;
using UnityEngine;

namespace LitTimeline.Editor
{
    /// <summary>
    /// Editor-side helpers for Spine integration. Keeps the window code free of
    /// Spine API calls so the existing tween code remains readable.
    /// </summary>
    internal static class SpineEditorSupport
    {
        public struct SpineAnimationInfo
        {
            public string Name;
            public float Duration;
        }

        public static SkeletonDataAsset GetSkeletonDataAsset(SkeletonAnimation sa)
        {
            return sa != null ? sa.SkeletonDataAsset : null;
        }

        /// <summary>
        /// Return all animations on the given SkeletonAnimation, with their base
        /// durations in seconds. Empty list if the asset is missing or invalid.
        /// </summary>
        public static List<SpineAnimationInfo> GetAnimations(SkeletonAnimation sa)
        {
            var result = new List<SpineAnimationInfo>();
            if (sa == null) return result;

            var dataAsset = sa.SkeletonDataAsset;
            if (dataAsset == null) return result;

            var skeletonData = dataAsset.GetSkeletonData(quiet: true);
            if (skeletonData == null) return result;

            var anims = skeletonData.Animations;
            for (int i = 0; i < anims.Count; i++)
            {
                var anim = anims.Items[i];
                if (anim == null) continue;
                result.Add(new SpineAnimationInfo
                {
                    Name = anim.Name,
                    Duration = anim.Duration,
                });
            }

            return result;
        }

        /// <summary>Resolve the SkeletonAnimation referenced by an entry's binding.</summary>
        public static SkeletonAnimation ResolveSkeleton(LitTimelineController ctrl, PropertyBinding binding)
        {
            return SpineLayerDriver.ResolveSkeleton(ctrl, binding);
        }

        /// <summary>Find an animation's base duration on a SkeletonAnimation, or 1f if missing.</summary>
        public static float GetAnimationDuration(SkeletonAnimation sa, string animationName)
        {
            if (sa == null || string.IsNullOrEmpty(animationName)) return 1f;
            var dataAsset = sa.SkeletonDataAsset;
            if (dataAsset == null) return 1f;
            var skeletonData = dataAsset.GetSkeletonData(quiet: true);
            var anim = skeletonData?.FindAnimation(animationName);
            return anim != null ? Mathf.Max(0.0001f, anim.Duration) : 1f;
        }

        /// <summary>Restore every SkeletonAnimation found in <paramref name="data"/> to setup pose.</summary>
        public static void ResetSkeletons(LitTimelineController ctrl, TimelineSequenceData data)
        {
            SpineLayerDriver.Reset(ctrl, data);
        }
    }
}
#endif
