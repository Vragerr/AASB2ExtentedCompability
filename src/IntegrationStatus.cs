using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using LudeonTK;
using Verse;

namespace AASB2ExtendedCompatibility
{
    internal static class IntegrationStatus
    {
        private static readonly Dictionary<string, string> Failures = new Dictionary<string, string>();

        internal static void NoteFailure(string adapter, string method, Exception error) =>
            Failures[adapter + " / " + method] = error.GetBaseException().Message;

        internal static bool Registered(System.Reflection.MethodBase method, string owner, System.Reflection.MethodInfo patch = null)
        {
            var info = method == null ? null : Harmony.GetPatchInfo(method);
            return info != null && info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                .Any(entry => entry.owner == owner && (patch == null || entry.PatchMethod == patch));
        }

        private static void Append(StringBuilder output, string title, string owner, int required,
            string[] dependencies, bool setting = true, bool targetDef = true, bool failed = false)
        {
            string status = null;
            foreach (var package in dependencies)
            {
                var installed = ModLister.GetModWithIdentifier(package, false);
                output.AppendLine().Append("  ").Append(package).Append(" version=").Append(installed?.ModVersion ?? "-")
                    .Append(" active=").Append(ModsConfig.IsActive(package));
                if (!ModsConfig.IsActive(package) && status == null)
                    status = installed == null ? "MOD_NOT_INSTALLED" : "MOD_NOT_ACTIVE";
            }
            var targets = Harmony.GetAllPatchedMethods().Where(method => Registered(method, owner)).ToList();
            if (status == null)
                status = !setting ? "DISABLED_BY_SETTING" : !targetDef ? "TARGET_DEF_MISSING" :
                    targets.Count >= required ? "ACTIVE" : targets.Count > 0 ? "PARTIALLY_ACTIVE" :
                    failed ? "PATCH_INSTALL_FAILED" : "CONTRACT_MISMATCH";
            output.AppendLine().Append(title).Append(" ").Append(status).Append(" owner=").Append(owner)
                .Append(" observed=").Append(targets.Count).Append('/').Append(required);
            foreach (var method in targets)
                output.AppendLine().Append("  ").Append(method.DeclaringType?.FullName).Append('.').Append(method)
                    .Append(" assembly=").Append(method.DeclaringType?.Assembly.GetName().Version);
        }

        [DebugAction("AASB2 Extended", "Log integration status")]
        internal static void LogStatus()
        {
            var report = new StringBuilder("[AASB2 Extended] Integration status");
            var factory = new[] { "astryl.asabovesobelow2", "vanillaexpanded.vfefactory" };
            bool horizonEnabled = CompatibilityStatus.HorizonAtStartup;
            if (ModsConfig.IsActive(HorizonCompatibility.Package) &&
                CompatibilityStatus.FloorSettings?.EnableDevelopmentHorizon != horizonEnabled)
                report.AppendLine().Append("Endless Horizon: restart pending.");
            var horizon = new[] { "astryl.asabovesobelow2", HorizonCompatibility.Package };
            var owner = CompatibilityStatus.HarmonyId;
            Append(report, "Factory transfer", owner + ".factory-ports", 7, factory,
                targetDef: DefDatabase<ResearchProjectDef>.GetNamedSilentFail("VFE_BasicFactories") != null,
                failed: Failures.Keys.Any(key => key.StartsWith("Factory transfer")));
            Append(report, "Factory presentation", owner + ".factory-ui", 2, factory,
                failed: Failures.Keys.Any(key => key.StartsWith("Factory presentation")));
            Append(report, "Factory cargo return", owner + ".factory-return", 1, factory);
            Append(report, "Endless Horizon surface", owner + ".horizon", 3, horizon, setting: horizonEnabled);
            Append(report, "Endless Horizon upper rendering", owner + ".horizon-upper", 7, horizon, setting: horizonEnabled);
            Append(report, "Endless Horizon curtain", owner + ".horizon-curtain", 3, horizon, setting: horizonEnabled,
                failed: Failures.Keys.Any(key => key.StartsWith("Endless Horizon curtain")));
            Append(report, "Endless Horizon build counter", owner + ".horizon-monitor", 1, horizon, setting: horizonEnabled);
            Append(report, "Endless Horizon geological scatter", owner + ".horizon-geological-scatter", 1, horizon, setting: horizonEnabled);
            Append(report, "Endless Horizon surface samples", owner + ".horizon-surface-samples", 2, horizon, setting: horizonEnabled);
            Append(report, "Glass dynamic", owner + ".glass-dynamic", 1, new[] { "astryl.asabovesobelow2" },
                setting: CompatibilityStatus.FloorSettings?.EnableVisual == true);
            Append(report, "Glass direct submissions", owner + ".glass-direct", 1, new[] { "astryl.asabovesobelow2" },
                setting: CompatibilityStatus.FloorSettings?.EnableVisual == true);
            Append(report, "Ordinary melee commit", owner + ".melee-commit", 1, new[] { "astryl.asabovesobelow2" });
            Append(report, "Glass gameplay light transfer", owner + ".glass-light-transfer", 2,
                new[] { "astryl.asabovesobelow2" },
                setting: GlassLightTransfer.Enabled,
                failed: Failures.Keys.Any(key => key.StartsWith("Glass light transfer")));
            Append(report, "Glass native RGB", owner + ".glass-rgb", 1, new[] { "astryl.asabovesobelow2" },
                setting: CompatibilityStatus.FloorSettings?.EnableVisual == true,
                failed: Failures.Keys.Any(key => key.StartsWith("Glass native RGB")));
            Append(report, "Glass invalidation", owner + ".glass-dirty", 1, new[] { "astryl.asabovesobelow2" },
                setting: CompatibilityStatus.FloorSettings?.EnableVisual == true);
            Append(report, "Dubs Skylights glass", owner + ".skylights", 2,
                new[] { "astryl.asabovesobelow2", "dubwise.dubsskylights" },
                setting: CompatibilityStatus.FloorSettings?.EnableDubsSkylights != false);
            Append(report, "Factory band boundary", owner + ".bands", 1, factory);
            Append(report, "Anima tree limits", owner + ".anima", 2,
                new[] { "astryl.asabovesobelow2", "ludeon.rimworld.royalty" },
                setting: CompatibilityStatus.FloorSettings?.LimitAnimaTrees == true ||
                    CompatibilityStatus.FloorSettings?.AnimaSurfaceOnly == true,
                targetDef: DefDatabase<ThingDef>.GetNamedSilentFail("Plant_TreeAnima") != null,
                failed: Failures.Keys.Any(key => key.StartsWith("Anima tree limits")));
            Append(report, "Skiptech autonomous", owner + ".skiptech-autonomous", 11,
                new[] { "astryl.asabovesobelow2", SkiptechAutonomous.Package },
                setting: CompatibilityStatus.FloorSettings?.EnableSkiptech != false,
                failed: Failures.Keys.Any(key => key.StartsWith("Skiptech autonomous")));
            if (PipeProjection.OpenAirReady)
                report.AppendLine().Append("Open-air pipes: native world print active.");
            Append(report, "Rimefeller risers", owner + ".rimefeller-risers", 2,
                new[] { "astryl.asabovesobelow2", RimefellerRisers.Package },
                setting: CompatibilityStatus.FloorSettings?.EnableRimefellerRisers != false);
            Append(report, "DBH riser recovery", owner + ".dbh-riser-load", 2,
                new[] { "astryl.asabovesobelow2", DbhRiserLoad.Package },
                setting: CompatibilityStatus.FloorSettings?.EnableDbhRiserRecovery != false);
            Append(report, "Dubs heating risers", owner + ".dch-riser", 2,
                new[] { "astryl.asabovesobelow2", DchRiser.Package }, setting: CompatibilityStatus.DchAtStartup);
            Append(report, "Column pipe protection", owner + ".column-pipes", 3, new[] { "astryl.asabovesobelow2" },
                setting: CompatibilityStatus.FloorSettings?.ProtectColumnPipes != false);
            report.AppendLine().Append("Runtime Compatibility Fixes");
            Append(report, "Missing pawn silhouette", owner + ".silhouettes", 1, new[] { "astryl.asabovesobelow2" },
                setting: CompatibilityStatus.FloorSettings?.GuardMissingSilhouettes != false,
                failed: Failures.Keys.Any(key => key.StartsWith("Missing pawn silhouette")));
            foreach (var entry in Failures)
                report.AppendLine().Append("  PATCH_INSTALL_FAILED ").Append(entry.Key).Append(" — ").Append(entry.Value);
            DiagnosticReports.Write("compatibility", report.ToString());
        }
    }
}

