using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LitTimeline
{
    public enum ExtraParamType
    {
        None
    }

    public class PropertyDescriptor
    {
        public string PropertyName;
        public string DisplayName;
        public PropertyType ValueType;
        public ExtraParamType ExtraParam;
    }

    public static class PropertyAccessorRegistry
    {
        private static readonly Dictionary<(string, string), Func<PropertyAccessor>> _factories
            = new Dictionary<(string, string), Func<PropertyAccessor>>();

        private static readonly Dictionary<string, List<PropertyDescriptor>> _byType
            = new Dictionary<string, List<PropertyDescriptor>>();

        static PropertyAccessorRegistry()
        {
            // Transform
            RegisterVector3<Transform>("localPosition", "Local Position",
                c => c.localPosition, (c, v) => c.localPosition = v);
            RegisterRotation<Transform>("localEulerAngles", "Local Rotation", isLocal: true);
            RegisterVector3<Transform>("localScale", "Local Scale",
                c => c.localScale, (c, v) => c.localScale = v);
            RegisterVector3<Transform>("position", "Position (World)",
                c => c.position, (c, v) => c.position = v);

            // RectTransform (UI)
            RegisterVector3<RectTransform>("localPosition", "Local Position",
                c => c.localPosition, (c, v) => c.localPosition = v);
            RegisterRotation<RectTransform>("localEulerAngles", "Local Rotation", isLocal: true);
            RegisterVector3<RectTransform>("localScale", "Local Scale",
                c => c.localScale, (c, v) => c.localScale = v);
            RegisterVector2<RectTransform>("anchoredPosition", "Anchored Position",
                c => c.anchoredPosition, (c, v) => c.anchoredPosition = v);
            RegisterVector2<RectTransform>("sizeDelta", "Size Delta",
                c => c.sizeDelta, (c, v) => c.sizeDelta = v);

            // CanvasGroup
            RegisterFloat<CanvasGroup>("alpha", "Alpha",
                c => c.alpha, (c, v) => c.alpha = v);

            // Image
            RegisterColor<Image>("color", "Color",
                c => c.color, (c, v) => c.color = v);
            RegisterFloat<Image>("fillAmount", "Fill Amount",
                c => c.fillAmount, (c, v) => c.fillAmount = v);

            // SpriteRenderer
            RegisterColor<SpriteRenderer>("color", "Color",
                c => c.color, (c, v) => c.color = v);

            // Light
            RegisterFloat<Light>("intensity", "Intensity",
                c => c.intensity, (c, v) => c.intensity = v);

            // AudioSource
            RegisterFloat<AudioSource>("volume", "Volume",
                c => c.volume, (c, v) => c.volume = v);
            RegisterFloat<AudioSource>("pitch", "Pitch",
                c => c.pitch, (c, v) => c.pitch = v);

            // TextMeshPro (world space)
            RegisterColor<TextMeshPro>("color", "Color",
                c => c.color, (c, v) => c.color = v);
            RegisterFloat<TextMeshPro>("alpha", "Alpha",
                c => c.alpha, (c, v) => c.alpha = v);
            RegisterFloat<TextMeshPro>("fontSize", "Font Size",
                c => c.fontSize, (c, v) => c.fontSize = v);
            RegisterFloat<TextMeshPro>("characterSpacing", "Character Spacing",
                c => c.characterSpacing, (c, v) => c.characterSpacing = v);
            RegisterFloat<TextMeshPro>("wordSpacing", "Word Spacing",
                c => c.wordSpacing, (c, v) => c.wordSpacing = v);
            RegisterFloat<TextMeshPro>("lineSpacing", "Line Spacing",
                c => c.lineSpacing, (c, v) => c.lineSpacing = v);

            // TextMeshProUGUI (UI)
            RegisterColor<TextMeshProUGUI>("color", "Color",
                c => c.color, (c, v) => c.color = v);
            RegisterFloat<TextMeshProUGUI>("alpha", "Alpha",
                c => c.alpha, (c, v) => c.alpha = v);
            RegisterFloat<TextMeshProUGUI>("fontSize", "Font Size",
                c => c.fontSize, (c, v) => c.fontSize = v);
            RegisterFloat<TextMeshProUGUI>("characterSpacing", "Character Spacing",
                c => c.characterSpacing, (c, v) => c.characterSpacing = v);
            RegisterFloat<TextMeshProUGUI>("wordSpacing", "Word Spacing",
                c => c.wordSpacing, (c, v) => c.wordSpacing = v);
            RegisterFloat<TextMeshProUGUI>("lineSpacing", "Line Spacing",
                c => c.lineSpacing, (c, v) => c.lineSpacing = v);
        }

        private static void RegisterRotation<T>(string propName, string displayName, bool isLocal) where T : Component
        {
            string typeName = typeof(T).FullName;
            _factories[(typeName, propName)] = () => new RotationPropertyAccessor(isLocal);
            AddDescriptor(typeName, propName, displayName, PropertyType.Vector3);
        }

        private static void RegisterVector3<T>(string propName, string displayName,
            Func<T, Vector3> getter, Action<T, Vector3> setter) where T : Component
        {
            string typeName = typeof(T).FullName;
            Func<PropertyAccessor> factory = () =>
                new Vector3PropertyAccessor(c => getter((T)c), (c, v) => setter((T)c, v));
            _factories[(typeName, propName)] = factory;
            AddDescriptor(typeName, propName, displayName, PropertyType.Vector3);
        }

        private static void RegisterVector2<T>(string propName, string displayName,
            Func<T, Vector2> getter, Action<T, Vector2> setter) where T : Component
        {
            string typeName = typeof(T).FullName;
            Func<PropertyAccessor> factory = () =>
                new Vector2PropertyAccessor(c => getter((T)c), (c, v) => setter((T)c, v));
            _factories[(typeName, propName)] = factory;
            AddDescriptor(typeName, propName, displayName, PropertyType.Vector2);
        }

        private static void RegisterFloat<T>(string propName, string displayName,
            Func<T, float> getter, Action<T, float> setter) where T : Component
        {
            string typeName = typeof(T).FullName;
            Func<PropertyAccessor> factory = () =>
                new FloatPropertyAccessor(c => getter((T)c), (c, v) => setter((T)c, v));
            _factories[(typeName, propName)] = factory;
            AddDescriptor(typeName, propName, displayName, PropertyType.Float);
        }

        private static void RegisterColor<T>(string propName, string displayName,
            Func<T, Color> getter, Action<T, Color> setter) where T : Component
        {
            string typeName = typeof(T).FullName;
            Func<PropertyAccessor> factory = () =>
                new ColorPropertyAccessor(c => getter((T)c), (c, v) => setter((T)c, v));
            _factories[(typeName, propName)] = factory;
            AddDescriptor(typeName, propName, displayName, PropertyType.Color);
        }

        public static PropertyDescriptor GetDescriptor(string componentTypeName, string propertyName)
        {
            if (_byType.TryGetValue(componentTypeName, out var list))
                foreach (var d in list)
                    if (d.PropertyName == propertyName)
                        return d;

            return BuildMaterialDescriptor(propertyName);
        }

        private static PropertyDescriptor BuildMaterialDescriptor(string propertyName)
        {
            if (propertyName.StartsWith("mat_float:"))
                return new PropertyDescriptor { PropertyName = propertyName, DisplayName = "Material / " + propertyName.Substring(10), ValueType = PropertyType.Float };
            if (propertyName.StartsWith("mat_color:"))
                return new PropertyDescriptor { PropertyName = propertyName, DisplayName = "Material / " + propertyName.Substring(10), ValueType = PropertyType.Color };
            return null;
        }

        private static void AddDescriptor(string typeName, string propName, string displayName, PropertyType valueType)
        {
            if (!_byType.TryGetValue(typeName, out var list))
            {
                list = new List<PropertyDescriptor>();
                _byType[typeName] = list;
            }

            list.Add(new PropertyDescriptor { PropertyName = propName, DisplayName = displayName, ValueType = valueType });
        }

        public static PropertyAccessor Get(string componentTypeName, string propertyName)
        {
            if (_factories.TryGetValue((componentTypeName, propertyName), out var factory))
                return factory();

            if (propertyName.StartsWith("mat_float:"))
                return new MaterialFloatPropertyAccessor(propertyName.Substring(10));
            if (propertyName.StartsWith("mat_color:"))
                return new MaterialColorPropertyAccessor(propertyName.Substring(10));

            return null;
        }

        public static IReadOnlyList<PropertyDescriptor> GetSupportedProperties(string componentTypeName)
        {
            return _byType.TryGetValue(componentTypeName, out var list)
                ? list
                : Array.Empty<PropertyDescriptor>();
        }

        public static bool IsTypeSupported(string componentTypeName) =>
            _byType.ContainsKey(componentTypeName);
    }
}
