using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Decides which game methods get a Harmony prefix. Deliberately conservative: the cost of
    /// patching a per-frame method is a frame-rate regression the user would notice, and the cost
    /// of patching a property getter is an unreadable log, so both are excluded by shape rather
    /// than by hoping a rate limit catches them.
    /// </summary>
    public sealed class TraceFilter
    {
        /// <summary>Namespaces whose classes are candidates. Exact match; the empty string is the root namespace.</summary>
        private static readonly HashSet<string> IncludedNamespaces = new HashSet<string>(StringComparer.Ordinal)
        {
            string.Empty,
            "Bulbul",
            "Bulbul.Mobile",
            "Bulbul.Web",
            "NestopiSystem",
            "NestopiSystem.Steam",
            "KanKikuchi.AudioManager",
        };

        /// <summary>
        /// Class-name suffixes that mark a game system. The first block is the original list; the
        /// second is the music surface added later (players, playlists, importers
        /// and the music UI all live in the included namespaces).
        /// </summary>
        private static readonly string[] SystemSuffixes =
        {
            "Service", "Controller", "Manager", "Reader", "Presenter", "Model",
            "Selector", "Loader", "Handler", "System", "Ctrl", "Director", "Scope",

            "Player", "PlayList", "Playlist", "Importer", "UI",

            // Interaction surface (14:17 addendum). Only 16 of roughly 300 classes that handle
            // input reached the trace before this: the rest are Views, Dialogs and Buttons.
            "View", "Dialog", "Button", "Buttons", "Popover", "Toggle", "Switch",
            "Handle", "Forwarder", "Helper", "Facility", "Trigger", "Field",
        };

        /// <summary>Class-name prefixes that mark a game system (FacilityClickHeroine, FacilityCalendar, ...).</summary>
        private static readonly string[] SystemPrefixes = { "Facility" };

        /// <summary>
        /// UnityEngine.EventSystems handler interfaces, by simple name. A class implementing any of
        /// them is by definition something the user touches. Held as strings because Core must not
        /// reference UnityEngine; the plugin matches them against real interface names.
        /// </summary>
        private static readonly string[] InteractionInterfaceNames =
        {
            "IPointerClickHandler", "IPointerDownHandler", "IPointerUpHandler",
            "IPointerEnterHandler", "IPointerExitHandler",
            "IBeginDragHandler", "IDragHandler", "IEndDragHandler",
            "ISubmitHandler", "ISelectHandler", "IDeselectHandler", "IScrollHandler",
        };

        /// <summary>Method-name prefixes that mark an input callback.</summary>
        private static readonly string[] InteractionMethodPrefixes =
        {
            "OnPointer", "OnClick", "OnSubmit", "OnValueChanged", "OnEndEdit",
        };

        /// <summary>Method names that mark an input callback exactly.</summary>
        private static readonly string[] InteractionMethodNames =
        {
            "OnBeginDrag", "OnDrag", "OnEndDrag", "OnSelect", "OnDeselect",
        };

        /// <summary>Classes traced whatever their name ends in.</summary>
        private static readonly HashSet<string> AllowedClasses = new HashSet<string>(StringComparer.Ordinal)
        {
            "HeroineAI",
            "ScenarioReader",
            "StorySystemUI",
            "FontSupplier",

            // Web API: every type in Bulbul.Web is a request/response DTO with no system suffix,
            // so without these two the whole network surface would be invisible.
            "WebApiPulser",
            "WebApiErrorBehavior",

            // Music surface that carries no system suffix (observed in the game).
            "GameAudioInfo",
            "MusicImportButton",
            "MusicImportFailedViewPC",
            "MusicPlayListButtons",
            "MusicPlayListView",
            "MusicPlayingIconViewer",
        };

        /// <summary>Per-frame and object-protocol methods that are never traced.</summary>
        private static readonly HashSet<string> ExcludedMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "Update", "LateUpdate", "FixedUpdate", "OnGUI", "Tick",
            "OnAnimatorIK", "OnAnimatorMove", "OnRenderObject", "OnWillRenderObject",
            "Equals", "GetHashCode", "ToString", "GetType", "Finalize", "MemberwiseClone",

            // A language protocol method, not a game event. It is also the one method Harmony
            // failed to compile in the first in-game run (Bulbul.HabitItemDateUI+ViewModel,
            // whose out parameters defeat argument capture).
            "Deconstruct",
        };

        /// <summary>Method-name prefixes that mark an accessor or an operator.</summary>
        private static readonly string[] ExcludedPrefixes = { "get_", "set_", "add_", "remove_", "op_" };

        /// <summary>
        /// Private methods worth the noise, keyed both as <c>Class.Method</c> and as
        /// <c>Namespace.Class.Method</c> so a name that is ambiguous across namespaces still works.
        /// </summary>
        private static readonly HashSet<string> AllowedPrivateMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "HeroineAI.ChangeState",
            "ScenarioReader.CommandVoiceText",
            "ScenarioReader.CommandPomodoroVoiceText",
            "AudioManager.GetAudioClip",
            "KanKikuchi.AudioManager.AudioManager.GetAudioClip",
        };

        private readonly TraceConfig _config;
        private readonly Regex? _include;
        private readonly Regex? _exclude;

        /// <summary>Creates a filter for one configuration. An unparseable regex is reported and ignored.</summary>
        public TraceFilter(TraceConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _include = Compile(config.IncludeRegex, nameof(config.IncludeRegex));
            _exclude = Compile(config.ExcludeRegex, nameof(config.ExcludeRegex));
        }

        private static Regex? Compile(string? pattern, string which)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return null;
            }

            try
            {
                return new Regex(pattern, RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                // A typo in the user's config must not stop the trace from starting.
                TraceDiagnostics.Report($"TRACE: {which} is not a valid regex and was ignored: {pattern}", ex);
                return null;
            }
        }

        /// <summary>Convenience overload for an ordinary concrete method (the five-value form).</summary>
        public bool ShouldPatch(string? namespaceName, string className, string methodName, bool isPublic, bool isStatic) =>
            ShouldPatch(MethodCandidate.Ordinary(namespaceName, className, methodName, isPublic, isStatic));

        /// <summary>True when this method should be given a trace prefix.</summary>
        public bool ShouldPatch(MethodCandidate candidate)
        {
            if (_config.Level == TraceLevel.Off)
            {
                return false;
            }

            if (string.IsNullOrEmpty(candidate.ClassName) || string.IsNullOrEmpty(candidate.MethodName))
            {
                return false;
            }

            if (!IsPatchableShape(candidate))
            {
                return false;
            }

            if (!IsIncludedNamespace(candidate.NamespaceName))
            {
                return false;
            }

            if (!IsTracedClass(candidate))
            {
                return false;
            }

            if (!IsTracedMethod(candidate))
            {
                return false;
            }

            if (_config.Level == TraceLevel.Events && !CuratedEvents.IsCurated(candidate.ClassName, candidate.MethodName))
            {
                return false;
            }

            return PassesUserRegexes(candidate.Key);
        }

        /// <summary>Shape rules: Harmony cannot patch these, or patching them corrupts the log.</summary>
        private static bool IsPatchableShape(MethodCandidate candidate)
        {
            if (candidate.IsAbstract || candidate.IsDeclaringTypeInterface || candidate.IsGenericDefinition || !candidate.HasBody)
            {
                return false;
            }

            // Compiler-generated: lambdas, local functions, async state machines, iterator classes.
            return candidate.MethodName.IndexOf('<') < 0
                && candidate.ClassName.IndexOf('<') < 0
                && candidate.ClassName.IndexOf("d__", StringComparison.Ordinal) < 0;
        }

        private static bool IsIncludedNamespace(string? namespaceName) =>
            IncludedNamespaces.Contains(namespaceName ?? string.Empty);

        private static bool IsTracedClass(MethodCandidate candidate)
        {
            var className = candidate.ClassName;

            if (AllowedClasses.Contains(className) || candidate.IsInteractionClass)
            {
                return true;
            }

            foreach (var suffix in SystemSuffixes)
            {
                if (className.EndsWith(suffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            foreach (var prefix in SystemPrefixes)
            {
                if (className.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when a method name is one of Unity's input callbacks.</summary>
        public static bool IsInteractionMethodName(string? methodName)
        {
            if (string.IsNullOrEmpty(methodName))
            {
                return false;
            }

            foreach (var name in InteractionMethodNames)
            {
                if (string.Equals(methodName, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            foreach (var prefix in InteractionMethodPrefixes)
            {
                if (methodName!.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when an interface simple name is one of Unity's event-handler interfaces.</summary>
        public static bool IsInteractionInterfaceName(string? interfaceName) =>
            !string.IsNullOrEmpty(interfaceName) && Array.IndexOf(InteractionInterfaceNames, interfaceName) >= 0;

        private static bool IsTracedMethod(MethodCandidate candidate)
        {
            var name = candidate.MethodName;

            // Constructors and static constructors.
            if (name.Length > 0 && name[0] == '.')
            {
                return false;
            }

            if (ExcludedMethods.Contains(name))
            {
                return false;
            }

            foreach (var prefix in ExcludedPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (candidate.IsPublic)
            {
                return true;
            }

            return AllowedPrivateMethods.Contains(candidate.Key)
                || AllowedPrivateMethods.Contains((candidate.NamespaceName ?? string.Empty) + "." + candidate.Key);
        }

        /// <summary>The user's own regexes, applied last, on <c>Class.Method</c>.</summary>
        private bool PassesUserRegexes(string key)
        {
            if (_include != null && !_include.IsMatch(key))
            {
                return false;
            }

            return _exclude is null || !_exclude.IsMatch(key);
        }

        /// <summary>The effective namespace list, for the start-up summary.</summary>
        public static IReadOnlyCollection<string> Namespaces => IncludedNamespaces;

        /// <summary>The effective class-name suffixes, for the start-up summary.</summary>
        public static IReadOnlyList<string> Suffixes => SystemSuffixes;

        /// <summary>The event-handler interface names the plugin looks for, by simple name.</summary>
        public static IReadOnlyList<string> InteractionInterfaces => InteractionInterfaceNames;
    }
}
