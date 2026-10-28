using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core
{
    /// <summary>
    /// One member of the game the framework relies on: the executable form of one
    /// verified seam. A target is pure data - assembly, type and member
    /// names as strings - so the table can name members of assemblies this project does not
    /// reference at compile time, and so it can be unit-tested without Unity.
    /// </summary>
    public sealed class PatchTarget
    {
        private static readonly string[] NoParameters = new string[0];

        /// <summary>
        /// Creates a target.
        /// </summary>
        /// <param name="id">Stable, unique, lower-case dotted id used in log lines, e.g. <c>heroine.playvoice</c>.</param>
        /// <param name="assembly">Simple assembly name, e.g. <c>Assembly-CSharp</c>.</param>
        /// <param name="typeName">
        /// Reflection type name, e.g. <c>Bulbul.HeroineService</c>, <c>HeroineAI+ActionStateType</c>
        /// for a nested type, or <c>KanKikuchi.AudioManager.AudioManager`1</c> for a generic definition.
        /// </param>
        /// <param name="memberKind">Method, field or property.</param>
        /// <param name="memberName">Member name, private members included.</param>
        /// <param name="parameterTypeNames">
        /// Full parameter type names in declaration order, for <see cref="Core.MemberKind.Method"/> only.
        /// An empty or null list means <em>the method takes no parameters</em> - it is not a wildcard.
        /// </param>
        /// <param name="isStatic">Whether the member is static.</param>
        /// <param name="expectedSignature">
        /// Optional canonical signature (see <see cref="PatchSignature"/>), e.g. <c>UniTask Load()</c>.
        /// When set, a resolver that finds the member but reads a different signature reports the
        /// target as missing, because the seam is no longer the shape the framework was written against.
        /// </param>
        /// <exception cref="ArgumentException">
        /// A required name is null or blank, or a non-method target was given parameter types.
        /// </exception>
        public PatchTarget(
            string id,
            string assembly,
            string typeName,
            MemberKind memberKind,
            string memberName,
            string[]? parameterTypeNames = null,
            bool isStatic = false,
            string? expectedSignature = null)
        {
            Id = Require(id, nameof(id));
            Assembly = Require(assembly, nameof(assembly));
            TypeName = Require(typeName, nameof(typeName));
            MemberName = Require(memberName, nameof(memberName));
            MemberKind = memberKind;
            IsStatic = isStatic;
            ExpectedSignature = string.IsNullOrWhiteSpace(expectedSignature) ? null : expectedSignature;

            var parameters = parameterTypeNames ?? NoParameters;
            if (parameters.Length > 0 && memberKind != MemberKind.Method)
            {
                throw new ArgumentException(
                    $"parameter types are only meaningful for a method target ('{id}' is a {memberKind})",
                    nameof(parameterTypeNames));
            }

            var copy = new string[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                copy[i] = Require(parameters[i], nameof(parameterTypeNames));
            }

            ParameterTypeNames = copy;
        }

        /// <summary>Stable, unique id used in <c>PATCH TARGET MISSING</c> log lines.</summary>
        public string Id { get; }

        /// <summary>Simple name of the assembly that must declare the type.</summary>
        public string Assembly { get; }

        /// <summary>Reflection name of the declaring type.</summary>
        public string TypeName { get; }

        /// <summary>Method, field or property.</summary>
        public MemberKind MemberKind { get; }

        /// <summary>Member name, private members included.</summary>
        public string MemberName { get; }

        /// <summary>Full parameter type names; empty means the method takes no parameters.</summary>
        public IReadOnlyList<string> ParameterTypeNames { get; }

        /// <summary>Whether the member is static.</summary>
        public bool IsStatic { get; }

        /// <summary>Canonical signature the framework was written against, or null if unchecked.</summary>
        public string? ExpectedSignature { get; }

        /// <summary>
        /// Human-readable location of the target, e.g.
        /// <c>Assembly-CSharp!Bulbul.HeroineService.PlayVoice</c>.
        /// </summary>
        public string Describe() => Assembly + "!" + TypeName + "." + MemberName;

        /// <inheritdoc />
        public override string ToString() => Id + " (" + Describe() + ")";

        private static string Require(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("must not be null or blank", parameterName);
            }

            return value;
        }
    }
}
