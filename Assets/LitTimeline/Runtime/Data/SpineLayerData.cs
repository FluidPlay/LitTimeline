using System;

namespace LitTimeline
{
    public enum LayerType
    {
        Tween = 0,
        Spine = 1,
    }

    // Loopable mirrors LitMotion.LoopType.Restart; OneShot clamps to a single iteration.
    // Kept as a Spine-specific enum so the inspector only exposes the two options
    // the user asked for, regardless of how the Tween LoopType evolves.
    public enum SpineLoopMode
    {
        Loopable = 0,
        OneShot = 1,
    }

    [Serializable]
    public class SpineLayerData
    {
        // Name of the Spine animation to play (key into SkeletonData.Animations).
        public string animationName;

        // Base duration in seconds of the selected animation at 1x speed.
        // Stored on the clip so the editor knows the "natural" length without
        // needing the SkeletonAnimation component resolved.
        public float animationLength = 1f;

        public SpineLoopMode loopMode = SpineLoopMode.Loopable;
        public bool bridgeSpineEvents;
    }
}
