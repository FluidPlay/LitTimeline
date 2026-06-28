using UnityEditor;
using UnityEngine;

namespace LitTimeline.Editor
{
    [CustomEditor(typeof(LitTimelineController))]
    public class LitTimelineControllerInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var ctrl = (LitTimelineController)target;

            if (GUILayout.Button("Open Lit Timeline", GUILayout.Height(28)))
                LitTimelineWindow.ShowWindow();

            GUILayout.Space(6);

            EditorGUI.BeginChangeCheck();
            var newClip = (LitTimelineClip)EditorGUILayout.ObjectField(
                "Clip", ctrl.Clip, typeof(LitTimelineClip), false);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(ctrl, "Assign Timeline Clip");
                ctrl.SetClip(newClip);
                EditorUtility.SetDirty(ctrl);
            }

            if (ctrl.Clip == null)
            {
                GUILayout.Space(4);
                if (GUILayout.Button("Create New Clip", GUILayout.Height(24)))
                {
                    string path = EditorUtility.SaveFilePanelInProject(
                        "Create Timeline Clip",
                        ctrl.gameObject.name + "_TimelineClip",
                        "asset",
                        "Save Timeline Clip asset");
                    if (!string.IsNullOrEmpty(path))
                    {
                        var clip = ScriptableObject.CreateInstance<LitTimelineClip>();
                        AssetDatabase.CreateAsset(clip, path);
                        AssetDatabase.SaveAssets();
                        Undo.RecordObject(ctrl, "Assign Timeline Clip");
                        ctrl.SetClip(clip);
                        EditorUtility.SetDirty(ctrl);
                    }
                }
            }
            else
            {
                var seq = ctrl.Sequence;
                GUILayout.Space(6);

                EditorGUI.BeginChangeCheck();
                float newTimeScale = EditorGUILayout.FloatField("Time Scale", seq.timeScale);
                bool newPlayOnAwake = EditorGUILayout.Toggle("Play On Awake", seq.playOnAwake);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(ctrl.Clip, "Edit Sequence Settings");
                    seq.timeScale = newTimeScale;
                    seq.playOnAwake = newPlayOnAwake;
                    EditorUtility.SetDirty(ctrl.Clip);
                }

                GUILayout.Space(4);

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.LabelField("Entries", seq.entries.Count.ToString());
                EditorGUILayout.LabelField("Duration", $"{seq.TotalDuration:F2}s");
                EditorGUI.EndDisabledGroup();

                GUILayout.Space(4);

                EditorGUI.BeginDisabledGroup(!EditorApplication.isPlaying);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Play")) 
                    ctrl.Play();
                if (ctrl.IsPaused)
                {
                    if (GUILayout.Button("Resume")) 
                        ctrl.Resume();
                }
                else if (GUILayout.Button("Pause")) 
                    ctrl.Pause();
                if (GUILayout.Button("Stop")) 
                    ctrl.Stop();
                if (GUILayout.Button("Rewind")) 
                    ctrl.Rewind();
                GUILayout.EndHorizontal();
                EditorGUI.EndDisabledGroup();
            }

            GUILayout.Space(8);
            EditorGUILayout.LabelField("Events", EditorStyles.boldLabel);
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_onPlay"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_onPause"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_onStop"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_onComplete"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_onLoop"));
            serializedObject.ApplyModifiedProperties();
        }

        public override bool RequiresConstantRepaint() => EditorApplication.isPlaying;
    }
}
