using System;
using System.IO;
using CwyLs.ModLoader.Core.Trace;

namespace CWY.Seamcheck
{
    /// <summary>Command-line entry point; see <see cref="SeamcheckOptions.Usage"/>.</summary>
    internal static class Program
    {
        /// <summary>Exit code for a tool failure, as opposed to a missing target (1).</summary>
        internal const int ToolError = 2;

        private static int Main(string[] args)
        {
            SeamcheckOptions options;
            try
            {
                options = SeamcheckOptions.Parse(args, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            }
            catch (SeamcheckException ex)
            {
                Console.Error.WriteLine("seamcheck: " + ex.Message);
                return ToolError;
            }

            if (options.ShowHelp)
            {
                Console.Out.Write(SeamcheckOptions.Usage);
                return 0;
            }

            var log = new ToolLog(Console.Error, options.Verbose);

            // The shared Trace code reports through TraceDiagnostics; route it here so nothing it
            // catches disappears (it is the in-game trace's debug channel).
            TraceDiagnostics.UseSink((message, error) =>
                log.Debug(error is null ? message : message + ": " + error.GetType().Name + ": " + error.Message));

            try
            {
                var toolName = typeof(Program).Assembly.GetName().Name ?? "seamcheck";
                var result = SeamcheckRunner.Run(options, AppContext.BaseDirectory, new[] { toolName }, log);
                ReportWriter.WriteText(result, Console.Out);
                if (options.JsonPath != null)
                {
                    ReportWriter.WriteJson(result, options.JsonPath);
                    Console.Out.WriteLine("seamcheck: wrote " + Path.GetFullPath(options.JsonPath));
                }

                return result.ExitCode;
            }
            catch (Exception ex)
            {
                // Anything unexpected is a tool failure, never a silent "0".
                log.Error(ex.ToString());
                return ToolError;
            }
        }
    }
}
