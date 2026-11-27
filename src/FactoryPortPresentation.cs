using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    internal static class FactoryPortPresentation
    {
        private static MethodInfo baseDraw;
        private static FieldInfo nativeOverlay;
        private static readonly HashSet<MethodInfo> TunnelActions = new HashSet<MethodInfo>();
        internal static bool GizmosInstalled { get; private set; }
        internal static bool OverlayInstalled { get; private set; }

        internal static void Install(Type conveyor, Type tunnel)
        {
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".factory-ui");
            GizmosInstalled = Patch(harmony, tunnel, "GetGizmos", typeof(IEnumerable<Gizmo>), Type.EmptyTypes, nameof(Gizmos));
            var args = new[] { typeof(Vector3), typeof(bool) };
            baseDraw = AccessTools.DeclaredMethod(conveyor, "DrawAt", args);
            var cache = AccessTools.TypeByName("VanillaFurnitureExpandedFactory.GraphicsCache");
            nativeOverlay = cache == null ? null : AccessTools.DeclaredField(cache, "OverlayNoLink");
            OverlayInstalled = Patch(harmony, tunnel, "DrawAt", typeof(void), args, nameof(Draw), true);
        }

        private static bool Patch(Harmony harmony, Type type, string target, Type returns, Type[] args,
            string patch, bool transpiler = false)
        {
            try
            {
                var method = AccessTools.DeclaredMethod(type, target, args);
                if (method?.ReturnType != returns || method.IsStatic)
                    throw new InvalidOperationException("Missing or incompatible Factory " + target);
                if (!transpiler)
                    ReadNativeActions(method, type);
                var implementation = new HarmonyMethod(typeof(FactoryPortPresentation), patch);
                harmony.Patch(method, postfix: transpiler ? null : implementation,
                    transpiler: transpiler ? implementation : null);
                var patches = Harmony.GetPatchInfo(method);
                var entries = transpiler ? patches?.Transpilers : patches?.Postfixes;
                if (entries == null || !System.Linq.Enumerable.Any(entries,
                    entry => entry.owner == harmony.Id && entry.PatchMethod == implementation.method))
                    throw new InvalidOperationException("Harmony did not register " + patch);
                return true;
            }
            catch (Exception error)
            {
                IntegrationStatus.NoteFailure("Factory presentation", target, error);
                Log.Warning("[AASB2 Extended] Factory " + target + " adaptation skipped: " + error.Message);
                return false;
            }
        }

        private static void ReadNativeActions(MethodInfo original, Type tunnel)
        {
            var iterator = AccessTools.EnumeratorMoveNext(original);
            if (iterator == null)
                throw new InvalidOperationException("Factory gizmo iterator missing.");
            var body = PatchProcessor.GetOriginalInstructions(iterator);
            var actionField = AccessTools.DeclaredField(typeof(Command_Action), nameof(Command_Action.action));
            var actions = new HashSet<MethodInfo>();
            foreach (string key in new[] { "VFEFactory_CommandCreateLink", "VFEFactory_CommandBreakLink", "VFEFactory_CommandSelectLinked" })
            {
                int label = body.FindIndex(op => op.opcode == OpCodes.Ldstr && Equals(op.operand, key));
                if (label < 0)
                    throw new InvalidOperationException("Factory gizmo token missing: " + key);
                MethodInfo action = null;
                for (int index = label + 1; index < Math.Min(body.Count, label + 72); index++)
                {
                    var op = body[index];
                    if (op.opcode != OpCodes.Ldftn && op.opcode != OpCodes.Ldvirtftn)
                        continue;
                    if (index + 2 < body.Count && body[index + 2].StoresField(actionField))
                        action = op.operand as MethodInfo;
                    break;
                }
                if (action == null || action.ReturnType != typeof(void) || action.GetParameters().Length != 0 ||
                    (action.DeclaringType != tunnel && action.DeclaringType?.DeclaringType != tunnel) || !actions.Add(action))
                    throw new InvalidOperationException("Factory gizmo action mismatch: " + key);
            }
            TunnelActions.UnionWith(actions);
        }

        private static void Gizmos(Thing __instance, ref IEnumerable<Gizmo> __result)
        {
            if (__result != null && __instance.def.HasComp(typeof(CompVerticalFactoryPort)))
                __result = WithoutTunnelActions(__result);
        }

        private static IEnumerable<Gizmo> WithoutTunnelActions(IEnumerable<Gizmo> commands)
        {
            foreach (var gizmo in commands)
                if (!(gizmo is Command_Action action) || action.action == null ||
                    !TunnelActions.Contains(action.action.Method))
                    yield return gizmo;
        }

        private static bool IsVertical(Thing thing) => thing.def.HasComp(typeof(CompVerticalFactoryPort));

        private static bool WarnUnlinked(Thing thing) =>
            (thing as ThingWithComps)?.GetComp<CompVerticalFactoryPort>()?.WarnUnlinked() == true;

        private static IEnumerable<CodeInstruction> Draw(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var body = new List<CodeInstruction>(instructions);
            if (baseDraw == null || nativeOverlay == null ||
                body.FindAll(op => op.Calls(baseDraw)).Count != 1 ||
                body.FindAll(op => op.LoadsField(nativeOverlay)).Count != 1 ||
                body.FindAll(op => op.opcode == OpCodes.Ret).Count != 1)
                throw new InvalidOperationException("Unexpected Factory overlay IL.");
            int draw = body.FindIndex(op => op.Calls(baseDraw));
            int overlay = body.FindIndex(op => op.LoadsField(nativeOverlay));
            int end = body.FindIndex(op => op.opcode == OpCodes.Ret);
            if (draw >= overlay || overlay >= end)
                throw new InvalidOperationException("Unexpected Factory overlay order.");
            var drawPos = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos));
            int position = body.FindIndex(draw + 1, op => op.Calls(drawPos));
            if (position <= draw + 2 || position >= overlay || body[position - 1].opcode != OpCodes.Ldarg_0)
                throw new InvalidOperationException("Unexpected Factory warning start.");
            var skip = generator.DefineLabel();
            var warning = generator.DefineLabel();
            var normal = generator.DefineLabel();
            body[end].labels.Add(skip);
            body[position - 1].labels.Add(warning);
            body[draw + 1].labels.Add(normal);
            body.InsertRange(draw + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(FactoryPortPresentation), nameof(IsVertical))),
                new CodeInstruction(OpCodes.Brfalse, normal),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(FactoryPortPresentation), nameof(WarnUnlinked))),
                new CodeInstruction(OpCodes.Brtrue, warning),
                new CodeInstruction(OpCodes.Br, skip)
            });
            return body;
        }
    }
}
