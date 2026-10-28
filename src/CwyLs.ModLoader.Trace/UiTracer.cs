using System;
using System.Collections.Generic;
using System.Globalization;
using CwyLs.ModLoader.Core.Trace;
using CwyLs.ModLoader.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>
    /// The <c>sys:"ui"</c> layer: user-interface events read from Unity's own controls rather than
    /// from game class names.
    /// </summary>
    /// <remarks>
    /// Game classes tell you that <c>SettingController.Apply</c> ran; they do not tell you that the
    /// user pressed the button labelled "Settings". Patching UGUI and TextMeshPro directly covers
    /// every control in the game, including ones whose class names no filter would ever match.
    /// <para>
    /// Not every control movement is a user doing something: in the 95-second run,
    /// <c>UI_FacilityMusic/ProgressBar/Slider</c> alone wrote 4139 of the UI lines because the
    /// music player drives it every frame. So value changes carry <c>src</c> — user or code,
    /// decided by whether a pointer or submit callback fired on that same control recently — and
    /// everything except the unambiguous user actions goes through the same adaptive downsample as
    /// a polling game method.
    /// </para>
    /// Each target was verified in the game's own Managed assemblies (UnityEngine.UI.dll,
    /// Unity.TextMeshPro.dll): <c>Button.Press()</c> private, <c>Toggle.Set(bool,bool)</c> private,
    /// <c>Slider.Set(float,bool)</c> protected, <c>Slider.OnPointerDown/OnDrag(PointerEventData)</c>
    /// public, <c>Toggle.OnPointerClick(PointerEventData)</c>/<c>OnSubmit(BaseEventData)</c> public,
    /// <c>TMP_InputField.SendOnValueChanged()</c> private, <c>SendOnSubmit()</c>/<c>SendOnEndEdit()</c>
    /// protected, <c>ActivateInputField()</c>/<c>DeactivateInputField(bool)</c> public,
    /// <c>TMP_Dropdown.OnSelectItem(Toggle)</c> private,
    /// <c>EventSystem.SetSelectedGameObject(GameObject, BaseEventData)</c> public.
    /// <para>
    /// <b>Our own controls are not traced.</b> Every control whose hierarchy root is one of the
    /// framework's (<see cref="UiOwnership"/>: a root name starting with <c>CWY.</c>, such as the
    /// game-style overlay canvas <c>CWY.GameStyleCanvas</c>) is skipped by every hook here, focus
    /// moving onto or off such a control is skipped, and a click that lands on it writes no
    /// <c>CLICK</c> line (<see cref="IsPointerOverOwnUi"/>). Typing a console command is the
    /// user talking to the framework, not to the game; before this, the Esc that closes the
    /// console wrote a stray <c>TYPED ""</c>.
    /// </para>
    /// </remarks>
    internal static class UiTracer
    {
        /// <summary>A slider moved within this long of a pointer-down on it is the user dragging.</summary>
        private const double SliderIntentSeconds = 2d;

        /// <summary>A toggle set within this long of a click or submit on it is the user.</summary>
        private const double ToggleIntentSeconds = 1d;

        /// <summary>Drag lines written per second per control.</summary>
        private const int DragPerSecond = 10;

        /// <summary>Hierarchy paths cached. UGUI reparents controls rarely; walking is the cost here.</summary>
        private const int MaxCachedPaths = 512;

        /// <summary>
        /// Events that are user intent by their nature, so they bypass the downsample entirely:
        /// a button press or a submit is never something the game does to itself.
        /// </summary>
        private static readonly HashSet<string> Unthrottled = new HashSet<string>(StringComparer.Ordinal)
        {
            "Button/press",
            "Toggle/valueChanged",
            "TMP_InputField/submit",
            "TMP_InputField/endEdit",
            "TMP_Dropdown/select",
            "EventSystem/focus",
        };

        private static readonly InteractionSource Intent = new InteractionSource();
        private static readonly object PathGate = new object();
        private static readonly Dictionary<int, ControlInfo?> PathCache = new Dictionary<int, ControlInfo?>();

        /// <summary>Reused by <see cref="IsPointerOverOwnUi"/>; only ever touched on the main thread.</summary>
        private static readonly List<RaycastResult> Hits = new List<RaycastResult>();

        /// <summary>What the tracer needs to know about one control: where it is, and whose it is.</summary>
        private sealed class ControlInfo
        {
            internal ControlInfo(string path, bool own)
            {
                Path = path;
                Own = own;
            }

            /// <summary>The hierarchy path, root first (<see cref="UiPath"/>).</summary>
            internal string Path { get; }

            /// <summary>True when the control belongs to the framework (<see cref="UiOwnership"/>).</summary>
            internal bool Own { get; }
        }

        private static TraceConfig? _config;
        private static RateLimiter? _throttle;
        private static RateLimiter? _drag;

        /// <summary>Patches the Unity control surface. Each target is optional: a missing one is a warning.</summary>
        /// <returns>Number of controls successfully patched.</returns>
        internal static int Install(Harmony harmony, TraceConfig config)
        {
            _config = config;
            _throttle = new RateLimiter(config.RateLimitPerMethodPerSecond, null, config.PollingCapPerSecond);
            _drag = new RateLimiter(DragPerSecond);

            var patched = 0;
            patched += Patch(harmony, typeof(Button), "Press", nameof(OnButtonPress), Type.EmptyTypes);

            patched += Patch(harmony, typeof(Toggle), "Set", nameof(OnToggleSet), new[] { typeof(bool), typeof(bool) });
            patched += Patch(harmony, typeof(Toggle), "OnPointerClick", nameof(OnTogglePointerClick), new[] { typeof(PointerEventData) });
            patched += Patch(harmony, typeof(Toggle), "OnSubmit", nameof(OnToggleSubmit), new[] { typeof(BaseEventData) });

            patched += Patch(harmony, typeof(Slider), "Set", nameof(OnSliderSet), new[] { typeof(float), typeof(bool) });
            patched += Patch(harmony, typeof(Slider), "OnPointerDown", nameof(OnSliderPointerDown), new[] { typeof(PointerEventData) });
            patched += Patch(harmony, typeof(Slider), "OnDrag", nameof(OnSliderDrag), new[] { typeof(PointerEventData) });

            patched += Patch(harmony, typeof(TMP_InputField), "SendOnValueChanged", nameof(OnInputValueChanged), Type.EmptyTypes);
            patched += Patch(harmony, typeof(TMP_InputField), "SendOnSubmit", nameof(OnInputSubmit), Type.EmptyTypes);
            patched += Patch(harmony, typeof(TMP_InputField), "SendOnEndEdit", nameof(OnInputEndEdit), Type.EmptyTypes);
            patched += Patch(harmony, typeof(TMP_InputField), "ActivateInputField", nameof(OnInputActivate), Type.EmptyTypes);
            patched += Patch(harmony, typeof(TMP_InputField), "DeactivateInputField", nameof(OnInputDeactivate), new[] { typeof(bool) });

            patched += Patch(harmony, typeof(TMP_Dropdown), "OnSelectItem", nameof(OnDropdownSelect), new[] { typeof(Toggle) });
            patched += Patch(harmony, typeof(EventSystem), "SetSelectedGameObject", nameof(OnFocusChanged), new[] { typeof(GameObject), typeof(BaseEventData) });

            ConsoleMirror.Print(TraceConsoleLines.Status(
                TraceRuntime.Now,
                $"ui layer patched {patched} Unity controls; controls under {UiOwnership.FrameworkRootPrefix}* roots are not traced"));
            return patched;
        }

        private static int Patch(Harmony harmony, Type type, string methodName, string prefixName, Type[] parameters)
        {
            var target = AccessTools.Method(type, methodName, parameters);
            if (target is null)
            {
                CwyLsModLoader.Log.LogWarning(
                    $"TRACE: ui target {type.Name}.{methodName} is gone in this Unity version; that event will not be traced");
                return 0;
            }

            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(UiTracer), prefixName)));
                return 1;
            }
            catch (Exception ex)
            {
                CwyLsModLoader.Log.LogWarning($"TRACE: could not patch {type.Name}.{methodName}: {ex.Message}");
                return 0;
            }
        }

        // ----- prefixes ------------------------------------------------------------------------

        private static void OnButtonPress(Button __instance) => Emit(__instance, "Button", "press", null, 0d);

        private static void OnToggleSet(Toggle __instance, bool value) =>
            Emit(__instance, "Toggle", "valueChanged", value ? "true" : "false", ToggleIntentSeconds);

        private static void OnTogglePointerClick(Toggle __instance) => NoteIntent(__instance);

        private static void OnToggleSubmit(Toggle __instance) => NoteIntent(__instance);

        private static void OnSliderSet(Slider __instance, float input) =>
            Emit(__instance, "Slider", "valueChanged", Number(input), SliderIntentSeconds);

        private static void OnSliderPointerDown(Slider __instance) => NoteIntent(__instance);

        private static void OnSliderDrag(Slider __instance)
        {
            NoteIntent(__instance);
            Emit(__instance, "Slider", "drag", __instance == null ? null : Number(__instance.value), 0d, InteractionSource.User);
        }

        private static void OnInputValueChanged(TMP_InputField __instance) =>
            Emit(__instance, "TMP_InputField", "valueChanged", Text(__instance), 0d);

        private static void OnInputSubmit(TMP_InputField __instance) =>
            Emit(__instance, "TMP_InputField", "submit", Text(__instance), 0d);

        private static void OnInputEndEdit(TMP_InputField __instance) =>
            Emit(__instance, "TMP_InputField", "endEdit", Text(__instance), 0d);

        private static void OnInputActivate(TMP_InputField __instance) =>
            Emit(__instance, "TMP_InputField", "activate", null, 0d);

        private static void OnInputDeactivate(TMP_InputField __instance) =>
            Emit(__instance, "TMP_InputField", "deactivate", Text(__instance), 0d);

        private static void OnDropdownSelect(TMP_Dropdown __instance) =>
            Emit(__instance, "TMP_Dropdown", "select", Label(__instance == null ? null : __instance.captionText), 0d);

        private static void OnFocusChanged(EventSystem __instance, GameObject selected)
        {
            // Focus moving onto one of our controls, or off one of ours to nothing, is ours too.
            if (IsOwn(selected)
                || (selected == null && __instance != null && IsOwn(__instance.currentSelectedGameObject)))
            {
                return;
            }

            Write("EventSystem", "focus", selected == null ? null : PathOf(selected.transform), null, null, source: null);
        }

        // ----- routing -------------------------------------------------------------------------

        /// <summary>Records that the user just touched this control, for the <c>src</c> decision.</summary>
        private static void NoteIntent(Component? control)
        {
            if (control == null)
            {
                return;
            }

            var info = Describe(control.transform);
            if (info != null && info.Own)
            {
                return;
            }

            Intent.NoteUserIntent(info?.Path);
        }

        /// <summary>
        /// Builds and routes one control event. The path is resolved first because it is the
        /// throttle key, and the label is only read once the line is definitely going to be
        /// written: a code-driven slider must not pay for a component search sixty times a second.
        /// </summary>
        private static void Emit(
            Component? control,
            string controlType,
            string eventName,
            string? value,
            double intentWindowSeconds,
            string? forcedSource = null)
        {
            if (control == null)
            {
                return;
            }

            // The path is also the throttle key; the ownership bit rides in the same cache entry.
            var info = Describe(control.transform);
            if (info != null && info.Own)
            {
                return;
            }

            var path = info?.Path;
            var source = forcedSource ?? (intentWindowSeconds > 0d ? Intent.Classify(path, intentWindowSeconds) : null);

            Write(controlType, eventName, path, control, value, source);
        }

        private static void Write(
            string controlType,
            string eventName,
            string? path,
            object? labelSource,
            string? value,
            string? source)
        {
            var kind = controlType + "/" + eventName;
            int? aggregate = null;

            if (!Unthrottled.Contains(kind))
            {
                var key = kind + "|" + (path ?? "?");

                if (string.Equals(kind, "Slider/drag", StringComparison.Ordinal))
                {
                    // A real drag is user intent, but sixty samples a second is still sixty; a flat
                    // cap keeps the shape of the gesture without the volume.
                    if (_drag != null && !_drag.TryAcquire(key, out _))
                    {
                        return;
                    }
                }
                else if (_throttle != null)
                {
                    var verdict = _throttle.Observe(key);

                    if (verdict.EnteredPolling)
                    {
                        // A polling notice is trace-file business.
                        CwyLsModLoader.Log.LogDebug(
                            $"TRACE: ui {key} is code-driven (~{verdict.ObservedPerSecond.ToString(CultureInfo.InvariantCulture)}/s), aggregating");
                    }

                    if (verdict.Action == RateAction.Suppress)
                    {
                        if (verdict.RemembersPayload)
                        {
                            _throttle.Remember(key, value);
                        }

                        return;
                    }

                    if (verdict.Action == RateAction.Aggregate)
                    {
                        aggregate = verdict.AggregateCount;

                        // The line stands for the window that just closed, so it shows that
                        // window's last value; this call's value opens the next one.
                        var closing = verdict.AggregatePayload as string;
                        if (verdict.RemembersPayload)
                        {
                            _throttle.Remember(key, value);
                        }

                        if (closing != null)
                        {
                            value = closing;
                        }
                    }
                }
            }

            var label = labelSource is Component component ? LabelOf(component) : labelSource as string;
            TraceRuntime.WriteUi(controlType, eventName, path, label, value, source, aggregate, ShouldMirror(kind, source));
        }

        /// <summary>
        /// What reaches the console: the four unambiguous user actions. Sliders, per-keystroke input
        /// and focus changes stay in the file, where they can be read without drowning everything else.
        /// </summary>
        private static bool ShouldMirror(string kind, string? source)
        {
            switch (kind)
            {
                case "Button/press":
                case "TMP_InputField/submit":
                case "TMP_InputField/endEdit":
                case "TMP_Dropdown/select":
                    return true;

                case "Toggle/valueChanged":
                    return string.Equals(source, InteractionSource.User, StringComparison.Ordinal);

                default:
                    return false;
            }
        }

        // ----- helpers -------------------------------------------------------------------------

        private static string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>
        /// What the user typed, subject to <see cref="TraceConfig.LogInputText"/>: text fields in
        /// this game hold notes and todos, so the content is opt-outable.
        /// </summary>
        private static string? Text(TMP_InputField? field)
        {
            if (field == null)
            {
                return null;
            }

            try
            {
                var text = field.text ?? string.Empty;
                return _config?.LogInputText == false
                    ? text.Length.ToString(CultureInfo.InvariantCulture) + " chars"
                    : text;
            }
            catch (Exception ex)
            {
                TraceDiagnostics.Report("TRACE: could not read an input field's text", ex);
                return null;
            }
        }

        /// <summary>The control's hierarchy path, root first; null when it cannot be read.</summary>
        internal static string? PathOf(Transform? leaf) => Describe(leaf)?.Path;

        /// <summary>
        /// True when a pointer at this screen position is over the framework's own UI: the topmost
        /// UI raycast hit (the same ordering the game's <c>InputController</c> reads) belongs to a
        /// framework root. Main thread only; called once per written click.
        /// </summary>
        /// <param name="screenPosition">Pointer position in screen pixels (<c>Input.mousePosition</c>).</param>
        internal static bool IsPointerOverOwnUi(Vector3 screenPosition)
        {
            try
            {
                var system = EventSystem.current;
                if (system == null)
                {
                    return false;
                }

                var pointer = new PointerEventData(system) { position = screenPosition };
                Hits.Clear();
                system.RaycastAll(pointer, Hits);

                var top = Hits.Count > 0 ? Hits[0].gameObject : null;
                Hits.Clear();
                return IsOwn(top);
            }
            catch (Exception ex)
            {
                // Traced rather than dropped: a click we cannot attribute is still the player's.
                TraceDiagnostics.Report("TRACE: could not check whether a click landed on the framework's own UI", ex);
                return false;
            }
        }

        /// <summary>True when the object belongs to the framework (<see cref="UiOwnership"/>).</summary>
        private static bool IsOwn(GameObject? target)
        {
            if (target == null)
            {
                return false;
            }

            var info = Describe(target.transform);
            return info != null && info.Own;
        }

        /// <summary>
        /// Walks up to the scene root once per control and caches what it found: the path from
        /// <see cref="UiPath"/> and whether the root is one of the framework's.
        /// </summary>
        private static ControlInfo? Describe(Transform? leaf)
        {
            if (leaf == null)
            {
                return null;
            }

            int id;
            try
            {
                id = leaf.GetInstanceID();
            }
            catch (Exception ex)
            {
                TraceDiagnostics.Report("TRACE: could not identify a UI transform", ex);
                return null;
            }

            lock (PathGate)
            {
                if (PathCache.TryGetValue(id, out var cached))
                {
                    return cached;
                }
            }

            var info = Build(leaf);

            lock (PathGate)
            {
                if (PathCache.Count >= MaxCachedPaths)
                {
                    // A scene change can retire hundreds of controls at once; clearing wholesale is
                    // cheaper and simpler than tracking ages for something this easy to rebuild.
                    PathCache.Clear();
                }

                PathCache[id] = info;
            }

            return info;
        }

        private static ControlInfo? Build(Transform leaf)
        {
            try
            {
                var names = new List<string?>();
                var current = leaf;
                // Bounded so a malformed hierarchy (or a cycle) cannot hang a UI callback.
                for (var depth = 0; current != null && depth < UiPath.MaxSegments * 4; depth++)
                {
                    names.Add(current.name);
                    current = current.parent;
                }

                // The walk normally ends at the root; a hierarchy deeper than the bound asks Unity.
                var rootName = current == null ? names[names.Count - 1] : leaf.root.name;

                names.Reverse();
                return new ControlInfo(UiPath.Build(names), UiOwnership.IsFrameworkRoot(rootName));
            }
            catch (Exception ex)
            {
                TraceDiagnostics.Report("TRACE: could not build a UI hierarchy path", ex);
                return null;
            }
        }

        /// <summary>The control's visible text, from a TMP_Text or a legacy Text anywhere beneath it.</summary>
        private static string? LabelOf(Component control)
        {
            try
            {
                var tmp = control.GetComponentInChildren<TMP_Text>(includeInactive: true);
                if (tmp != null)
                {
                    return Label(tmp);
                }

                var legacy = control.GetComponentInChildren<Text>(includeInactive: true);
                return legacy == null ? null : Trim(legacy.text);
            }
            catch (Exception ex)
            {
                TraceDiagnostics.Report("TRACE: could not read a UI control's label", ex);
                return null;
            }
        }

        private static string? Label(TMP_Text? text) => text == null ? null : Trim(text.text);

        private static string? Trim(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var max = _config?.MaxStringLength ?? TraceConfig.DefaultMaxStringLength;
            return text!.Length <= max ? text : text.Substring(0, max) + ArgFormatter.Ellipsis;
        }
    }
}
