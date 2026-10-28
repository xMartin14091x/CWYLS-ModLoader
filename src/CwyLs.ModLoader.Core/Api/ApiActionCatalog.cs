using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Actions;
using CwyLs.ModLoader.Core.Json;

namespace CwyLs.ModLoader.Core.Api
{
    /// <summary>What the API itself decides about an action before the runner sees it.</summary>
    public enum ApiActionVerdict
    {
        /// <summary>An API caller may run it now.</summary>
        Allowed = 0,

        /// <summary>The tier gate refuses API callers (<c>[Api] AllowActions=false</c>).</summary>
        Denied = 1,

        /// <summary>It needs the player's Yes in the game, and no in-game confirmation is installed: refused, never auto-approved.</summary>
        NeedsConfirmation = 2,
    }

    /// <summary>
    /// The action catalog as JSON (<c>GET /v1/actions</c>), the API-side pre-gate, and the strict
    /// binding of a JSON argument object onto an action's typed parameters. Each descriptor
    /// carries a ready-made JSON Schema (<c>inputSchema</c>) so a client (for example an MCP
    /// server) can build an LLM tool from it without guessing.
    /// </summary>
    public static class ApiActionCatalog
    {
        /// <summary><c>read</c>, <c>act</c> or <c>write</c>.</summary>
        /// <param name="tier">The tier.</param>
        public static string TierName(ActionTier tier) => tier switch
        {
            ActionTier.Read => "read",
            ActionTier.Act => "act",
            ActionTier.Write => "write",
            _ => "unknown",
        };

        /// <summary><c>ok</c>, <c>failed</c>, <c>invalidArgs</c>, <c>notFound</c>, <c>denied</c>, <c>declined</c>, <c>timedOut</c> or <c>notAvailable</c>.</summary>
        /// <param name="status">The status.</param>
        public static string StatusName(ActionStatus status) => ApiJson.Camel(status.ToString()) ?? "unknown";

        /// <summary><c>none</c>, <c>semantic</c> or <c>uiClick</c>.</summary>
        /// <param name="path">The path.</param>
        public static string PathName(ActionPath path) => ApiJson.Camel(path.ToString()) ?? "none";

        /// <summary>The JSON Schema type of a parameter kind.</summary>
        /// <param name="kind">The kind.</param>
        public static string SchemaType(ActionValueKind kind) => kind switch
        {
            ActionValueKind.Integer => "integer",
            ActionValueKind.Number => "number",
            ActionValueKind.Boolean => "boolean",
            _ => "string",
        };

        /// <summary>
        /// The API's own pre-gate (defence in depth before the runner's): the default
        /// <see cref="ActionGate"/> table for an API caller, with <c>[Data] AllowWrites</c> left to the
        /// runner, plus the rule that anything needing the player's confirmation is refused while no
        /// in-game confirmation dialog is installed.
        /// </summary>
        /// <param name="descriptor">The action.</param>
        /// <param name="allowActions"><c>[Api] AllowActions</c>.</param>
        /// <param name="hasConfirmationGate">True once the in-game dialog is installed.</param>
        /// <param name="reason">The rule, in words.</param>
        public static ApiActionVerdict Decide(ActionDescriptor descriptor, bool allowActions, bool hasConfirmationGate, out string reason)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            var decision = ActionGate.Decide(
                descriptor,
                ActionCaller.Api,
                new ActionGateSettings { AllowApiActions = allowActions, AllowWrites = true });
            switch (decision.Verdict)
            {
                case ActionGateVerdict.Deny:
                    reason = decision.Reason;
                    return ApiActionVerdict.Denied;
                case ActionGateVerdict.Confirm when !hasConfirmationGate:
                    reason = decision.Reason + ", but the in-game confirmation dialog is not installed, so it is refused";
                    return ApiActionVerdict.NeedsConfirmation;
                case ActionGateVerdict.Confirm:
                    reason = decision.Reason;
                    return ApiActionVerdict.Allowed;
                default:
                    reason = decision.Reason;
                    return ApiActionVerdict.Allowed;
            }
        }

        /// <summary>One descriptor as JSON, with its JSON Schema and the API verdict.</summary>
        /// <param name="descriptor">The action.</param>
        /// <param name="owner">Who registered it (<c>cwy</c> or a mod id).</param>
        /// <param name="allowActions"><c>[Api] AllowActions</c>.</param>
        /// <param name="hasConfirmationGate">True once the in-game dialog is installed.</param>
        public static JsonObject Describe(ActionDescriptor descriptor, string owner, bool allowActions, bool hasConfirmationGate)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            var parameters = new List<object?>(descriptor.Parameters.Count);
            var properties = new JsonObject();
            var required = new List<object?>();
            foreach (var p in descriptor.Parameters)
            {
                var allowed = new List<object?>(p.AllowedValues.Count);
                foreach (var value in p.AllowedValues)
                {
                    allowed.Add(value);
                }

                var parameter = new JsonObject
                {
                    ["name"] = p.Name,
                    ["type"] = SchemaType(p.Kind),
                    ["required"] = p.Required,
                    ["description"] = p.Description,
                };
                var property = new JsonObject { ["type"] = SchemaType(p.Kind), ["description"] = p.Description };
                if (allowed.Count > 0)
                {
                    parameter["enum"] = allowed;
                    property["enum"] = allowed;
                }

                parameters.Add(parameter);
                properties[p.Name] = property;
                if (p.Required)
                {
                    required.Add(p.Name);
                }
            }

            var verdict = Decide(descriptor, allowActions, hasConfirmationGate, out var reason);
            return new JsonObject
            {
                ["name"] = descriptor.Name,
                ["family"] = descriptor.Family,
                ["owner"] = owner,
                ["tier"] = TierName(descriptor.Tier),
                ["destructive"] = descriptor.IsDestructive,
                ["description"] = descriptor.Description,
                ["parameters"] = parameters,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                    ["additionalProperties"] = false,
                },
                ["api"] = new JsonObject
                {
                    ["allowed"] = verdict == ApiActionVerdict.Allowed,
                    ["needsConfirmation"] = descriptor.Tier == ActionTier.Write || descriptor.IsDestructive,
                    ["reason"] = reason,
                },
            };
        }

        /// <summary>The response body of <c>GET /v1/actions</c>: every action sorted by name.</summary>
        /// <param name="registry">The registry.</param>
        /// <param name="services">The runner and confirmation slots.</param>
        /// <param name="allowActions"><c>[Api] AllowActions</c>.</param>
        public static string CatalogJson(ActionRegistry registry, ActionServices services, bool allowActions)
        {
            if (registry is null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            if (services is null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            var all = registry.All;
            var list = new List<object?>(all.Count);
            foreach (var binding in all)
            {
                list.Add(Describe(binding.Descriptor, binding.Owner, allowActions, services.HasConfirmationGate));
            }

            return ApiJson.Write(new JsonObject
            {
                ["count"] = list.Count,
                ["runner"] = services.HasRunner,
                ["allowActions"] = allowActions,
                ["confirmationGate"] = services.HasConfirmationGate,
                ["actions"] = list,
            });
        }

        /// <summary>
        /// Binds a JSON object of arguments onto an action's parameters. Strict: every member
        /// must name a parameter (case-insensitive), the JSON type must match the parameter's kind
        /// (an integer may be written <c>3</c> or <c>3.0</c>), allowed values are enforced
        /// (case-insensitive), required parameters must be present and not null, an optional one
        /// given as null counts as absent.
        /// </summary>
        /// <param name="descriptor">The action.</param>
        /// <param name="args">The argument object, or null for none.</param>
        /// <param name="bound">The arguments, or null on failure.</param>
        /// <param name="problem">Which argument and why, or empty.</param>
        public static bool TryBind(ActionDescriptor descriptor, JsonObject? args, out ActionArgs? bound, out string problem)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            bound = null;
            var pairs = new List<KeyValuePair<string, object?>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (args != null)
            {
                foreach (var member in args)
                {
                    var parameter = descriptor.FindParameter(member.Key);
                    if (parameter is null)
                    {
                        problem = "\"" + member.Key + "\" is not a parameter of " + descriptor.Name + Expected(descriptor);
                        return false;
                    }

                    if (!seen.Add(parameter.Name))
                    {
                        problem = "the argument \"" + parameter.Name + "\" is given twice";
                        return false;
                    }

                    if (member.Value is null)
                    {
                        if (parameter.Required)
                        {
                            problem = "the argument \"" + parameter.Name + "\" is required and cannot be null";
                            return false;
                        }

                        continue;
                    }

                    if (!TryConvert(parameter, member.Value, out var value, out problem))
                    {
                        return false;
                    }

                    pairs.Add(new KeyValuePair<string, object?>(parameter.Name, value));
                }
            }

            foreach (var parameter in descriptor.Parameters)
            {
                if (parameter.Required && !seen.Contains(parameter.Name))
                {
                    problem = "the argument \"" + parameter.Name + "\" (" + SchemaType(parameter.Kind) + ") is required";
                    return false;
                }
            }

            bound = ActionArgs.From(pairs);
            problem = string.Empty;
            return true;
        }

        /// <summary>A stable fingerprint of a bound call, for idempotency: name and arguments sorted by name with their types.</summary>
        /// <param name="action">The action name.</param>
        /// <param name="args">The bound arguments.</param>
        public static string Fingerprint(string action, ActionArgs args)
        {
            var keys = new List<string>();
            foreach (var key in args.Keys)
            {
                keys.Add(key);
            }

            keys.Sort(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder(action.ToLowerInvariant());
            foreach (var key in keys)
            {
                var value = args[key];
                sb.Append('|').Append(key.ToLowerInvariant()).Append('=');
                switch (value)
                {
                    case string s:
                        sb.Append("s:").Append(s);
                        break;
                    case long l:
                        sb.Append("i:").Append(l.ToString(CultureInfo.InvariantCulture));
                        break;
                    case double d:
                        sb.Append("n:").Append(d.ToString("R", CultureInfo.InvariantCulture));
                        break;
                    case bool b:
                        sb.Append("b:").Append(b ? "true" : "false");
                        break;
                    default:
                        sb.Append("null");
                        break;
                }
            }

            return sb.ToString();
        }

        private static bool TryConvert(ActionParameter parameter, object value, out object? converted, out string problem)
        {
            converted = null;
            switch (parameter.Kind)
            {
                case ActionValueKind.String:
                    if (!(value is string text))
                    {
                        problem = "the argument \"" + parameter.Name + "\" must be a string";
                        return false;
                    }

                    if (parameter.AllowedValues.Count > 0)
                    {
                        string? match = null;
                        foreach (var allowed in parameter.AllowedValues)
                        {
                            if (string.Equals(allowed, text, StringComparison.OrdinalIgnoreCase))
                            {
                                match = allowed;
                                break;
                            }
                        }

                        if (match is null)
                        {
                            problem = "the argument \"" + parameter.Name + "\" must be one of: " + string.Join(", ", parameter.AllowedValues);
                            return false;
                        }

                        converted = match;
                    }
                    else
                    {
                        converted = text;
                    }

                    problem = string.Empty;
                    return true;

                case ActionValueKind.Integer:
                    switch (value)
                    {
                        case long whole:
                            converted = whole;
                            problem = string.Empty;
                            return true;
                        case double real when Math.Abs(real % 1d) < double.Epsilon && real >= long.MinValue && real <= long.MaxValue:
                            converted = (long)real;
                            problem = string.Empty;
                            return true;
                        default:
                            problem = "the argument \"" + parameter.Name + "\" must be a whole number";
                            return false;
                    }

                case ActionValueKind.Number:
                    switch (value)
                    {
                        case long whole:
                            converted = (double)whole;
                            problem = string.Empty;
                            return true;
                        case double real:
                            converted = real;
                            problem = string.Empty;
                            return true;
                        default:
                            problem = "the argument \"" + parameter.Name + "\" must be a number";
                            return false;
                    }

                case ActionValueKind.Boolean:
                    if (value is bool flag)
                    {
                        converted = flag;
                        problem = string.Empty;
                        return true;
                    }

                    problem = "the argument \"" + parameter.Name + "\" must be true or false";
                    return false;

                default:
                    problem = "the parameter \"" + parameter.Name + "\" has an unknown kind";
                    return false;
            }
        }

        private static string Expected(ActionDescriptor descriptor)
        {
            if (descriptor.Parameters.Count == 0)
            {
                return " (it takes no arguments)";
            }

            var names = new List<string>(descriptor.Parameters.Count);
            foreach (var p in descriptor.Parameters)
            {
                names.Add(p.Name);
            }

            return " (expected: " + string.Join(", ", names) + ")";
        }
    }
}
