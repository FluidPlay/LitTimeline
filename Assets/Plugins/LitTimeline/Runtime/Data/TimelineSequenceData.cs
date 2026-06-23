using System;
using System.Collections.Generic;
using UnityEngine;

namespace LitTimeline
{
    [Serializable]
    public class TimelineSequenceData
    {
        public string displayName = "Sequence";
        public float timeScale = 1f;
        public bool playOnAwake;
        public bool autoKillOnComplete = true;
        public List<TimelineEntryData> entries = new List<TimelineEntryData>();
        public List<EventMarkerData> markers = new List<EventMarkerData>();

        public float TotalDuration
        {
            get
            {
                float max = 0f;
                foreach (var e in entries)
                    if (e.isEnabled && e.EndTime > max)
                        max = e.EndTime;
                return max;
            }
        }
    }
}
