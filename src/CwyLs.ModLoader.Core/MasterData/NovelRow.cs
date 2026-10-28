namespace CwyLs.ModLoader.Core.MasterData
{
    /// <summary>
    /// One row of <c>data/novel.json</c>: a single command in a scenario group - a line of
    /// dialogue, a motion change, a wait, the end of the group.
    /// <para>
    /// Mirrors <c>Bulbul.MasterData.NovelData</c>.
    /// The three enum-typed columns are
    /// held as the numbers the game stores, parsed by name or number against
    /// <see cref="MasterDataEnums"/>; the adapter casts them back to the game enums.
    /// </para>
    /// <para>
    /// The defaults are the neutral values the game's own rows use for a row that does nothing
    /// special (<c>"0"</c> string args, <c>-1</c> motions, look speed 1 s), taken from
    /// the game's own NovelMaster rows.
    /// </para>
    /// </summary>
    public sealed class NovelRow
    {
        /// <summary>
        /// Row id. Required. Not unique in the game's own data - 26 ids repeat - so an override
        /// only applies when the id names exactly one game row (see <c>MasterDataMerge</c>).
        /// </summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Id of the scenario group this row belongs to. Required.</summary>
        public string ScenarioGroupId { get; init; } = string.Empty;

        /// <summary>What the row does; a member of <see cref="MasterDataEnums.Command"/>. Required.</summary>
        public int Command { get; init; }

        /// <summary>
        /// First argument; meaning depends on <see cref="Command"/>. For <c>VoiceText</c> it is
        /// parsed as an int and <c>"1"</c> means "move her mouth" (<c>ScenarioReader</c>).
        /// </summary>
        public string Arg1 { get; init; } = "0";

        /// <summary>Second argument, numeric.</summary>
        public float Arg2 { get; init; }

        /// <summary>Third argument.</summary>
        public string Arg3 { get; init; } = "0";

        /// <summary>Fourth argument.</summary>
        public string Arg4 { get; init; } = "0";

        /// <summary>Who speaks; a member of <see cref="MasterDataEnums.Talker"/>.</summary>
        public int Talker { get; init; }

        /// <summary>Id of the localization row holding this line's text and voice; <c>"0"</c> for none.</summary>
        public string LocalizationId { get; init; } = "0";

        /// <summary>Body animation index, or -1 to leave it alone.</summary>
        public int BodyMotion { get; init; } = -1;

        /// <summary>Facial animation index, or -1 to leave it alone.</summary>
        public int FacialMotion { get; init; } = -1;

        /// <summary>How far she turns to look at the camera, 0..1.</summary>
        public float LookScale { get; init; }

        /// <summary>How long the look tween takes, in seconds.</summary>
        public float LookSpeedSeconds { get; init; } = 1f;

        /// <summary>Easing of the look tween; a member of <see cref="MasterDataEnums.Ease"/>.</summary>
        public int LookEaseType { get; init; }

        /// <summary><c>id</c> in <c>group</c>.</summary>
        public override string ToString() => Id + " in " + ScenarioGroupId;
    }
}
