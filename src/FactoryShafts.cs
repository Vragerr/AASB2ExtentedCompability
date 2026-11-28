using System;
using System.Collections.Generic;
using System.Text;
using Verse;

namespace AASB2ExtendedCompatibility
{
    public sealed class FactoryShafts : MapComponent
    {
        private readonly Dictionary<IntVec2, FactoryShaft> shafts = new Dictionary<IntVec2, FactoryShaft>();

        public FactoryShafts(Map map) : base(map) { }

        internal FactoryShaft Register(IntVec2 local, CompVerticalFactoryPort port)
        {
            if (!shafts.TryGetValue(local, out var shaft))
                shafts.Add(local, shaft = new FactoryShaft(map, local));
            return shaft.Add(port) ? shaft : null;
        }

        internal void Unregister(FactoryShaft shaft, CompVerticalFactoryPort port)
        {
            shaft.Remove(port);
            if (shaft.Count == 0)
                shafts.Remove(shaft.Local);
        }
    }

    internal sealed class FactoryShaft
    {
        private const int TicksPerBoundary = 40;
        private readonly Map map;
        private readonly SortedDictionary<int, CompVerticalFactoryPort> ports =
            new SortedDictionary<int, CompVerticalFactoryPort>();
        private readonly Dictionary<int, CompVerticalFactoryPort> outputs = new Dictionary<int, CompVerticalFactoryPort>();
        internal int Revision { get; private set; }
        private bool restorePending = true;
        private int lastTick = -1;
        private int lastServedFloor = int.MinValue;
        private CompVerticalFactoryPort source, target;
        private Thing cargo;
        private int due;

        internal IntVec2 Local { get; }
        internal int Count => ports.Count;

        internal FactoryShaft(Map map, IntVec2 local)
        {
            this.map = map;
            Local = local;
        }

        internal bool Add(CompVerticalFactoryPort port)
        {
            if (ports.ContainsKey(port.Floor))
                return false;
            Revision++;
            ports.Add(port.Floor, port);
            if (port.Output)
                outputs.Add(port.parent.thingIDNumber, port);
            return true;
        }

        internal void Remove(CompVerticalFactoryPort port)
        {
            if (At(port.Floor) != port)
                return;
            Revision++;
            ports.Remove(port.Floor);
            if (port.Output)
                outputs.Remove(port.parent.thingIDNumber);
            if (source != null && Length(source, target) == 0)
                Clear();
        }

        internal CompVerticalFactoryPort At(int floor) =>
            ports.TryGetValue(floor, out var port) ? port : null;

        internal IEnumerable<CompVerticalFactoryPort> Outputs
        {
            get
            {
                foreach (var port in ports.Values)
                    if (port.Output)
                        yield return port;
            }
        }

        internal CompVerticalFactoryPort FindOutput(int id) =>
            outputs.TryGetValue(id, out var output) ? output : null;

        internal int InputCount(CompVerticalFactoryPort output)
        {
            var count = 0;
            foreach (var port in ports.Values)
                if (port.Port.input && port.IsAssigned(output.parent.thingIDNumber) && Length(port, output) > 0)
                    count++;
            return count;
        }

        internal int Length(CompVerticalFactoryPort from, CompVerticalFactoryPort to) =>
            RouteFailure(from, to) == null ? Math.Abs(to.Floor - from.Floor) : 0;

        internal string RouteFailure(CompVerticalFactoryPort from, CompVerticalFactoryPort to)
        {
            if (from == null || to?.Output != true || from.Floor == to.Floor ||
                At(from.Floor) != from || At(to.Floor) != to)
                return "AASBExt.Factory.OutputGone";
            int start = Math.Min(from.Floor, to.Floor);
            int end = Math.Max(from.Floor, to.Floor);
            for (int floor = start; floor <= end; floor++)
            {
                var port = At(floor);
                if (port?.parent.Spawned != true)
                    return "AASBExt.Factory.RouteBroken";
                if (!CompVerticalFactoryPort.Supported(map, port.parent.Position))
                    return "AASBExt.Factory.RouteUnsupported";
            }
            return null;
        }

        internal int Remaining(CompVerticalFactoryPort port) =>
            source == port ? Math.Max(0, due - Find.TickManager.TicksGame) : 0;

        internal void Cancel(CompVerticalFactoryPort port)
        {
            Revision++;
            if (source == port)
                Clear();
        }

        private void Clear()
        {
            if (source != null)
                source.PendingOutput = source.PendingCargo = source.PendingDue = 0;
            source = null;
            target = null;
            cargo = null;
            due = 0;
        }

        private bool StillValid() =>
            source.Powered && target.Powered && source.AllowsDestination(target.parent.thingIDNumber, cargo) &&
            source.Cargo?.Contains(cargo) == true && target.Accepts(cargo) && Length(source, target) > 0;

        internal void Advance(int tick)
        {
            if (tick == lastTick)
                return;
            lastTick = tick;
            if (restorePending)
            {
                restorePending = false;
                RestorePending();
            }
            if (source != null)
            {
                if (!StillValid())
                {
                    lastServedFloor = source.Floor;
                    Clear();
                }
                else if (tick >= due)
                {
                    var origin = source;
                    var output = target;
                    if (origin.Cargo.TryTransferToContainer(cargo, output.Cargo, false))
                    {
                        origin.RecordArrival(output.parent.thingIDNumber);
                        FactoryPorts.Restart(output.parent);
                    }
                    lastServedFloor = origin.Floor;
                    Clear();
                }
                return;
            }

            if (Start(tick, afterLast: true))
                return;
            Start(tick, afterLast: false);
        }

        private bool Start(int tick, bool afterLast)
        {
            foreach (var port in ports.Values)
            {
                if ((port.Floor > lastServedFloor) != afterLast || !port.Port.input || !port.Powered ||
                    port.Cargo?.Count != 1)
                    continue;
                var item = port.Cargo[0];
                var output = port.ChooseOutput(item);
                if (output == null)
                    continue;
                int duration = Length(port, output) * TicksPerBoundary;
                SetActive(port, output, item, tick + duration);
                return true;
            }
            return false;
        }

        private void SetActive(CompVerticalFactoryPort port, CompVerticalFactoryPort output, Thing item, int deadline)
        {
            source = port;
            target = output;
            cargo = item;
            due = deadline;
            port.PendingOutput = output.parent.thingIDNumber;
            port.PendingCargo = item.thingIDNumber;
            port.PendingDue = deadline;
        }

        private void RestorePending()
        {
            foreach (var port in ports.Values)
            {
                int outputId = port.PendingOutput;
                if (!port.Port.input || outputId == 0)
                    continue;
                var output = FindOutput(outputId);
                var item = port.Cargo?.Count == 1 ? port.Cargo[0] : null;
                if (source == null && item?.thingIDNumber == port.PendingCargo && port.Powered &&
                    Length(port, output) > 0 && port.AllowsDestination(outputId, item) && output.Accepts(item))
                    SetActive(port, output, item, port.PendingDue);
                else
                    port.PendingOutput = port.PendingCargo = port.PendingDue = 0;
            }
        }

        internal string Describe(CompVerticalFactoryPort port)
        {
            var result = new StringBuilder($"local={Local} band={port.Floor} ");
            foreach (var node in ports.Values)
                result.Append(node.Floor).Append(':')
                    .Append(node.Port.input ? "input" : node.Output ? "output" : "section")
                    .Append('#').Append(node.parent.thingIDNumber).Append(' ');
            var configured = port.DisplayOutput;
            return result.Append(" hops=").Append(Length(port, configured)).Append(" activeInput=")
                .Append(source?.parent.thingIDNumber).Append(" activeOutput=").Append(target?.parent.thingIDNumber)
                .Append(" remaining=").Append(Remaining(port)).ToString();
        }
    }
}
