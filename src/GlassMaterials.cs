using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class GlassMaterials
    {
        internal static readonly Material Deck;
        internal static readonly Material Skylight;

        static GlassMaterials()
        {
            Deck = MaterialPool.MatFrom("AASBExt/GlassDeck", ShaderDatabase.Transparent, 3000);
            Skylight = MaterialPool.MatFrom("AASBExt/GlassSkylight", ShaderDatabase.Transparent, 3000);
        }
    }
}
