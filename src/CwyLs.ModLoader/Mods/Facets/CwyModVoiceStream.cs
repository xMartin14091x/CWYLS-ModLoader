namespace CwyLs.ModLoader.Mods
{
    /// <summary>
    /// VoiceStream: Satone speaking audio that arrives in chunks (for example from a speech synthesizer), with mouth movement and
    /// per-chunk text. A slot until a voice-stream backend is installed with
    /// <c>ModFacetBackends.Use&lt;ICwyModVoiceStream&gt;</c> and members are added here; only the framework
    /// implements this interface. Whole clips already work through <c>Satone.SayWithClip</c>.
    /// </summary>
    public interface ICwyModVoiceStream : ICwyModFacet
    {
    }

    /// <summary>The per-mod VoiceStream facet.</summary>
    internal sealed class ModVoiceStream : ModFacetProxy<ICwyModVoiceStream>, ICwyModVoiceStream
    {
        /// <summary>The status without a backend.</summary>
        internal const string NotAvailable = "streamed voice is not available: no voice-stream backend is installed";

        internal ModVoiceStream(ModHelper helper)
            : base(helper, "voiceStream", NotAvailable)
        {
        }
    }
}
