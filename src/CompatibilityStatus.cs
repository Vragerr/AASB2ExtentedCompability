using System;
using System.Reflection;
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

        internal static bool HorizonAtStartup { get; private set; } = true;
        internal static FloorPrototypeSettings FloorSettings { get; private set; }

        public CompatibilityStatus(ModContentPack content) : base(content)
        {
            FloorSettings = GetSettings<FloorPrototypeSettings>();
            HorizonAtStartup = FloorSettings.EnableEndlessHorizon;
        }

        public override string SettingsCategory() => "AASB2 Extended Compatibility";

        public override void DoSettingsWindowContents(Rect area)
        {
            tabs ??= new[]
            {
                "World", "Graphics", "Engineering",
                "Compatibility", "Diagnostics"
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
                bool selected = Widgets.ButtonText(button, ("AASBExt.Tab." + tabs[index]).Translate());
                GUI.color = previous;
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
            var content = new Rect(0, 0, Mathf.Max(1, area.width - 20), tab == 4 ? 580f : 410f);
            if (tab == 3)
            {
                statusLines = GetStatusLines();
                content.height = 12 + (ModsConfig.IsActive(HorizonCompatibility.Package) ? 44 : 0) +
                    (ModsConfig.IsActive(SkiptechAutonomous.Package) ? 44 : 0) +
                    (GiddyUpSkipdoors.Ready || SilhouetteGuard.Ready ? 100 : 0);
                foreach (var line in statusLines)
                    content.height += Text.CalcHeight(line, content.width) + 8f;
            }
            Widgets.BeginScrollView(area, ref scroll, content);
            var listing = new Listing_Standard();
            listing.Begin(content);
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
                    if (BandCompatibility.SkylightsReady)
                        listing.CheckboxLabeled("AASBExt.Settings.DubsSkylights".Translate(), ref FloorSettings.EnableDubsSkylights,
                            "AASBExt.Settings.DubsSkylightsTip".Translate());
                    if (BandCompatibility.VisualReady && (HasDeck || BandCompatibility.SkylightsReady))
                        listing.CheckboxLabeled("AASBExt.Floor.Visual".Translate(), ref FloorSettings.EnableVisual,
                            "AASBExt.Settings.ReloadMap".Translate());
                    else
                        listing.Label("AASBExt.Settings.VisualUnavailable".Translate());
                    break;
                case 2:
                    if (ColumnPipeGuard.Ready)
                        listing.CheckboxLabeled("AASBExt.ColumnPipe.Enable".Translate(), ref FloorSettings.ProtectColumnPipes,
                            "AASBExt.ColumnPipe.Tip".Translate());
                    if (FactoryPorts.Ready)
                        listing.CheckboxLabeled("AASBExt.Factory.Enable".Translate(), ref FloorSettings.EnableFactoryPorts);
                    if (DefDatabase<ThingDef>.GetNamedSilentFail("AASBExt_VerticalVent") != null)
                        listing.CheckboxLabeled("AASBExt.Settings.VentExchange".Translate(), ref FloorSettings.EnableVentExchange, "AASBExt.Settings.VentExchangeTip".Translate());
                    break;
                case 3:
                    if (ModsConfig.IsActive(HorizonCompatibility.Package))
                        listing.CheckboxLabeled("AASBExt.Settings.EndlessHorizon".Translate(), ref FloorSettings.EnableEndlessHorizon,
                            "AASBExt.Settings.EndlessHorizonTip".Translate());
                    if (ModsConfig.IsActive(SkiptechAutonomous.Package) && SkiptechAutonomous.Ready)
                        listing.CheckboxLabeled("AASBExt.Settings.Skiptech".Translate(), ref FloorSettings.EnableSkiptech, "AASBExt.Settings.SkiptechTip".Translate());
                    if (GiddyUpSkipdoors.Ready && SkiptechAutonomous.Ready)
                        listing.CheckboxLabeled("AASBExt.Settings.MountedSkipdoors".Translate(), ref FloorSettings.ProtectMountedSkipdoors,
                            "AASBExt.Settings.MountedSkipdoorsTip".Translate());
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
                    if (listing.ButtonText("AASBExt.Settings.LogStatus".Translate()))
                        IntegrationStatus.LogStatus();
                    if (Find.CurrentMap != null && listing.ButtonText("AASBExt.Diagnostics.Column".Translate()))
                        UtilitiesDiagnostics.LogSnapshot();
                    if (Find.CurrentMap != null && DefDatabase<ThingDef>.GetNamedSilentFail("AASBExt_VerticalVent") != null &&
                        listing.ButtonText("AASBExt.Diagnostics.Vents".Translate()))
                        Find.CurrentMap.GetComponent<VerticalVentExchange>().SaveReport();
                    if (GiddyUpSkipdoors.Ready && listing.ButtonText("AASBExt.Diagnostics.Giddy".Translate()))
                        GiddyUpSkipdoors.SaveReport();
                    if (Find.CurrentMap != null && ModsConfig.IsActive(HorizonCompatibility.Package) &&
                        listing.ButtonText("AASBExt.Diagnostics.Horizon".Translate()))
                        HorizonCompatibility.Inspect();
                    if (BandCompatibility.IsBanded(Find.CurrentMap) && listing.ButtonText("AASBExt.Diagnostics.Geology".Translate()))
                        LocalCompatibilityDiagnostics.InspectSurface();
                    if (Find.Selector?.SingleSelectedThing is Thing selected && selected.Spawned &&
                        ModsConfig.IsActive(SkiptechAutonomous.Package) && listing.ButtonText("AASBExt.Diagnostics.Skipdoor".Translate()))
                        LocalCompatibilityDiagnostics.SaveSkipdoor(selected.Map, selected.Position);
                    if (FactoryPorts.Ready && Find.Selector?.SingleSelectedThing is ThingWithComps port &&
                        port.Spawned && port.GetComp<CompVerticalFactoryPort>() != null &&
                        listing.ButtonText("AASBExt.Diagnostics.Conveyor".Translate()))
                        CompVerticalFactoryPort.SaveConveyorReport(port.Map, port.Position);
                    if (BandCompatibility.IsBanded(Find.CurrentMap) &&
                        (FloorSettings.EnableVisual || GlassLightTransfer.Enabled) &&
                        listing.ButtonText("AASBExt.Diagnostics.Glass".Translate()))
                        BandCompatibility.SaveGlassReport(Find.CurrentMap, Find.CameraDriver.CurrentViewRect.CenterCell);
                    var returns = Find.CurrentMap?.GetComponent<FactoryPortReturns>();
                    if (returns?.GetDirectlyHeldThings()?.Count > 0 &&
                        listing.ButtonText("AASBExt.Factory.Release".Translate()))
                        returns.Release();
                    DiagnosticReports.DrawStatus(listing);
                    break;
            }
            listing.End();
            Widgets.EndScrollView();
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
                Describe("Endless Horizon", HorizonCompatibility.Package, contract: HorizonCompatibility.Ready, enabled: HorizonAtStartup),
                Describe("DBH", "dubwise.dubsbadhygiene"),
                Describe("VEF PipeSystem", "oskarpotocki.vanillafactionsexpanded.core", "PipeSystem.PipeNetManager"),
                Describe("VFE Factory", "vanillaexpanded.vfefactory", contract: FactoryPorts.Ready),
                Describe("AB2 Vehicles", "drati.ab2vehicles"),
                Describe("MigCorp Skiptech", SkiptechAutonomous.Package, contract: SkiptechAutonomous.Ready, enabled: FloorSettings.EnableSkiptech),
                Describe("Dubs Skylights", "dubwise.dubsskylights", contract: BandCompatibility.SkylightsReady, enabled: FloorSettings.EnableDubsSkylights),
                !HasDeck
                    ? "AASBExt.Status.DeckMissing".Translate().ToString()
                    : "AASBExt.Status.DeckAvailable".Translate().ToString(),
            };
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


