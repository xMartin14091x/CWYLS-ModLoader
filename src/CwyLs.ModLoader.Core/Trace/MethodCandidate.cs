namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// One method offered to <see cref="TraceFilter"/>. Everything the filter needs is a plain
    /// value, so the decision can be unit-tested without loading a single game assembly.
    /// </summary>
    public readonly struct MethodCandidate
    {
        /// <summary>Declaring namespace; empty or <c>null</c> for the root namespace.</summary>
        public string? NamespaceName { get; }

        /// <summary>Simple declaring class name, without its namespace.</summary>
        public string ClassName { get; }

        /// <summary>Method name as reflection reports it (so <c>get_Foo</c>, <c>.ctor</c>, <c>&lt;X&gt;b__0</c> and friends).</summary>
        public string MethodName { get; }

        /// <summary>Whether the method is public.</summary>
        public bool IsPublic { get; }

        /// <summary>Whether the method is static.</summary>
        public bool IsStatic { get; }

        /// <summary>Whether the method is abstract (no body to patch).</summary>
        public bool IsAbstract { get; }

        /// <summary>Whether the method or its declaring type still has open generic parameters.</summary>
        public bool IsGenericDefinition { get; }

        /// <summary>Whether the method has IL to patch (false for extern/InternalCall).</summary>
        public bool HasBody { get; }

        /// <summary>Whether the declaring type is an interface.</summary>
        public bool IsDeclaringTypeInterface { get; }

        /// <summary>
        /// Whether the declaring type handles user input: it implements one of
        /// <see cref="TraceFilter.InteractionInterfaces"/>, or declares a method whose name
        /// <see cref="TraceFilter.IsInteractionMethodName"/> accepts. The caller decides this,
        /// because answering it needs reflection over UnityEngine types that Core cannot see.
        /// </summary>
        public bool IsInteractionClass { get; }

        /// <summary>Creates a candidate, spelling out every shape flag.</summary>
        public MethodCandidate(
            string? namespaceName,
            string className,
            string methodName,
            bool isPublic,
            bool isStatic,
            bool isAbstract,
            bool isGenericDefinition,
            bool hasBody,
            bool isDeclaringTypeInterface,
            bool isInteractionClass = false)
        {
            NamespaceName = namespaceName;
            ClassName = className;
            MethodName = methodName;
            IsPublic = isPublic;
            IsStatic = isStatic;
            IsAbstract = isAbstract;
            IsGenericDefinition = isGenericDefinition;
            HasBody = hasBody;
            IsDeclaringTypeInterface = isDeclaringTypeInterface;
            IsInteractionClass = isInteractionClass;
        }

        /// <summary>
        /// Creates an ordinary concrete method: a body, no open generics, not an interface member.
        /// This is the shape described as (namespace, class, method, isPublic, isStatic).
        /// </summary>
        public static MethodCandidate Ordinary(
            string? namespaceName,
            string className,
            string methodName,
            bool isPublic = true,
            bool isStatic = false,
            bool isInteractionClass = false) =>
            new MethodCandidate(
                namespaceName, className, methodName, isPublic, isStatic, false, false, true, false, isInteractionClass);

        /// <summary>The <c>Class.Method</c> key the rate limiter, curated list and user regexes all use.</summary>
        public string Key => ClassName + "." + MethodName;
    }
}
