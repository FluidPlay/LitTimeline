using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LitTimeline.Editor
{
    public class DiscoveredProperty
    {
        public string HierarchyPath;
        public string ComponentTypeName;
        public string ComponentShortName;
        public string PropertyName;
        public string DisplayName;
        public PropertyType ValueType;
        public GameObject OwnerObject;
    }

    public static class ComponentPropertyScanner
    {
        public static List<DiscoveredProperty> Scan(Transform root)
        {
            var results = new List<DiscoveredProperty>();
            ScanRecursive(root, root, results);
            return results;
        }

        private static void ScanRecursive(Transform root, Transform current, List<DiscoveredProperty> results)
        {
            string path = AnimationUtility.CalculateTransformPath(current, root);

            foreach (var component in current.GetComponents<Component>())
            {
                if (component == null) continue;
                string typeName = component.GetType().FullName;

                var descriptors = PropertyAccessorRegistry.GetSupportedProperties(typeName);
                foreach (var desc in descriptors)
                {
                    results.Add(new DiscoveredProperty
                    {
                        HierarchyPath = path,
                        ComponentTypeName = typeName,
                        ComponentShortName = component.GetType().Name,
                        PropertyName = desc.PropertyName,
                        DisplayName = desc.DisplayName,
                        ValueType = desc.ValueType,
                        OwnerObject = current.gameObject
                    });
                }

                if (component is Renderer renderer)
                    ScanMaterialProperties(renderer, path, typeName, results);
            }

            foreach (Transform child in current)
                ScanRecursive(root, child, results);
        }

        private static void ScanMaterialProperties(Renderer renderer, string path, string typeName, List<DiscoveredProperty> results)
        {
            if (renderer.sharedMaterial == null) return;
            var shader = renderer.sharedMaterial.shader;
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyFlags(i).HasFlag(ShaderPropertyFlags.HideInInspector))
                    continue;
                var shaderPropType = shader.GetPropertyType(i); //ShaderUtil.GetPropertyType(shader, i));
                string shaderPropName = shader.GetPropertyName(i); //ShaderUtil.GetPropertyName(shader, i));
                string shaderPropDesc = shader.GetPropertyDescription(i); //ShaderUtil.GetPropertyDescription(shader, i));

                string propName;
                PropertyType valueType;
                if (shaderPropType == ShaderPropertyType.Color) //if (shaderPropType == ShaderUtil.ShaderPropertyType.Color)
                {
                    propName = "mat_color:" + shaderPropName;
                    valueType = PropertyType.Color;
                } else 
                    continue;

                results.Add(new DiscoveredProperty
                {
                    HierarchyPath = path,
                    ComponentTypeName = typeName,
                    ComponentShortName = renderer.GetType().Name,
                    PropertyName = propName,
                    DisplayName = "Material / " + shaderPropDesc,
                    ValueType = valueType,
                    OwnerObject = renderer.gameObject
                });
            }
        }
    }
}
