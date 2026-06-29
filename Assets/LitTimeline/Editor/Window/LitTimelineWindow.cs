using System.Collections.Generic;
using LitMotion;
using UnityEditor;
using UnityEngine;
#if USING_SPINE
using Spine.Unity;
#endif

namespace LitTimeline.Editor
{
    public class LitTimelineWindow : EditorWindow
    {
        // ─── Layout constants ──────────────────────────────────────────────────
        private const float LabelWidth = 220f;
        private const float TimelineHeight = 24f;
        private const float HeaderHeight = 22f;
        private float _inspectorActualHeight = 180f; // measured each Repaint, used next frame
        private const float HandleWidth = 8f;
        private const float MinBlockWidth = 4f;
        private const float TimelineMinSecs = 5f;
        private const float TimeRulerHeight = 20f;
        private const float MarkerTrackHeight = 18f;

        // ─── State ─────────────────────────────────────────────────────────────
        private LitTimelineWindowState _state = new LitTimelineWindowState();
        private Vector2 _scrollPos;

        // Timeline view
        private float _viewDuration = 5f;
        private float _pixelsPerSec = 100f;
        private float _timelineScrollX = 0f; // seconds offset (pan)

        // Pan state
        private bool _panDragging;
        private float _panStartMouseX;
        private float _panStartScrollX;

        // Drag state
        private enum DragMode
        {
            None,
            MoveBlock,
            ResizeLeft,
            ResizeRight
        }

        private DragMode _dragMode;
        private TimelineEntryData _dragEntry;
        private float _dragStartMouseX;
        private float _dragStartDelay;
        private float _dragStartDuration;
        private bool _scrubDragging;
        private float _dragAccumulatedY;
        private Dictionary<TimelineEntryData, float> _dragStartDelays = new Dictionary<TimelineEntryData, float>();
        private List<List<TimelineEntryData>> _cachedTracks = new List<List<TimelineEntryData>>();
        private HashSet<string> _missingEntryIds = new HashSet<string>();

        // Multi-selection
        private HashSet<TimelineEntryData> _selectedEntries = new HashSet<TimelineEntryData>();

        // Marker state
        private bool _markerDragging;
        private EventMarkerData _dragMarker;
        private float _markerDragStartMouseX;
        private float _markerDragStartTime;
        private string _renamingMarkerId;
        private string _renamingMarkerName;
        private Rect _renamingMarkerWorldRect;
        private bool _focusMarkerRename;

        // ─── Styles (lazy) ─────────────────────────────────────────────────────
        private static GUIStyle _blockStyle;
        private static GUIStyle _labelStyle;
        private static GUIStyle _headerStyle;
        private static GUIStyle _whiteMiniLabel;
        private static Color _blockColorOff = new Color(0.4f, 0.4f, 0.4f, 0.6f);
        private static Color _tickColor = new Color(0.5f, 0.5f, 0.5f, 1f);

        // Spine block palette: #6e2cd1 unselected, #9f6adf selected. Picked directly
        // by entry.layerType so chained Spine entries on a tween-colored track still
        // show purple (matches the user's spec).
        private static readonly Color _spineColorUnselected = new Color(110f / 255f, 44f / 255f, 209f / 255f, 0.9f);
        private static readonly Color _spineColorSelected = new Color(159f / 255f, 106f / 255f, 223f / 255f, 0.95f);

        private static readonly Color[] _palette = new[]
        {
            new Color(0.28f, 0.56f, 0.90f, 0.9f),
            new Color(0.90f, 0.45f, 0.28f, 0.9f),
            new Color(0.35f, 0.80f, 0.45f, 0.9f),
            new Color(0.80f, 0.35f, 0.75f, 0.9f),
            new Color(0.88f, 0.78f, 0.22f, 0.9f),
            new Color(0.30f, 0.78f, 0.78f, 0.9f),
            new Color(0.88f, 0.32f, 0.48f, 0.9f),
            new Color(0.58f, 0.44f, 0.26f, 0.9f),
        };

        // ─── Menu ──────────────────────────────────────────────────────────────
        [MenuItem("Tools/Lit Timeline")]
        public static void ShowWindow()
        {
            var w = GetWindow<LitTimelineWindow>("Lit Timeline");
            w.minSize = new Vector2(600, 400);
        }

        // ─── Lifecycle ─────────────────────────────────────────────────────────
        private void OnEnable()
        {
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            _state.Evaluate();
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            _state.ExitPreviewMode();
        }

        private void OnFocus()
        {
            ValidateBindings();
            Repaint();
        }

        private void OnSelectionChanged()
        {
            _state.Evaluate();
            _selectedEntries.Clear();
            ValidateBindings();
            Repaint();
        }

        private void ValidateBindings()
        {
            _missingEntryIds.Clear();
            if (_state.Controller?.Sequence == null) return;
            foreach (var entry in _state.Controller.Sequence.entries)
            {
                if (entry.binding == null)
                {
                    _missingEntryIds.Add(entry.entryId);
                    continue;
                }

                if (entry.layerType == LayerType.Spine)
                {
#if USING_SPINE
                    var sa = SpineLayerDriver.ResolveSkeleton(_state.Controller, entry.binding);
                    if (sa == null) _missingEntryIds.Add(entry.entryId);
#endif
                    // Without USING_SPINE we can't compile against SkeletonAnimation;
                    // the data is still preserved, so don't flag it as missing.
                    continue;
                }

                var comp = LitTimelineWindowState.ResolveComponent(_state.Controller, entry.binding);
                if (comp == null) _missingEntryIds.Add(entry.entryId);
            }
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode)
                _state.ExitPreviewMode();
        }

        // ─── Main GUI ──────────────────────────────────────────────────────────
        private void OnGUI()
        {
            InitStyles();

            if (_renamingMarkerId != null)
            {
                Event ev = Event.current;
                if (ev.rawType == EventType.MouseDown && !_renamingMarkerWorldRect.Contains(ev.mousePosition))
                {
                    CommitMarkerRename();
                    Repaint();
                }
                else if (ev.type == EventType.KeyDown)
                {
                    if (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter)
                    {
                        CommitMarkerRename();
                        ev.Use();
                        Repaint();
                    }
                    else if (ev.keyCode == KeyCode.Escape)
                    {
                        _renamingMarkerId = null;
                        _renamingMarkerName = null;
                        _focusMarkerRename = false;
                        GUI.FocusControl(null);
                        ev.Use();
                        Repaint();
                    }
                }
            }

            // Guard: controller destroyed externally (deleted from hierarchy/component removed).
            if (_state.Mode == WindowMode.HasController && !_state.Controller)
            {
                if (Event.current.type == EventType.Layout)
                    EditorApplication.delayCall += () =>
                    {
                        _state.Evaluate();
                        Repaint();
                    };
                DrawNoSelection();
                return;
            }

            // Keyboard shortcuts for selected entries
            if (_selectedEntries.Count > 0 && _state.Controller?.Clip != null)
            {
                Event e = Event.current;
                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Delete)
                {
                    Undo.RecordObject(_state.Controller.Clip, "Remove Timeline Entries");
                    foreach (var entry in _selectedEntries)
                        _state.Controller.Sequence.entries.Remove(entry);
                    ClearSelection();
                    EditorUtility.SetDirty(_state.Controller.Clip);
                    e.Use();
                    Repaint();
                }
                else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.D && e.control)
                {
                    var sources = new List<TimelineEntryData>(_selectedEntries);
                    ClearSelection();
                    foreach (var src in sources)
                        AddToSelection(DuplicateEntry(_state.Controller, _state.Controller.Sequence, src));
                    e.Use();
                }
            }

            switch (_state.Mode)
            {
                case WindowMode.NoSelection: DrawNoSelection(); break;
                case WindowMode.NoComponent: DrawNoComponent(); break;
                case WindowMode.NoClip: DrawNoClip(); break;
                case WindowMode.HasController: DrawMainUI(); break;
            }

            if (_state.IsPreviewPlaying)
                Repaint();
        }

        // ─── State panels ──────────────────────────────────────────────────────
        private void DrawNoSelection()
        {
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label("Select a GameObject to begin.", EditorStyles.largeLabel);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
        }

        private void DrawNoComponent()
        {
            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical();

            GUILayout.Label($"\"{Selection.activeGameObject?.name}\" has no LitTimelineController.", EditorStyles.wordWrappedLabel);
            GUILayout.Space(8);

            if (GUILayout.Button("Create Timeline Animation", GUILayout.Height(30)))
            {
                Undo.AddComponent<LitTimelineController>(Selection.activeGameObject);
                _state.Evaluate();
            }

            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
        }

        private void DrawNoClip()
        {
            var ctrl = _state.Controller;

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(GUILayout.Width(320));

            GUILayout.Label($"\"{ctrl.gameObject.name}\" has no Timeline Clip assigned.", EditorStyles.wordWrappedLabel);
            GUILayout.Space(8);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Assign Clip:", GUILayout.Width(80));
            var assigned = (LitTimelineClip)EditorGUILayout.ObjectField(null, typeof(LitTimelineClip), false);
            if (assigned != null)
            {
                Undo.RecordObject(ctrl, "Assign Timeline Clip");
                ctrl.SetClip(assigned);
                EditorUtility.SetDirty(ctrl);
                _state.Evaluate();
                Repaint();
            }

            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            if (GUILayout.Button("Create New Clip", GUILayout.Height(28)))
            {
                var newClip = CreateNewClip(ctrl.gameObject.name);
                if (newClip != null)
                {
                    Undo.RecordObject(ctrl, "Assign Timeline Clip");
                    ctrl.SetClip(newClip);
                    EditorUtility.SetDirty(ctrl);
                    _state.Evaluate();
                    Repaint();
                }
            }

            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
        }

        private void DrawMainUI()
        {
            var ctrl = _state.Controller;
            var seq = ctrl.Sequence;

            _viewDuration = (position.width - LabelWidth) / _pixelsPerSec;

            DrawToolbar(ctrl, seq);
            DrawTrackArea(ctrl, seq);

            if (_state.SelectedEntry != null)
            {
                DrawEntryInspector(_state.SelectedEntry, ctrl);
                if (Event.current.type == EventType.Repaint)
                {
                    float h = GUILayoutUtility.GetLastRect().height;
                    if (h > 10f) _inspectorActualHeight = h;
                }
            }
        }

        // ─── Toolbar ───────────────────────────────────────────────────────────
        private void DrawToolbar(LitTimelineController ctrl, TimelineSequenceData seq)
        {
            GUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button(ctrl.gameObject.name, EditorStyles.toolbarButton, GUILayout.Width(100)))
            {
                Selection.activeGameObject = ctrl.gameObject;
                EditorGUIUtility.PingObject(ctrl.gameObject);
                SceneView.lastActiveSceneView.FrameSelected();
            }

            var newClip = (LitTimelineClip)EditorGUILayout.ObjectField(
                ctrl.Clip, typeof(LitTimelineClip), false, GUILayout.Width(130));
            if (newClip != ctrl.Clip)
            {
                Undo.RecordObject(ctrl, "Assign Timeline Clip");
                ctrl.SetClip(newClip);
                EditorUtility.SetDirty(ctrl);
                _state.Evaluate();
                Repaint();
            }

            if (GUILayout.Button("New Clip", EditorStyles.toolbarButton, GUILayout.Width(58)))
            {
                var created = CreateNewClip(ctrl.gameObject.name);
                if (created != null)
                {
                    Undo.RecordObject(ctrl, "Assign Timeline Clip");
                    ctrl.SetClip(created);
                    EditorUtility.SetDirty(ctrl);
                    _state.Evaluate();
                    Repaint();
                }
            }

            GUILayout.FlexibleSpace();

            // Preview toggle
            EditorGUI.BeginDisabledGroup(EditorApplication.isPlaying);
            bool previewNow = GUILayout.Toggle(
                _state.IsPreviewEnabled,
                "Preview",
                EditorStyles.toolbarButton,
                GUILayout.Width(55));
            if (previewNow != _state.IsPreviewEnabled)
            {
                if (previewNow) _state.EnterPreviewMode();
                else _state.ExitPreviewMode();
            }

            EditorGUI.EndDisabledGroup();

            GUILayout.Space(4);

            EditorGUI.BeginDisabledGroup(EditorApplication.isPlaying);

            bool playToggle = GUILayout.Toggle(
                _state.IsPreviewPlaying,
                new GUIContent(EditorGUIUtility.IconContent("PlayButton").image, "Play preview"),
                EditorStyles.toolbarButton, GUILayout.Width(30));
            if (playToggle != _state.IsPreviewPlaying)
            {
                if (playToggle)
                {
                    if (!_state.IsPreviewEnabled) _state.EnterPreviewMode();
                    _state.StartPreview();
                }
                else _state.PausePreview();
            }

            bool pauseToggle = GUILayout.Toggle(
                _state.IsPreviewEnabled && !_state.IsPreviewPlaying,
                new GUIContent(EditorGUIUtility.IconContent("PauseButton").image, "Pause preview"),
                EditorStyles.toolbarButton, GUILayout.Width(30));
            if (pauseToggle != (_state.IsPreviewEnabled && !_state.IsPreviewPlaying))
            {
                if (pauseToggle) _state.PausePreview();
                else _state.StartPreview();
            }

            if (GUILayout.Button(new GUIContent(EditorGUIUtility.IconContent("PreMatQuad").image, "Stop preview"), EditorStyles.toolbarButton, GUILayout.Width(30)))
            {
                _state.StopPreview();
                ClearSelection();
            }

            if (GUILayout.Button(new GUIContent(EditorGUIUtility.IconContent("Animation.FirstKey").image, "Rewind to start"), EditorStyles.toolbarButton, GUILayout.Width(30)))
                _state.RewindPreview();

            GUILayout.Space(2);

            _state.PreviewLoop = GUILayout.Toggle(
                _state.PreviewLoop,
                new GUIContent(EditorGUIUtility.IconContent("RotateTool").image, "Loop preview"),
                EditorStyles.toolbarButton,
                GUILayout.Width(30));

            EditorGUI.EndDisabledGroup();

            GUILayout.Space(6);

            EditorGUI.BeginDisabledGroup(EditorApplication.isPlaying);
            GUILayout.Label("Speed", EditorStyles.miniLabel, GUILayout.Width(36));
            float newSpeed = GUILayout.HorizontalSlider(_state.PreviewSpeed, 0.25f, 2f, GUILayout.Width(70));
            if (!Mathf.Approximately(newSpeed, _state.PreviewSpeed))
                _state.PreviewSpeed = newSpeed;
            GUILayout.Label($"{_state.PreviewSpeed:F2}x", EditorStyles.miniLabel, GUILayout.Width(34));
            EditorGUI.EndDisabledGroup();

            GUILayout.Space(4);

            if (GUILayout.Button(new GUIContent("⟲ View", "Reset timeline zoom and scroll"), EditorStyles.toolbarButton, GUILayout.Width(48)))
            {
                _pixelsPerSec = 100f;
                _timelineScrollX = 0f;
                Repaint();
            }

            GUILayout.Space(4);

            if (GUILayout.Button("+ Add Property", EditorStyles.toolbarButton))
                ShowAddPropertyMenu(ctrl, seq);

            GUILayout.EndHorizontal();
        }

        // ─── Track area ────────────────────────────────────────────────────────
        private void DrawTrackArea(LitTimelineController ctrl, TimelineSequenceData seq)
        {
            Event ev = Event.current;

            if (ev.type == EventType.ScrollWheel)
            {
                float factor = ev.delta.y > 0f ? 0.85f : 1.15f;
                _pixelsPerSec = Mathf.Clamp(_pixelsPerSec * factor, 100f, 500f);
                ev.Use();
                Repaint();
            }
            else if (ev.type == EventType.MouseDown && ev.button == 2)
            {
                _panDragging = true;
                _panStartMouseX = ev.mousePosition.x;
                _panStartScrollX = _timelineScrollX;
                ev.Use();
            }
            else if (_panDragging)
            {
                if (ev.type == EventType.MouseDrag)
                {
                    float delta = (ev.mousePosition.x - _panStartMouseX) / _pixelsPerSec;
                    _timelineScrollX = Mathf.Max(0f, _panStartScrollX - delta);
                    ev.Use();
                    Repaint();
                }
                else if (ev.type == EventType.MouseUp)
                {
                    _panDragging = false;
                    ev.Use();
                }
            }

            float inspectorH = _state.SelectedEntry == null ? 0f : _inspectorActualHeight;
            float availableHeight = position.height
                                    - HeaderHeight
                                    - TimeRulerHeight
                                    - inspectorH;

            _scrollPos = GUILayout.BeginScrollView(_scrollPos, false, false, GUIStyle.none, GUI.skin.verticalScrollbar, GUILayout.Height(availableHeight));

            DrawTimeRuler();
            DrawMarkerTrack(seq, ctrl);

            _cachedTracks = GetTrackGroups(seq.entries);
            var tracks = _cachedTracks;

            if (tracks.Count == 0)
            {
                GUILayout.Space(20);
                GUILayout.Label("  No properties. Click '+ Add Property'.", EditorStyles.miniLabel);
            }
            else
            {
                for (int ti = 0; ti < tracks.Count; ti++)
                    DrawTrackRow(tracks[ti], ti, ctrl, seq);
            }

            GUILayout.EndScrollView();

            EditorGUI.DrawRect(new Rect(LabelWidth - 1, HeaderHeight, 1f, availableHeight + TimeRulerHeight), new Color(0.1f, 0.1f, 0.1f, 1f));

            Rect timelineArea = new Rect(LabelWidth, HeaderHeight, position.width - LabelWidth, availableHeight + TimeRulerHeight);
            EditorGUIUtility.AddCursorRect(timelineArea, _panDragging ? MouseCursor.Pan : MouseCursor.Arrow);

            Event evEmpty = Event.current;
            if (evEmpty.type == EventType.MouseDown && evEmpty.button == 0 && !evEmpty.shift && !evEmpty.control)
            {
                Rect fullArea = new Rect(0, HeaderHeight, position.width, availableHeight);
                if (fullArea.Contains(evEmpty.mousePosition))
                {
                    ClearSelection();
                    evEmpty.Use();
                    Repaint();
                }
            }

            HandleDrag();
        }

        private static List<List<TimelineEntryData>> GetTrackGroups(List<TimelineEntryData> entries)
        {
            var groups = new List<List<TimelineEntryData>>();
            var seen = new Dictionary<string, List<TimelineEntryData>>();

            foreach (var entry in entries)
            {
                string id = string.IsNullOrEmpty(entry.trackId) ? entry.entryId : entry.trackId;
                if (!seen.TryGetValue(id, out var group))
                {
                    group = new List<TimelineEntryData>();
                    seen[id] = group;
                    groups.Add(group);
                }

                group.Add(entry);
            }

            return groups;
        }

        private static readonly float[] _tickCandidates = { 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 30f, 60f };

        private const float SnapInterval = 0.01f;

        private static float SnapValue(float value, bool free = false)
        {
            if (free) return value;
            return Mathf.Round(value / SnapInterval) * SnapInterval;
        }

        private static float PickInterval(float pixelsPerSec, float minPx)
        {
            foreach (var c in _tickCandidates)
                if (c * pixelsPerSec >= minPx)
                    return c;
            return _tickCandidates[_tickCandidates.Length - 1];
        }

        private static string FormatTick(float t)
        {
            float rounded = Mathf.Round(t * 1000f) / 1000f;
            if (Mathf.Approximately(rounded, Mathf.Round(rounded)))
                return $"{Mathf.RoundToInt(rounded)}s";
            string s = $"{rounded:F2}".TrimEnd('0');
            return s + "s";
        }

        private void DrawTimeRuler()
        {
            Rect rulerRect = GUILayoutUtility.GetRect(position.width, TimeRulerHeight);
            EditorGUI.DrawRect(rulerRect, rulerColor);

            float minorInterval = PickInterval(_pixelsPerSec, minPx: 12f);
            float majorInterval = PickInterval(_pixelsPerSec, minPx: 55f);

            float startTime = _timelineScrollX;
            float endTime = _timelineScrollX + _viewDuration + minorInterval;

            int firstMinor = Mathf.FloorToInt(startTime / minorInterval);
            int lastMinor = Mathf.CeilToInt(endTime / minorInterval);
            for (int i = firstMinor; i <= lastMinor; i++)
            {
                float t = i * minorInterval;
                float x = LabelWidth + (t - _timelineScrollX) * _pixelsPerSec;
                if (x < LabelWidth) continue;
                EditorGUI.DrawRect(new Rect(x, rulerRect.yMax - TimeRulerHeight * 0.35f, 1f, TimeRulerHeight * 0.35f),
                    new Color(0.5f, 0.5f, 0.5f, 0.6f));
            }

            int firstMajor = Mathf.FloorToInt(startTime / majorInterval);
            int lastMajor = Mathf.CeilToInt(endTime / majorInterval);
            for (int i = firstMajor; i <= lastMajor; i++)
            {
                float t = i * majorInterval;
                float x = LabelWidth + (t - _timelineScrollX) * _pixelsPerSec;
                if (x < LabelWidth) continue;
                EditorGUI.DrawRect(new Rect(x, rulerRect.y, 1f, TimeRulerHeight), _tickColor);
                GUI.Label(new Rect(x + 2f, rulerRect.y, 48f, TimeRulerHeight), FormatTick(t), EditorStyles.miniLabel);
            }

            Event e = Event.current;
            Rect scrubZone = new Rect(LabelWidth, rulerRect.y, rulerRect.width - LabelWidth, rulerRect.height);
            if (e.button == 0 && e.type == EventType.MouseDown && scrubZone.Contains(e.mousePosition))
            {
                if (!_state.IsPreviewEnabled) _state.EnterPreviewMode();
                _scrubDragging = true;
                float t = (e.mousePosition.x - LabelWidth) / _pixelsPerSec + _timelineScrollX;
                _state.GotoTime(t);
                e.Use();
                Repaint();
            }

            float phX = LabelWidth + (_state.CurrentTime - _timelineScrollX) * _pixelsPerSec;
            if (phX >= LabelWidth)
            {
                EditorGUI.DrawRect(new Rect(phX - 1, rulerRect.y, 2, rulerRect.height), new Color(1f, 0.3f, 0.3f, 1f));
                if (_state.CurrentTime > 0.001f)
                    GUI.Label(new Rect(phX + 3, rulerRect.y, 50f, rulerRect.height), $"{_state.CurrentTime:F2}s", EditorStyles.miniLabel);
            }
        }

        private void DrawTrackRow(List<TimelineEntryData> track, int trackIndex, LitTimelineController ctrl, TimelineSequenceData seq)
        {
            bool anySelected = track.Exists(e => _selectedEntries.Contains(e));

            GUILayout.BeginHorizontal(GUILayout.Height(TimelineHeight));

            var labelRect = GUILayoutUtility.GetRect(LabelWidth, TimelineHeight,
                GUILayout.Width(LabelWidth), GUILayout.Height(TimelineHeight));

            EditorGUI.DrawRect(labelRect, anySelected
                ? new Color(0.25f, 0.35f, 0.25f, 0.5f)
                : (trackIndex % 2 == 0 ? new Color(0.18f, 0.18f, 0.18f, 0.3f) : new Color(0.22f, 0.22f, 0.22f, 0.3f)));

            Rect toggleRect = new Rect(labelRect.x + 2, labelRect.y + 3, 16, 16);
            bool allEnabled = track.TrueForAll(e => e.isEnabled);
            bool enabled = GUI.Toggle(toggleRect, allEnabled, new GUIContent("", "Enable / disable track"));
            if (enabled != allEnabled)
            {
                Undo.RecordObject(ctrl.Clip, "Toggle Track");
                foreach (var te in track) te.isEnabled = enabled;
                EditorUtility.SetDirty(ctrl.Clip);
            }

            var firstEntry = track[0];
            bool trackMissing = track.Exists(e => _missingEntryIds.Contains(e.entryId));
            float warnWidth = trackMissing ? 18f : 0f;
            Rect textRect = new Rect(labelRect.x + 20, labelRect.y, labelRect.width - 58 - warnWidth, labelRect.height);
            if (GUI.Button(textRect, TrackLabel(firstEntry, ctrl.transform), EditorStyles.label))
            {
                if (anySelected) ClearSelection();
                else SetSelection(firstEntry);
            }

            if (trackMissing)
            {
                Rect warnRect = new Rect(textRect.xMax + 2, labelRect.y + 2, 16, 16);
                var warnContent = new GUIContent(
                    EditorGUIUtility.IconContent("console.warnicon.sml").image,
                    "Missing: bound object or component no longer exists.");
                GUI.Label(warnRect, warnContent, GUIStyle.none);
            }

            Rect addRect = new Rect(labelRect.xMax - 38, labelRect.y + 2, 18, 18);
            if (GUI.Button(addRect, new GUIContent("+", "Add chained tween to this track"), EditorStyles.miniButton))
                ShowChainMenu(ctrl, seq, track);

            Rect delRect = new Rect(labelRect.xMax - 18, labelRect.y + 2, 18, 18);
            if (GUI.Button(delRect, new GUIContent("×", "Delete track"), EditorStyles.miniButton))
            {
                Undo.RecordObject(ctrl.Clip, "Remove Track");
                foreach (var te in track) seq.entries.Remove(te);
                foreach (var te in track) _selectedEntries.Remove(te);
                if (!_selectedEntries.Contains(_state.SelectedEntry)) _state.SelectedEntry = null;
                EditorUtility.SetDirty(ctrl.Clip);
                GUILayout.EndHorizontal();
                return;
            }

            Rect trackRect = GUILayoutUtility.GetRect(
                position.width - LabelWidth, TimelineHeight,
                GUILayout.ExpandWidth(true), GUILayout.Height(TimelineHeight));

            EditorGUI.DrawRect(trackRect, new Color(0.12f, 0.12f, 0.12f, 0.4f));

            Color tColor = track[0].trackColor;
            foreach (var entry in track)
                DrawBlock(entry, trackRect, ctrl, seq, tColor);

            Event evTrack = Event.current;
            if (evTrack.button == 0 && evTrack.type == EventType.MouseDown
                                    && trackRect.Contains(evTrack.mousePosition))
            {
                if (!_state.IsPreviewEnabled) _state.EnterPreviewMode();
                _scrubDragging = true;
                float t = (evTrack.mousePosition.x - LabelWidth) / _pixelsPerSec + _timelineScrollX;
                _state.GotoTime(t);
                evTrack.Use();
                Repaint();
            }

            float phX = trackRect.x + (_state.CurrentTime - _timelineScrollX) * _pixelsPerSec;
            if (phX >= trackRect.x)
                EditorGUI.DrawRect(new Rect(phX - 1, trackRect.y, 2, trackRect.height), new Color(1f, 0.3f, 0.3f, 0.85f));

            GUILayout.EndHorizontal();
        }

        private void DrawMarkerTrack(TimelineSequenceData seq, LitTimelineController ctrl)
        {
            if (seq.markers == null) seq.markers = new System.Collections.Generic.List<EventMarkerData>();

            GUILayout.BeginHorizontal(GUILayout.Height(MarkerTrackHeight));

            Rect labelRect = GUILayoutUtility.GetRect(LabelWidth, MarkerTrackHeight,
                GUILayout.Width(LabelWidth), GUILayout.Height(MarkerTrackHeight));
            EditorGUI.DrawRect(labelRect, new Color(0.13f, 0.12f, 0.10f, 0.7f));
            GUI.Label(new Rect(labelRect.x + 20, labelRect.y + 1, labelRect.width - 40, labelRect.height),
                "Event Markers", EditorStyles.miniLabel);

            Rect addRect = new Rect(labelRect.xMax - 18, labelRect.y + 1, 18, 16);
            EditorGUI.BeginDisabledGroup(!_state.IsPreviewEnabled);
            if (GUI.Button(addRect, new GUIContent("+", "Add marker at playhead (enter Preview mode first)"), EditorStyles.miniButton))
            {
                float newMarkerTime = SnapValue(Mathf.Max(0f, _state.CurrentTime));
                if (!seq.markers.Exists(m => Mathf.Approximately(m.time, newMarkerTime)))
                {
                    Undo.RecordObject(ctrl.Clip, "Add Event Marker");
                    var m = new EventMarkerData { time = newMarkerTime };
                    seq.markers.Add(m);
                    EditorUtility.SetDirty(ctrl.Clip);
                    _renamingMarkerId = m.markerId;
                    _renamingMarkerName = m.displayName;
                    _focusMarkerRename = true;
                    Repaint();
                }
            }
            EditorGUI.EndDisabledGroup();

            Rect trackRect = GUILayoutUtility.GetRect(
                position.width - LabelWidth, MarkerTrackHeight,
                GUILayout.ExpandWidth(true), GUILayout.Height(MarkerTrackHeight));
            EditorGUI.DrawRect(trackRect, new Color(0.09f, 0.09f, 0.07f, 0.6f));

            float phX = trackRect.x + (_state.CurrentTime - _timelineScrollX) * _pixelsPerSec;
            if (phX >= trackRect.x)
                EditorGUI.DrawRect(new Rect(phX - 1, trackRect.y, 2, trackRect.height), new Color(1f, 0.3f, 0.3f, 0.85f));

            Event e = Event.current;

            for (int mi = seq.markers.Count - 1; mi >= 0; mi--)
            {
                var marker = seq.markers[mi];
                float mx = trackRect.x + (marker.time - _timelineScrollX) * _pixelsPerSec;
                if (mx < trackRect.x - 12f || mx > trackRect.xMax + 12f) continue;

                Color markerColor = marker.isEnabled
                    ? new Color(1f, 0.82f, 0.1f, 0.95f)
                    : new Color(0.5f, 0.5f, 0.5f, 0.5f);

                float capSize = 7f;
                float capCenterY = trackRect.y + capSize * 0.5f + 1f;
                Matrix4x4 savedMatrix = GUI.matrix;
                GUIUtility.RotateAroundPivot(45f, new Vector2(mx, capCenterY));
                EditorGUI.DrawRect(new Rect(mx - capSize * 0.5f, capCenterY - capSize * 0.5f, capSize, capSize), markerColor);
                GUI.matrix = savedMatrix;

                float stemTop = capCenterY + capSize * 0.5f;
                EditorGUI.DrawRect(new Rect(mx - 0.5f, stemTop, 1f, trackRect.yMax - stemTop - 1f), markerColor);

                Rect hitRect = new Rect(mx - 6, trackRect.y, 12, trackRect.height);

                if (marker.markerId == _renamingMarkerId)
                {
                    string controlName = "MarkerRename_" + marker.markerId;
                    GUI.SetNextControlName(controlName);
                    Rect renameRect = new Rect(mx + 4, trackRect.y + 1, 70, MarkerTrackHeight - 3);
                    _renamingMarkerWorldRect = renameRect;
                    _renamingMarkerName = GUI.TextField(renameRect, _renamingMarkerName ?? marker.displayName, EditorStyles.miniTextField);
                    if (_focusMarkerRename)
                    {
                        EditorGUI.FocusTextInControl(controlName);
                        _focusMarkerRename = false;
                    }
                }
                else
                {
                    GUI.Label(new Rect(mx + 5, trackRect.y + 1, 80, trackRect.height),
                        marker.displayName, EditorStyles.miniLabel);
                }

                if (e.type == EventType.ContextClick && hitRect.Contains(e.mousePosition))
                {
                    var capturedMarker = marker;
                    var capturedSeq = seq;
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Rename"), false, () =>
                    {
                        _renamingMarkerId = capturedMarker.markerId;
                        _renamingMarkerName = capturedMarker.displayName;
                        _focusMarkerRename = true;
                        Repaint();
                    });
                    menu.AddItem(new GUIContent(capturedMarker.isEnabled ? "Disable" : "Enable"), false, () =>
                    {
                        Undo.RecordObject(ctrl.Clip, "Toggle Marker");
                        capturedMarker.isEnabled = !capturedMarker.isEnabled;
                        EditorUtility.SetDirty(ctrl.Clip);
                    });
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Delete"), false, () =>
                    {
                        Undo.RecordObject(ctrl.Clip, "Delete Marker");
                        capturedSeq.markers.Remove(capturedMarker);
                        if (_renamingMarkerId == capturedMarker.markerId) _renamingMarkerId = null;
                        EditorUtility.SetDirty(ctrl.Clip);
                        Repaint();
                    });
                    menu.ShowAsContext();
                    e.Use();
                    GUILayout.EndHorizontal();
                    return;
                }

                if (e.type == EventType.MouseDown && e.button == 0 && hitRect.Contains(e.mousePosition))
                {
                    if (_renamingMarkerId != null && _renamingMarkerId != marker.markerId)
                    {
                        var renaming = seq.markers.Find(m => m.markerId == _renamingMarkerId);
                        if (renaming != null)
                        {
                            Undo.RecordObject(ctrl.Clip, "Rename Marker");
                            renaming.displayName = _renamingMarkerName ?? renaming.displayName;
                            EditorUtility.SetDirty(ctrl.Clip);
                        }

                        _renamingMarkerId = null;
                        _renamingMarkerName = null;
                    }

                    _markerDragging = true;
                    _dragMarker = marker;
                    _markerDragStartMouseX = e.mousePosition.x;
                    _markerDragStartTime = marker.time;
                    e.Use();
                    GUILayout.EndHorizontal();
                    return;
                }
            }

            GUILayout.EndHorizontal();
        }

        private void CommitMarkerRename()
        {
            if (_renamingMarkerId == null) return;
            var seq = _state.Controller?.Sequence;
            var clip = _state.Controller?.Clip;
            if (seq?.markers != null && clip != null)
            {
                var m = seq.markers.Find(x => x.markerId == _renamingMarkerId);
                if (m != null)
                {
                    Undo.RecordObject(clip, "Rename Marker");
                    m.displayName = string.IsNullOrEmpty(_renamingMarkerName) ? m.displayName : _renamingMarkerName;
                    EditorUtility.SetDirty(clip);
                }
            }

            _renamingMarkerId = null;
            _renamingMarkerName = null;
            GUI.FocusControl(null);
        }

        private void DrawBlock(TimelineEntryData entry, Rect trackRect, LitTimelineController ctrl, TimelineSequenceData seq, Color trackColor)
        {
            bool isSelected = _selectedEntries.Contains(entry);

            float blockX = trackRect.x + (entry.delay - _timelineScrollX) * _pixelsPerSec;
            float blockW = Mathf.Max(MinBlockWidth, entry.EffectiveDuration * _pixelsPerSec);

            float clippedX = Mathf.Max(blockX, trackRect.x);
            float clippedW = blockX + blockW - clippedX;
            if (clippedW <= 0f) return;

            Rect blockRect = new Rect(blockX, trackRect.y + 2, blockW, trackRect.height - 4);
            Rect drawRect = new Rect(clippedX, trackRect.y + 2, clippedW, trackRect.height - 4);

            bool isSpine = entry.layerType == LayerType.Spine;
            Color baseColor = isSpine ? _spineColorUnselected : trackColor;
            Color highlight = isSpine ? _spineColorSelected : Color.Lerp(trackColor, Color.white, 0.35f);
            Color blockCol = !entry.isEnabled ? _blockColorOff
                : isSelected ? highlight
                : baseColor;
            EditorGUI.DrawRect(drawRect, blockCol);

            if (clippedW > 40)
            {
                if (_whiteMiniLabel == null)
                {
                    _whiteMiniLabel = new GUIStyle(EditorStyles.miniLabel);
                    _whiteMiniLabel.normal.textColor = Color.white;
                }

                GUI.Label(new Rect(clippedX + 4, drawRect.y, clippedW - 8, drawRect.height),
                    BlockLabel(entry, ctrl.transform), _whiteMiniLabel);
            }

            Event e = Event.current;

            if (e.type == EventType.ContextClick && blockRect.Contains(e.mousePosition))
            {
                var menu = new GenericMenu();
                var cap = entry;
                menu.AddItem(new GUIContent("Duplicate"), false, () =>
                    SetSelection(DuplicateEntry(ctrl, seq, cap)));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Delete"), false, () =>
                {
                    Undo.RecordObject(ctrl.Clip, "Remove Timeline Entry");
                    seq.entries.Remove(cap);
                    _selectedEntries.Remove(cap);
                    if (_state.SelectedEntry == cap) _state.SelectedEntry = _selectedEntries.Count > 0 ? _state.SelectedEntry : null;
                    EditorUtility.SetDirty(ctrl.Clip);
                    Repaint();
                });
                menu.ShowAsContext();
                e.Use();
                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                Rect leftHandle = new Rect(blockRect.x, blockRect.y, HandleWidth, blockRect.height);
                Rect rightHandle = new Rect(blockRect.xMax - HandleWidth, blockRect.y, HandleWidth, blockRect.height);

                if (leftHandle.Contains(e.mousePosition))
                {
                    SetSelection(entry);
                    BeginDrag(DragMode.ResizeLeft, entry);
                }
                else if (rightHandle.Contains(e.mousePosition))
                {
                    SetSelection(entry);
                    BeginDrag(DragMode.ResizeRight, entry);
                }
                else if (blockRect.Contains(e.mousePosition))
                {
                    if (e.shift)
                        AddToSelection(entry);
                    else if (e.control)
                        ToggleSelection(entry);
                    else
                    {
                        if (!isSelected)
                            SetSelection(entry);
                        BeginDrag(DragMode.MoveBlock, entry);
                    }
                }
            }

            EditorGUIUtility.AddCursorRect(new Rect(blockRect.x, blockRect.y, HandleWidth, blockRect.height), MouseCursor.ResizeHorizontal);
            EditorGUIUtility.AddCursorRect(new Rect(blockRect.xMax - HandleWidth, blockRect.y, HandleWidth, blockRect.height), MouseCursor.ResizeHorizontal);
            EditorGUIUtility.AddCursorRect(blockRect, MouseCursor.Pan);

            bool isResizing = _dragEntry == entry &&
                              (_dragMode == DragMode.ResizeLeft || _dragMode == DragMode.ResizeRight);
            if (isResizing && Event.current.type == EventType.Repaint)
            {
                string dLabel = $"{entry.EffectiveDuration:F2}s";
                Vector2 dSize = EditorStyles.miniLabel.CalcSize(new GUIContent(dLabel));
                float dX = Mathf.Clamp(blockRect.x + blockW * 0.5f - dSize.x * 0.5f,
                    trackRect.x, trackRect.xMax - dSize.x);
                Rect bgRect = new Rect(dX - 2, blockRect.y - dSize.y - 2, dSize.x + 4, dSize.y + 2);
                EditorGUI.DrawRect(bgRect, new Color(0f, 0f, 0f, 0.75f));
                GUI.Label(new Rect(dX, bgRect.y + 1, dSize.x, dSize.y), dLabel, EditorStyles.miniLabel);
            }
        }

        // ─── Add property context menu ─────────────────────────────────────────
        private void ShowAddPropertyMenu(LitTimelineController ctrl, TimelineSequenceData seq)
        {
            var props = ComponentPropertyScanner.Scan(ctrl.transform);
            var menu = new GenericMenu();

            foreach (var prop in props)
            {
                var capturedProp = prop;
                string label = string.IsNullOrEmpty(prop.HierarchyPath)
                    ? $"{prop.ComponentShortName}/{prop.DisplayName}"
                    : $"{prop.HierarchyPath}/{prop.ComponentShortName}/{prop.DisplayName}";

                menu.AddItem(new GUIContent(label), false, () =>
                {
                    var binding = new PropertyBinding
                    {
                        hierarchyPath = capturedProp.HierarchyPath,
                        componentTypeName = capturedProp.ComponentTypeName,
                        propertyName = capturedProp.PropertyName,
                        axis = PropertyAxis.None
                    };
                    AddEntry(ctrl, seq, binding);
                });
            }

#if USING_SPINE
            AppendSpineMenuItems(menu, ctrl, path =>
            {
                AddSpineEntry(ctrl, seq, path);
            });
#endif

            if (menu.GetItemCount() == 0)
                menu.AddDisabledItem(new GUIContent("No supported properties found"));

            menu.ShowAsContext();
        }

#if USING_SPINE
        private static void AppendSpineMenuItems(GenericMenu menu, LitTimelineController ctrl,
            System.Action<string> onPick)
        {
            ScanSpineSkeletons(ctrl.transform, (path, sa) =>
            {
                string capturedPath = path;
                string label = string.IsNullOrEmpty(path)
                    ? "SkeletonAnimation/Spine Animation"
                    : $"{path}/SkeletonAnimation/Spine Animation";
                menu.AddItem(new GUIContent(label), false, () => onPick(capturedPath));
            });
        }

        private static void ScanSpineSkeletons(Transform root, System.Action<string, SkeletonAnimation> visit)
        {
            ScanSpineRecursive(root, root, visit);
        }

        private static void ScanSpineRecursive(Transform root, Transform current,
            System.Action<string, SkeletonAnimation> visit)
        {
            string path = AnimationUtility.CalculateTransformPath(current, root);
            var sa = current.GetComponent<SkeletonAnimation>();
            if (sa != null) visit(path, sa);
            foreach (Transform child in current)
                ScanSpineRecursive(root, child, visit);
        }
#endif

        // ─── Chain context menu ────────────────────────────────────────────────
        private void ShowChainMenu(LitTimelineController ctrl, TimelineSequenceData seq, List<TimelineEntryData> track)
        {
            var props = ComponentPropertyScanner.Scan(ctrl.transform);
            var menu = new GenericMenu();

            foreach (var prop in props)
            {
                var capturedProp = prop;
                var capturedTrack = track;
                string label = string.IsNullOrEmpty(prop.HierarchyPath)
                    ? $"{prop.ComponentShortName}/{prop.DisplayName}"
                    : $"{prop.HierarchyPath}/{prop.ComponentShortName}/{prop.DisplayName}";

                menu.AddItem(new GUIContent(label), false, () =>
                    AddChainEntry(ctrl, seq, capturedTrack, capturedProp));
            }

#if USING_SPINE
            var capturedChainTrack = track;
            AppendSpineMenuItems(menu, ctrl, path =>
            {
                AddChainedSpineEntry(ctrl, seq, capturedChainTrack, path);
            });
#endif

            if (menu.GetItemCount() == 0)
                menu.AddDisabledItem(new GUIContent("No supported properties found"));

            menu.ShowAsContext();
        }

        private void AddChainEntry(LitTimelineController ctrl, TimelineSequenceData seq,
            List<TimelineEntryData> track, DiscoveredProperty prop)
        {
            float chainDelay = 0f;
            foreach (var e in track)
                if (e.EndTime > chainDelay)
                    chainDelay = e.EndTime;

            var binding = new PropertyBinding
            {
                hierarchyPath = prop.HierarchyPath,
                componentTypeName = prop.ComponentTypeName,
                propertyName = prop.PropertyName,
            };

            var entry = new TimelineEntryData
            {
                binding = binding,
                delay = chainDelay,
                trackId = track[0].trackId,
                trackColor = track[0].trackColor,
                startValue = PropertyValueUnion.DefaultForType(prop.ValueType),
                endValue = PropertyValueUnion.DefaultForType(prop.ValueType),
            };

            Undo.RecordObject(ctrl.Clip, "Add Chain Timeline Entry");
            seq.entries.Add(entry);
            _state.SelectedEntry = entry;
            EditorUtility.SetDirty(ctrl.Clip);
            Repaint();
        }

        // ─── Drag processing ───────────────────────────────────────────────────
        private void BeginDrag(DragMode mode, TimelineEntryData entry)
        {
            _dragMode = mode;
            _dragEntry = entry;
            _dragStartMouseX = Event.current.mousePosition.x;
            _dragStartDelay = entry.delay;
            _dragStartDuration = entry.EffectiveDuration;
            _dragAccumulatedY = 0f;
            _dragStartDelays.Clear();
            foreach (var sel in _selectedEntries)
                _dragStartDelays[sel] = sel.delay;
            Event.current.Use();
        }

        private void HandleDrag()
        {
            Event e = Event.current;

            if (_markerDragging)
            {
                if (e.type == EventType.MouseDrag)
                {
                    float delta = (e.mousePosition.x - _markerDragStartMouseX) / _pixelsPerSec;
                    Undo.RecordObject(_state.Controller.Clip, "Move Event Marker");
                    _dragMarker.time = SnapValue(Mathf.Max(0f, _markerDragStartTime + delta), e.control);
                    EditorUtility.SetDirty(_state.Controller.Clip);
                    e.Use();
                    Repaint();
                }
                else if (e.type == EventType.MouseUp)
                {
                    _markerDragging = false;
                    _dragMarker = null;
                    e.Use();
                }

                return;
            }

            if (_scrubDragging)
            {
                if (e.type == EventType.MouseDrag)
                {
                    float t = (e.mousePosition.x - LabelWidth) / _pixelsPerSec + _timelineScrollX;
                    _state.GotoTime(t);
                    e.Use();
                    Repaint();
                }
                else if (e.type == EventType.MouseUp)
                {
                    _scrubDragging = false;
                    e.Use();
                }

                return;
            }

            if (_dragMode == DragMode.None || _dragEntry == null) return;

            if (e.type == EventType.MouseDrag)
            {
                float deltaSecs = (e.mousePosition.x - _dragStartMouseX) / _pixelsPerSec;
                Undo.RecordObject(_state.Controller.Clip, "Edit Timeline Timing");

                switch (_dragMode)
                {
                    case DragMode.MoveBlock:
                    {
                        float desiredDelay = SnapValue(Mathf.Max(0f, _dragStartDelay + deltaSecs), e.control);
                        float dur = _dragStartDuration;
                        FindTrackGap(_dragEntry, desiredDelay, dur, out float prevEnd, out float nextStart);
                        _dragEntry.delay = Mathf.Clamp(desiredDelay, prevEnd, Mathf.Max(prevEnd, nextStart - dur));

                        float actualDelta = _dragEntry.delay - _dragStartDelay;
                        foreach (var kvp in _dragStartDelays)
                        {
                            if (kvp.Key == _dragEntry) continue;
                            kvp.Key.delay = Mathf.Max(0f, SnapValue(kvp.Value + actualDelta, e.control));
                        }

                        _dragAccumulatedY += e.delta.y;
                        if (Mathf.Abs(_dragAccumulatedY) >= TimelineHeight)
                        {
                            int dir = _dragAccumulatedY > 0f ? 1 : -1;
                            int primaryIdx = _cachedTracks.FindIndex(t => t.Contains(_dragEntry));
                            if (primaryIdx >= 0)
                            {
                                var seqEntries = _state.Controller.Sequence.entries;
                                var moves = new List<(TimelineEntryData entry, string newTrackId, int tgtIdx)>();
                                foreach (var sel in _selectedEntries)
                                {
                                    int curIdx = _cachedTracks.FindIndex(t => t.Contains(sel));
                                    if (curIdx < 0) continue;
                                    int tgtIdx = curIdx + dir;
                                    string newId = (tgtIdx < 0 || tgtIdx >= _cachedTracks.Count)
                                        ? System.Guid.NewGuid().ToString()
                                        : _cachedTracks[tgtIdx][0].trackId;
                                    moves.Add((sel, newId, tgtIdx));
                                }

                                foreach (var (sel, newId, tgtIdx) in moves)
                                {
                                    sel.trackId = newId;
                                    seqEntries.Remove(sel);
                                    if (tgtIdx < 0)
                                    {
                                        seqEntries.Insert(0, sel);
                                    }
                                    else if (tgtIdx >= _cachedTracks.Count)
                                    {
                                        seqEntries.Add(sel);
                                    }
                                    else
                                    {
                                        var targetTrack = _cachedTracks[tgtIdx];
                                        int insertAt = seqEntries.Count;
                                        for (int ei = seqEntries.Count - 1; ei >= 0; ei--)
                                        {
                                            if (targetTrack.Contains(seqEntries[ei]))
                                            {
                                                insertAt = ei + 1;
                                                break;
                                            }
                                        }

                                        seqEntries.Insert(insertAt, sel);
                                    }
                                }

                                _cachedTracks = GetTrackGroups(seqEntries);
                                _dragAccumulatedY -= dir * TimelineHeight;
                            }
                        }

                        break;
                    }
                    case DragMode.ResizeLeft:
                    {
                        float newDelay = SnapValue(Mathf.Clamp(_dragStartDelay + deltaSecs,
                            0f, _dragStartDelay + _dragStartDuration - 0.05f), e.control);
                        float newEffDur = _dragStartDuration - (newDelay - _dragStartDelay);
                        FindTrackGap(_dragEntry, newDelay, newEffDur, out float prevEnd, out _);
                        newDelay = Mathf.Max(newDelay, prevEnd);
                        newEffDur = _dragStartDuration - (newDelay - _dragStartDelay);
                        ApplyResizedEffectiveDuration(_dragEntry, newEffDur);
                        _dragEntry.delay = newDelay;
                        break;
                    }
                    case DragMode.ResizeRight:
                    {
                        float newEffDur = SnapValue(Mathf.Max(0.05f, _dragStartDuration + deltaSecs), e.control);
                        FindTrackGap(_dragEntry, _dragEntry.delay, newEffDur, out _, out float nextStart);
                        newEffDur = Mathf.Min(newEffDur, Mathf.Max(0.05f, nextStart - _dragEntry.delay));
                        ApplyResizedEffectiveDuration(_dragEntry, newEffDur);
                        break;
                    }
                }

                EditorUtility.SetDirty(_state.Controller.Clip);
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseUp)
            {
                _dragMode = DragMode.None;
                _dragEntry = null;
                e.Use();
            }
        }

        // For tween layers, the user-authored `duration` is the canonical playback length
        // and Speed scales the on-timeline footprint, so resizing rewrites duration.
        // For Spine layers, `duration == animationLength` is fixed (animation has a
        // natural length); resizing instead recomputes Speed.
        private static void ApplyResizedEffectiveDuration(TimelineEntryData entry, float newEffDur)
        {
            if (entry.layerType == LayerType.Spine)
            {
                float animLen = Mathf.Max(0.0001f, entry.spine != null ? entry.spine.animationLength : entry.duration);
                entry.duration = animLen;
                entry.speed = Mathf.Max(0.001f, animLen / Mathf.Max(0.001f, newEffDur));
            }
            else
            {
                entry.duration = newEffDur * Mathf.Max(0.001f, entry.speed);
            }
        }

        private void FindTrackGap(TimelineEntryData dragged, float desiredDelay, float duration,
            out float prevEnd, out float nextStart)
        {
            prevEnd = 0f;
            nextStart = float.MaxValue;

            foreach (var track in _cachedTracks)
            {
                if (!track.Contains(dragged)) continue;
                foreach (var e in track)
                {
                    if (e == dragged) continue;
                    if (_selectedEntries.Contains(e)) continue;
                    if (e.delay < desiredDelay)
                        prevEnd = Mathf.Max(prevEnd, e.EndTime);
                    else
                        nextStart = Mathf.Min(nextStart, e.delay);
                }

                break;
            }
        }

        // ─── Entry inspector ───────────────────────────────────────────────────
        private void DrawEntryInspector(TimelineEntryData entry, LitTimelineController ctrl)
        {
#if USING_SPINE
            if (entry.layerType == LayerType.Spine)
            {
                DrawSpineEntryInspector(entry, ctrl);
                return;
            }
#endif

            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(EditorStyles.helpBox);

            GUILayout.Space(5);

            bool isMissing = _missingEntryIds.Contains(entry.entryId);
            if (isMissing)
            {
                var prevColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f, 1f);
                GUILayout.BeginHorizontal(EditorStyles.helpBox);
                GUI.backgroundColor = prevColor;
                GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(20));
                GUILayout.Label(
                    $"Missing: \"{entry.binding?.hierarchyPath}\" not found. Object may have been deleted.",
                    EditorStyles.wordWrappedMiniLabel);
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(40));
            string defaultName = BlockLabel(entry, ctrl.transform);
            string displayedName = string.IsNullOrEmpty(entry.displayName) ? defaultName : entry.displayName;
            string newName = EditorGUILayout.DelayedTextField(displayedName, GUILayout.Width(200));
            if (newName != displayedName)
            {
                Undo.RecordObject(ctrl.Clip, "Rename Timeline Entry");
                entry.displayName = (newName == defaultName) ? string.Empty : newName;
                EditorUtility.SetDirty(ctrl.Clip);
            }

            if (!string.IsNullOrEmpty(entry.displayName) && GUILayout.Button(new GUIContent("↺", "Reset name to default"), EditorStyles.miniButton, GUILayout.Width(20)))
            {
                Undo.RecordObject(ctrl.Clip, "Reset Timeline Entry Name");
                entry.displayName = string.Empty;
                EditorUtility.SetDirty(ctrl.Clip);
            }

            GUILayout.EndHorizontal();

            var entryTrack = _cachedTracks.Find(t => t.Contains(entry));
            if (entryTrack != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Color", GUILayout.Width(40));
                Color newTrackColor = EditorGUILayout.ColorField(entryTrack[0].trackColor, GUILayout.Width(200));
                if (newTrackColor != entryTrack[0].trackColor)
                {
                    Undo.RecordObject(ctrl.Clip, "Change Track Color");
                    foreach (var te in entryTrack) te.trackColor = newTrackColor;
                    EditorUtility.SetDirty(ctrl.Clip);
                }

                GUILayout.EndHorizontal();
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            EditorGUI.BeginDisabledGroup(isMissing);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Delay", GUILayout.Width(38));
            float newDelay = EditorGUILayout.DelayedFloatField(entry.delay, GUILayout.Width(52));
            if (newDelay != entry.delay) entry.delay = SnapValue(Mathf.Max(0f, newDelay));
            GUILayout.Space(8);
            GUILayout.Label("Duration", GUILayout.Width(55));
            float newDur = EditorGUILayout.DelayedFloatField(entry.duration, GUILayout.Width(52));
            if (newDur != entry.duration) entry.duration = SnapValue(Mathf.Max(0.01f, newDur));
            if (Mathf.Abs(entry.speed - 1f) > 0.001f)
                GUILayout.Label($"= {entry.EffectiveDuration:F2}s", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(position.width * 0.5f));
            entry.useCustomCurve = EditorGUILayout.Toggle("Custom Curve", entry.useCustomCurve);
            if (entry.useCustomCurve)
            {
                if (entry.customEaseCurve == null)
                    entry.customEaseCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
                entry.customEaseCurve = EditorGUILayout.CurveField("Ease Curve",
                    entry.customEaseCurve, Color.cyan, new Rect(0, 0, 1f, 1f));
            }
            else
            {
                entry.ease = (Ease)EditorGUILayout.EnumPopup("Ease", entry.ease);
            }
            entry.loopType = (LoopType)EditorGUILayout.EnumPopup("Loop Type", entry.loopType);
            entry.loops = EditorGUILayout.IntField("Loops", entry.loops);
            entry.speed = Mathf.Max(0.001f, EditorGUILayout.FloatField("Speed", entry.speed));
            entry.useCurrentAsStart = EditorGUILayout.Toggle("Use Current As Start", entry.useCurrentAsStart);

            var desc = entry.binding != null
                ? PropertyAccessorRegistry.GetDescriptor(entry.binding.componentTypeName, entry.binding.propertyName)
                : null;

            GUILayout.EndVertical();

            GUILayout.BeginVertical();
            EditorGUI.BeginDisabledGroup(entry.useCurrentAsStart);
            DrawStartValueField(entry, ctrl, desc);
            EditorGUI.EndDisabledGroup();
            GUILayout.BeginHorizontal();
            DrawCaptureButton(ctrl, entry, isStart: false);
            GUILayout.Label("End", GUILayout.Width(35));
            DrawValueFieldNoLabel(ref entry.endValue, desc);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            EditorGUI.EndDisabledGroup();

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(ctrl.Clip, "Edit Timeline Entry");
                EditorUtility.SetDirty(ctrl.Clip);
            }

            GUILayout.EndVertical();
        }

#if USING_SPINE
        private void DrawSpineEntryInspector(TimelineEntryData entry, LitTimelineController ctrl)
        {
            if (entry.spine == null) entry.spine = new SpineLayerData();

            GUILayout.FlexibleSpace();
            GUILayout.BeginVertical(EditorStyles.helpBox);

            GUILayout.Space(5);

            bool isMissing = _missingEntryIds.Contains(entry.entryId);
            if (isMissing)
            {
                var prevColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f, 1f);
                GUILayout.BeginHorizontal(EditorStyles.helpBox);
                GUI.backgroundColor = prevColor;
                GUILayout.Label(EditorGUIUtility.IconContent("console.warnicon.sml"), GUILayout.Width(20));
                GUILayout.Label(
                    $"Missing: SkeletonAnimation at \"{entry.binding?.hierarchyPath}\" not found.",
                    EditorStyles.wordWrappedMiniLabel);
                GUILayout.EndHorizontal();
            }

            // Name + Animation dropdown (Animation replaces the tween's Color picker).
            GUILayout.BeginHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(40));
            string defaultName = BlockLabel(entry, ctrl.transform);
            string displayedName = string.IsNullOrEmpty(entry.displayName) ? defaultName : entry.displayName;
            string newName = EditorGUILayout.DelayedTextField(displayedName, GUILayout.Width(200));
            if (newName != displayedName)
            {
                Undo.RecordObject(ctrl.Clip, "Rename Spine Layer");
                entry.displayName = (newName == defaultName) ? string.Empty : newName;
                EditorUtility.SetDirty(ctrl.Clip);
            }
            if (!string.IsNullOrEmpty(entry.displayName) && GUILayout.Button(new GUIContent("↺", "Reset name to default"), EditorStyles.miniButton, GUILayout.Width(20)))
            {
                Undo.RecordObject(ctrl.Clip, "Reset Spine Layer Name");
                entry.displayName = string.Empty;
                EditorUtility.SetDirty(ctrl.Clip);
            }
            GUILayout.EndHorizontal();

            // Animation dropdown sourced from the SkeletonAnimation's data.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Animation", GUILayout.Width(70));

            var sa = SpineEditorSupport.ResolveSkeleton(ctrl, entry.binding);
            var anims = SpineEditorSupport.GetAnimations(sa);

            EditorGUI.BeginDisabledGroup(isMissing || anims.Count == 0);
            int currentIdx = -1;
            string[] names = new string[anims.Count];
            for (int i = 0; i < anims.Count; i++)
            {
                names[i] = anims[i].Name;
                if (anims[i].Name == entry.spine.animationName) currentIdx = i;
            }

            int displayIdx = currentIdx >= 0 ? currentIdx : 0;
            int newIdx = anims.Count == 0
                ? -1
                : EditorGUILayout.Popup(displayIdx, names, GUILayout.Width(180));

            if (anims.Count == 0)
            {
                GUILayout.Label(sa == null ? "(no SkeletonAnimation)" : "(no animations)", EditorStyles.miniLabel);
            }
            else if (newIdx != currentIdx && newIdx >= 0 && newIdx < anims.Count)
            {
                Undo.RecordObject(ctrl.Clip, "Change Spine Animation");
                entry.spine.animationName = anims[newIdx].Name;
                // Changing animation rebases the natural length and the on-timeline
                // length follows (duration = animationLength; effective uses speed).
                entry.spine.animationLength = Mathf.Max(0.0001f, anims[newIdx].Duration);
                entry.duration = entry.spine.animationLength;
                EditorUtility.SetDirty(ctrl.Clip);
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            EditorGUI.BeginDisabledGroup(isMissing);

            // Delay row + read-only effective duration display.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Delay", GUILayout.Width(38));
            float newDelay = EditorGUILayout.DelayedFloatField(entry.delay, GUILayout.Width(52));
            if (newDelay != entry.delay) entry.delay = SnapValue(Mathf.Max(0f, newDelay));
            GUILayout.Space(8);
            GUILayout.Label("Duration", GUILayout.Width(55));
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.FloatField(entry.EffectiveDuration, GUILayout.Width(52));
            EditorGUI.EndDisabledGroup();
            GUILayout.Label($"(= {entry.spine.animationLength:F2}s / {Mathf.Max(0.001f, entry.speed):F2}x)", EditorStyles.miniLabel);
            GUILayout.EndHorizontal();

            // Loop Type | Loops | Speed | Bridge toggle.
            GUILayout.BeginVertical(GUILayout.Width(position.width * 0.5f));
            entry.spine.loopMode = (SpineLoopMode)EditorGUILayout.EnumPopup("Loop Type", entry.spine.loopMode);
            entry.loops = Mathf.Max(1, EditorGUILayout.IntField("Loops", entry.loops));

            float newSpeed = Mathf.Max(0.001f, EditorGUILayout.FloatField("Speed", entry.speed));
            if (!Mathf.Approximately(newSpeed, entry.speed))
            {
                entry.speed = newSpeed;
                // Keep duration locked to animationLength; on-timeline length follows speed.
                entry.duration = Mathf.Max(0.0001f, entry.spine.animationLength);
            }

            GUILayout.EndVertical();

            EditorGUI.EndDisabledGroup();

            GUILayout.Space(4);
            entry.spine.bridgeSpineEvents = EditorGUILayout.ToggleLeft(
                new GUIContent("Bridge Spine Events",
                    "Call LitTimelineController.OnSpineEvent for each Spine event encountered while this layer plays."),
                entry.spine.bridgeSpineEvents);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(ctrl.Clip, "Edit Spine Layer");
                EditorUtility.SetDirty(ctrl.Clip);
            }

            GUILayout.EndVertical();
        }
#endif

        private static string TrackLabel(TimelineEntryData entry, Transform root) =>
            string.IsNullOrEmpty(entry.displayName)
                ? ObjectName(entry.binding, root)
                : entry.displayName;

        private static string BlockLabel(TimelineEntryData entry, Transform root)
        {
            if (!string.IsNullOrEmpty(entry.displayName)) return entry.displayName;
            if (entry.binding == null) return "?";
            string objName = ObjectName(entry.binding, root);

            if (entry.layerType == LayerType.Spine)
                return $"{objName} - Spine Animation";

            var blockDesc = PropertyAccessorRegistry.GetDescriptor(entry.binding.componentTypeName, entry.binding.propertyName);
            string propDisplay = blockDesc?.DisplayName ?? entry.binding.propertyName;

            return $"{objName} - {propDisplay}";
        }

        private static string ObjectName(PropertyBinding binding, Transform root)
        {
            if (binding == null) return "?";
            if (string.IsNullOrEmpty(binding.hierarchyPath)) return root.name;
            int slash = binding.hierarchyPath.LastIndexOf('/');
            return slash >= 0 ? binding.hierarchyPath.Substring(slash + 1) : binding.hierarchyPath;
        }

        private void DrawStartValueField(TimelineEntryData entry, LitTimelineController ctrl, PropertyDescriptor desc = null)
        {
            if (!string.IsNullOrEmpty(entry.linkedStartEntryId))
            {
                var linked = ctrl.Sequence?.entries.Find(e => e.entryId == entry.linkedStartEntryId);
                string linkLabel = linked != null ? TrackLabel(linked, ctrl.transform) : "Missing Link";

                GUILayout.BeginHorizontal();
                DrawCaptureButton(ctrl, entry, isStart: true);
                GUILayout.Label("Start", GUILayout.Width(35));
                GUILayout.Label($"→ {linkLabel}", EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("×", "Unlink start value"), EditorStyles.miniButton, GUILayout.Width(20)))
                {
                    Undo.RecordObject(ctrl.Clip, "Unlink Start Value");
                    entry.linkedStartEntryId = null;
                    EditorUtility.SetDirty(ctrl.Clip);
                }

                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.BeginHorizontal();
                DrawCaptureButton(ctrl, entry, isStart: true);
                GUILayout.Label("Start", GUILayout.Width(35));
                DrawValueFieldNoLabel(ref entry.startValue, desc);
                if (GUILayout.Button(new GUIContent("🔗", "Link start value to another entry's end value"), EditorStyles.miniButton, GUILayout.Width(24)))
                    ShowLinkMenu(ctrl, entry);
                GUILayout.EndHorizontal();
            }
        }

        private void DrawCaptureButton(LitTimelineController ctrl, TimelineEntryData entry, bool isStart)
        {
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = new Color(0.85f, 0.18f, 0.18f, 1f);
            if (GUILayout.Button(new GUIContent("●", isStart ? "Capture current value as Start" : "Capture current value as End"), EditorStyles.miniButton, GUILayout.Width(22)))
                CaptureValue(ctrl, entry, isStart);
            GUI.backgroundColor = prev;
        }

        private static void DrawValueFieldNoLabel(ref PropertyValueUnion value, PropertyDescriptor desc = null)
        {
            switch (value.type)
            {
                case PropertyType.Float:
                    value.floatValue = EditorGUILayout.FloatField(value.floatValue); break;
                case PropertyType.Vector2:
                    value.vector2Value = EditorGUILayout.Vector2Field(GUIContent.none, value.vector2Value); break;
                case PropertyType.Vector3:
                    value.vector3Value = EditorGUILayout.Vector3Field(GUIContent.none, value.vector3Value); break;
                case PropertyType.Color:
                    value.colorValue = EditorGUILayout.ColorField(GUIContent.none, value.colorValue); break;
            }
        }

        private void ShowLinkMenu(LitTimelineController ctrl, TimelineEntryData entry)
        {
            var menu = new GenericMenu();
            foreach (var other in ctrl.Sequence.entries)
            {
                if (other.entryId == entry.entryId) continue;
                if (other.endValue.type != entry.startValue.type) continue;
                var cap = other;
                string label = TrackLabel(other, ctrl.transform);
                menu.AddItem(new GUIContent(label), false, () =>
                {
                    Undo.RecordObject(ctrl.Clip, "Link Start Value");
                    entry.linkedStartEntryId = cap.entryId;
                    EditorUtility.SetDirty(ctrl.Clip);
                    Repaint();
                });
            }

            if (menu.GetItemCount() == 0)
                menu.AddDisabledItem(new GUIContent("No compatible entries"));
            menu.ShowAsContext();
        }

        private void DrawValueField(string label, ref PropertyValueUnion value)
        {
            switch (value.type)
            {
                case PropertyType.Float:
                    value.floatValue = EditorGUILayout.FloatField(label, value.floatValue); break;
                case PropertyType.Vector2:
                    value.vector2Value = EditorGUILayout.Vector2Field(label, value.vector2Value); break;
                case PropertyType.Vector3:
                    value.vector3Value = EditorGUILayout.Vector3Field(label, value.vector3Value); break;
                case PropertyType.Color:
                    value.colorValue = EditorGUILayout.ColorField(label, value.colorValue); break;
            }
        }

        // ─── Selection helpers ────────────────────────────────────────────────
        private bool IsSelected(TimelineEntryData e) => _selectedEntries.Contains(e);

        private void SetSelection(TimelineEntryData e)
        {
            _selectedEntries.Clear();
            if (e != null) _selectedEntries.Add(e);
            _state.SelectedEntry = e;
        }

        private void AddToSelection(TimelineEntryData e)
        {
            if (e == null) return;
            _selectedEntries.Add(e);
            _state.SelectedEntry = e;
        }

        private void ToggleSelection(TimelineEntryData e)
        {
            if (e == null) return;
            if (_selectedEntries.Contains(e))
            {
                _selectedEntries.Remove(e);
                _state.SelectedEntry = _selectedEntries.Count > 0 ? _state.SelectedEntry : null;
            }
            else
            {
                _selectedEntries.Add(e);
                _state.SelectedEntry = e;
            }
        }

        private void ClearSelection()
        {
            _selectedEntries.Clear();
            _state.SelectedEntry = null;
        }

        // ─── Actions ───────────────────────────────────────────────────────────
        private TimelineEntryData DuplicateEntry(LitTimelineController ctrl, TimelineSequenceData seq, TimelineEntryData source)
        {
            Undo.RecordObject(ctrl.Clip, "Duplicate Timeline Entry");

            var copy = new TimelineEntryData
            {
                entryId = System.Guid.NewGuid().ToString(),
                trackId = source.trackId,
                displayName = source.displayName,
                trackColor = source.trackColor,
                binding = source.binding == null
                    ? null
                    : new PropertyBinding
                    {
                        hierarchyPath = source.binding.hierarchyPath,
                        componentTypeName = source.binding.componentTypeName,
                        propertyName = source.binding.propertyName,
                        axis = source.binding.axis,
                    },
                startValue = source.startValue,
                endValue = source.endValue,
                useCurrentAsStart = source.useCurrentAsStart,
                linkedStartEntryId = source.linkedStartEntryId,
                delay = source.EndTime,
                duration = source.duration,
                speed = source.speed,
                ease = source.ease,
                useCustomCurve = source.useCustomCurve,
                customEaseCurve = source.customEaseCurve != null
                    ? new AnimationCurve(source.customEaseCurve.keys)
                    : AnimationCurve.Linear(0, 0, 1, 1),
                loopType = source.loopType,
                loops = source.loops,
                isEnabled = source.isEnabled,
                layerType = source.layerType,
                spine = source.spine == null
                    ? new SpineLayerData()
                    : new SpineLayerData
                    {
                        animationName = source.spine.animationName,
                        animationLength = source.spine.animationLength,
                        loopMode = source.spine.loopMode,
                        bridgeSpineEvents = source.spine.bridgeSpineEvents,
                    },
            };

            seq.entries.Add(copy);
            EditorUtility.SetDirty(ctrl.Clip);
            Repaint();
            return copy;
        }

        private void AddEntry(LitTimelineController ctrl, TimelineSequenceData seq, PropertyBinding binding)
        {
            Undo.RecordObject(ctrl.Clip, "Add Timeline Entry");

            var entry = new TimelineEntryData
            {
                binding = binding,
                trackColor = PickTrackColor(seq),
            };

            var entryDesc = PropertyAccessorRegistry.GetDescriptor(binding.componentTypeName, binding.propertyName);
            if (entryDesc != null)
            {
                entry.startValue = PropertyValueUnion.DefaultForType(entryDesc.ValueType);
                entry.endValue = PropertyValueUnion.DefaultForType(entryDesc.ValueType);
            }

            seq.entries.Add(entry);
            CaptureValue(ctrl, entry, isStart: true);
            _state.SelectedEntry = entry;
            EditorUtility.SetDirty(ctrl.Clip);
        }

#if USING_SPINE
        private void AddSpineEntry(LitTimelineController ctrl, TimelineSequenceData seq, string hierarchyPath)
        {
            Undo.RecordObject(ctrl.Clip, "Add Spine Layer");

            var binding = new PropertyBinding
            {
                hierarchyPath = hierarchyPath,
                componentTypeName = typeof(SkeletonAnimation).FullName,
                propertyName = SpineSentinelProperty,
                axis = PropertyAxis.None,
            };

            var entry = BuildSpineEntry(ctrl, binding, 0f);
            seq.entries.Add(entry);
            _state.SelectedEntry = entry;
            EditorUtility.SetDirty(ctrl.Clip);
        }

        private void AddChainedSpineEntry(LitTimelineController ctrl, TimelineSequenceData seq,
            List<TimelineEntryData> track, string hierarchyPath)
        {
            float chainDelay = 0f;
            foreach (var e in track)
                if (e.EndTime > chainDelay)
                    chainDelay = e.EndTime;

            var binding = new PropertyBinding
            {
                hierarchyPath = hierarchyPath,
                componentTypeName = typeof(SkeletonAnimation).FullName,
                propertyName = SpineSentinelProperty,
                axis = PropertyAxis.None,
            };

            Undo.RecordObject(ctrl.Clip, "Add Chained Spine Layer");
            var entry = BuildSpineEntry(ctrl, binding, chainDelay);
            entry.trackId = track[0].trackId;
            seq.entries.Add(entry);
            _state.SelectedEntry = entry;
            EditorUtility.SetDirty(ctrl.Clip);
        }

        // Sentinel value stored in PropertyBinding.propertyName for Spine layers.
        // No PropertyAccessor is registered for it, so any code path that incorrectly
        // tries to look up an accessor for a Spine layer harmlessly returns null.
        private const string SpineSentinelProperty = "spine_animation";

        private TimelineEntryData BuildSpineEntry(LitTimelineController ctrl, PropertyBinding binding, float delay)
        {
            var entry = new TimelineEntryData
            {
                binding = binding,
                layerType = LayerType.Spine,
                trackColor = _spineColorUnselected,
                delay = delay,
                speed = 1f,
                loops = 1,
                spine = new SpineLayerData(),
            };

            var sa = SpineEditorSupport.ResolveSkeleton(ctrl, binding);
            var anims = SpineEditorSupport.GetAnimations(sa);
            if (anims.Count > 0)
            {
                entry.spine.animationName = anims[0].Name;
                entry.spine.animationLength = Mathf.Max(0.0001f, anims[0].Duration);
            }
            else
            {
                entry.spine.animationName = string.Empty;
                entry.spine.animationLength = 1f;
            }
            entry.duration = entry.spine.animationLength;
            return entry;
        }
#endif

        private void CaptureValue(LitTimelineController ctrl, TimelineEntryData entry, bool isStart)
        {
            if (entry.binding == null) return;

            var accessor = PropertyAccessorRegistry.Get(entry.binding.componentTypeName, entry.binding.propertyName);
            if (accessor == null) return;

            Transform t = string.IsNullOrEmpty(entry.binding.hierarchyPath)
                ? ctrl.transform
                : ctrl.transform.Find(entry.binding.hierarchyPath);
            if (t == null) return;

            var type = System.Type.GetType(entry.binding.componentTypeName);
            if (type == null)
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType(entry.binding.componentTypeName);
                    if (type != null) break;
                }

            if (type == null) return;

            var component = t.GetComponent(type);
            if (component == null) return;

            Undo.RecordObject(ctrl.Clip, isStart ? "Capture Start" : "Capture End");
            var captured = accessor.ReadValue(component);
            if (isStart) entry.startValue = captured;
            else entry.endValue = captured;
            EditorUtility.SetDirty(ctrl.Clip);
        }

        private static Color PickTrackColor(TimelineSequenceData seq)
        {
            var tracks = GetTrackGroups(seq.entries);
            var used = new System.Collections.Generic.HashSet<Color>();
            foreach (var t in tracks)
                used.Add(t[0].trackColor);
            foreach (var c in _palette)
                if (!used.Contains(c))
                    return c;
            return _palette[tracks.Count % _palette.Length];
        }

        // ─── Asset creation ────────────────────────────────────────────────────
        private static LitTimelineClip CreateNewClip(string baseName)
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Create Timeline Clip", baseName + "_TimelineClip", "asset", "Save Timeline Clip asset");
            if (string.IsNullOrEmpty(path)) return null;

            var clip = CreateInstance<LitTimelineClip>();
            AssetDatabase.CreateAsset(clip, path);
            AssetDatabase.SaveAssets();
            return clip;
        }

        // ─── Styles ────────────────────────────────────────────────────────────
        private static void InitStyles()
        {
            if (_blockStyle != null) return;

            _blockStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                normal = { textColor = Color.white }
            };

            _labelStyle = new GUIStyle(EditorStyles.label) { fontSize = 11 };

            _headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
        }

        private static Color rulerColor => new Color(0.15f, 0.15f, 0.15f, 1f);
    }
}
