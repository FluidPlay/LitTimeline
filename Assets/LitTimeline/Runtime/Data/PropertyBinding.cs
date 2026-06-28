using System;
using UnityEngine;

namespace LitTimeline
{
    [Serializable]
    public class PropertyBinding
    {
        // Transform path relative to the LitTimelineController (empty = self)
        public string hierarchyPath;

        // e.g. "UnityEngine.Transform"
        public string componentTypeName;

        // e.g. "localPosition"
        public string propertyName;
        public PropertyAxis axis;

        public string DisplayLabel =>
            string.IsNullOrEmpty(hierarchyPath)
                ? $"{ShortTypeName}.{propertyName}"
                : $"{hierarchyPath} > {ShortTypeName}.{propertyName}";

        private string ShortTypeName
        {
            get
            {
                if (string.IsNullOrEmpty(componentTypeName)) return "?";
                int dot = componentTypeName.LastIndexOf('.');
                return dot >= 0 ? componentTypeName.Substring(dot + 1) : componentTypeName;
            }
        }
    }
}
