using System;
using CwyLs.ModLoader.Core.Trace;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CwyLs.ModLoader.Trace
{
    /// <summary>
    /// The one formatting case Core cannot express: a <see cref="Object"/> is best described by its
    /// scene name, and asking a destroyed one for that name throws. Installed into
    /// <see cref="ArgFormatter.ObjectHook"/> so Core keeps its "no UnityEngine reference" rule.
    /// </summary>
    internal static class UnityArgHook
    {
        /// <summary>Formats Unity objects; returns <c>null</c> for anything else.</summary>
        public static string? Format(object value)
        {
            if (!(value is Object unityObject))
            {
                return null;
            }

            var typeName = value.GetType().Name;

            // Unity's overloaded == reports a destroyed object as null; touching .name would throw.
            if (unityObject == null)
            {
                return typeName + "(destroyed)";
            }

            try
            {
                return typeName + "(" + unityObject.name + ")";
            }
            catch (Exception ex)
            {
                // Reading .name off the main thread throws; the type alone is still useful.
                TraceDiagnostics.Report($"TRACE: could not read the name of a {typeName}", ex);
                return typeName;
            }
        }
    }
}
