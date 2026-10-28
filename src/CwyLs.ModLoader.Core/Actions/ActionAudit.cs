using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CwyLs.ModLoader.Core.Logging;

namespace CwyLs.ModLoader.Core.Actions
{
    /// <summary>
    /// One finished action call, as the <c>ACTION</c> audit line records it. Built by the runner;
    /// values are already redacted (<see cref="ActionAudit.RedactArgs"/>), so a record can go to
    /// any log. Immutable.
    /// </summary>
    public sealed class ActionAuditRecord
    {
        /// <summary>Creates a record. Called by the runner.</summary>
        /// <param name="name">The action name as asked (the registered name when found).</param>
        /// <param name="tier">The action's tier, or null when no action has the name.</param>
        /// <param name="request">The request.</param>
        /// <param name="status">How it ended.</param>
        /// <param name="path">Which way it reached the game.</param>
        /// <param name="milliseconds">Wall-clock time from the request to the answer.</param>
        /// <param name="confirmed">True when the player approved it in the in-game confirmation.</param>
        /// <param name="late">True when the handler finished after the caller stopped waiting.</param>
        /// <param name="args">The arguments, redacted.</param>
        /// <param name="detail">The result message, redacted.</param>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public ActionAuditRecord(
            string name,
            ActionTier? tier,
            ActionRequest request,
            ActionStatus status,
            ActionPath path,
            double milliseconds,
            bool confirmed,
            bool late,
            string args,
            string detail)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            Name = string.IsNullOrWhiteSpace(name) ? request.Name : name;
            Tier = tier;
            Caller = request.Caller;
            CallerId = request.CallerId;
            RequestId = request.RequestId;
            Status = status;
            Path = path;
            Milliseconds = milliseconds < 0 ? 0 : milliseconds;
            Confirmed = confirmed;
            Late = late;
            Args = args ?? string.Empty;
            Detail = detail ?? string.Empty;
        }

        /// <summary>The action name.</summary>
        public string Name { get; }

        /// <summary>The tier, or null when no action has the name.</summary>
        public ActionTier? Tier { get; }

        /// <summary>Who asked.</summary>
        public ActionCaller Caller { get; }

        /// <summary>The caller's name (mod id, <c>console</c>, scene group, API client).</summary>
        public string CallerId { get; }

        /// <summary>The API idempotency key, or null.</summary>
        public string? RequestId { get; }

        /// <summary>How it ended.</summary>
        public ActionStatus Status { get; }

        /// <summary>Which way it reached the game.</summary>
        public ActionPath Path { get; }

        /// <summary>Wall-clock milliseconds from the request to the answer.</summary>
        public double Milliseconds { get; }

        /// <summary>True when the player approved it in the in-game confirmation.</summary>
        public bool Confirmed { get; }

        /// <summary>True when the handler finished after the caller stopped waiting (the caller was told TimedOut).</summary>
        public bool Late { get; }

        /// <summary>The arguments, redacted: <c>title=Rain text=&lt;private:12&gt;</c>.</summary>
        public string Args { get; }

        /// <summary>The result message, redacted for Write-tier handlers.</summary>
        public string Detail { get; }
    }

    /// <summary>
    /// The <c>ACTION</c> audit line: what ran, for whom, how it ended - and never the player's text
    /// (envelope privacy rule: ids, counts and lengths only).
    /// </summary>
    /// <remarks>
    /// <para><b>Redaction.</b> An argument value is shown only when it belongs to a declared
    /// parameter that is not <see cref="ActionParameter.IsPrivate"/> and, on a
    /// <see cref="ActionTier.Write"/> action, is not text. Everything else - private and Write-tier
    /// text, undeclared names, every value of a name no action has - prints as
    /// <c>&lt;private:N&gt;</c> (N = characters). A Write-tier handler's own message is replaced by its
    /// length too, because it may quote what was written.</para>
    /// <para><b>Where.</b> Act and Write calls, and every refusal or failure of any tier, are console
    /// lines (tag <c>ACTION</c>; failed, timed-out and late ones at Warn). A Read call that worked is
    /// trace-file only (<c>action/run</c>), so a client polling state cannot flood the console.</para>
    /// </remarks>
    public static class ActionAudit
    {
        /// <summary>The trace <c>sys</c> of every action record.</summary>
        public const string TraceSys = "action";

        /// <summary>The trace name of the audit record (<c>action/run</c>).</summary>
        public const string RunEvent = "run";

        /// <summary>The console columns of the audit line, in order.</summary>
        public const string ConsoleFields = "via,by,args,ms,detail";

        /// <summary>Longest argument value shown before it is cut.</summary>
        public const int MaxValueLength = 48;

        /// <summary>Longest detail shown before it is cut.</summary>
        public const int MaxDetailLength = 160;

        /// <summary>The arguments as the audit line shows them, redacted (see the class remarks).</summary>
        /// <param name="descriptor">The action, or null when no action has the name (then every value is redacted).</param>
        /// <param name="args">The arguments; null means none.</param>
        /// <returns><c>title=Rain volume=0.5</c>; empty for no arguments.</returns>
        public static string RedactArgs(ActionDescriptor? descriptor, ActionArgs? args)
        {
            if (args is null || args.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var pair in args)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(Cut(pair.Key, 32)).Append('=');
                if (pair.Value is null)
                {
                    sb.Append("null");
                    continue;
                }

                var text = args.GetString(pair.Key) ?? string.Empty;
                var parameter = descriptor?.FindParameter(pair.Key);
                if (IsShown(descriptor, parameter))
                {
                    sb.Append(Cut(text, MaxValueLength));
                }
                else
                {
                    sb.Append(Private(text.Length));
                }
            }

            return sb.ToString();
        }

        /// <summary>The placeholder for a hidden value of <paramref name="length"/> characters: <c>&lt;private:12&gt;</c>.</summary>
        /// <param name="length">The value's length.</param>
        public static string Private(int length) => "<private:" + length.ToString(CultureInfo.InvariantCulture) + ">";

        /// <summary>
        /// The result message as the audit line shows it: a Write-tier handler's message becomes
        /// <c>&lt;private:N&gt;</c>; anything else is cut to <see cref="MaxDetailLength"/>.
        /// </summary>
        /// <param name="tier">The action's tier, or null.</param>
        /// <param name="message">The message.</param>
        /// <param name="fromHandler">True when the handler wrote it (false for the runner's own refusals).</param>
        public static string RedactDetail(ActionTier? tier, string? message, bool fromHandler)
        {
            var text = message ?? string.Empty;
            if (fromHandler && tier == ActionTier.Write && text.Length > 0)
            {
                return Private(text.Length);
            }

            return Cut(text, MaxDetailLength);
        }

        /// <summary>True when the record belongs on the console (everything except a Read call that worked).</summary>
        /// <param name="record">The record.</param>
        public static bool ShowsOnConsole(ActionAuditRecord record) =>
            record != null && !(record.Status == ActionStatus.Ok && record.Tier == ActionTier.Read && !record.Late);

        /// <summary>The console level: Warn for Failed, TimedOut and late records, Info otherwise.</summary>
        /// <param name="record">The record.</param>
        public static ConsoleLevel Level(ActionAuditRecord record) =>
            record.Late || record.Status == ActionStatus.Failed || record.Status == ActionStatus.TimedOut
                ? ConsoleLevel.Warn
                : ConsoleLevel.Info;

        /// <summary>The primary column: <c>music.play ok</c>, <c>todo.add denied</c>, <c>music.import ok (late)</c>.</summary>
        /// <param name="record">The record.</param>
        public static string Primary(ActionAuditRecord record) =>
            Cut(record.Name, ActionDescriptor.MaxNameLength) + " " + Word(record.Status) + (record.Late ? " (late)" : string.Empty);

        /// <summary>The payload of the record: the console columns first, then the rest for the trace file.</summary>
        /// <param name="record">The record.</param>
        public static (string Key, object? Value)[] Fields(ActionAuditRecord record) => new (string Key, object? Value)[]
        {
            ("via", record.Path == ActionPath.None ? null : Word(record.Path)),
            ("by", Word(record.Caller) + ":" + record.CallerId),
            ("args", record.Args.Length == 0 ? null : record.Args),
            ("ms", Math.Round(record.Milliseconds, 1).ToString("0.#", CultureInfo.InvariantCulture)),
            ("detail", record.Detail.Length == 0 ? null : record.Detail),
            ("action", record.Name),
            ("tier", record.Tier.HasValue ? Word(record.Tier.Value) : null),
            ("caller", Word(record.Caller)),
            ("callerId", record.CallerId),
            ("request", record.RequestId),
            ("status", Word(record.Status)),
            ("path", Word(record.Path)),
            ("confirmed", record.Confirmed),
            ("late", record.Late),
        };

        /// <summary>
        /// Publishes the record on the trace bus: a console line or, for a Read call that worked, a
        /// trace-file record. Safe from any thread (the bus is).
        /// </summary>
        /// <param name="record">The record; null is ignored.</param>
        public static void Publish(ActionAuditRecord? record)
        {
            if (record is null)
            {
                return;
            }

            var fields = Fields(record);
            if (!ShowsOnConsole(record))
            {
                ConsoleEvents.File(TraceSys, RunEvent, fields);
                return;
            }

            var level = Level(record);
            if (level == ConsoleLevel.Info)
            {
                ConsoleEvents.Emit(TraceSys, RunEvent, ConsoleTags.Action, Primary(record), ConsoleFields, fields);
            }
            else
            {
                ConsoleEvents.EmitAt(TraceSys, RunEvent, ConsoleTags.Action, level, Primary(record), ConsoleFields, fields);
            }
        }

        /// <summary>The lower-case word for an enum value in the audit line (<c>ok</c>, <c>uiclick</c>, <c>api</c>).</summary>
        /// <param name="value">The value.</param>
        public static string Word(Enum value) => value.ToString().ToLowerInvariant();

        private static bool IsShown(ActionDescriptor? descriptor, ActionParameter? parameter)
        {
            if (descriptor is null || parameter is null || parameter.IsPrivate)
            {
                return false;
            }

            return !(descriptor.Tier == ActionTier.Write && parameter.Kind == ActionValueKind.String);
        }

        private static string Cut(string text, int limit) =>
            text.Length <= limit ? text : text.Substring(0, limit) + "…";
    }
}
