using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    // Scope only our projected submissions. Normal map draws enter the original method untouched.
    [StaticConstructorOnStartup]
    internal static class GlassDynamic
    {
        [ThreadStatic] private static GlassDrawBuffers current;
        internal static bool Ready { get; private set; }

        static GlassDynamic()
        {
            var method = AccessTools.DeclaredMethod(typeof(Graphics), "DrawMeshCore", new[]
            {
                typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int), typeof(int),
                typeof(MaterialPropertyBlock), typeof(int)
            });
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".glass-dynamic");
            try
            {
                if (method?.ReturnType != typeof(void) || !method.IsStatic)
                    throw new InvalidOperationException("Waylith DrawMeshCore signature differs.");
                var patch = new HarmonyMethod(typeof(GlassDynamic), nameof(ClipSubmission));
                harmony.Patch(method, prefix: patch);
                Ready = IntegrationStatus.Registered(method, harmony.Id, patch.method);
                if (Ready)
                    InstallDirectSubmissions();
            }
            catch (Exception error)
            {
                IntegrationStatus.NoteFailure("Glass dynamic", "DrawMeshCore", error);
                Log.Warning("[AASB2 Extended] Glass dynamic projection unavailable: " + error.Message);
            }
        }

        private static void InstallDirectSubmissions()
        {
            var harmony = new Harmony(CompatibilityStatus.HarmonyId + ".glass-direct");
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(Graphics)))
            {
                if (method.Name != nameof(Graphics.DrawMesh) || !method.IsStatic || method.ReturnType != typeof(void))
                    continue;
                var parameters = method.GetParameters();
                if (parameters.Length < 3 || parameters[0].ParameterType != typeof(Mesh) ||
                    parameters[1].ParameterType != typeof(Matrix4x4))
                    continue;
                try
                {
                    var body = PatchProcessor.GetOriginalInstructions(method);
                    if (!body.Exists(code => code.operand is MethodInfo call &&
                        call.Name == "Enqueue" && call.DeclaringType?.FullName == "Waylith.Rendering.MeshDrawSubmission"))
                        continue;
                    int part = Array.FindIndex(parameters, argument => argument.Name == "submeshIndex");
                    string hook = part == -1 ? nameof(ClipDirect) : part == 5 && parameters[part].ParameterType == typeof(int)
                        ? nameof(ClipDirectPart) : null;
                    if (hook == null)
                        continue;
                    var patch = new HarmonyMethod(typeof(GlassDynamic), hook);
                    harmony.Patch(method, prefix: patch);
                    if (!IntegrationStatus.Registered(method, harmony.Id, patch.method))
                        throw new InvalidOperationException("Direct submission hook did not register.");
                }
                catch (Exception error)
                {
                    IntegrationStatus.NoteFailure("Glass direct submissions", method.ToString(), error);
                }
            }
        }

        private static bool ClipDirect(ref Mesh __0, ref Matrix4x4 __1)
        {
            int part = 0;
            return ClipSubmission(ref __0, ref __1, ref part);
        }

        private static bool ClipDirectPart(ref Mesh __0, ref Matrix4x4 __1, ref int __5) =>
            ClipSubmission(ref __0, ref __1, ref __5);

        internal static void Draw(GlassDrawBuffers buffers, Thing thing)
        {
            if (current != null)
                return;
            current = buffers;
            try
            {
                if (FactoryGlass.TryDraw(thing))
                    return;
                if (thing is Pawn pawn)
                {
                    var position = pawn.DrawPos;
                    pawn.DynamicDrawPhaseAt((DrawPhase)0, position, false);
                    pawn.DynamicDrawPhaseAt((DrawPhase)1, position, false);
                    pawn.DynamicDrawPhaseAt((DrawPhase)2, position, false);
                }
                else if (thing.def.drawerType == DrawerType.RealtimeOnly)
                    thing.Graphic?.Draw(thing.DrawPos, thing.Rotation, thing);
            }
            catch (Exception error)
            {
                Log.WarningOnce("[AASB2 Extended] Glass dynamic projection skipped " + thing.def.defName +
                    ": " + error.GetBaseException().Message, thing.def.defName.GetHashCode() ^ 173806);
            }
            finally
            {
                current = null;
            }
        }

        private static bool ClipSubmission(ref Mesh __0, ref Matrix4x4 __1, ref int __4)
        {
            if (current == null)
                return true;
            try
            {
                var clipped = current.Clip(__0, __1, __4);
                if (clipped == null)
                    return false;
                __0 = clipped;
                __1 = Matrix4x4.identity;
                __4 = 0;
                return true;
            }
            catch (Exception error)
            {
                Log.WarningOnce("[AASB2 Extended] Glass mesh clipping skipped: " + error.Message, 16360841);
                return false;
            }
        }
    }

    public sealed class GlassDrawBuffers : MapComponent
    {
        private const int MaxMeshes = 4096;
        private readonly List<Mesh>[] pools = { new List<Mesh>(), new List<Mesh>() };
        private readonly List<Vector3> positions = new List<Vector3>();
        private readonly List<Vector2> uv = new List<Vector2>();
        private readonly List<Color32> colors = new List<Color32>();
        private readonly List<GlassStaticPrint.Vertex> a = new List<GlassStaticPrint.Vertex>(8);
        private readonly List<GlassStaticPrint.Vertex> b = new List<GlassStaticPrint.Vertex>(8);
        private readonly Dictionary<(Mesh mesh, int part), int[]> meshIndices = new Dictionary<(Mesh, int), int[]>();
        private LayerSubMesh output;
        private Dictionary<IntVec3, IntVec3> windows;
        private int frame = -1;
        private int used;
        private float altitude;
        private int attempted, readable, emittedTriangles;

        [DebugAction("AASB2 Extended", "Inspect glass draw budget", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void InspectBudget()
        {
            var buffers = Find.CurrentMap?.GetComponent<GlassDrawBuffers>();
            if (buffers != null)
                DiagnosticReports.Write("glass-rendering", $"[AASB2 Extended] Glass draw frame={buffers.frame} attempts={buffers.attempted} " +
                    $"readable={buffers.readable} meshes={buffers.used} triangles={buffers.emittedTriangles} " +
                    $"pool={buffers.pools[0].Count}+{buffers.pools[1].Count} limit={MaxMeshes}");
        }

        public GlassDrawBuffers(Map map) : base(map) { }

        internal void Begin(Dictionary<IntVec3, IntVec3> mask, float layer)
        {
            windows = mask;
            altitude = layer;
            if (frame != Time.frameCount)
            {
                frame = Time.frameCount;
                used = attempted = readable = emittedTriangles = 0;
                meshIndices.Clear();
            }
        }

        internal Mesh Clip(Mesh source, Matrix4x4 matrix, int submesh)
        {
            attempted++;
            if (source == null || !source.isReadable || submesh < 0 || submesh >= source.subMeshCount ||
                source.vertexCount > 1024 || used >= MaxMeshes)
                return null;
            readable++;
            positions.Clear();
            uv.Clear();
            colors.Clear();
            source.GetVertices(positions);
            source.GetUVs(0, uv);
            source.GetColors(colors);
            if (!meshIndices.TryGetValue((source, submesh), out var indices))
            {
                indices = source.GetIndices(submesh);
                if (meshIndices.Count < 512)
                    meshIndices.Add((source, submesh), indices);
            }
            if (indices.Length % 3 != 0 || indices.Length > 3072)
                return null;
            var pool = pools[frame & 1];
            if (output == null)
            {
                var scratch = new Mesh();
                output = new LayerSubMesh(scratch, MatBases.LightOverlay, null);
            }
            output.verts.Clear();
            output.uvs.Clear();
            output.colors.Clear();
            output.tris.Clear();
            for (int i = 0; i < indices.Length; i += 3)
                GlassStaticPrint.ClipTriangle(output, Read(indices[i]), Read(indices[i + 1]), Read(indices[i + 2]),
                    windows, a, b, altitude);
            if (output.tris.Count == 0)
                return null;
            if (used == pool.Count)
            {
                var created = new Mesh();
                created.MarkDynamic();
                pool.Add(created);
            }
            emittedTriangles += output.tris.Count / 3;
            var result = pool[used++];
            result.Clear();
            result.SetVertices(output.verts);
            result.SetUVs(0, output.uvs);
            result.SetColors(output.colors);
            result.SetTriangles(output.tris, 0);
            return result;

            GlassStaticPrint.Vertex Read(int index)
            {
                var p = matrix.MultiplyPoint3x4(positions[index]);
                return new GlassStaticPrint.Vertex(p,
                    index < uv.Count ? new Vector3(uv[index].x, uv[index].y, 0f) : default,
                    index < colors.Count ? colors[index] : new Color32(255, 255, 255, 255));
            }
        }

        public override void MapRemoved()
        {
            foreach (var pool in pools)
            {
                foreach (var mesh in pool)
                    UnityEngine.Object.Destroy(mesh);
                pool.Clear();
            }
            if (output != null)
                UnityEngine.Object.Destroy(output.mesh);
            output = null;
            windows = null;
            meshIndices.Clear();
        }
    }
}
