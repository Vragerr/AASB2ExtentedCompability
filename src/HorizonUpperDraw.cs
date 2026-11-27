using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    internal static class HorizonUpperDraw
    {
        [ThreadStatic] private static int offsetX;
        [ThreadStatic] private static int offsetZ;
        [ThreadStatic] private static float depth;

        internal static T AtOffset<T>(int x, int z, int floor, Func<T> draw)
        {
            int previousX = offsetX, previousZ = offsetZ;
            float previousDepth = depth;
            depth = -0.12f * Math.Min(floor, 6);
            offsetX = x;
            offsetZ = z;
            try { return draw(); }
            finally { offsetX = previousX; offsetZ = previousZ; depth = previousDepth; }
        }

        internal static bool Install(Assembly assembly)
        {
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".horizon-upper");
            string[] types = { "HorizonState", "HorizonChunk", "HorizonThingsChunk", "HorizonFog",
                "HorizonLighting", "RingDarkness", "ExtensionRing" };
            string[] names = { "DrawRing", "DrawLayer", "Draw", "Draw", "Draw", "Draw", "DrawStrip" };
            Type[] returns = { typeof(void), typeof(void), typeof(int), typeof(void), typeof(void), typeof(int), typeof(int) };
            int[] drawCounts = { 2, 1, 1, 1, 1, 2, 1 };
            try
            {
                for (int i = 0; i < types.Length; i++)
                {
                    var type = assembly.GetType("EndlessHorizon." + types[i]);
                    var method = type == null ? null : AccessTools.DeclaredMethod(type, names[i]);
                    if (method?.ReturnType != returns[i] ||
                        FindMatrices(PatchProcessor.GetOriginalInstructions(method)).Count != drawCounts[i])
                        throw new InvalidOperationException("Renderer IL differs: " + types[i] + "." + names[i]);
                    var patch = new HarmonyMethod(typeof(HorizonUpperDraw), nameof(TranslateMatrices));
                    harmony.Patch(method, transpiler: patch);
                    if (!IntegrationStatus.Registered(method, harmony.Id, patch.method))
                        throw new InvalidOperationException("Renderer patch did not register: " + types[i]);
                }
                return true;
            }
            catch (Exception error)
            {
                harmony.UnpatchAll(harmony.Id);
                IntegrationStatus.NoteFailure("Endless Horizon upper rendering", "DrawRing", error);
                Log.Warning("[AASB2 Extended] Upper horizon rendering disabled: " + error.Message);
                return false;
            }
        }

        private static List<CodeInstruction> FindMatrices(List<CodeInstruction> body)
        {
            var matrices = new List<CodeInstruction>();
            var identity = AccessTools.PropertyGetter(typeof(Matrix4x4), nameof(Matrix4x4.identity));
            for (int i = 0; i < body.Count; i++)
            {
                var op = body[i];
                if (!(op.operand is MethodInfo draw) || draw.DeclaringType != typeof(Graphics) ||
                    draw.Name != nameof(Graphics.DrawMesh))
                    continue;
                var args = draw.GetParameters();
                if (args.Length < 2 || args[1].ParameterType != typeof(Matrix4x4))
                    throw new InvalidOperationException("Unexpected Horizon mesh transform signature.");
                int j = i - 1;
                while (j >= 0 && i - j <= args.Length + 6 && !body[j].Calls(identity))
                    j--;
                if (j < 0 || !body[j].Calls(identity) || i - j > args.Length + 6 || matrices.Contains(body[j]))
                    throw new InvalidOperationException("No unique matrix load for Horizon mesh.");
                matrices.Add(body[j]);
            }
            return matrices;
        }

        private static IEnumerable<CodeInstruction> TranslateMatrices(IEnumerable<CodeInstruction> instructions)
        {
            var body = new List<CodeInstruction>(instructions);
            var replacement = AccessTools.DeclaredMethod(typeof(HorizonUpperDraw), nameof(WorldMatrix));
            foreach (var op in FindMatrices(body))
                op.operand = replacement;
            return body;
        }

        private static Matrix4x4 WorldMatrix() => offsetX == 0 && offsetZ == 0
            ? Matrix4x4.identity : Matrix4x4.Translate(new Vector3(offsetX, depth, offsetZ));
    }
}
