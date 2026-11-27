using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AASB2ExtendedCompatibility
{
    public sealed class FactoryDestination : IExposable
    {
        internal int Id;
        internal ThingFilter Filter;

        public void ExposeData()
        {
            Scribe_Values.Look(ref Id, "output");
            Scribe_Deep.Look(ref Filter, "filter");
        }
    }

    public sealed partial class CompVerticalFactoryPort
    {
        private List<FactoryDestination> routes;
        private FactoryRoutingMode routingMode;
        private int lastOutput;
        internal int PendingOutput;
        internal int PendingCargo;
        internal int PendingDue;

        private void ExposeRoutes()
        {
            Scribe_Collections.Look(ref routes, "verticalFactoryRoutes", LookMode.Deep);
            Scribe_Values.Look(ref routingMode, "verticalFactoryRoutingMode", FactoryRoutingMode.Fixed);
            Scribe_Values.Look(ref lastOutput, "verticalFactoryLastOutput");
            Scribe_Values.Look(ref PendingOutput, "verticalFactoryPendingOutput");
            Scribe_Values.Look(ref PendingCargo, "verticalFactoryPendingCargo");
            Scribe_Values.Look(ref PendingDue, "verticalFactoryPendingDue");
        }

        private List<FactoryDestination> Routes
        {
            get
            {
                if (routes != null)
                    return routes;
                TryRestoreLegacyDestination();
                routes = new List<FactoryDestination>();
                if (destination != 0)
                    routes.Add(new FactoryDestination { Id = destination });
                return routes;
            }
        }

        internal CompVerticalFactoryPort DisplayOutput
        {
            get
            {
                var entries = Routes;
                return shaft?.FindOutput(PendingOutput != 0 ? PendingOutput : entries.Count == 0 ? 0 : entries[0].Id);
            }
        }

        internal bool AllowsDestination(int id, Thing item)
        {
            var entries = Routes;
            int limit = routingMode == FactoryRoutingMode.Fixed ? Math.Min(1, entries.Count) : entries.Count;
            for (int index = 0; index < limit; index++)
                if (entries[index].Id == id)
                    return entries[index].Filter?.Allows(item) != false;
            return false;
        }

        internal bool IsAssigned(int id) => routingMode == FactoryRoutingMode.Fixed
            ? Routes.Count != 0 && Routes[0].Id == id
            : Routes.Exists(entry => entry.Id == id);

        private int indicatorRevision = -1;
        private int indicatorTick = int.MinValue;
        private bool indicator;

        internal bool WarnUnlinked()
        {
            if (!parent.Spawned || Port.section)
                return false;
            if (shaft == null)
                return true;
            int tick = Find.TickManager.TicksGame / 30;
            if (indicatorRevision == shaft.Revision && tick == indicatorTick)
                return indicator;
            indicatorRevision = shaft.Revision;
            indicatorTick = tick;
            indicator = Port.input ? !HasStructuralRoute() : shaft.InputCount(this) == 0;
            return indicator;
        }

        private bool HasStructuralRoute()
        {
            var entries = Routes;
            int count = routingMode == FactoryRoutingMode.Fixed ? Math.Min(1, entries.Count) : entries.Count;
            for (int i = 0; i < count; i++)
                if (shaft.Length(this, shaft.FindOutput(entries[i].Id)) > 0)
                    return true;
            return false;
        }

        internal CompVerticalFactoryPort ChooseOutput(Thing item) =>
            FactoryRouteOrder.Select(routingMode, Routes, lastOutput, (Port: this, Item: item),
                static entry => entry.Id,
                static (state, entry) =>
                {
                    var output = state.Port.shaft.FindOutput(entry.Id);
                    return entry.Filter?.Allows(state.Item) != false && state.Port.CanSend(output, state.Item)
                        ? output : null;
                },
                routingMode == FactoryRoutingMode.FirstAvailable ? shaft.Outputs : null,
                static (state, output) => state.Port.AllowsDestination(output.parent.thingIDNumber, state.Item) &&
                    state.Port.CanSend(output, state.Item));

        private bool CanSend(CompVerticalFactoryPort output, Thing item) =>
            shaft.Length(this, output) > 0 && output.Accepts(item);

        internal void RecordArrival(int id) => lastOutput = id;

        private void ConfigureRoutes()
        {
            if (shaft == null)
                return;
            var entries = Routes;
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("AASBExt.Factory.Mode".Translate(ModeLabel(routingMode)), SelectMode),
                new FloatMenuOption("AASBExt.Factory.ClearDestination".Translate(), () =>
                {
                    entries.Clear();
                    RouteChanged();
                })
            };
            foreach (var output in shaft.Outputs)
            {
                int id = output.parent.thingIDNumber;
                var entry = entries.Find(route => route.Id == id);
                string label = Where(output);
                if (shaft.RouteFailure(this, output) is string failure)
                    label += "  ·  " + failure.Translate();
                if (entry == null)
                {
                    options.Add(new FloatMenuOption("AASBExt.Factory.AddRoute".Translate(label), () =>
                    {
                        if (!output.parent.Spawned || shaft?.FindOutput(id) != output)
                            return;
                        if (routingMode == FactoryRoutingMode.Fixed)
                            entries.Clear();
                        entries.Add(new FactoryDestination { Id = id });
                        RouteChanged();
                    }));
                }
                else
                {
                    options.Add(new FloatMenuOption("AASBExt.Factory.EditRoute".Translate(entries.IndexOf(entry) + 1, label),
                        () => EditRoute(entry)));
                }
            }
            foreach (var entry in entries)
                if (shaft.FindOutput(entry.Id) == null)
                    options.Add(new FloatMenuOption("AASBExt.Factory.MissingOutput".Translate(entry.Id), () => EditRoute(entry)));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static string ModeLabel(FactoryRoutingMode mode) => ("AASBExt.Factory.Mode." + mode).Translate();

        private void SelectMode()
        {
            var options = new List<FloatMenuOption>();
            foreach (FactoryRoutingMode mode in Enum.GetValues(typeof(FactoryRoutingMode)))
                options.Add(new FloatMenuOption(ModeLabel(mode), () =>
                {
                    routingMode = mode;
                    RouteChanged();
                }));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void EditRoute(FactoryDestination entry)
        {
            var entries = Routes;
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("AASBExt.Factory.RemoveRoute".Translate(), () =>
                {
                    entries.Remove(entry);
                    RouteChanged();
                }),
                new FloatMenuOption("AASBExt.Factory.Filter".Translate(), () =>
                {
                    if (entry.Filter == null)
                    {
                        entry.Filter = new ThingFilter();
                        entry.Filter.SetAllowAll(null);
                    }
                    Find.WindowStack.Add(new FactoryFilterWindow(entry.Filter));
                }),
                new FloatMenuOption("AASBExt.Factory.ClearFilter".Translate(), () => entry.Filter = null)
            };
            int index = entries.IndexOf(entry);
            if (index > 0)
                options.Add(new FloatMenuOption("AASBExt.Factory.RaisePriority".Translate(), () =>
                {
                    entries.RemoveAt(index);
                    entries.Insert(index - 1, entry);
                    RouteChanged();
                }));
            var output = shaft?.FindOutput(entry.Id);
            if (output?.parent.Spawned == true)
                options.Add(new FloatMenuOption("AASBExt.Factory.ShowPartner".Translate(),
                    () => CameraJumper.TryJumpAndSelect(output.parent)));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private void RouteChanged()
        {
            routeSelected = true;
            destination = Routes.Count == 0 ? 0 : Routes[0].Id;
            shaft?.Cancel(this);
        }

        private sealed class FactoryFilterWindow : Window
        {
            private readonly ThingFilter filter;
            private readonly ThingFilterUI.UIState state = new ThingFilterUI.UIState();

            internal FactoryFilterWindow(ThingFilter filter)
            {
                this.filter = filter;
                doCloseX = true;
            }

            public override Vector2 InitialSize => new Vector2(440, 540);

            public override void DoWindowContents(Rect inRect) =>
                ThingFilterUI.DoThingFilterConfigWindow(inRect, state, filter);
        }
    }
}
