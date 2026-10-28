using System;
using System.Text;

namespace CwyLs.ModLoader.Core.Smoke
{
    /// <summary>
    /// Serialises a <see cref="SmokeReport"/> to the compact JSON written to
    /// <c>BepInEx/smoke-report.json</c>, using <see cref="MiniJson"/> so no game or
    /// third-party assembly is needed (see MiniJson for why).
    /// </summary>
    public static class SmokeReportWriter
    {
        /// <summary>
        /// The report as one line of JSON:
        /// <c>{"session","ticket","unity","game","time","ok","passed","total","detail","checks":[{"id","ok","informational","detail","elapsedMs"}]}</c>.
        /// <para>
        /// <c>informational</c> marks a failure that only means "not exercised yet this session",
        /// so a viewer can colour it the way the runner logs it: a warning, not an error.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="report"/> is null.</exception>
        public static string ToJson(SmokeReport report)
        {
            if (report is null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            return MiniJson.Object(
                MiniJson.Member("session", MiniJson.String(report.Session)),
                MiniJson.Member("ticket", MiniJson.String(report.Ticket)),
                MiniJson.Member("unity", MiniJson.String(report.Unity)),
                MiniJson.Member("game", MiniJson.String(report.Game)),
                MiniJson.Member("time", MiniJson.String(MiniJson.Iso8601(report.Time))),
                MiniJson.Member("ok", MiniJson.Bool(report.Ok)),
                MiniJson.Member("passed", MiniJson.Number((long)report.Passed)),
                MiniJson.Member("total", MiniJson.Number((long)report.Total)),
                MiniJson.Member("detail", MiniJson.String(report.Detail)),
                MiniJson.Member("checks", Checks(report)));
        }

        // MiniJson composes objects only; the array is three lines here rather than a new
        // public helper on a shared type.
        private static string Checks(SmokeReport report)
        {
            var sb = new StringBuilder("[");
            for (var i = 0; i < report.Checks.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                var check = report.Checks[i];
                sb.Append(MiniJson.Object(
                    MiniJson.Member("id", MiniJson.String(check.Id)),
                    MiniJson.Member("ok", MiniJson.Bool(check.Ok)),
                    MiniJson.Member("informational", MiniJson.Bool(check.Informational)),
                    MiniJson.Member("detail", MiniJson.String(check.Detail)),
                    MiniJson.Member("elapsedMs", MiniJson.Number(check.ElapsedMs))));
            }

            return sb.Append(']').ToString();
        }
    }
}
