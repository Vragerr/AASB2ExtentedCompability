using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace AASB2ExtendedCompatibility
{
    internal static class HorizonSurfaceSampling
    {
        private static MethodInfo developed;
        private static FieldInfo reconstruction;
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
                var generator = assembly.GetType("EndlessHorizon.GeneratorModule");
                var mountain = assembly.GetType("EndlessHorizon.MountainMask");
                var raw = mountain == null ? null : AccessTools.DeclaredMethod(mountain, "GridRawSample", new[] { typeof(IntVec3) });
                reconstruction = generator == null ? null : AccessTools.Field(generator, "Generator");
                var map = mountain == null ? null : AccessTools.Field(mountain, "map");
                var gen = mountain == null ? null : AccessTools.Field(mountain, "gen");
                if (raw?.ReturnType != typeof(int) || reconstruction?.FieldType != target ||
                    map?.FieldType != typeof(Map) || gen?.FieldType != generator)
                    throw new InvalidOperationException("Native mountain sample contract differs.");
                var edge = new HarmonyMethod(typeof(HorizonSurfaceSampling), nameof(KeepOpenMirrorEdge));
                harmony.Patch(raw, postfix: edge);
                var patch = new HarmonyMethod(typeof(HorizonSurfaceSampling), nameof(RestrictSamples));
                harmony.Patch(iterator, transpiler: patch);
                if (!IntegrationStatus.Registered(iterator, harmony.Id, patch.method) ||
                    !IntegrationStatus.Registered(raw, harmony.Id, edge.method))
                    throw new InvalidOperationException("Horizon sampling guards did not register.");
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Endless Horizon comparison", "CompareSteps", error);
                Log.Warning("[AASB2 Extended] Horizon surface sampling not applied: " + error.Message);
            }
        }

        internal static void InstallGeologicalScatter(Assembly assembly)
        {
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".horizon-geological-scatter");
            try
            {
                var rocks = assembly.GetType("EndlessHorizon.GenerationRocks");
                var snapshot = assembly.GetType("EndlessHorizon.GenerationSnapshot");
                var random = assembly.GetType("EndlessHorizon.GenerationRandom");
                var scatter = rocks == null || snapshot == null || random == null ? null :
                    AccessTools.DeclaredMethod(rocks, "Scatter", new[] { typeof(Map), snapshot, typeof(int), typeof(Func<ThingDef>), random });
                var iterator = scatter == null ? null : AccessTools.EnumeratorMoveNext(scatter);
                var building = AccessTools.Field(typeof(ThingDef), nameof(ThingDef.building));
                if (iterator == null || scatter.ReturnType != typeof(IEnumerable<bool>) ||
                    !PatchProcessor.GetOriginalInstructions(iterator).Exists(op => Equals(op.operand, building)))
                    throw new InvalidOperationException("Horizon geological scatter no longer reads the expected rock definition.");
                harmony.Patch(iterator, transpiler: new HarmonyMethod(typeof(HorizonSurfaceSampling), nameof(FilterScatterChoice)));
                if (!IntegrationStatus.Registered(iterator, harmony.Id))
                    throw new InvalidOperationException("Horizon geological scatter guard did not register.");
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Endless Horizon geological scatter", "GenerationRocks.Scatter", error);
                Log.Warning("[AASB2 Extended] Horizon geological scatter repair unavailable: " + error.GetBaseException().Message);
            }
        }

        private static IEnumerable<CodeInstruction> FilterScatterChoice(IEnumerable<CodeInstruction> input)
        {
            var body = new List<CodeInstruction>(input);
            var choice = AccessTools.Method(typeof(Func<ThingDef>), "Invoke");
            int index = body.FindIndex(op => op.Calls(choice));
            if (index < 0 || body.FindAll(op => op.Calls(choice)).Count != 1)
                throw new InvalidOperationException("Horizon scatter choice no longer has a unique source.");
            body.Insert(index + 1, new CodeInstruction(OpCodes.Call,
                AccessTools.DeclaredMethod(typeof(HorizonSurfaceSampling), nameof(ValidRock))));
            return body;
        }

        // The iterator's existing null branch skips a single invalid selection.
        private static ThingDef ValidRock(ThingDef def) => def?.building == null ? null : def;

        private static void KeepOpenMirrorEdge(Map ___map, object ___gen, IntVec3 __0, ref int __result)
        {
            if (__result < 0 || ___gen == null || !domains.TryGetValue(___map, out var domain) || !domain.Valid ||
                !HorizonSurfaceRegion.TryEdgeSample(domain.Bounds, __0, out var edge) ||
                reconstruction.GetValue(___gen) != null)
                return;
            // Restrict only the mirrored mountain mask. Terrain, flora, roofs and snow
            // still read their native field samples, including in the generator branch.
            var rock = edge.GetEdifice(___map);
            if (rock?.def.mineable != true && ___map.roofGrid.RoofAt(edge)?.isThickRoof != true)
                __result = -1;
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
                (instructions[index + 1].opcode != OpCodes.Brtrue &&
                 instructions[index + 1].opcode != OpCodes.Brtrue_S))
                throw new InvalidOperationException("Horizon comparison no longer skips developed cells at this site.");
            instructions[index].operand = AccessTools.DeclaredMethod(typeof(HorizonSurfaceSampling), nameof(SkipSample));
            return instructions;
        }
    }
}
