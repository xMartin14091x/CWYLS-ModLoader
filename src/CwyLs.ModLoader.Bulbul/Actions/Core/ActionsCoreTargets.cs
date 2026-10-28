using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.Actions
{
    /// <summary>
    /// The game and container members <see cref="GameServices"/> calls, as patch-target data
    /// (strings only, so seamcheck evaluates the table without the game). Nothing here is patched.
    /// Registered once from <see cref="BulbulActionsCore.Initialize"/>; checked against game 1.17.3.
    /// </summary>
    /// <remarks>
    /// A missing member switches off one lookup path with one WARN (the others keep working): the
    /// room scope (<see cref="RoomScopeId"/>), the project scope (<see cref="ProjectScopeId"/>), or
    /// both container paths (<see cref="ContainerId"/>, <see cref="TryResolveId"/>). The scene
    /// lookup for components (<c>Object.FindObjectOfType</c>) is the engine's and always stays.
    /// </remarks>
    public static class ActionsCoreTargets
    {
        /// <summary>The game assembly.</summary>
        public const string GameAssembly = "Assembly-CSharp";

        /// <summary>The DI container assembly.</summary>
        public const string ContainerAssembly = "VContainer";

        /// <summary>The room scope type is present (the lookup finds the live scope with <c>FindObjectOfType</c>; <c>GetOrCreate</c> is never called, it can instantiate one).</summary>
        public const string RoomScopeId = "actions.services.roomscope";

        /// <summary><c>ProjectLifetimeScope.GetOrCreate()</c>: returns the live project scope or null, no side effect.</summary>
        public const string ProjectScopeId = "actions.services.projectscope";

        /// <summary><c>LifetimeScope.Container</c>: the scope's resolver.</summary>
        public const string ContainerId = "actions.services.container";

        /// <summary><c>IObjectResolver.TryResolve(Type, out object, object)</c>: a lookup that answers false instead of throwing.</summary>
        public const string TryResolveId = "actions.services.tryresolve";

        /// <summary>Every target, built once.</summary>
        public static IReadOnlyList<PatchTarget> All { get; } = new[]
        {
            // Type presence only; never called, because it can instantiate a scope.
            new PatchTarget(RoomScopeId, GameAssembly, "Bulbul.RoomLifetimeScope", MemberKind.Method, "GetOrCreate",
                isStatic: true, expectedSignature: "static RoomLifetimeScope GetOrCreate()"),

            // Returns the live instance, or null before boot.
            new PatchTarget(ProjectScopeId, GameAssembly, "NestopiSystem.DIContainers.ProjectLifetimeScope", MemberKind.Method, "GetOrCreate",
                isStatic: true, expectedSignature: "static ProjectLifetimeScope GetOrCreate()"),

            new PatchTarget(ContainerId, ContainerAssembly, "VContainer.Unity.LifetimeScope", MemberKind.Property, "Container",
                expectedSignature: "IObjectResolver Container { get; set; }"),

            // Called through IObjectResolverExtensions.TryResolve<T>.
            new PatchTarget(TryResolveId, ContainerAssembly, "VContainer.IObjectResolver", MemberKind.Method, "TryResolve",
                new[] { "System.Type", "System.Object&", "System.Object" },
                expectedSignature: "bool TryResolve(Type, object&, object)"),
        };
    }
}
