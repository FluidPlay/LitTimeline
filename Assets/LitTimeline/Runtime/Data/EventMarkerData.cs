using System;
using UnityEngine;

namespace LitTimeline
{
    [Serializable]
    public class EventMarkerData
    {
        public string markerId = Guid.NewGuid().ToString();
        public string displayName = "Marker";
        public float time;
        public bool isEnabled = true;

        public event Action OnTrigger;
        internal void InvokeTrigger() => OnTrigger?.Invoke();
    }
}
