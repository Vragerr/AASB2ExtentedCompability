using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AASB2ExtendedCompatibility
{
    // Protect the final ordinary melee commit. Target selection and pathing are independent.
    [StaticConstructorOnStartup]
    internal static class VerticalMeleeGuard
    {
        static VerticalMeleeGuard()
        {
            if (!ModsConfig.IsActive("astryl.asabovesobelow2") || !BandCompatibility.PortsReady)
                return;
            var original = AccessTools.DeclaredMethod(typeof(Verb_MeleeAttack), "TryCastShot", Type.EmptyTypes);
            if (original?.ReturnType != typeof(bool) || original.IsStatic)
                return;
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".melee-commit");
            try
            {
                var patch = new HarmonyMethod(typeof(VerticalMeleeGuard), nameof(BeforeMeleeCommit)) { priority = Priority.First };
                harmony.Patch(original, prefix: patch);
                if (!IntegrationStatus.Registered(original, harmony.Id, patch.method))
                    throw new InvalidOperationException("Melee commit guard did not register.");
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Ordinary melee", "Verb_MeleeAttack.TryCastShot", error);
                Log.Warning("[AASB2 Extended] Ordinary melee commit guard unavailable: " + error.Message);
            }
        }

        private static bool BeforeMeleeCommit(Verb_MeleeAttack __instance, ref bool __result)
        {
            var source = __instance.Caster;
            var target = __instance.CurrentTarget.Thing;
            var map = source?.Map;
            if (map == null || target?.Map != map || !BandCompatibility.IsBanded(map) ||
                BandCompatibility.SameFloor(map, source.Position, target.Position))
                return true;
            __result = false;
            return false;
        }
    }
}
