using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AASB2ExtendedCompatibility
{
    [StaticConstructorOnStartup]
    internal static class AnimaTreeLimit
    {
        private static readonly ThingDef anima;
        internal static bool Ready { get; }

        static AnimaTreeLimit()
        {
            if (!ModsConfig.RoyaltyActive || !ModsConfig.IsActive("astryl.asabovesobelow2"))
                return;
            anima = DefDatabase<ThingDef>.GetNamedSilentFail("Plant_TreeAnima");
            if (anima == null)
                return;
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".anima");
            try
            {
                var type = typeof(GenStep_SpecialTrees);
                var field = AccessTools.DeclaredField(type, "treeDef");
                var validate = AccessTools.DeclaredMethod(type, "CanSpawnAt", new[] { typeof(IntVec3), typeof(Map),
                    typeof(int), typeof(int), typeof(float), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) });
                var spawn = AccessTools.DeclaredMethod(type, "TrySpawnAt", new[] { typeof(IntVec3), typeof(Map),
                    typeof(float), typeof(Thing).MakeByRefType() });
                if (field?.FieldType != typeof(ThingDef) || validate?.ReturnType != typeof(bool) ||
                    spawn?.ReturnType != typeof(bool) ||
                    !PatchProcessor.GetOriginalInstructions(spawn).Exists(code => Equals(code.operand, field)))
                    throw new InvalidOperationException("Unexpected natural tree spawn contract.");
                var patch = new HarmonyMethod(typeof(AnimaTreeLimit), nameof(AllowNaturalSpawn));
                harmony.Patch(validate, prefix: patch);
                var spawnPatch = new HarmonyMethod(typeof(AnimaTreeLimit), nameof(BeforeSpawn));
                harmony.Patch(spawn, prefix: spawnPatch);
                Ready = IntegrationStatus.Registered(validate, harmony.Id, patch.method) &&
                    IntegrationStatus.Registered(spawn, harmony.Id, spawnPatch.method);
                if (!Ready)
                    throw new InvalidOperationException("Natural tree spawn guards did not register.");
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Anima tree limits", "CanSpawnAt / TrySpawnAt", error);
                Log.Warning("[AASB2 Extended] Anima tree limits unavailable: " + error.Message);
            }
        }

        private static bool BeforeSpawn(IntVec3 __0, Map __1, ThingDef ___treeDef, ref bool __result, ref Thing __3)
        {
            if (AllowNaturalSpawn(__0, __1, ___treeDef, ref __result))
                return true;
            __3 = null;
            return false;
        }

        private static bool AllowNaturalSpawn(IntVec3 __0, Map __1, ThingDef ___treeDef, ref bool __result)
        {
            var settings = CompatibilityStatus.FloorSettings;
            if (___treeDef != anima || settings == null ||
                (!settings.LimitAnimaTrees && !settings.AnimaSurfaceOnly) || __1 == null)
                return true;

            if ((!settings.AnimaSurfaceOnly || !BandCompatibility.IsBanded(__1) ||
                (BandCompatibility.TrySurface(__1, out var surface) && surface.Contains(__0))) &&
                (!settings.LimitAnimaTrees || __1.listerThings.ThingsOfDef(anima).Count < settings.MaxAnimaTrees))
                return true;

            __result = false;
            return false;
        }
    }
}
