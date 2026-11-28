using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    public sealed partial class CompVerticalFactoryPort
    {
        private void BuildLinked()
        {
            if (shaft == null || !parent.Spawned)
                return;
            var map = parent.Map;
            var options = new List<FloatMenuOption>();
            foreach (var floor in BandCompatibility.ShaftCells(map, parent.Position))
            {
                if (floor == parent.Position)
                    continue;
                BandCompatibility.TryLocate(map, floor, out _, out int band);
                var existing = shaft.At(band);
                string location = "AASBExt.Factory.Destination".Translate(
                    BandCompatibility.Level(map, floor), shaft.Local.x, shaft.Local.z);
                if (existing != null)
                {
                    options.Add(new FloatMenuOption(location + " · " + existing.parent.LabelCap,
                        () => CameraJumper.TryJumpAndSelect(existing.parent)));
                    continue;
                }

                if (Port.input || Port.section)
                    AddBuildOption(options, map, floor, location, "Output");
                if (Output || Port.section)
                    AddBuildOption(options, map, floor, location, "Input");
                AddBuildOption(options, map, floor, location, "Section");
            }
            if (options.Count == 0)
                options.Add(new FloatMenuOption("AASBExt.Factory.NoOtherFloor".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void AddBuildOption(List<FloatMenuOption> options, Map map, IntVec3 cell, string location, string suffix)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail("AASBExt_VerticalConveyor" + suffix);
            if (def == null)
                return;
            string label = location + " · " + def.LabelCap;
            var designator = new LinkedPortDesignator(def, map, cell);
            if (!designator.Visible)
                options.Add(new FloatMenuOption(label + " · " + "AASBExt.Factory.BuildUnavailable".Translate(), null));
            else
                options.Add(new FloatMenuOption(label, () =>
                {
                    if (Find.CurrentMap != map || !designator.Visible)
                        return;
                    CameraJumper.TryJump(cell, map);
                    Find.DesignatorManager.Select(designator);
                    var report = designator.CanDesignateCell(cell);
                    if (!report.Accepted && !report.Reason.NullOrEmpty())
                        Messages.Message(report.Reason, MessageTypeDefOf.RejectInput, false);
                }));
        }

        private sealed class LinkedPortDesignator : Designator_Build
        {
            private readonly Map sourceMap;
            private readonly IntVec3 target;
            private readonly List<IntVec3> outline;

            internal LinkedPortDesignator(ThingDef def, Map map, IntVec3 target) : base(def)
            {
                sourceMap = map;
                this.target = target;
                outline = new List<IntVec3> { target };
            }

            public override AcceptanceReport CanDesignateCell(IntVec3 cell)
            {
                if (Find.CurrentMap != sourceMap || cell != target)
                    return new AcceptanceReport("AASBExt.Factory.UseHighlightedCell".Translate());
                return base.CanDesignateCell(cell);
            }

            public override void SelectedUpdate()
            {
                base.SelectedUpdate();
                if (Find.CurrentMap == sourceMap)
                    GenDraw.DrawFieldEdges(outline, CanDesignateCell(target).Accepted ? Color.green : Color.red);
            }
        }
    }
}
