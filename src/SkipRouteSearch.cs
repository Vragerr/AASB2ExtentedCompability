using System;
using System.Collections.Generic;

namespace AASB2ExtendedCompatibility
{
    internal static class SkipRouteSearch
    {
        internal readonly record struct Edge(int Entry, int Exit);
        internal readonly record struct Route(IReadOnlyList<Edge> Steps, float Cost);

        internal static Route? Find(int count, Func<int, int> band, Func<int, bool> canEnter,
            Func<int, bool> canExit, Func<int, int, float> walk, Func<int, float> finish, Func<int, int, bool> canJump, float jumpCost)
        {
            if (count < 2 || !float.IsFinite(jumpCost) || jumpCost <= 0)
                return null;
            var best = new float[2 * count];
            var previous = new int[best.Length];
            Array.Fill(previous, -1);
            var closed = new bool[best.Length];
            Array.Fill(best, float.PositiveInfinity);
            for (int door = 0; door < count; door++)
                if (canEnter(door))
                    best[2 * door + 1] = walk(-1, door);

            int last = -1;
            float totalCost = float.PositiveInfinity;
            for (;;)
            {
                int current = -1;
                float cost = totalCost;
                for (int node = 0; node < best.Length; node++)
                    if (!closed[node] && best[node] < cost)
                    {
                        current = node;
                        cost = best[node];
                    }
                if (current == -1)
                    break;

                closed[current] = true;
                int from = current / 2;
                bool boarding = (current & 1) == 1;
                if (!boarding)
                {
                    float total = cost + finish(from);
                    if (total < totalCost)
                    {
                        last = current;
                        totalCost = total;
                    }
                }

                for (int to = 0; to < count; to++)
                {
                    bool same = band(from) == band(to);
                    int next = 2 * to + (boarding ? 0 : 1);
                    if (closed[next] || (boarding ? same || !canExit(to) || !canJump(from, to) : !same || !canEnter(to)))
                        continue;
                    float candidate = cost + (boarding ? jumpCost : walk(from, to));
                    if (candidate >= best[next])
                        continue;
                    best[next] = candidate;
                    previous[next] = current;
                }
            }
            if (last < 0)
                return null;
            var steps = new List<Edge>();
            for (int node = last; previous[node] >= 0; node = previous[node])
                if ((node & 1) == 0)
                    steps.Add(new Edge(previous[node] / 2, node / 2));
            steps.Reverse();
            return new Route(steps, totalCost);
        }
    }
}
