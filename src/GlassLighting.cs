using System;
using HarmonyLib;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class GlassLighting
    {
        static GlassLighting()
        {
            var bands = AccessTools.TypeByName("AsAboveSoBelow.ABBands");
            var lighting = bands?.Assembly.GetType("AsAboveSoBelow.SectionLayer_ABBelowLighting");
            var bandType = bands?.Assembly.GetType("AsAboveSoBelow.ABBandMap");
            if (lighting == null || bandType == null)
                return;

            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".glass-rgb");
            try
            {
                var source = AccessTools.DeclaredMethod(lighting, "SourceIndex",
                    new[] { typeof(Map), bandType, typeof(CellIndices), typeof(int) });
                if (source?.ReturnType != typeof(int) || !source.IsStatic)
                    throw new InvalidOperationException("AASB2 light-source index contract differs.");
                var patch = new HarmonyMethod(typeof(GlassLighting), nameof(UseProjectedSource));
                harmony.Patch(source, prefix: patch);
                if (!IntegrationStatus.Registered(source, harmony.Id, patch.method))
                    throw new InvalidOperationException("AASB2 light-source patch did not register.");
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Glass native RGB", "SectionLayer_ABBelowLighting.SourceIndex", error);
                Log.Warning("[AASB2 Extended] Projected light unavailable: " + error.Message);
            }
        }

        private static bool UseProjectedSource(Map __0, CellIndices __2, int __3, ref int __result)
        {
            if (!GlassVisualMap.TryLowerIndex(__0, __3, out int lower, view: true) ||
                !BandCompatibility.ViewPassable(__0, __2.IndexToCell(lower), out _))
                return true;
            __result = lower;
            return false;
        }
    }
}
