using System;
using System.Collections.Generic;
using System.IO;
using CwyLs.ModLoader.Trace;

namespace CWY.Seamcheck
{
    /// <summary>One platform to check: which game build, where its assemblies are.</summary>
    /// <param name="Key"><c>mac</c> or <c>win</c>, as on the command line.</param>
    /// <param name="TracePlatform">The same platform in the Trace plugin's terms, for its exclusion table.</param>
    /// <param name="ManagedDirectory">The game's <c>Managed/</c> folder for that platform.</param>
    internal sealed record PlatformSpec(string Key, TracePlatforms TracePlatform, string ManagedDirectory);

    /// <summary>Parsed command line.</summary>
    /// <param name="Platforms">The platforms to check, in order.</param>
    /// <param name="JsonPath">Where to write the JSON report, or null.</param>
    /// <param name="Verbose">Whether debug diagnostics are printed.</param>
    /// <param name="ShowHelp">True when the user asked for help; nothing else is meaningful then.</param>
    internal sealed record SeamcheckOptions(
        IReadOnlyList<PlatformSpec> Platforms,
        string? JsonPath,
        bool Verbose,
        bool ShowHelp)
    {
        /// <summary>The usage text.</summary>
        internal const string Usage =
            "usage: seamcheck.sh [--platform mac|win|all] [--managed <dir>] [--json <out.json>] [--verbose]\n"
            + "\n"
            + "Resolves every patch target the CWY plugins register against a game's Managed/ assemblies,\n"
            + "without running the game. Exit code: 0 all ok, 1 a target is missing or a trace\n"
            + "static-constructor hazard is not excluded, 2 tool error.\n"
            + "\n"
            + "  --platform   mac (default), win, or all (both, plus a per-platform difference list)\n"
            + "  --managed    the Managed/ folder to read; only with a single platform. Defaults:\n"
            + "               mac  <Steam>/steamapps/common/Chill with You Lo-Fi Story/.../Data/Managed\n"
            + "               win  <repo>/_ref/win-1.17.3/Managed (a copy of a Windows install's Managed/)\n"
            + "  --json       also write the full result as JSON (schema cwy.seamcheck/1)\n"
            + "  --repo       repository root, for the win default (seamcheck.sh passes it)\n"
            + "  --verbose    print debug diagnostics to stderr\n";

        /// <summary>Folder of a copy of a Windows install's Managed/, relative to the repository root.</summary>
        internal const string WindowsReferenceFolder = "_ref/win-1.17.3/Managed";

        /// <summary>The macOS player's Managed/ folder under a Steam library root.</summary>
        internal const string MacManagedUnderSteam =
            "steamapps/common/Chill with You Lo-Fi Story/Chill With You.app/Contents/Resources/Data/Managed";

        /// <summary>Parses the command line.</summary>
        /// <param name="args">The arguments.</param>
        /// <param name="home">The user's home folder, for the macOS default.</param>
        /// <returns>The options.</returns>
        /// <exception cref="SeamcheckException">An unknown option, a missing value or a contradiction.</exception>
        internal static SeamcheckOptions Parse(IReadOnlyList<string> args, string home)
        {
            string platform = "mac";
            string? managed = null;
            string? json = null;
            string? repo = null;
            var verbose = false;

            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                        return new SeamcheckOptions(Array.Empty<PlatformSpec>(), null, false, ShowHelp: true);
                    case "--platform":
                        platform = Value(args, ref i, arg);
                        break;
                    case "--managed":
                        managed = Value(args, ref i, arg);
                        break;
                    case "--json":
                        json = Value(args, ref i, arg);
                        break;
                    case "--repo":
                        repo = Value(args, ref i, arg);
                        break;
                    case "--verbose":
                        verbose = true;
                        break;
                    default:
                        throw new SeamcheckException("unknown option '" + arg + "'\n" + Usage);
                }
            }

            var keys = platform switch
            {
                "mac" => new[] { "mac" },
                "win" => new[] { "win" },
                "all" => new[] { "mac", "win" },
                _ => throw new SeamcheckException("--platform must be mac, win or all, not '" + platform + "'"),
            };

            if (managed != null && keys.Length > 1)
            {
                throw new SeamcheckException("--managed names one folder; use it with --platform mac or win, not all");
            }

            var specs = new List<PlatformSpec>();
            foreach (var key in keys)
            {
                var directory = managed ?? DefaultManaged(key, home, repo);
                specs.Add(new PlatformSpec(key, key == "win" ? TracePlatforms.Windows : TracePlatforms.MacOS, directory));
            }

            return new SeamcheckOptions(specs, json, verbose, ShowHelp: false);
        }

        /// <summary>The default Managed/ folder for a platform.</summary>
        /// <param name="key"><c>mac</c> or <c>win</c>.</param>
        /// <param name="home">The user's home folder.</param>
        /// <param name="repo">The repository root, or null.</param>
        /// <exception cref="SeamcheckException">The win default is asked for without a repository root.</exception>
        internal static string DefaultManaged(string key, string home, string? repo)
        {
            if (key == "mac")
            {
                return Path.Combine(home, "Library/Application Support/Steam", MacManagedUnderSteam);
            }

            if (string.IsNullOrWhiteSpace(repo))
            {
                throw new SeamcheckException("--platform win needs --managed <dir>, or --repo <root> for " + WindowsReferenceFolder);
            }

            return Path.Combine(repo!, WindowsReferenceFolder);
        }

        private static string Value(IReadOnlyList<string> args, ref int i, string option)
        {
            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new SeamcheckException(option + " needs a value");
            }

            i++;
            return args[i];
        }
    }
}
