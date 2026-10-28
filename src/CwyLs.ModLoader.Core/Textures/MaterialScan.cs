using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CwyLs.ModLoader.Core.Textures
{
    /// <summary>The type of a shader property, as Unity's <c>ShaderPropertyType</c> names it.</summary>
    public enum MaterialValueType
    {
        /// <summary>A colour.</summary>
        Color = 0,

        /// <summary>A vector.</summary>
        Vector,

        /// <summary>A float.</summary>
        Float,

        /// <summary>A float with a range.</summary>
        Range,

        /// <summary>A texture.</summary>
        Texture,

        /// <summary>An integer.</summary>
        Int,
    }

    /// <summary>One shader property and its live value on a material.</summary>
    public sealed class MaterialScanProperty
    {
        /// <summary>Creates a numeric or colour property reading.</summary>
        /// <param name="name">Property name, e.g. <c>_Color</c>.</param>
        /// <param name="type">Its type.</param>
        /// <param name="x">Value (float/int), or the first component.</param>
        /// <param name="y">Second component.</param>
        /// <param name="z">Third component.</param>
        /// <param name="w">Fourth component.</param>
        public MaterialScanProperty(string name, MaterialValueType type, double x, double y = 0, double z = 0, double w = 0)
        {
            Name = name ?? string.Empty;
            Type = type;
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        /// <summary>Creates a texture property reading.</summary>
        /// <param name="name">Property name, e.g. <c>_MainTex</c>.</param>
        /// <param name="textureName">The texture's name; null when the slot is empty.</param>
        /// <param name="width">Width in pixels.</param>
        /// <param name="height">Height in pixels.</param>
        public MaterialScanProperty(string name, string? textureName, int width, int height)
        {
            Name = name ?? string.Empty;
            Type = MaterialValueType.Texture;
            TextureName = textureName;
            Width = width;
            Height = height;
        }

        /// <summary>Property name.</summary>
        public string Name { get; }

        /// <summary>Type.</summary>
        public MaterialValueType Type { get; }

        /// <summary>First component or scalar value.</summary>
        public double X { get; }

        /// <summary>Second component.</summary>
        public double Y { get; }

        /// <summary>Third component.</summary>
        public double Z { get; }

        /// <summary>Fourth component.</summary>
        public double W { get; }

        /// <summary>Texture name; null for an empty slot or a non-texture property.</summary>
        public string? TextureName { get; }

        /// <summary>Texture width.</summary>
        public int Width { get; }

        /// <summary>Texture height.</summary>
        public int Height { get; }

        /// <summary>The value as JSON: a number, an array, a texture object or <c>null</c>.</summary>
        public string ValueJson()
        {
            switch (Type)
            {
                case MaterialValueType.Color:
                case MaterialValueType.Vector:
                    return "[" + Num(X) + ", " + Num(Y) + ", " + Num(Z) + ", " + Num(W) + "]";
                case MaterialValueType.Texture:
                    return TextureName is null
                        ? "null"
                        : "{\"name\": " + MiniJson.String(TextureName) + ", \"width\": " +
                          Width.ToString(CultureInfo.InvariantCulture) + ", \"height\": " +
                          Height.ToString(CultureInfo.InvariantCulture) + "}";
                default:
                    return Num(X);
            }
        }

        /// <summary>The value for a console line: <c>(0.87,0.81,0.78,1)</c>, <c>0.8</c>, <c>t_desk_base 2048x2048</c>.</summary>
        public string ValueText()
        {
            switch (Type)
            {
                case MaterialValueType.Color:
                case MaterialValueType.Vector:
                    return "(" + Short(X) + "," + Short(Y) + "," + Short(Z) + "," + Short(W) + ")";
                case MaterialValueType.Texture:
                    return TextureName is null
                        ? "-"
                        : TextureName + " " + Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture);
                default:
                    return Short(X);
            }
        }

        private static string Num(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "null" : Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);

        private static string Short(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>A renderer using a scanned material.</summary>
    public sealed class MaterialScanUser
    {
        /// <summary>Creates a user record.</summary>
        /// <param name="path">Scene path of the renderer.</param>
        /// <param name="active">True when the renderer is active in the hierarchy and enabled.</param>
        public MaterialScanUser(string path, bool active)
        {
            Path = path ?? string.Empty;
            Active = active;
        }

        /// <summary>Scene path.</summary>
        public string Path { get; }

        /// <summary>Active and enabled.</summary>
        public bool Active { get; }
    }

    /// <summary>One loaded material as <c>tex scan</c> saw it.</summary>
    public sealed class MaterialScanEntry
    {
        /// <summary>Creates an entry.</summary>
        /// <param name="name">Base name (no <c>" (Instance)"</c>).</param>
        /// <param name="isInstance">True for an instantiated copy.</param>
        /// <param name="instanceId">Unity instance id.</param>
        /// <param name="shader">Shader name.</param>
        /// <param name="renderQueue">Render queue.</param>
        /// <param name="users">Renderers using it; null means none.</param>
        /// <param name="properties">Properties and values; null means none were read.</param>
        public MaterialScanEntry(
            string name,
            bool isInstance,
            int instanceId,
            string shader,
            int renderQueue,
            IReadOnlyList<MaterialScanUser>? users,
            IReadOnlyList<MaterialScanProperty>? properties)
        {
            Name = name ?? string.Empty;
            IsInstance = isInstance;
            InstanceId = instanceId;
            Shader = shader ?? string.Empty;
            RenderQueue = renderQueue;
            Users = users ?? new MaterialScanUser[0];
            Properties = properties ?? new MaterialScanProperty[0];
            Family = SceneryMaterials.Classify(Name);
        }

        /// <summary>Base name.</summary>
        public string Name { get; }

        /// <summary>True for an instantiated copy.</summary>
        public bool IsInstance { get; }

        /// <summary>Unity instance id.</summary>
        public int InstanceId { get; }

        /// <summary>Shader name.</summary>
        public string Shader { get; }

        /// <summary>Render queue.</summary>
        public int RenderQueue { get; }

        /// <summary>Scenery family.</summary>
        public SceneryFamily Family { get; }

        /// <summary>Renderers using it.</summary>
        public IReadOnlyList<MaterialScanUser> Users { get; }

        /// <summary>Properties and values.</summary>
        public IReadOnlyList<MaterialScanProperty> Properties { get; }

        /// <summary>How many users are active.</summary>
        public int ActiveUsers
        {
            get
            {
                var count = 0;
                foreach (var user in Users)
                {
                    if (user.Active)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>The first property with this name, or null.</summary>
        /// <param name="name">Property name.</param>
        public MaterialScanProperty? Find(string name)
        {
            foreach (var property in Properties)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    return property;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The pure half of <c>tex scan</c> / <c>tex export</c>: console summary lines and the
    /// <c>materials.json</c> document, built from what the Unity side read.
    /// </summary>
    public static class MaterialScan
    {
        /// <summary>The properties a summary line shows when the material has them.</summary>
        public static IReadOnlyList<string> SummaryProperties { get; } =
            new[] { "_Color", "_BaseColor", "_MainTex", "_EmissionColor", "_MainTexHSVG" };

        /// <summary>The schema version of <c>materials.json</c>.</summary>
        public const int ExportSchema = 1;

        /// <summary>
        /// One console line per material:
        /// <c>mt_desk_1  [desk] lilToon  users 2 (2 active)  _Color=(1,1,1,1) _MainTex=t_desk_base_0 2048x2048</c>.
        /// </summary>
        /// <param name="entry">The material.</param>
        public static string SummaryLine(MaterialScanEntry entry)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            var sb = new StringBuilder(entry.Name);
            if (entry.IsInstance)
            {
                sb.Append(" (Instance)");
            }

            sb.Append("  [").Append(entry.Family.ToString().ToLowerInvariant()).Append("] ").Append(entry.Shader)
              .Append("  users ").Append(entry.Users.Count.ToString(CultureInfo.InvariantCulture))
              .Append(" (").Append(entry.ActiveUsers.ToString(CultureInfo.InvariantCulture)).Append(" active)");

            foreach (var name in SummaryProperties)
            {
                var property = entry.Find(name);
                if (property != null)
                {
                    sb.Append("  ").Append(name).Append('=').Append(property.ValueText());
                }
            }

            return sb.ToString();
        }

        /// <summary>The <c>materials.json</c> document: schema, when, and every material with users and properties.</summary>
        /// <param name="entries">The materials.</param>
        /// <param name="generatedAt">Timestamp written into the file.</param>
        /// <param name="filter">The patterns the scan used, for the reader.</param>
        public static string ToJson(IReadOnlyList<MaterialScanEntry> entries, DateTimeOffset generatedAt, IReadOnlyList<string> filter)
        {
            if (entries is null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            var sb = new StringBuilder(64 * 1024);
            sb.Append("{\n  \"schema\": ").Append(ExportSchema.ToString(CultureInfo.InvariantCulture))
              .Append(",\n  \"generatedAt\": ").Append(MiniJson.String(generatedAt.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture)))
              .Append(",\n  \"filter\": [");

            for (var i = 0; i < (filter?.Count ?? 0); i++)
            {
                sb.Append(i == 0 ? string.Empty : ", ").Append(MiniJson.String(filter![i]));
            }

            sb.Append("],\n  \"count\": ").Append(entries.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\n  \"materials\": [");

            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.Append(i == 0 ? "\n" : ",\n").Append("    {\"name\": ").Append(MiniJson.String(e.Name))
                  .Append(", \"instance\": ").Append(e.IsInstance ? "true" : "false")
                  .Append(", \"id\": ").Append(e.InstanceId.ToString(CultureInfo.InvariantCulture))
                  .Append(", \"family\": ").Append(MiniJson.String(e.Family.ToString().ToLowerInvariant()))
                  .Append(", \"shader\": ").Append(MiniJson.String(e.Shader))
                  .Append(", \"renderQueue\": ").Append(e.RenderQueue.ToString(CultureInfo.InvariantCulture))
                  .Append(",\n      \"users\": [");

                for (var u = 0; u < e.Users.Count; u++)
                {
                    sb.Append(u == 0 ? string.Empty : ", ").Append("{\"path\": ").Append(MiniJson.String(e.Users[u].Path))
                      .Append(", \"active\": ").Append(e.Users[u].Active ? "true" : "false").Append('}');
                }

                sb.Append("],\n      \"properties\": {");
                for (var p = 0; p < e.Properties.Count; p++)
                {
                    var property = e.Properties[p];
                    sb.Append(p == 0 ? "\n" : ",\n").Append("        ").Append(MiniJson.String(property.Name))
                      .Append(": {\"type\": ").Append(MiniJson.String(property.Type.ToString().ToLowerInvariant()))
                      .Append(", \"value\": ").Append(property.ValueJson()).Append('}');
                }

                sb.Append(e.Properties.Count == 0 ? "}}" : "\n      }}");
            }

            sb.Append(entries.Count == 0 ? "]\n}\n" : "\n  ]\n}\n");
            return sb.ToString();
        }
    }
}
