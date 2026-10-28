using System;
using System.Collections.Generic;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>
    /// An action as a tool an LLM or MCP client can call (a mod, an MCP server,
    /// <c>/v1/actions</c>): name, one-line description, tier hints and a JSON-schema object for the
    /// arguments. Pure: the result is plain dictionaries and lists that any JSON writer (for example
    /// <c>CwyLs.ModLoader.Core.Data.JsonWriter</c>) can serialise.
    /// </summary>
    /// <remarks>
    /// Shape (keys in this order):
    /// <code>
    /// { "name": "music.play", "description": "...", "tier": "act", "readOnly": false, "destructive": false,
    ///   "inputSchema": { "type": "object",
    ///                    "properties": { "title": { "type": "string", "description": "...", "enum": [...] },
    ///                                    "volume": { "type": "number", "description": "...", "minimum": 0, "maximum": 1 } },
    ///                    "required": [ "title" ],
    ///                    "additionalProperties": false } }
    /// </code>
    /// <c>readOnly</c>/<c>destructive</c> map onto MCP's <c>readOnlyHint</c>/<c>destructiveHint</c>.
    /// <c>additionalProperties: false</c> matches the runner, which refuses unknown arguments.
    /// </remarks>
    public static class ActionToolSchema
    {
        /// <summary>The tool description of one action.</summary>
        /// <param name="descriptor">The action.</param>
        /// <returns>A new dictionary (see the class remarks).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is null.</exception>
        public static IReadOnlyDictionary<string, object?> Build(ActionDescriptor descriptor)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            return new Dictionary<string, object?>
            {
                ["name"] = descriptor.Name,
                ["description"] = descriptor.Description,
                ["tier"] = ActionAudit.Word(descriptor.Tier),
                ["readOnly"] = descriptor.Tier == ActionTier.Read,
                ["destructive"] = descriptor.IsDestructive,
                ["inputSchema"] = InputSchema(descriptor),
            };
        }

        /// <summary>The tool descriptions of many actions, in the order given.</summary>
        /// <param name="descriptors">The actions (e.g. <see cref="ActionRegistry.Descriptors"/>).</param>
        public static IReadOnlyList<IReadOnlyDictionary<string, object?>> BuildAll(IEnumerable<ActionDescriptor>? descriptors)
        {
            var list = new List<IReadOnlyDictionary<string, object?>>();
            if (descriptors != null)
            {
                foreach (var descriptor in descriptors)
                {
                    if (descriptor != null)
                    {
                        list.Add(Build(descriptor));
                    }
                }
            }

            return list;
        }

        /// <summary>The JSON-schema object for an action's arguments.</summary>
        /// <param name="descriptor">The action.</param>
        /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is null.</exception>
        public static IReadOnlyDictionary<string, object?> InputSchema(ActionDescriptor descriptor)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            var properties = new Dictionary<string, object?>();
            var required = new List<object?>();
            foreach (var parameter in descriptor.Parameters)
            {
                properties[parameter.Name] = Property(parameter);
                if (parameter.Required)
                {
                    required.Add(parameter.Name);
                }
            }

            return new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required,
                ["additionalProperties"] = false,
            };
        }

        private static Dictionary<string, object?> Property(ActionParameter parameter)
        {
            var property = new Dictionary<string, object?>
            {
                ["type"] = ActionArgsValidation.Kind(parameter.Kind),
                ["description"] = parameter.Description,
            };

            if (parameter.AllowedValues.Count > 0)
            {
                var values = new List<object?>();
                foreach (var value in parameter.AllowedValues)
                {
                    values.Add(value);
                }

                property["enum"] = values;
            }

            if (parameter.Minimum.HasValue)
            {
                property["minimum"] = parameter.Minimum.Value;
            }

            if (parameter.Maximum.HasValue)
            {
                property["maximum"] = parameter.Maximum.Value;
            }

            return property;
        }
    }
}
