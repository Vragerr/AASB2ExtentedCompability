using System;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    public sealed class CompatibilityStatus : Mod
    {
        public const string HarmonyId = "local.aasb2extendedcompatibility";
        private Vector2 scroll;
        private int tab;
        private string[] tabs;

        internal static bool DchAtStartup { get; private set; } = true;
        internal static bool HorizonAtStartup { get; private set; }
        internal static FloorPrototypeSettings FloorSettings { get; private set; }

        public CompatibilityStatus(ModContentPack content) : base(content)
        {
            FloorSettings = GetSettings<FloorPrototypeSettings>();
            HorizonAtStartup = ModsConfig.IsActive(HorizonCompatibility.Package) &&
                FloorSettings.EnableDevelopmentHorizon;
            DchAtStartup = FloorSettings.EnableDchRisers;
        }

        public override string SettingsCategory() => "AASB2 Extended Compatibility";

        public override void DoSettingsWindowContents(Rect area)
        {
            tabs ??= new[]
            {
                "World", "Graphics", "Engineering",
                "Compatibility", "Diagnostics", "Development"
            };
            const float gap = 4f;
            int columns = Mathf.Clamp(Mathf.FloorToInt((area.width + gap) / 140f), 1, tabs.Length);
            int rows = (tabs.Length + columns - 1) / columns;
            float width = (area.width - (columns - 1) * gap) / columns;
            float rowHeight = 34f;
            foreach (string key in tabs)
                rowHeight = Mathf.Max(rowHeight, Text.CalcHeight(("AASBExt.Tab." + key).Translate(), Mathf.Max(1f, width - 16f)) + 10f);
            for (int index = 0; index < tabs.Length; index++)
            {
                var button = new Rect(area.x + index % columns * (width + gap),
                    area.y + index / columns * (rowHeight + gap), width, rowHeight);
                var previous = GUI.color;
                if (index == tab)
                    GUI.color = new Color(0.68f, 0.86f, 1f);
                bool selected;
                try { selected = Widgets.ButtonText(button, ("AASBExt.Tab." + tabs[index]).Translate()); }
                finally { GUI.color = previous; }
                if (selected && index != tab)
                {
                    tab = index;
                    scroll = Vector2.zero;
                }
            }
            area.yMin += rows * (rowHeight + gap) + 8f;
            if (area.height <= 0f)
                return;

            string[] statusLines = null;
            var content = new Rect(0, 0, Mathf.Max(1, area.width - 20), tab == 4 ? 630f : 510f);
            if (tab == 3)
            {
                statusLines = GetStatusLines();
                content.height = 240f;
                foreach (var line in statusLines)
                    content.height += Text.CalcHeight(line, content.width) + 8f;
            }
            Widgets.BeginScrollView(area, ref scroll, content);
            var listing = new Listing_Standard();
            try
            {
                listing.Begin(content);
                try
                {
                    switch (tab)
                    {
                        case 0:
                            if (AnimaTreeLimit.Ready)
                            {
                                listing.CheckboxLabeled("AASBExt.Anima.Enable".Translate(), ref FloorSettings.LimitAnimaTrees,
                                    "AASBExt.Anima.LimitTip".Translate());
                                if (FloorSettings.LimitAnimaTrees)
                                {
                                    listing.Label("AASBExt.Anima.Maximum".Translate(FloorSettings.MaxAnimaTrees));
                                    FloorSettings.MaxAnimaTrees = Mathf.RoundToInt(listing.Slider(FloorSettings.MaxAnimaTrees, 1f, 50f));
                                }
                                listing.CheckboxLabeled("AASBExt.Anima.SurfaceOnly".Translate(), ref FloorSettings.AnimaSurfaceOnly,
                                    "AASBExt.Anima.SurfaceTip".Translate());
                            }
                            else
                                listing.Label("AASBExt.Anima.Unavailable".Translate());
                            break;
                        case 1:
                            if (HasDeck)
                                listing.CheckboxLabeled("AASBExt.Floor.Enable".Translate(), ref FloorSettings.EnablePlacement);
                            else
                                listing.Label("AASBExt.Status.DeckMissing".Translate());
                            if (GlassLightTransfer.Ready && (HasDeck || BandCompatibility.SkylightsReady))
                                listing.CheckboxLabeled("AASBExt.Settings.GameplayLight".Translate(), ref FloorSettings.EnableGlassLight, "AASBExt.Settings.GameplayLightTip".Translate());
                            if (Available(listing, "Dubs Skylights", "dubwise.dubsskylights", BandCompatibility.SkylightsReady))
                                listing.CheckboxLabeled("AASBExt.Settings.DubsSkylights".Translate(), ref FloorSettings.EnableDubsSkylights,
                                    "AASBExt.Settings.DubsSkylightsTip".Translate());
                            if (BandCompatibility.VisualReady && (HasDeck || BandCompatibility.SkylightsReady))
                                listing.CheckboxLabeled("AASBExt.Floor.Visual".Translate(), ref FloorSettings.EnableVisual,
                                    "AASBExt.Settings.ReloadMap".Translate());
                            else
                                listing.Label("AASBExt.Settings.VisualUnavailable".Translate());
                            break;
                        case 2:
                            if (Available(listing, "Rimefeller", RimefellerRisers.Package, RimefellerRisers.Ready))
                                listing.CheckboxLabeled("AASBExt.Settings.RimefellerRisers".Translate(), ref FloorSettings.EnableRimefellerRisers,
                                    "AASBExt.Settings.RimefellerRisersTip".Translate());
                            if (Available(listing, "Dubs Bad Hygiene", DbhRiserLoad.Package, DbhRiserLoad.Ready))
                                listing.CheckboxLabeled("AASBExt.Settings.DbhRiserRecovery".Translate(), ref FloorSettings.EnableDbhRiserRecovery,
                                    "AASBExt.Settings.DbhRiserRecoveryTip".Translate());
                            if (DchRiser.ProvidedByAasb)
                                listing.Label("AASBExt.Settings.NativeHeating".Translate());
                            else if (Available(listing, "Dubs Central Heating", DchRiser.Package, !DchAtStartup || DchRiser.Ready))
                            {
                                listing.CheckboxLabeled("AASBExt.Settings.DchRisers".Translate(), ref FloorSettings.EnableDchRisers,
                                    "AASBExt.Settings.DchRisersTip".Translate());
                                if (DchAtStartup != FloorSettings.EnableDchRisers)
                                    listing.Label("AASBExt.Settings.RestartPending".Translate());
                            }
                            if (ColumnPipeGuard.Ready)
                                listing.CheckboxLabeled("AASBExt.ColumnPipe.Enable".Translate(), ref FloorSettings.ProtectColumnPipes,
                                    "AASBExt.ColumnPipe.Tip".Translate());
                            if (Available(listing, "Vanilla Furniture Expanded - Factory", "vanillaexpanded.vfefactory", FactoryPorts.Ready))
                                listing.CheckboxLabeled("AASBExt.Factory.Enable".Translate(), ref FloorSettings.EnableFactoryPorts);
                            if (DefDatabase<ThingDef>.GetNamedSilentFail("AASBExt_VerticalVent") != null)
                                listing.CheckboxLabeled("AASBExt.Settings.VentExchange".Translate(), ref FloorSettings.EnableVentExchange, "AASBExt.Settings.VentExchangeTip".Translate());
                            break;
                        case 3:
                            if (Available(listing, "MigCorp - Skiptech", SkiptechAutonomous.Package, SkiptechAutonomous.Ready))
                            {
                                listing.CheckboxLabeled("AASBExt.Settings.Skiptech".Translate(), ref FloorSettings.EnableSkiptech, "AASBExt.Settings.SkiptechTip".Translate());
                                if (!FloorSettings.EnableSkiptech)
                                    listing.Label("AASBExt.Settings.Disabled".Translate("MigCorp - Skiptech"));
                                else if (Available(listing, "Giddy-up", GiddyUpSkipdoors.Package, SkiptechAutonomous.MountedReady))
                                    listing.CheckboxLabeled("AASBExt.Settings.MountedSkipdoors".Translate(), ref FloorSettings.ProtectMountedSkipdoors,
                                        "AASBExt.Settings.MountedSkipdoorsTip".Translate());
                            }
                            else
                                listing.Label("AASBExt.Settings.MountNeedsSkiptech".Translate());
                            if (SilhouetteGuard.Ready)
                                listing.CheckboxLabeled("AASBExt.Settings.Silhouettes".Translate(), ref FloorSettings.GuardMissingSilhouettes,
                                    "AASBExt.Settings.SilhouettesTip".Translate());
                            foreach (var line in statusLines)
                            {
                                listing.Label(line);
                                listing.Gap(8f);
                            }
                            break;
                        case 4:
                            DrawDiagnostics(listing);
                            break;
                        case 5:
                            if (!Available(listing, "Endless Horizon", HorizonCompatibility.Package, compatible: true))
                                break;
                            bool failedHorizon = HorizonAtStartup && !HorizonCompatibility.Ready;
                            if (failedHorizon)
                                listing.Label(Describe("Endless Horizon", HorizonCompatibility.Package, contract: false));
                            else
                                listing.Label("AASBExt.Settings.EndlessHorizonTip".Translate());
                            listing.CheckboxLabeled("AASBExt.Settings.EndlessHorizon".Translate(), ref FloorSettings.EnableDevelopmentHorizon);
                            if (FloorSettings.EnableDevelopmentHorizon && !failedHorizon)
                                listing.CheckboxLabeled("AASBExt.Settings.UpperHorizon".Translate(), ref FloorSettings.EnableUpperHorizon,
                                    "AASBExt.Settings.UpperHorizonTip".Translate());
                            if (FloorSettings.EnableDevelopmentHorizon != HorizonAtStartup)
                                listing.Label("AASBExt.Settings.RestartPending".Translate());
                            if (HorizonAtStartup && HorizonCompatibility.Ready && Find.UIRoot is UIRoot_Play && Find.CurrentMap != null &&
                                listing.ButtonText("AASBExt.Diagnostics.Horizon".Translate()))
                                HorizonCompatibility.Inspect();
                            break;
                    }
                }
                finally { listing.End(); }
            }
            finally { Widgets.EndScrollView(); }
        }

        private static void DrawDiagnostics(Listing_Standard listing)
        {
            if (listing.ButtonText("AASBExt.Settings.LogStatus".Translate()))
                IntegrationStatus.LogStatus();

            var map = Find.UIRoot is UIRoot_Play ? Find.CurrentMap : null;
            if (map == null)
            {
                listing.Label("AASBExt.Diagnostics.NeedsMap".Translate());
                DiagnosticReports.DrawStatus(listing);
                return;
            }

            var selected = Find.Selector?.SingleSelectedThing;
            if (selected?.Spawned != true || selected.Map != map)
            {
                selected = null;
                listing.Label("AASBExt.Diagnostics.NeedsSelection".Translate());
            }
            if (listing.ButtonText("AASBExt.Diagnostics.Column".Translate()))
                UtilitiesDiagnostics.LogSnapshot();
            var vents = map.GetComponent<VerticalVentExchange>();
            if (vents != null && DefDatabase<ThingDef>.GetNamedSilentFail("AASBExt_VerticalVent") != null &&
                listing.ButtonText("AASBExt.Diagnostics.Vents".Translate()))
                vents.SaveReport();
            if (GiddyUpSkipdoors.Ready && selected is Pawn && listing.ButtonText("AASBExt.Diagnostics.Giddy".Translate()))
                GiddyUpSkipdoors.SaveReport();
            if (BandCompatibility.IsBanded(map) && listing.ButtonText("AASBExt.Diagnostics.Geology".Translate()))
                LocalCompatibilityDiagnostics.InspectSurface();
            if (selected != null && ModsConfig.IsActive(SkiptechAutonomous.Package) &&
                listing.ButtonText("AASBExt.Diagnostics.Skipdoor".Translate()))
                LocalCompatibilityDiagnostics.SaveSkipdoor(map, selected.Position);
            if (FactoryPorts.Ready && selected is ThingWithComps port && port.GetComp<CompVerticalFactoryPort>() != null &&
                listing.ButtonText("AASBExt.Diagnostics.Conveyor".Translate()))
                CompVerticalFactoryPort.SaveConveyorReport(map, port.Position);
            if (BandCompatibility.IsBanded(map) && (FloorSettings.EnableVisual || GlassLightTransfer.Enabled) &&
                listing.ButtonText("AASBExt.Diagnostics.Glass".Translate()))
                BandCompatibility.SaveGlassReport(map, Find.CameraDriver.CurrentViewRect.CenterCell);
            if (SkiptechAutonomous.Ready && selected is Pawn haulingPawn &&
                listing.ButtonText("AASBExt.Diagnostics.Hauling".Translate()))
                SkiptechAutonomous.ToggleNavigationTrace(haulingPawn);
            var returns = map.GetComponent<FactoryPortReturns>();
            if (returns?.GetDirectlyHeldThings()?.Count > 0 && listing.ButtonText("AASBExt.Factory.Release".Translate()))
                returns.Release();
            DiagnosticReports.DrawStatus(listing);
        }

        private static bool HasDeck => DefDatabase<TerrainDef>.GetNamedSilentFail("AASBExt_ExperimentalGlassFloor") != null;

        private static string[] GetStatusLines()
        {
            return new[]
            {
                "AASBExt.Status.Header".Translate().ToString(),
                Describe("AASB2", "astryl.asabovesobelow2", "AsAboveSoBelow.ABBands"),
                Describe("Geological Landforms", "m00nl1ght.geologicallandforms"),
                Describe("Biome Transitions", "m00nl1ght.geologicallandforms.biometransitions"),
                Describe("Map Preview", "m00nl1ght.mappreview"),
                Describe("VEF PipeSystem", "oskarpotocki.vanillafactionsexpanded.core", "PipeSystem.PipeNetManager"),
                Describe("AB2 Vehicles", "drati.ab2vehicles"),
                !HasDeck
                    ? "AASBExt.Status.DeckMissing".Translate().ToString()
                    : "AASBExt.Status.DeckAvailable".Translate().ToString(),
            };
        }

        private static bool Available(Listing_Standard listing, string title, string package, bool compatible)
        {
            if (ModsConfig.IsActive(package) && compatible)
                return true;
            listing.Label(Describe(title, package, contract: false));
            return false;
        }

        private static string Describe(string title, string packageId, string typeName = null, bool? contract = null, bool enabled = true)
        {
            if (!ModsConfig.IsActive(packageId))
            {
                foreach (var metadata in ModLister.AllInstalledMods)
                    if (string.Equals(packageId, metadata.PackageIdNonUnique, StringComparison.OrdinalIgnoreCase))
                        return "AASBExt.Status.Inactive".Translate(title).ToString();
                return "AASBExt.Status.Missing".Translate(title).ToString();
            }

            if (!enabled)
                return "AASBExt.Settings.Disabled".Translate(title).ToString();
            if (contract.HasValue)
                return (contract.Value ? "AASBExt.Status.Adapter" : "AASBExt.Status.Contract").Translate(title).ToString();

            if (typeName != null && !HasLoadedType(typeName))
                return "AASBExt.Status.TypeMissing".Translate(title, typeName).ToString();

            return "AASBExt.Status.Active".Translate(title).ToString();
        }

        private static bool HasLoadedType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.GetType(fullName, throwOnError: false) != null)
                        return true;
                }
                catch (TypeLoadException)
                {
                    // An optional dependency may be incomplete. Its status remains unresolved.
                }
                catch (System.IO.FileNotFoundException)
                {
                    // A referenced assembly is missing.
                }
            }
            return false;
        }
    }
}


