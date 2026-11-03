using System.Collections.Generic;
using UnityEngine;

namespace RealisticRoads
{
    // An existing road changed into another along a stretch: every segment on
    // the way between two points, swapped for the same version (ground,
    // elevated, bridge, slope, tunnel) of the new road on the same nodes. The
    // road's shape and its junctions stay as they are.
    public class UpgradePlan
    {
        public NetInfo Info;                                            // the new road
        public readonly List<ushort> Segments = new List<ushort>();    // in no particular order
        public readonly List<NetInfo> OldInfos = new List<NetInfo>();
        public readonly List<NetInfo> NewInfos = new List<NetInfo>();
        // The part of each segment that changes (0..1 along it): all of it,
        // except where a point is on its middle, which cuts it there.
        public readonly List<float> From = new List<float>(), To = new List<float>();
        // For a one-way or uneven road: each segment built from the first
        // point towards the second (null: each keeps the way it was drawn).
        public List<bool> Inverts;
        // Nodes with only upgraded segments: they become the new road too.
        public readonly List<KeyValuePair<ushort, NetInfo>> Nodes = new List<KeyValuePair<ushort, NetInfo>>();
        public List<Vector3> Path;                                      // the way along the roads, for the preview
        public float Length;
        public int Cost;                                                // the new road's price less the old one's
        public int Unchanged;                                           // segments that already are the new road
        public readonly List<string> Problems = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public bool Ok => Problems.Count == 0 && Segments.Count > 0;
    }

    public static class RoadUpgrade
    {
        private static readonly string[] VersionFields = { "m_elevatedInfo", "m_bridgeInfo", "m_slopeInfo", "m_tunnelInfo" };
        private static readonly string[] VersionNames = { "elevated", "bridge", "slope", "tunnel" };

        // A cut this close to a segment's end (metres) goes to the end instead;
        // a part shorter than this isn't changed.
        private const float MinPart = 8f;

        // Main thread. The upgrade from (segA, tA) to (segB, tB) into 'info'. A
        // point on a node ('onNodeA'/'onNodeB') starts there; one on the middle
        // of a road cuts it there, and only the part towards the other point
        // changes.
        public static UpgradePlan Make(NetInfo info, ushort segA, float tA, bool onNodeA, ushort segB, float tB, bool onNodeB)
        {
            var plan = new UpgradePlan { Info = info };
            if (info == null || segA == 0 || segB == 0) return plan;
            var segments = NetManager.instance.m_segments.m_buffer;
            var kind = segments[segA].Info;
            if (!RoadPlanner.SameKind(kind, segments[segB].Info))
            {
                plan.Problems.Add("The two points are on different kinds of network: click two points on the same road");
                return plan;
            }
            if (!RoadPlanner.SameKind(kind, info))
            {
                plan.Problems.Add(info.name + " can't replace a " + (kind != null ? kind.name : "network") +
                                  ": pick a road of the same kind (roads for roads, tracks for tracks)");
                return plan;
            }
            var used = new HashSet<ushort>();
            var along = new Dictionary<ushort, bool>();
            plan.Path = Decks.RoadPath(segA, tA, segB, tB, used, kind, along);
            // A one-way road, or one with more lanes one way: it runs from the
            // first point to the second, whichever way each old segment was drawn.
            bool directed = info.m_hasForwardVehicleLanes != info.m_hasBackwardVehicleLanes ||
                            info.m_forwardVehicleLaneCount != info.m_backwardVehicleLaneCount;
            if (directed) plan.Inverts = new List<bool>();
            if (plan.Path == null)
            {
                plan.Problems.Add("No way along the roads between the two points");
                return plan;
            }
            if (!onNodeA) used.Add(segA);
            if (!onNodeB) used.Add(segB);
            // The part of the end segments between the points. Both on one
            // segment, straight along it: the part between them; the way round
            // through the network: those segments whole, as before.
            var parts = new Dictionary<ushort, KeyValuePair<float, float>>();
            float pathLength = 0f;
            for (int i = 1; i < plan.Path.Count; i++) pathLength += (plan.Path[i] - plan.Path[i - 1]).magnitude;
            if (segA == segB)
            {
                if (!onNodeA && !onNodeB && Mathf.Abs(pathLength - Mathf.Abs(tA - tB) * segments[segA].m_averageLength) < 2f)
                    parts[segA] = new KeyValuePair<float, float>(Mathf.Min(tA, tB), Mathf.Max(tA, tB));
            }
            else
            {
                bool forward;
                if (!onNodeA && along.TryGetValue(segA, out forward))
                    parts[segA] = forward ? new KeyValuePair<float, float>(tA, 1f) : new KeyValuePair<float, float>(0f, tA);
                if (!onNodeB && along.TryGetValue(segB, out forward))
                    parts[segB] = forward ? new KeyValuePair<float, float>(0f, tB) : new KeyValuePair<float, float>(tB, 1f);
            }
            var ordered = new List<ushort>(used);
            ordered.Sort();

            var missing = new Dictionary<string, int>();
            bool wider = false;
            long cost = 0;
            var terrain = TerrainManager.instance;
            var nodes = NetManager.instance.m_nodes.m_buffer;
            foreach (ushort segment in ordered)
            {
                var s = segments[segment];
                if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || s.Info == null) continue;
                var old = s.Info;
                int version = VersionOf(old);
                var target = version < 0 ? info : RoadPlanner.Variant(info, VersionFields[version]);
                // An elevated road may become a bridge, and a bridge an elevated road.
                if (target == null && version == 0) target = RoadPlanner.Variant(info, VersionFields[1]);
                if (target == null && version == 1) target = RoadPlanner.Variant(info, VersionFields[0]);
                if (target == null)
                {
                    int count;
                    missing.TryGetValue(VersionNames[version], out count);
                    missing[VersionNames[version]] = count + 1;
                    continue;
                }
                bool wantInvert = false;
                if (plan.Inverts != null)
                {
                    bool forward;
                    wantInvert = along.TryGetValue(segment, out forward) ? !forward : (s.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None;
                }
                // Already the new road (and, one-way, already the right way round).
                if (target == old && (plan.Inverts == null || wantInvert == ((s.m_flags & NetSegment.Flags.Invert) != NetSegment.Flags.None)))
                {
                    plan.Unchanged++;
                    continue;
                }
                float from = 0f, to = 1f;
                KeyValuePair<float, float> part;
                if (parts.TryGetValue(segment, out part))
                {
                    float length = Mathf.Max(s.m_averageLength, 1f), snap = MinPart / length;
                    from = part.Key < snap ? 0f : part.Key;
                    to = part.Value > 1f - snap ? 1f : part.Value;
                    if ((to - from) * length < MinPart) continue;
                }
                plan.Segments.Add(segment);
                plan.OldInfos.Add(old);
                plan.NewInfos.Add(target);
                plan.From.Add(from);
                plan.To.Add(to);
                if (plan.Inverts != null) plan.Inverts.Add(wantInvert);
                plan.Length += s.m_averageLength * (to - from);
                if (target.m_halfWidth > old.m_halfWidth + 0.5f) wider = true;
                Vector3 a = nodes[s.m_startNode].m_position, b = nodes[s.m_endNode].m_position;
                float ha = a.y - terrain.SampleRawHeightSmooth(a), hb = b.y - terrain.SampleRawHeightSmooth(b);
                long difference = System.Math.Max(0L, NetworkBuilder.PieceCost(target, info, a, b, ha, hb) - NetworkBuilder.PieceCost(old, old, a, b, ha, hb));
                cost += (long)(difference * (to - from));
            }
            plan.Cost = (int)System.Math.Min(cost, int.MaxValue);

            // Nodes between upgraded segments only take the new road (a node at a
            // junction with other roads is left to them).
            var upgraded = new HashSet<ushort>(plan.Segments);
            var seen = new HashSet<ushort>();
            for (int k = 0; k < plan.Segments.Count; k++)
            {
                var s = segments[plan.Segments[k]];
                foreach (ushort node in new[] { s.m_startNode, s.m_endNode })
                {
                    if (!Reaches(plan, k, node) || !seen.Add(node)) continue;
                    NetInfo nodeInfo = null;
                    bool inner = true;
                    for (int i = 0; i < 8; i++)
                    {
                        ushort other = nodes[node].GetSegment(i);
                        if (other == 0) continue;
                        if (!upgraded.Contains(other) || !Reaches(plan, plan.Segments.IndexOf(other), node))
                        {
                            inner = false;
                            break;
                        }
                        var otherInfo = plan.NewInfos[plan.Segments.IndexOf(other)];
                        // A portal's node is the tunnel's, not the slope's.
                        if (nodeInfo == null || VersionOf(nodeInfo) == 2) nodeInfo = otherInfo;
                    }
                    if (inner && nodeInfo != null) plan.Nodes.Add(new KeyValuePair<ushort, NetInfo>(node, nodeInfo));
                }
            }

            foreach (var pair in missing)
                plan.Warnings.Add(pair.Value + " " + pair.Key + " segment(s) stay as they are: " + info.name + " has no " + pair.Key + " version");
            if (wider) plan.Warnings.Add("Wider than the road it replaces: bulldoze buildings in the way first (they are not removed)");
            if (plan.Segments.Count == 0)
                plan.Problems.Add(plan.Unchanged > 0 ? "It is already " + info.name + " all the way" : "Nothing to upgrade between the two points");
            return plan;
        }

        // Whether segment k's upgraded part runs to this end node of it.
        private static bool Reaches(UpgradePlan plan, int k, ushort node)
        {
            var s = NetManager.instance.m_segments.m_buffer[plan.Segments[k]];
            return node == s.m_startNode ? plan.From[k] <= 0f : node == s.m_endNode && plan.To[k] >= 1f;
        }

        // Which version of its road a network is: an index into VersionFields,
        // or -1 for a road itself (or one no road lists as a version).
        // Worked out once from every loaded road; Forget() for a new city.
        public static int VersionOf(NetInfo info)
        {
            if (info == null) return -1;
            if (versions == null)
            {
                versions = new Dictionary<NetInfo, int>();
                int count = PrefabCollection<NetInfo>.LoadedCount();
                for (uint i = 0; i < count; i++)
                {
                    var road = PrefabCollection<NetInfo>.GetLoaded(i);
                    if (road == null) continue;
                    for (int v = 0; v < VersionFields.Length; v++)
                    {
                        var version = RoadPlanner.Variant(road, VersionFields[v]);
                        if (version != null && version != road && !versions.ContainsKey(version)) versions[version] = v;
                    }
                }
            }
            int found;
            return versions.TryGetValue(info, out found) ? found : -1;
        }

        private static Dictionary<NetInfo, int> versions;

        public static void Forget()
        {
            versions = null;
        }
    }
}
