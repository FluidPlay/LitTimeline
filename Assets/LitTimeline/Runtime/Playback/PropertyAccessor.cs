using System;
using LitMotion;
using UnityEngine;

namespace LitTimeline
{
    public abstract class PropertyAccessor
    {
        /// <summary>
        /// Build a LitMotion motion that animates this property from <paramref name="from"/>
        /// to the entry's end value. The returned handle is added to a sequence by the caller.
        /// </summary>
        public abstract MotionHandle BuildMotion(Component target, TimelineEntryData entry, PropertyValueUnion from);

        public abstract void ApplyValue(Component target, PropertyValueUnion value);
        public abstract PropertyValueUnion ReadValue(Component target);

        // Within a sequence, infinite loops would make the sequence duration infinite,
        // so they are clamped to a single iteration. Standalone DOTween allowed this; LitMotion sequences do not.
        protected static int LoopsForSequence(TimelineEntryData entry) =>
            entry.loops < 0 ? 1 : Mathf.Max(1, entry.loops);

        protected static MotionBuilder<TValue, NoOptions, TAdapter> Configure<TValue, TAdapter>(
            MotionBuilder<TValue, NoOptions, TAdapter> builder, TimelineEntryData entry)
            where TValue : unmanaged
            where TAdapter : unmanaged, IMotionAdapter<TValue, NoOptions>
        {
            if (entry.useCustomCurve && entry.customEaseCurve != null)
                builder = builder.WithEase(FixEndpointTangents(entry.customEaseCurve));
            else
                builder = builder.WithEase(entry.ease);
            return builder.WithLoops(LoopsForSequence(entry), entry.loopType);
        }

        // Returns a copy of the curve with endpoint tangents clamped to >= 0.
        // Unity auto-smooth can give the first key a negative outTangent, causing the
        // curve to dip below 0 on the very first frames and producing a visible overshoot
        // opposite to the intended direction. A runtime copy is made so the user's saved
        // curve data is never modified.
        protected static AnimationCurve FixEndpointTangents(AnimationCurve src)
        {
            if (src == null || src.length == 0) return src;
            var keys = src.keys; // already a copy

            bool dirty = false;

            var first = keys[0];
            if (first.outTangent < 0f) { first.outTangent = 0f; keys[0] = first; dirty = true; }

            if (keys.Length > 1)
            {
                int last = keys.Length - 1;
                var lastKey = keys[last];
                if (lastKey.inTangent < 0f) { lastKey.inTangent = 0f; keys[last] = lastKey; dirty = true; }
            }

            return dirty ? new AnimationCurve(keys) : src;
        }
    }

    public sealed class Vector3PropertyAccessor : PropertyAccessor
    {
        private readonly Func<Component, Vector3> _getter;
        private readonly Action<Component, Vector3> _setter;

        public Vector3PropertyAccessor(Func<Component, Vector3> getter, Action<Component, Vector3> setter)
        {
            _getter = getter;
            _setter = setter;
        }

        public override MotionHandle BuildMotion(Component target, TimelineEntryData entry, PropertyValueUnion from)
        {
            Vector3 start = from.vector3Value;
            Vector3 end = entry.endValue.vector3Value;
            var setter = _setter;
            return Configure(LMotion.Create(start, end, entry.EffectiveDuration), entry)
                .Bind(target, (v, c) => setter(c, v));
        }

        public override void ApplyValue(Component target, PropertyValueUnion value) =>
            _setter(target, value.vector3Value);

        public override PropertyValueUnion ReadValue(Component target) =>
            PropertyValueUnion.FromVector3(_getter(target));
    }

    public sealed class FloatPropertyAccessor : PropertyAccessor
    {
        private readonly Func<Component, float> _getter;
        private readonly Action<Component, float> _setter;

        public FloatPropertyAccessor(Func<Component, float> getter, Action<Component, float> setter)
        {
            _getter = getter;
            _setter = setter;
        }

        public override MotionHandle BuildMotion(Component target, TimelineEntryData entry, PropertyValueUnion from)
        {
            float start = from.floatValue;
            float end = entry.endValue.floatValue;
            var setter = _setter;
            return Configure(LMotion.Create(start, end, entry.EffectiveDuration), entry)
                .Bind(target, (v, c) => setter(c, v));
        }

        public override void ApplyValue(Component target, PropertyValueUnion value) =>
            _setter(target, value.floatValue);

        public override PropertyValueUnion ReadValue(Component target) =>
            PropertyValueUnion.FromFloat(_getter(target));
    }

    public sealed class Vector2PropertyAccessor : PropertyAccessor
    {
        private readonly Func<Component, Vector2> _getter;
        private readonly Action<Component, Vector2> _setter;

        public Vector2PropertyAccessor(Func<Component, Vector2> getter, Action<Component, Vector2> setter)
        {
            _getter = getter;
            _setter = setter;
        }

        public override MotionHandle BuildMotion(Component target, TimelineEntryData entry, PropertyValueUnion from)
        {
            Vector2 start = from.vector2Value;
            Vector2 end = entry.endValue.vector2Value;
            var setter = _setter;
            return Configure(LMotion.Create(start, end, entry.EffectiveDuration), entry)
                .Bind(target, (v, c) => setter(c, v));
        }

        public override void ApplyValue(Component target, PropertyValueUnion value) =>
            _setter(target, value.vector2Value);

        public override PropertyValueUnion ReadValue(Component target) =>
            PropertyValueUnion.FromVector2(_getter(target));
    }

    // Rotation animates as euler angles (Vector3). DOTween's RotateMode has no LitMotion equivalent.
    public sealed class RotationPropertyAccessor : PropertyAccessor
    {
        private readonly bool _isLocal;

        public RotationPropertyAccessor(bool isLocal) { _isLocal = isLocal; }

        public override MotionHandle BuildMotion(Component target, TimelineEntryData entry, PropertyValueUnion from)
        {
            var t = (Transform)target;
            Vector3 start = from.vector3Value;
            Vector3 end = entry.endValue.vector3Value;
            bool isLocal = _isLocal;
            return Configure(LMotion.Create(start, end, entry.EffectiveDuration), entry)
                .Bind(t, (v, tr) =>
                {
                    if (isLocal) tr.localEulerAngles = v;
                    else tr.eulerAngles = v;
                });
        }

        public override void ApplyValue(Component target, PropertyValueUnion value)
        {
            var t = (Transform)target;
            if (_isLocal) t.localEulerAngles = value.vector3Value;
            else t.eulerAngles = value.vector3Value;
        }

        public override PropertyValueUnion ReadValue(Component target)
        {
            var t = (Transform)target;
            return PropertyValueUnion.FromVector3(_isLocal ? t.localEulerAngles : t.eulerAngles);
        }
    }

    public sealed class ColorPropertyAccessor : PropertyAccessor
    {
        private readonly Func<Component, Color> _getter;
        private readonly Action<Component, Color> _setter;

        public ColorPropertyAccessor(Func<Component, Color> getter, Action<Component, Color> setter)
        {
            _getter = getter;
            _setter = setter;
        }

        public override MotionHandle BuildMotion(Component target, TimelineEntryData entry, PropertyValueUnion from)
        {
            Color start = from.colorValue;
            Color end = entry.endValue.colorValue;
            var setter = _setter;
            return Configure(LMotion.Create(start, end, entry.EffectiveDuration), entry)
                .Bind(target, (v, c) => setter(c, v));
        }

        public override void ApplyValue(Component target, PropertyValueUnion value) =>
            _setter(target, value.colorValue);

        public override PropertyValueUnion ReadValue(Component target) =>
            PropertyValueUnion.FromColor(_getter(target));
    }

    // Property name convention: "mat_float:<shaderProp>" or "mat_color:<shaderProp>"
    public sealed class MaterialFloatPropertyAccessor : PropertyAccessor
    {
        private readonly string _shaderProp;

        public MaterialFloatPropertyAccessor(string shaderProp) { _shaderProp = shaderProp; }

        private Material Mat(Component target)
        {
            var r = (Renderer)target;
            return Application.isPlaying ? r.material : r.sharedMaterial;
        }

        public override MotionHandle BuildMotion(Component target, TimelineEntryData entry, PropertyValueUnion from)
        {
            var mat = Mat(target);
            string prop = _shaderProp;
            float start = from.floatValue;
            float end = entry.endValue.floatValue;
            return Configure(LMotion.Create(start, end, entry.EffectiveDuration), entry)
                .Bind(mat, (v, m) => m.SetFloat(prop, v));
        }

        public override void ApplyValue(Component target, PropertyValueUnion value) =>
            Mat(target).SetFloat(_shaderProp, value.floatValue);

        public override PropertyValueUnion ReadValue(Component target) =>
            PropertyValueUnion.FromFloat(Mat(target).GetFloat(_shaderProp));
    }

    public sealed class MaterialColorPropertyAccessor : PropertyAccessor
    {
        private readonly string _shaderProp;

        public MaterialColorPropertyAccessor(string shaderProp) { _shaderProp = shaderProp; }

        private Material Mat(Component target)
        {
            var r = (Renderer)target;
            return Application.isPlaying ? r.material : r.sharedMaterial;
        }

        public override MotionHandle BuildMotion(Component target, TimelineEntryData entry, PropertyValueUnion from)
        {
            var mat = Mat(target);
            string prop = _shaderProp;
            Color start = from.colorValue;
            Color end = entry.endValue.colorValue;
            return Configure(LMotion.Create(start, end, entry.EffectiveDuration), entry)
                .Bind(mat, (v, m) => m.SetColor(prop, v));
        }

        public override void ApplyValue(Component target, PropertyValueUnion value) =>
            Mat(target).SetColor(_shaderProp, value.colorValue);

        public override PropertyValueUnion ReadValue(Component target) =>
            PropertyValueUnion.FromColor(Mat(target).GetColor(_shaderProp));
    }
}
