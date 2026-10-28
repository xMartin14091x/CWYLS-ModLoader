using System.Collections.Generic;
using System.Text;

namespace CwyLs.ModLoader.Core.Trace
{
    /// <summary>
    /// Builds the <c>Canvas/RoomUI/SettingButton</c> path that identifies a control in the scene.
    /// The walk itself belongs to the plugin (it needs <c>Transform</c>); the joining and capping
    /// live here so the rule is one testable function rather than a loop inside a Harmony prefix.
    /// </summary>
    public static class UiPath
    {
        /// <summary>Longest path written. Deep UGUI hierarchies are mostly layout wrappers.</summary>
        public const int MaxSegments = 12;

        /// <summary>Marks a path whose root end was cut.</summary>
        public const string Elision = "…";

        /// <summary>
        /// Joins hierarchy names with '/'. Names must be ordered root first; when there are more
        /// than <see cref="MaxSegments"/>, the segments nearest the control are kept, because those
        /// are the ones that identify it.
        /// </summary>
        public static string Build(IReadOnlyList<string?>? namesRootFirst)
        {
            if (namesRootFirst is null || namesRootFirst.Count == 0)
            {
                return string.Empty;
            }

            var start = namesRootFirst.Count > MaxSegments ? namesRootFirst.Count - MaxSegments : 0;
            var sb = new StringBuilder();

            if (start > 0)
            {
                sb.Append(Elision).Append('/');
            }

            for (var i = start; i < namesRootFirst.Count; i++)
            {
                if (i > start)
                {
                    sb.Append('/');
                }

                var name = namesRootFirst[i];
                sb.Append(string.IsNullOrEmpty(name) ? "?" : name);
            }

            return sb.ToString();
        }
    }
}
