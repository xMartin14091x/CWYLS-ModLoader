using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>
    /// Marks a framework class as an action family: the runtime finds it in the
    /// framework's assemblies on the first frame, creates it once and calls
    /// <see cref="IActionFamily.Register"/>. Adding a family is adding a file - no init line, no
    /// edit to a shared file. The class implements <see cref="IActionFamily"/> and has a
    /// parameterless constructor (any visibility).
    /// </summary>
    /// <remarks>
    /// Only assemblies named <c>CwyLs.ModLoader*</c> are searched. Code mods publish their actions with
    /// <c>helper.Actions.Register</c> instead. Keep the constructor and static fields free of game
    /// calls: families are created before the room exists; resolve game services inside handlers.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class ActionFamilyAttribute : Attribute
    {
        /// <summary>Marks the class as the family <paramref name="family"/>.</summary>
        /// <param name="family">The family name, e.g. <c>music</c>: every action it registers is named <c>&lt;family&gt;.&lt;verb&gt;</c>.</param>
        public ActionFamilyAttribute(string family) => Family = family ?? string.Empty;

        /// <summary>The family name.</summary>
        public string Family { get; }
    }

    /// <summary>A family of actions found by <see cref="ActionFamilyAttribute"/>.</summary>
    public interface IActionFamily
    {
        /// <summary>
        /// Registers the family's actions with <see cref="ActionFamilyContext.Add"/>, or declines with
        /// <see cref="ActionFamilyContext.Skip"/> (a seam is missing, a feature is off). Called once, on
        /// the main thread, before the room exists. A throw marks the family Failed and takes back
        /// whatever it had added.
        /// </summary>
        /// <param name="context">Where to register.</param>
        void Register(ActionFamilyContext context);
    }

    /// <summary>What a family's <see cref="IActionFamily.Register"/> registers through. Not thread-safe; used during that call only.</summary>
    public sealed class ActionFamilyContext
    {
        private readonly ActionRegistry _registry;
        private readonly List<IDisposable> _registrations = new List<IDisposable>();
        private readonly List<string> _names = new List<string>();
        private readonly List<string> _problems = new List<string>();

        internal ActionFamilyContext(ActionRegistry registry, string family)
        {
            _registry = registry;
            Family = family;
        }

        /// <summary>The family name: every action added is named <c>&lt;Family&gt;.&lt;verb&gt;</c>.</summary>
        public string Family { get; }

        /// <summary>The reason given to <see cref="Skip"/>, or null.</summary>
        public string? SkipReason { get; private set; }

        internal string[] Names => _names.ToArray();

        internal string[] Problems => _problems.ToArray();

        /// <summary>
        /// Registers one action as a framework action. Refused (false, recorded as a problem and
        /// reported as one WARN) when the name is not under the family, is already taken, or the
        /// family has called <see cref="Skip"/>.
        /// </summary>
        /// <param name="descriptor">What the action is.</param>
        /// <param name="handler">What runs it, on the main thread.</param>
        /// <returns>True when registered.</returns>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        public bool Add(ActionDescriptor descriptor, ActionHandler handler)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (SkipReason != null)
            {
                _problems.Add(descriptor.Name + ": the family was skipped (" + SkipReason + ")");
                return false;
            }

            if (!descriptor.Name.StartsWith(Family + ".", StringComparison.Ordinal))
            {
                _problems.Add(descriptor.Name + ": not in the family " + Family + " (names start with \"" + Family + ".\")");
                return false;
            }

            if (!_registry.TryRegister(ActionRegistry.FrameworkOwner, descriptor, handler, out var registration, out var problem))
            {
                _problems.Add(descriptor.Name + ": " + problem);
                return false;
            }

            _registrations.Add(registration!);
            _names.Add(descriptor.Name);
            return true;
        }

        /// <summary>
        /// Declines to register (a missing seam, a switched-off feature): reported once as Skipped
        /// with the reason; anything already added is taken back.
        /// </summary>
        /// <param name="reason">Why, naming the seam or switch.</param>
        public void Skip(string reason)
        {
            SkipReason = string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason.Trim();
            TakeBack();
        }

        internal void TakeBack()
        {
            foreach (var registration in _registrations)
            {
                registration.Dispose();
            }

            _registrations.Clear();
            _names.Clear();
        }
    }

    /// <summary>How a family's registration ended.</summary>
    public enum ActionFamilyState
    {
        /// <summary>Its actions are registered (possibly fewer than it tried: see the problems).</summary>
        Registered = 0,

        /// <summary>It declined (<see cref="ActionFamilyContext.Skip"/>); nothing registered.</summary>
        Skipped = 1,

        /// <summary>It could not be created or threw; nothing registered.</summary>
        Failed = 2,
    }

    /// <summary>One family's outcome. Immutable.</summary>
    public sealed class ActionFamilyOutcome
    {
        internal ActionFamilyOutcome(string family, string typeName, ActionFamilyState state, IReadOnlyList<string> actions, string detail, IReadOnlyList<string> problems)
        {
            Family = family;
            TypeName = typeName;
            State = state;
            Actions = actions;
            Detail = detail;
            Problems = problems;
        }

        /// <summary>The family name as declared.</summary>
        public string Family { get; }

        /// <summary>The class's full name.</summary>
        public string TypeName { get; }

        /// <summary>How it ended.</summary>
        public ActionFamilyState State { get; }

        /// <summary>The actions it registered, in the order added.</summary>
        public IReadOnlyList<string> Actions { get; }

        /// <summary>Why it was skipped or failed; empty when registered.</summary>
        public string Detail { get; }

        /// <summary>Refused <see cref="ActionFamilyContext.Add"/> calls.</summary>
        public IReadOnlyList<string> Problems { get; }

        /// <summary><c>music (MusicActions): Registered, 7 action(s)</c>.</summary>
        public override string ToString()
        {
            var shortType = TypeName.Substring(TypeName.LastIndexOf('.') + 1);
            return Family + " (" + shortType + "): " + State +
                   (State == ActionFamilyState.Registered ? ", " + Actions.Count + " action(s)" : string.Empty) +
                   (Detail.Length == 0 ? string.Empty : " - " + Detail) +
                   (Problems.Count == 0 ? string.Empty : " [" + Problems.Count + " refused]");
        }
    }

    /// <summary>
    /// Finds the <see cref="ActionFamilyAttribute"/> classes in a set of assemblies and registers
    /// each family once into one registry. Thread-safe; each type is handled at most once, so
    /// discovering the same assembly again only picks up what is new.
    /// </summary>
    public sealed class ActionFamilyCatalog
    {
        private static readonly Regex FamilyRegex =
            new Regex(@"\A[a-z0-9][a-z0-9_\-]*(\.[a-z0-9][a-z0-9_\-]*)*\z", RegexOptions.CultureInvariant);

        private readonly object _gate = new object();
        private readonly ActionRegistry _registry;
        private readonly HashSet<Type> _seen = new HashSet<Type>();
        private readonly List<ActionFamilyOutcome> _outcomes = new List<ActionFamilyOutcome>();
        private readonly List<string> _assemblyProblems = new List<string>();

        /// <summary>Creates a catalog over <paramref name="registry"/>.</summary>
        /// <param name="registry">Where the families register (<see cref="ActionRegistry.Shared"/> in game).</param>
        /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
        public ActionFamilyCatalog(ActionRegistry registry) =>
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));

        /// <summary>Every outcome so far, in discovery order.</summary>
        public IReadOnlyList<ActionFamilyOutcome> Outcomes
        {
            get { lock (_gate) { return _outcomes.ToArray(); } }
        }

        /// <summary>Assemblies whose types could not all be read (the readable ones were still searched).</summary>
        public IReadOnlyList<string> AssemblyProblems
        {
            get { lock (_gate) { return _assemblyProblems.ToArray(); } }
        }

        /// <summary>True when <paramref name="family"/> is a usable family name (lower-case dotted segments, like an action name without its verb).</summary>
        /// <param name="family">A candidate.</param>
        public static bool IsValidFamily(string? family) =>
            family != null && family.Length < ActionDescriptor.MaxNameLength && FamilyRegex.IsMatch(family);

        /// <summary>
        /// Finds and registers the families in <paramref name="assemblies"/>, in order of family
        /// name then type name.
        /// </summary>
        /// <param name="assemblies">The assemblies to search; nulls are skipped.</param>
        /// <returns>The outcomes of the families found by this call (not the earlier ones).</returns>
        public IReadOnlyList<ActionFamilyOutcome> Discover(IEnumerable<Assembly?> assemblies)
        {
            var found = new List<(string Family, Type Type)>();
            if (assemblies != null)
            {
                foreach (var assembly in assemblies)
                {
                    if (assembly != null)
                    {
                        Collect(assembly, found);
                    }
                }
            }

            found.Sort((a, b) =>
            {
                var byFamily = string.CompareOrdinal(a.Family, b.Family);
                return byFamily != 0 ? byFamily : string.CompareOrdinal(a.Type.FullName, b.Type.FullName);
            });

            var outcomes = new List<ActionFamilyOutcome>();
            lock (_gate)
            {
                foreach (var (family, type) in found)
                {
                    if (!_seen.Add(type))
                    {
                        continue;
                    }

                    var outcome = Register(family, type);
                    _outcomes.Add(outcome);
                    outcomes.Add(outcome);
                }
            }

            return outcomes;
        }

        /// <summary><c>3 families: 2 registered (9 actions), 1 skipped, 0 failed</c>.</summary>
        public string Summary()
        {
            int registered = 0, skipped = 0, failed = 0, actions = 0;
            foreach (var outcome in Outcomes)
            {
                switch (outcome.State)
                {
                    case ActionFamilyState.Registered:
                        registered++;
                        actions += outcome.Actions.Count;
                        break;
                    case ActionFamilyState.Skipped:
                        skipped++;
                        break;
                    default:
                        failed++;
                        break;
                }
            }

            return (registered + skipped + failed) + " famil" + (registered + skipped + failed == 1 ? "y" : "ies") + ": " +
                   registered + " registered (" + actions + " action" + (actions == 1 ? string.Empty : "s") + "), " +
                   skipped + " skipped, " + failed + " failed";
        }

        private void Collect(Assembly assembly, List<(string Family, Type Type)> found)
        {
            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types;
                var first = ex.LoaderExceptions != null && ex.LoaderExceptions.Length > 0 && ex.LoaderExceptions[0] != null
                    ? ex.LoaderExceptions[0]!.GetType().Name + ": " + ex.LoaderExceptions[0]!.Message
                    : "no loader detail";
                lock (_gate)
                {
                    _assemblyProblems.Add(assembly.GetName().Name + ": some types could not be loaded (" + first + ")");
                }
            }

            foreach (var type in types)
            {
                if (type is null)
                {
                    continue;
                }

                if (type.GetCustomAttributes(typeof(ActionFamilyAttribute), false) is ActionFamilyAttribute[] marks && marks.Length > 0)
                {
                    found.Add((marks[0].Family, type));
                }
            }
        }

        private ActionFamilyOutcome Register(string family, Type type)
        {
            var typeName = type.FullName ?? type.Name;
            var none = Array.Empty<string>();

            if (!IsValidFamily(family))
            {
                return new ActionFamilyOutcome(family, typeName, ActionFamilyState.Failed, none,
                    "\"" + family + "\" is not a family name (lower-case dotted segments, like music)", none);
            }

            if (type.IsAbstract || type.IsGenericTypeDefinition || !typeof(IActionFamily).IsAssignableFrom(type))
            {
                return new ActionFamilyOutcome(family, typeName, ActionFamilyState.Failed, none,
                    "the class must be concrete, not generic, and implement IActionFamily", none);
            }

            IActionFamily instance;
            try
            {
                instance = (IActionFamily)Activator.CreateInstance(type, nonPublic: true)!;
            }
            catch (Exception ex)
            {
                var inner = Unwrap(ex);
                return new ActionFamilyOutcome(family, typeName, ActionFamilyState.Failed, none,
                    "could not be created: " + inner.GetType().Name + ": " + inner.Message, none);
            }

            var context = new ActionFamilyContext(_registry, family);
            try
            {
                instance.Register(context);
            }
            catch (Exception ex)
            {
                context.TakeBack();
                return new ActionFamilyOutcome(family, typeName, ActionFamilyState.Failed, none,
                    "Register threw " + ex.GetType().Name + ": " + ex.Message, context.Problems);
            }

            if (context.SkipReason != null)
            {
                return new ActionFamilyOutcome(family, typeName, ActionFamilyState.Skipped, none, context.SkipReason, context.Problems);
            }

            return new ActionFamilyOutcome(family, typeName, ActionFamilyState.Registered, context.Names, string.Empty, context.Problems);
        }

        private static Exception Unwrap(Exception ex)
        {
            var current = ex;
            while ((current is TargetInvocationException || current is TypeInitializationException) && current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current;
        }
    }
}
