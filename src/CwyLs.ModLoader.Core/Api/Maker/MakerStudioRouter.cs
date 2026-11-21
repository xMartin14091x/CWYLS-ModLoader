namespace CwyLs.ModLoader.Core.Api.Maker
{
    /// <summary>The Studio v2 route boundary. M2 exposes capability discovery; feature routes arrive with their backends.</summary>
    public static class MakerStudioRouter
    {
        public const string Segment = "studio";
        public const string CapabilitiesSegment = "capabilities";
        public const string StatusSegment = "status";

        /// <summary>Serialises only installed features. The caller has already enforced the Maker security pipeline.</summary>
        public static ApiResponse Capabilities(MakerStudioBackends backends) =>
            ApiResponse.Json(
                200,
                ApiJson.Write(MakerStudioJson.Capabilities(new MakerStudioCapabilities { Features = backends.Features })));
    }
}
