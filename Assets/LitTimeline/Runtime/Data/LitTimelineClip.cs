using UnityEngine;

namespace LitTimeline
{
    [CreateAssetMenu(fileName = "NewTimelineClip", menuName = "LitTimeline/Timeline Clip")]
    public class LitTimelineClip : ScriptableObject
    {
        [SerializeField] private TimelineSequenceData data = new TimelineSequenceData();
        public TimelineSequenceData Data => data;
    }
}
