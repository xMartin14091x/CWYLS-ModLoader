using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Input
{
    /// <summary>
    /// Decides whether the control that currently has keyboard focus is a text field, so a hotkey
    /// never fires while the user is typing a note, a todo or a profile name.
    /// <para>
    /// The decision is made on type <em>names</em> rather than types: the framework core must not
    /// reference TextMeshPro, and the same rule has to cover both <c>UnityEngine.UI.InputField</c>
    /// and <c>TMPro.TMP_InputField</c> (the game uses the latter). The caller reads the components
    /// off <c>EventSystem.current.currentSelectedGameObject</c> and passes their type names here.
    /// </para>
    /// </summary>
    public static class TextEntryGuard
    {
        /// <summary>Every type whose simple name ends in this is treated as text entry.</summary>
        public const string InputFieldSuffix = "InputField";

        /// <summary>True when a component of this type accepts typed text.</summary>
        /// <param name="typeName">
        /// Simple or fully-qualified type name, e.g. <c>TMP_InputField</c> or
        /// <c>TMPro.TMP_InputField</c>. Null or empty is not text entry.
        /// </param>
        public static bool IsTextEntryType(string? typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return false;
            }

            var simple = typeName!;
            var dot = simple.LastIndexOf('.');
            if (dot >= 0 && dot < simple.Length - 1)
            {
                simple = simple.Substring(dot + 1);
            }

            // A generic or nested name (TMP_InputField`1, Outer+Inner) still ends in the suffix.
            return simple.EndsWith(InputFieldSuffix, StringComparison.Ordinal);
        }

        /// <summary>True when any of the focused control's components accepts typed text.</summary>
        /// <param name="componentTypeNames">Type names of the components on the focused object; null means none.</param>
        public static bool IsTextEntry(IEnumerable<string?>? componentTypeNames)
        {
            if (componentTypeNames is null)
            {
                return false;
            }

            foreach (var name in componentTypeNames)
            {
                if (IsTextEntryType(name))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
