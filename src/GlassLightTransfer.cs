using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class GlassLightTransfer
    {
        internal static bool Ready { get; }
        [ThreadStatic] private static bool samplingSource, invalidating;
        private static Func<GlowGrid, IntVec3, bool, Color32> sourceGlow;
        internal static bool Enabled => CompatibilityStatus.FloorSettings?.EnableGlassLight != false;
        private const float Transmission = 0.9f;

        static GlassLightTransfer()
        {
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".glass-light-transfer");
            try
            {
                var raw = AccessTools.DeclaredMethod(typeof(GlowGrid), "GetAccumulatedGlowAt", new[] { typeof(int), typeof(bool) });
                var ground = AccessTools.DeclaredMethod(typeof(GlowGrid), nameof(GlowGrid.GroundGlowAt),
                    new[] { typeof(IntVec3), typeof(bool), typeof(bool) });
                var field = AccessTools.DeclaredField(typeof(GlowGrid), "map");
                if (raw?.ReturnType != typeof(Color32) || ground?.ReturnType != typeof(float) ||
                    field?.FieldType != typeof(Map) || !PatchProcessor.GetOriginalInstructions(ground)
                        .Exists(op => op.operand is System.Reflection.MethodInfo method && method.Name == raw.Name))
                    throw new InvalidOperationException("Unexpected GlowGrid sample contract.");

                var byCell = AccessTools.DeclaredMethod(typeof(GlowGrid), raw.Name, new[] { typeof(IntVec3), typeof(bool) });
                if (byCell?.ReturnType != typeof(Color32))
                    throw new InvalidOperationException("Unexpected GlowGrid cell sample contract.");
                sourceGlow = byCell.CreateDelegate<Func<GlowGrid, IntVec3, bool, Color32>>();
                harmony.Patch(raw, postfix: After(nameof(TransferArtificial)));
                harmony.Patch(ground, postfix: After(nameof(TransferDaylight)));
                if (!IntegrationStatus.Registered(raw, harmony.Id) || !IntegrationStatus.Registered(ground, harmony.Id))
                    throw new InvalidOperationException("GlowGrid transfer patches did not register.");
                Ready = true;
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Glass light transfer", "GlowGrid", error);
                Log.Warning("[AASB2 Extended] Glass light transfer unavailable: " + error.Message);
            }
        }

        internal static void InvalidateBelow(Map map, IntVec3 upper, bool apertureChanged)
        {
            if (!Ready || invalidating || !BandCompatibility.TryAdjacent(map, upper, false, out var lower) ||
                !lower.InBounds(map) ||
                (!apertureChanged && (!BandCompatibility.TryLightOpening(map, lower, out var source) || source != upper)))
                return;
            invalidating = true;
            try { map.mapDrawer.MapMeshDirty(lower, MapMeshFlagDefOf.GroundGlow, true, false); }
            finally { invalidating = false; }
        }

        private static HarmonyMethod After(string name) => new HarmonyMethod(typeof(GlassLightTransfer), name)
        {
            priority = Priority.Last
        };

        private static void TransferArtificial(GlowGrid __instance, Map ___map, int __0, bool __1, ref Color32 __result)
        {
            if (!Enabled || samplingSource || !GlassVisualMap.TryUpperIndex(___map, __0, out int upperIndex))
                return;
            var upper = ___map.cellIndices.IndexToCell(upperIndex);
            if (!BandCompatibility.LightPassable(___map, ___map.cellIndices.IndexToCell(__0), upper))
                return;

            samplingSource = true;
            try
            {
                var source = sourceGlow(__instance, upper, __1);
                __result = new Color32(Mix(__result.r, source.r), Mix(__result.g, source.g),
                    Mix(__result.b, source.b), source.a == 1 ? (byte)1 : __result.a);
            }
            finally { samplingSource = false; }
        }

        private static byte Mix(byte existing, byte incoming) =>
            Math.Max(existing, (byte)Mathf.RoundToInt(incoming * Transmission));

        private static void TransferDaylight(Map ___map, IntVec3 __0, bool __2, ref float __result)
        {
            if (!Enabled || samplingSource || __2 || __result >= Transmission ||
                !BandCompatibility.TryLightOpening(___map, __0, out var upper) ||
                ___map.roofGrid.Roofed(upper))
                return;
            __result = Mathf.Max(__result, ___map.skyManager.CurSkyGlow * Transmission);
        }
    }
}
