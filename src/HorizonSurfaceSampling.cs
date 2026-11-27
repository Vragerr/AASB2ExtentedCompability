using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AASB2ExtendedCompatibility
{
    internal static class HorizonSurfaceSampling
    {
        private static MethodInfo developed;
        private static Func<Map, IntVec3, bool> isDeveloped;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Map, Domain> domains =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Map, Domain>();

        private sealed class Domain
        {
            internal CellRect Bounds;
            internal bool Valid;
        }

        internal static void Update(Map map, CellRect bounds)
        {
            var state = domains.GetOrCreateValue(map);
            state.Bounds = bounds;
            state.Valid = true;
        }

        internal static void Install(Assembly assembly)
        {
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".horizon-surface-samples");
            try
            {
                var target = assembly.GetType("EndlessHorizon.GenReconstruction");
                var compare = target == null ? null : AccessTools.DeclaredMethod(target, "CompareSteps", new[] { typeof(Map) });
                developed = target == null ? null : AccessTools.DeclaredMethod(target, "Developed", new[] { typeof(Map), typeof(IntVec3) });
                if (compare?.ReturnType != typeof(IEnumerable<bool>) || developed?.ReturnType != typeof(bool) || !developed.IsStatic)
                    throw new InvalidOperationException("Horizon comparison contract differs.");
                isDeveloped = developed.CreateDelegate<Func<Map, IntVec3, bool>>();
                var iterator = AccessTools.EnumeratorMoveNext(compare);
                if (iterator == null || iterator.ReturnType != typeof(bool))
                    throw new InvalidOperationException("Horizon comparison iterator missing.");
                var patch = new HarmonyMethod(typeof(HorizonSurfaceSampling), nameof(RestrictSamples));
                harmony.Patch(iterator, transpiler: patch);
                if (!IntegrationStatus.Registered(iterator, harmony.Id, patch.method))
                    throw new InvalidOperationException("Horizon comparison guard did not register.");
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Endless Horizon comparison", "CompareSteps", error);
                Log.Warning("[AASB2 Extended] Horizon surface sampling not applied: " + error.Message);
            }
        }

        private static bool SkipSample(Map map, IntVec3 cell) =>
            (domains.TryGetValue(map, out var domain) && domain.Valid && !domain.Bounds.Contains(cell)) ||
            isDeveloped(map, cell);

        private static IEnumerable<CodeInstruction> RestrictSamples(IEnumerable<CodeInstruction> input)
        {
            var instructions = new List<CodeInstruction>(input);
            int index = instructions.FindIndex(code => code.Calls(developed));
            if (index < 0 || index + 1 >= instructions.Count ||
                instructions.FindAll(code => code.Calls(developed)).Count != 1 ||
                (instructions[index + 1].opcode != System.Reflection.Emit.OpCodes.Brtrue &&
                 instructions[index + 1].opcode != System.Reflection.Emit.OpCodes.Brtrue_S))
                throw new InvalidOperationException("Horizon comparison no longer skips developed cells at this site.");
            instructions[index].operand = AccessTools.DeclaredMethod(typeof(HorizonSurfaceSampling), nameof(SkipSample));
            return instructions;
        }
    }
}
