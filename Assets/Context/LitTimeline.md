# LitTimeline — Context

Port of the DOTween-based **Tween Animator** package onto **LitMotion 2.0.2**, delivered as a
self-contained in-project plugin. The original package was left untouched.

## Locations

- New plugin: `Assets/Plugins/LitTimeline` (namespace `LitTimeline` / `LitTimeline.Editor`).
- Source package (reference only): `Library/PackageCache/com.heatinteractive.tweenanimator@ac3c240090d5` (namespace `TweenAnimator`, uses `DG.Tweening`).
- LitMotion: `Library/PackageCache/com.annulusgames.lit-motion@2317f5d282a9` (asmdef `LitMotion`, v2.0.2).
- DOTween (original dependency, not used by LitTimeline): `Assets/Plugins/DOTween`.
- Plan file: `c:\Users\Breno\.cursor\plans\littimeline_dotween_to_litmotion_port_2e2d9f95.plan.md` (do not edit).

## Status: COMPLETE

All plan to-dos implemented. Both assemblies compile with **zero errors** (verified in
`%LOCALAPPDATA%/Unity/Editor/Editor.log`: `LitTimeline.Runtime.dll` + `LitTimeline.Editor.dll`
built and copied to `Library/ScriptAssemblies`). Only benign warnings remain (obsolete `ShaderUtil`
APIs — present in the original package too).

> Live scrub/play-mode smoke test could NOT be auto-run: the Unity MCP transport disconnected
> mid-session (`McpManagerClientHub` errors; all MCP calls returned "Response data is null").
> This is environmental, not a code issue. Manual verification step is still pending (see below).

## File map

### Runtime — asmdef `LitTimeline.Runtime` (refs: `LitMotion`, `UnityEngine.UI`, `Unity.TextMeshPro`; no DOTween)

- `Runtime/Data/PropertyValueUnion.cs` — `PropertyType` (Float/Vector2/Vector3/Color), `PropertyAxis`, value union + `FromX`/`DefaultForType` helpers.
- `Runtime/Data/PropertyBinding.cs` — `hierarchyPath` / `componentTypeName` / `propertyName` / `axis` (was `TweenPropertyBinding`).
- `Runtime/Data/EventMarkerData.cs` — marker id/name/time/enabled + `OnTrigger` (internal `InvokeTrigger`).
- `Runtime/Data/TimelineEntryData.cs` — entry data. **Ease/LoopType are LitMotion enums; `rotateMode` removed.** `EffectiveDuration = duration / max(0.001, speed)`, `EndTime = delay + EffectiveDuration`. Events `OnStart`/`OnComplete` (internal invokers).
- `Runtime/Data/TimelineSequenceData.cs` — list of entries + markers, `timeScale`, `playOnAwake`, `autoKillOnComplete`, `TotalDuration` (single duration, ignores loops — matches original).
- `Runtime/Data/LitTimelineClip.cs` — `ScriptableObject`, `CreateAssetMenu("LitTimeline/Timeline Clip")`.
- `Runtime/Playback/PropertyAccessor.cs` — abstract `BuildMotion(component, entry, from)` + concrete Vector3 / Float / Vector2 / Rotation(euler) / Color / MaterialFloat / MaterialColor accessors. `ApplyValue`/`ReadValue` unchanged from original.
- `Runtime/Playback/PropertyAccessorRegistry.cs` — registers supported component properties; `ExtraParamType` reduced to `{ None }` (RotateMode removed). Material props via `mat_float:`/`mat_color:` prefixes.
- `Runtime/Playback/LitSequenceBuilder.cs` — `Build(controller, data)` → `LSequence.Create()`, `Insert(entry.delay, handle)` per enabled entry, `Run()` returns root `MotionHandle`. Resolves component + bakes `from` value (current/linked/explicit).
- `Runtime/Components/LitTimelineController.cs` — `MonoBehaviour`. See engine model below.

### Editor — asmdef `LitTimeline.Editor` (refs: `LitTimeline.Runtime`, `LitMotion`, `LitMotion.Editor`, `UnityEngine.UI`, `Unity.TextMeshPro`; Editor platform only, `autoReferenced:false`)

- `Editor/Window/LitTimelineWindowState.cs` — edit-mode preview/scrub via **manual interpolation**; easing uses `LitMotion.EaseUtility.Evaluate(localT, ease)`. Snapshot/restore, two-pass apply (pre-start vs active/complete), `useCurrentAsStart` caching.
- `Editor/Window/LitTimelineWindow.cs` — full timeline UI (tracks, blocks, drag/resize, zoom/pan, ruler, playhead, markers, multi-select, inspector). Ease/LoopType popups use LitMotion enums; RotateMode field removed. Menu `Tools/Lit Timeline`.
- `Editor/Drawers/LitTimelineControllerInspector.cs` — clip field, create-clip, sequence settings, runtime Play/Pause/Stop/Rewind buttons, event property fields. Menu button "Open Lit Timeline".
- `Editor/Drawers/LitTimelineClipEditor.cs` — AnimationClip export settings (samples/sec, key reduction, tolerance, name) persisted via EditorPrefs (`LitTimeline.Convert.*`).
- `Editor/Pickers/ComponentPropertyScanner.cs` — `DiscoveredProperty` + recursive scan (incl. material color props).
- `Editor/Pickers/PropertyPickerWindow.cs` — two-column object/property picker (ported; window uses GenericMenu instead, but kept for API parity).
- `Editor/Converters/TimelineClipToAnimationClipConverter.cs` — bakes clip → `AnimationClip`. Easing via `EaseUtility.Evaluate`; `LoopBounds`/`FinalValue` handle Yoyo+Flip (reverse on odd), Incremental, Restart. Menu `Assets/LitTimeline/Convert to AnimationClip`.

## Controller engine model (key design)

LitMotion has no Pause and no reverse playback, so the controller does NOT let LitMotion auto-tick:

1. `LitSequenceBuilder.Build(...)` returns the root sequence `MotionHandle`.
2. Immediately `handle.PlaybackSpeed = 0` → LitMotion's player loop never advances it.
3. `Update()` advances a private `_time` (`+= deltaTime * TimeScale * _direction`) and sets
   `_seqHandle.Time = _time`. Setting the root's `Time` drives all child motions
   (`MotionSequenceSource.Time` calls `MotionManager.SetTime` on each child). This is the same
   mechanism LitMotion uses internally, so scrubbing is reliable.
4. Pause = `_isPlaying = false`; Resume = `_isPlaying = true`; Stop = `handle.Cancel()`.
5. `PlayBackward*` sets `_direction = -1` and drives `_time` downward from the end.
6. **Callback poller** (`PollCallbacks`): compares previous vs current `_time`, fires on forward
   crossings — entry `OnStart` (at `delay`), per-iteration `OnLoop(i)` (for looped entries),
   entry `OnComplete` (at `delay + EffectiveDuration*loops`, only if `loops >= 0`), marker
   `OnTrigger` (at `marker.time`). Replaces DOTween `InsertCallback`.

Public API preserved: `Play/Play(clip)/PlayAsync/PlayFromTime/PlayFromNormalizedTime/PlayBackward(+FromTime/+FromNormalizedTime)/Pause/Resume/Stop/Rewind/GotoTime/GotoNormalizedTime/SetClip`,
properties `IsPlaying/IsPaused/IsComplete/Duration/CurrentTime/NormalizedTime/TimeScale`, name
indexer `this[string]`, `GetEntry/RebuildEntryCache`, `GetMarker/AddMarker/RemoveMarker`, UnityEvents
`OnPlayEvent/OnPauseEvent/OnStopEvent/OnCompleteEvent/OnLoopEvent`, C# events
`OnPlay/OnPause/OnStop/OnComplete/OnLoop`. `[InternalsVisibleTo("LitTimeline.Editor")]` set.

## DOTween → LitMotion mapping applied

- `DOTween.To` → `LMotion.Create(from, to, dur).Bind(state, (v, s) => ...)`. **Important:** LitMotion
  `Bind(state, action)` invokes `action(value, state)` — the setter arg order is (value, state),
  opposite to the registry's `(component, value)` setters; accessors wrap with `(v, c) => setter(c, v)`.
- `.SetEase` → `.WithEase(Ease)` or `.WithEase(AnimationCurve)` (custom curve, endpoint tangents clamped ≥0 via `FixEndpointTangents`).
- `.SetLoops` → `.WithLoops(loops, LoopType)`.
- `DOTween.Sequence()/Insert` → `LSequence.Create()/Insert(position, handle)/Run()`.
- `seq.timeScale`→ controller `_time` stepping; `seq.Goto`→ `handle.Time`; `Kill`→ `Cancel`; `Complete`→ `Complete`.
- Editor easing `DOVirtual.EasedValue` and converter `EaseManager.Evaluate` → `EaseUtility.Evaluate`.
- Enums: `DG.Tweening.Ease`→`LitMotion.Ease`; `DG.Tweening.LoopType`→`LitMotion.LoopType` (Restart=0, Flip=1, Incremental=2, Yoyo=3).

## Behavioral deltas (intentional)

- Rotation animates as euler Vector3 only (DOTween `RotateMode` dropped).
- Pause / backward playback are emulated (`PlaybackSpeed=0` + manual `Time` stepping).
- Entry/marker callbacks fire via per-frame polling on forward crossings (forward-playback parity).
- Infinite-loop entries (`loops < 0`) are clamped to one iteration inside a sequence to avoid
  infinite sequence duration (`LoopsForSequence`).
- `TotalDuration`/`Duration` ignore per-entry loops (matches original); looped children may be
  clamped at the sequence end.

## Pending manual verification (MCP was offline)

In the Unity Editor:
1. Add `LitTimelineController` to a GameObject, create/assign a `LitTimelineClip`.
2. Open `Tools ▸ Lit Timeline`; add a property track, set start/end, enable Preview, scrub & play.
3. Enter Play Mode and confirm `Play()` runs, callbacks/markers fire, Pause/Stop/PlayBackward work.
4. Test `Assets ▸ LitTimeline ▸ Convert to AnimationClip` on a clip asset.

## Useful MCP tools for follow-up (server: project-0-TweenMotionTest-ai-game-developer)

`assets-refresh` (use `ForceSynchronousImport`), `console-get-logs` (filter Error/Exception),
`script-execute` (Roslyn smoke tests). Tool schemas live under
`C:\Users\Breno\.cursor\projects\...\mcps\project-0-TweenMotionTest-ai-game-developer\tools\`.
Note: no `editor-application-*` tool is enabled in this project.
