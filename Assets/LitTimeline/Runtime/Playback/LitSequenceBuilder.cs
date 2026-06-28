using System;
using System.Collections.Generic;
using LitMotion;
using UnityEngine;

namespace LitTimeline
{
    public static class LitSequenceBuilder
    {
        /// <summary>
        /// Build a LitMotion sequence from the timeline data and return its root handle.
        /// Each entry becomes a motion inserted at its delay. Entry/marker callbacks are
        /// fired by <see cref="LitTimelineController"/> via playhead polling, not here.
        /// </summary>
        public static MotionHandle Build(LitTimelineController controller, TimelineSequenceData data)
        {
            var seqBuilder = LSequence.Create();

            var entryById = new Dictionary<string, TimelineEntryData>();
            foreach (var e in data.entries)
                entryById[e.entryId] = e;

            foreach (var entry in data.entries)
            {
                if (!entry.isEnabled || entry.binding == null) continue;

                var component = ResolveComponent(controller, entry.binding);
                if (component == null)
                {
                    string target = string.IsNullOrEmpty(entry.binding.hierarchyPath)
                        ? controller.gameObject.name
                        : $"{controller.gameObject.name}/{entry.binding.hierarchyPath}";
                    Debug.LogError(
                        $"[LitTimeline] Skipping entry \"{entry.displayName}\": " +
                        $"object or component not found (path: \"{target}\", type: {entry.binding.componentTypeName}). " +
                        $"Check if the object was deleted or renamed.",
                        controller);
                    continue;
                }

                var accessor = PropertyAccessorRegistry.Get(entry.binding.componentTypeName, entry.binding.propertyName);
                if (accessor == null)
                {
                    Debug.LogError(
                        $"[LitTimeline] Skipping entry \"{entry.displayName}\": " +
                        $"no accessor registered for {entry.binding.componentTypeName}.{entry.binding.propertyName}.",
                        controller);
                    continue;
                }

                // Resolve the from-value. Unlike DOTween's lazy getter, LitMotion bakes the
                // start value at build time, so "use current as start" captures it now.
                PropertyValueUnion startVal;
                if (entry.useCurrentAsStart)
                {
                    startVal = accessor.ReadValue(component);
                }
                else
                {
                    startVal = entry.startValue;
                    if (!string.IsNullOrEmpty(entry.linkedStartEntryId) &&
                        entryById.TryGetValue(entry.linkedStartEntryId, out var linked))
                        startVal = linked.endValue;
                }

                var handle = accessor.BuildMotion(component, entry, startVal);
                seqBuilder.Insert(entry.delay, handle);
            }

            return seqBuilder.Run();
        }

        public static Component ResolveComponent(LitTimelineController ctrl, PropertyBinding binding)
        {
            if (binding == null) return null;

            Transform target = string.IsNullOrEmpty(binding.hierarchyPath)
                ? ctrl.transform
                : ctrl.transform.Find(binding.hierarchyPath);

            if (target == null) return null;

            var type = ResolveType(binding.componentTypeName);
            return type != null ? target.GetComponent(type) : null;
        }

        private static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;

            var t = Type.GetType(typeName);
            if (t != null) return t;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = assembly.GetType(typeName);
                if (t != null) return t;
            }

            return null;
        }
    }
}
