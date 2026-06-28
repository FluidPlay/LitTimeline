using System;
using UnityEngine;

namespace LitTimeline
{
    public enum PropertyType
    {
        Float,
        Vector2,
        Vector3,
        Color,
    }

    public enum PropertyAxis
    {
        None,
        X,
        Y,
        Z,
        R,
        G,
        B,
        A
    }

    [Serializable]
    public struct PropertyValueUnion
    {
        public PropertyType type;
        public float floatValue;
        public Vector2 vector2Value;
        public Vector3 vector3Value;
        public Color colorValue;

        public static PropertyValueUnion FromFloat(float v) => new PropertyValueUnion { type = PropertyType.Float, floatValue = v };
        public static PropertyValueUnion FromVector2(Vector2 v) => new PropertyValueUnion { type = PropertyType.Vector2, vector2Value = v };
        public static PropertyValueUnion FromVector3(Vector3 v) => new PropertyValueUnion { type = PropertyType.Vector3, vector3Value = v };
        public static PropertyValueUnion FromColor(Color v) => new PropertyValueUnion { type = PropertyType.Color, colorValue = v };

        public static PropertyValueUnion DefaultForType(PropertyType t) => t switch
        {
            PropertyType.Color => FromColor(Color.white),
            PropertyType.Vector3 => FromVector3(Vector3.zero),
            PropertyType.Vector2 => FromVector2(Vector2.zero),
            _ => FromFloat(0f),
        };
    }
}
