using System.Collections.Generic;
using CwyLs.ModLoader.Core;

namespace CwyLs.ModLoader.Bulbul.Subtitles
{
    /// <summary>
    /// The seams the text-box subtitle feature patches, as patch-target data, so a game update that
    /// moves one is reported at startup instead of failing quietly later.
    /// </summary>
    /// <remarks>
    /// Every signature below was checked against the shipped v1.17.2 game:
    /// <c>Bulbul.LocalizationMasterWrapper.Get</c>, <c>Bulbul.ScenarioTextMessage.StartText</c> and
    /// <c>Bulbul.ScenarioReader</c>'s <c>AutoNext</c> and <c>_currentNovel</c>.
    /// </remarks>
    public static class SubtitlePatchTargets
    {
        /// <summary>Assembly every target below lives in.</summary>
        public const string GameAssembly = "Assembly-CSharp";

        /// <summary>The type that turns a master-data row into the text the game displays.</summary>
        public const string LocalizationWrapperTypeName = "Bulbul.LocalizationMasterWrapper";

        /// <summary>The component that owns the story text box's TextMeshPro label.</summary>
        public const string TextMessageTypeName = "Bulbul.ScenarioTextMessage";

        /// <summary>The scenario reader, whose <c>AutoNext</c> decides how long a line stays up.</summary>
        public const string ScenarioReaderTypeName = "Bulbul.ScenarioReader";

        /// <summary>The method the text seam postfixes.</summary>
        public const string GetMethodName = "Get";

        /// <summary>The method that tells us a line really reached the game's text box.</summary>
        public const string StartTextMethodName = "StartText";

        /// <summary>The method whose wait the reading-time hold raises.</summary>
        public const string AutoNextMethodName = "AutoNext";

        /// <summary>The reader's field holding the row being read.</summary>
        public const string CurrentNovelFieldName = "_currentNovel";

        /// <summary>The seams, in the order they are described in the README.</summary>
        public static IReadOnlyList<PatchTarget> All { get; } = new[]
        {
            // Every localized string in the game funnels through this one overload: Get(data),
            // TryGet(...) and both Bind(...) overloads all call it.
            new PatchTarget(
                "subtitles.localization.get", GameAssembly, LocalizationWrapperTypeName,
                MemberKind.Method, GetMethodName,
                new[] { "Bulbul.MasterData.LocalizationData", "Bulbul.GameLanguageType" },
                expectedSignature: "string Get(LocalizationData, GameLanguageType)"),

            // ScenarioReader.CommandVoiceText hands the line to the current text message's
            // StartText: what the box shows, and since when.
            new PatchTarget(
                "subtitles.textmessage.starttext", GameAssembly, TextMessageTypeName,
                MemberKind.Method, StartTextMethodName,
                new[] { "System.String" },
                expectedSignature: "void StartText(string)"),

            // The label the text lands on, and the one p2.subtitles.native reads back.
            new PatchTarget(
                "subtitles.textmessage.label", GameAssembly, TextMessageTypeName,
                MemberKind.Field, "_textMessage",
                expectedSignature: "TextMeshProUGUI _textMessage"),

            // The wait before the next row; the reading-time hold raises it.
            new PatchTarget(
                "subtitles.scenario.autonext", GameAssembly, ScenarioReaderTypeName,
                MemberKind.Method, AutoNextMethodName,
                new[] { "System.Single" },
                expectedSignature: "UniTask AutoNext(float)"),

            // Which row AutoNext is finishing (its command, group and id).
            new PatchTarget(
                "subtitles.scenario.currentnovel", GameAssembly, ScenarioReaderTypeName,
                MemberKind.Field, CurrentNovelFieldName,
                expectedSignature: "NovelData _currentNovel"),
        };
    }
}
