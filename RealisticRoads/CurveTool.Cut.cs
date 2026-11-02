using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealisticRoads
{
    // Median cuts: a metro or train track coming down from a raised track's end
    // through an open cut in a road's middle into a tunnel, or up out of a
    // tunnel's end to the deck level. One drawn line: the track along it (one
    // slope piece, the game's own, for the open cut with its portal), and the
    // road beside it on the ground with an open gap for it (SplitRoads' median
    // cut roads): along a six-lane or four-lane road, that road under the line;
    // on open ground, a stretch of the six-lane road before it and its ground
    // road along the line. Built part by part, one Ctrl+Z for all of it.
    public partial class CurveTool
    {
        private const float CutDepth = RoadPlanner.TunnelMinDepth + 1f;   // the tunnel at the bottom of the cut: this far under the ground

        // A node or a segment of the median cut's track (any of its versions).
        private bool OnCutTrack(ushort node, ushort segment)
        {
            var track = CutTrack;
            if (track == null || track.m_class == null) return false;
            var net = NetManager.instance;
            if (segment != 0) return IsTrackOf(net.m_segments.m_buffer[segment].Info, track);
            if (node == 0) return false;
            for (int i = 0; i < 8; i++)
            {
                ushort s = net.m_nodes.m_buffer[node].GetSegment(i);
                if (s != 0 && IsTrackOf(net.m_segments.m_buffer[s].Info, track)) return true;
            }
            return false;
        }

        private static bool IsTrackOf(NetInfo info, NetInfo track)
        {
            return info != null && info.m_class != null && track != null && track.m_class != null &&
                   info.m_class.m_service == track.m_class.m_service && info.m_class.m_subService == track.m_class.m_subService;
        }

        // The road a median cut is made in, under 'p' and running along
        // 'heading' (within 30 degrees): a two-way six-lane or four-lane road
        // with median cut roads made for it. Null if none ('segment' 0).
        private static NetInfo CutRoadUnder(Vector3 p, Vector3 heading, out ushort segment, out float t)
        {
            segment = 0;
            t = 0f;
            var segments = NetManager.instance.m_segments.m_buffer;
            Vector3 way = CurveMath.FlatNormal(heading);
            float ahead = Mathf.Cos(30f * Mathf.Deg2Rad), best = float.MaxValue;
            NetInfo found = null;
            foreach (ushort s in RoadPlanner.NearbySegments(p, 40f))
            {
                if ((segments[s].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) continue;
                var info = segments[s].Info;
                if (info == null || SplitRoads.WidestCut(info) <= 0f) continue;
                Vector3 onRoad;
                float at = CrossingFinder.ClosestT(s, p, out onRoad);
                float distance = CurveMath.FlatDistance(onRoad, p);
                if (distance > info.m_halfWidth || distance >= best) continue;
                Vector3 a, b, c, d;
                CrossingFinder.SegmentBezier(s, out a, out b, out c, out d);
                if (way != Vector3.zero && Mathf.Abs(Vector3.Dot(CurveMath.FlatNormal(CurveMath.BezierDerivative(a, b, c, d, at)), way)) < ahead) continue;
                best = distance;
                found = info;
                segment = s;
                t = at;
            }
            return found;
        }

        // The median cut's profile: down from the raised track it starts on at
        // its grade to CutDepth under the ground (or the tunnel it ends on), then
        // level; or up from the tunnel it starts on to the deck level (or the
        // raised track it ends on), then level. Null if fine, else why not.
        private string CutProfile(List<Vector3> controls, List<ushort> nodes, int n, float length, out bool down)
        {
            down = false;
            medianProfile = null;
            medianSummary = null;
            var track = CutTrack;
            string kind = medianRamp == 4 ? "metro" : "train";
            if (track == null) return "The game's " + kind + " track wasn't found";
            var terrain = TerrainManager.instance;
            var net = NetManager.instance;
            float grade = RoadPlanner.MedianCutGrade(track);
            if (nodes[0] == 0)
                return "Start the " + MedianRampNames[medianRamp] + " on the end of a raised or underground " + kind + " track: it comes down from a raised one " +
                       "into a tunnel, or up out of a tunnel";
            Vector3 start = net.m_nodes.m_buffer[nodes[0]].m_position;
            Vector3 end = nodes[n] != 0 ? net.m_nodes.m_buffer[nodes[n]].m_position : controls[n];
            float startGround = terrain.SampleRawHeightSmooth(start), endGround = terrain.SampleRawHeightSmooth(end);
            float elevation = start.y - startGround;
            string pct = (grade * 100f).ToString("0.#") + " %";
            if (elevation >= DeckJoinHeight)
            {
                down = true;
                float top = start.y;
                bool joinsTunnel = nodes[n] != 0 && end.y - endGround <= -RoadPlanner.TunnelMinDepth;
                float bottom = joinsTunnel ? end.y : endGround - CutDepth;
                if (top - bottom < 1f) return "The track it starts on is no higher than where the tunnel would be";
                float fall = (top - bottom) / grade;
                if (length < fall + MinSpacing)
                    return "The track needs " + (fall + MinSpacing).ToString("N0") + " m to come down at " + pct + " from " + elevation.ToString("F1") + " m up into a tunnel " +
                           CutDepth.ToString("F0") + " m under the ground, and the line drawn is " + length.ToString("N0") + " m: draw it longer";
                medianProfile = (d, total) => Mathf.Max(bottom, top - grade * d);
                medianSummary = "down " + (top - bottom).ToString("F1") + " m at " + pct + " in " + fall.ToString("N0") + " m: through an open cut in the road's middle " +
                                "into a tunnel, then in the tunnel for " + Mathf.Max(0f, length - fall).ToString("N0") + " m";
                return null;
            }
            if (elevation <= -RoadPlanner.TunnelMinDepth + 0.5f)
            {
                float from = start.y;
                bool joinsDeck = nodes[n] != 0 && end.y - endGround >= DeckJoinHeight;
                string over = null;
                float target = joinsDeck ? end.y : startGround + MedianDeckLevel(controls, nodes[n], out over);
                if (target - from < 1f) return "The track it ends on is no higher than the tunnel it starts in";
                float rise = (target - from) / grade;
                if (length < rise)
                    return "The track needs " + rise.ToString("N0") + " m to come up at " + pct + " from the tunnel to " + (joinsDeck ? "the track it joins" : "the deck level") +
                           " and the line drawn is " + length.ToString("N0") + " m: draw it longer";
                medianProfile = (d, total) => Mathf.Min(target, from + grade * d);
                medianSummary = "up " + (target - from).ToString("F1") + " m at " + pct + " in " + rise.ToString("N0") + " m: out of the tunnel through an open cut in the road's middle " +
                                "to " + (joinsDeck ? "the track it joins" : "the deck level" + (over != null ? " (" + over + ")" : "")) + ", then level for " +
                                Mathf.Max(0f, length - rise).ToString("N0") + " m";
                return null;
            }
            return "Start the " + MedianRampNames[medianRamp] + " on the end of a raised " + kind + " track (at least " + DeckJoinHeight.ToString("F0") +
                   " m up) or an underground one (at least " + RoadPlanner.TunnelMinDepth.ToString("F0") + " m down); this one is " + elevation.ToString("F1") + " m from the ground";
        }

        // Each road the track crosses: over it where the track is high enough,
        // under it in the tunnel where it is deep enough (else the plan says
        // where the track is too close to it). Not a deck's crossing: the track
        // is held at its own profile.
        private void TypeCutCrossings(Division division)
        {
            if (medianProfile == null || division.Crossings == null) return;
            foreach (var crossing in division.Crossings)
            {
                crossing.OnDeck = false;
                crossing.UnderDeck = false;
                float h = medianProfile(crossing.Distance, division.Length);
                crossing.Type = h > crossing.Point.y ? CrossingType.Bridge : CrossingType.Tunnel;
            }
        }

        // The open cut is one slope piece, the game's own (it is made to go from
        // the ground down to a tunnel's depth along one segment, the portal at its
        // deep end): the track's nodes below the ground but not yet in the tunnel
        // go, but for the one nearest the ground, which is put on it.
        private void OneSlope(Division division)
        {
            var plan = division.Plan;
            if (plan == null || plan.Kinds == null) return;
            var terrain = TerrainManager.instance;
            int count = plan.Points.Count;
            Func<int, float> elevation = i => plan.Points[i].y - terrain.SampleRawHeightSmooth(plan.Points[i]);
            int first = -1, last = -1;
            for (int i = 0; i < count; i++)
            {
                if (plan.Kinds[i] == NodeKind.Tunnel || elevation(i) > -1f) continue;
                if (first < 0) first = i;
                last = i;
            }
            if (first < 0) return;
            bool tunnelAfter = last + 1 < count && plan.Kinds[last + 1] == NodeKind.Tunnel;
            bool tunnelBefore = first > 0 && plan.Kinds[first - 1] == NodeKind.Tunnel;
            if (tunnelAfter == tunnelBefore) return;   // no tunnel next to it (CheckCut says so), or a dip between two
            int surface = tunnelAfter ? first - 1 : last + 1;
            if (surface < 0 || surface >= count) return;
            int keep = surface;
            float best = Mathf.Abs(elevation(surface));
            for (int i = first; i <= last; i++)
            {
                float e = Mathf.Abs(elevation(i));
                if (e >= best) continue;
                best = e;
                keep = i;
            }
            for (int c = 0; c < division.CrossingIndex.Count; c++)
            {
                int k = division.CrossingIndex[c];
                if (k >= first && k <= last && k != keep)
                    plan.Problems.Add("A road crosses the open cut at " + division.Crossings[c].Distance.ToString("N0") + " m (" + division.Crossings[c].ExistingName +
                                      "): start or end the line so the cut is clear of it");
            }
            var p = plan.Points[keep];
            p.y = terrain.SampleRawHeightSmooth(p);
            plan.Points[keep] = p;
            plan.Kinds[keep] = NodeKind.Ground;
            for (int i = last; i >= first; i--)
            {
                if (i == keep) continue;
                division.Points.RemoveAt(i);
                division.Tangents.RemoveAt(i);
                division.InTangents.RemoveAt(i);
                RoadPlanner.RemoveNode(plan, i);
                for (int c = 0; c < division.CrossingIndex.Count; c++)
                {
                    if (division.CrossingIndex[c] == i) division.CrossingIndex[c] = -1;
                    else if (division.CrossingIndex[c] > i) division.CrossingIndex[c]--;
                }
            }
        }

        // What the panel says about a median cut, and what stops it being built.
        private void CheckCut(Division division, string profileProblem)
        {
            var plan = division.Plan;
            plan.MedianAlong = cutRoad != null;
            if (profileProblem != null)
            {
                plan.Problems.Insert(0, profileProblem);
                return;
            }
            bool tunnel = false, surface = false;
            foreach (var kind in plan.Kinds)
            {
                if (kind == NodeKind.Tunnel) tunnel = true;
                else if (kind == NodeKind.Ground || kind == NodeKind.Elevated || kind == NodeKind.Bridge) surface = true;
            }
            if (!tunnel)
                plan.Problems.Insert(0, "The track isn't in a tunnel anywhere (two of its nodes at least " + RoadPlanner.TunnelMinDepth.ToString("F0") +
                                        " m under the ground): draw the line on further");
            else if (!surface) plan.Problems.Insert(0, "The track never comes out of the ground: start it on a raised track, or end it on one");
            if (MedianGroundSplit == null)
                plan.Problems.Insert(0, "No road with a gap wide enough for this track (" + (2f * TrackHalf(CutTrack)).ToString("F1") + " m) was made when the city loaded");
            if (medianSummary != null) plan.Warnings.Add("Median cut: " + medianSummary);
            if (cutRoad != null)
                plan.Warnings.Add("The " + Title(cutRoad) + " under it becomes its ground road along the line, with an open gap for the track " +
                                  "(its inner lanes end where the gap begins)");
            else
                plan.Warnings.Add("Not along a six-lane or four-lane road: " + LeadIn.ToString("F0") + " m of " + Title(MedianGroundRoad) +
                                  " is built before it, and its ground road (with the gap) along the line");
        }

        // The track first (one Ctrl+Z group with the rest).
        private void StartCut(Division division)
        {
            var track = DrawNetwork;
            var ground = MedianGroundSplit;
            if (track == null || ground == null)
            {
                Mod.Log("Median cut not built: its track or its road with the gap wasn't found");
                return;
            }
            if (!division.Plan.MedianAlong)
            {
                var check = DivideGround(division, ground);
                string why = check == null || check.Plan == null ? "its road can't be drawn here"
                    : !check.Ok ? "its road bends too sharply"
                    : !check.Plan.Ok ? "its road: " + string.Join("; ", check.Plan.Problems.ToArray()) : null;
                if (why != null)
                {
                    Mod.Log("Median cut not built: " + why);
                    Mod.WarnNow("Median cut not built", char.ToUpper(why[0]) + why.Substring(1) + ".");
                    return;
                }
            }
            var waiting = new PendingParallel
            {
                Main = division, Ground = ground, Full = MedianGroundRoad, Group = NetworkBuilder.NewGroup(), RampInfo = track,
                Cut = true, Stage = MedianStage.CutTrack,
            };
            Mod.Log("Median cut " + MedianRampNames[medianRamp] + ": " + (medianSummary ?? "no profile") + "; road " + waiting.Full.name + ", ground road " + ground.name);
            int slopes = 0, tunnels = 0;
            for (int i = 1; i < division.Plan.Kinds.Length; i++)
            {
                var a = division.Plan.Kinds[i - 1];
                var b = division.Plan.Kinds[i];
                if (a == NodeKind.Tunnel && b == NodeKind.Tunnel) tunnels++;
                else if (a == NodeKind.Tunnel || b == NodeKind.Tunnel) slopes++;
            }
            Mod.Log("Median cut, step 1: building " + track.name + ": " + division.Length.ToString("F0") + " m in " + (division.Points.Count - 1) + " segment(s) (" +
                    slopes + " slope piece(s), " + tunnels + " in the tunnel), " + division.Crossings.Count + " crossing(s)");
            NetworkBuilder.BuildCurve(track, division.Plan, waiting.Group, built => waiting.State = built ? 1 : 2);
            pending = waiting;
        }

        // Each part once the one before it is built (FinishParallel).
        private void FinishCut(PendingParallel waiting)
        {
            var cut = waiting.Main;
            bool failed = waiting.State != 1 || (waiting.Stage == MedianStage.CutConvert && waiting.Converted < 0);
            if (!failed) waiting.AnyBuilt = true;
            if (failed)
            {
                FailMedian(waiting, waiting.Stage == MedianStage.CutTrack ? "the track couldn't be built" :
                                    waiting.Stage == MedianStage.CutSplit ? "the road under where it starts wasn't found (draw it along the middle of the road)" :
                                    waiting.Stage == MedianStage.CutConvert ? "the road under it couldn't be turned into its ground road" :
                                    waiting.Stage == MedianStage.CutLeadIn ? "the " + LeadIn.ToString("F0") + " m of " + Title(waiting.Full) + " couldn't be built" :
                                    waiting.Stage == MedianStage.CutStarts || waiting.Stage == MedianStage.CutEndStarts ? "the pieces where the gap begins couldn't be made: " + waiting.StartsWhy :
                                    "its road couldn't be built");
                return;
            }
            int lanes = waiting.Full != null ? waiting.Full.m_forwardVehicleLaneCount + waiting.Full.m_backwardVehicleLaneCount : 0;
            switch (waiting.Stage)
            {
                case MedianStage.CutTrack:
                    Mod.Log("Median cut, step 2: the track is built");
                    if (cut.Plan.MedianAlong)
                    {
                        NextMedian(waiting, MedianStage.CutSplit);
                        NetworkBuilder.RoadNodeAt(cut.Polyline[0], cut.Polyline[1] - cut.Polyline[0], lanes, waiting.Group, node =>
                        {
                            waiting.Origin = node;
                            waiting.State = node != 0 ? 1 : 2;
                        });
                        return;
                    }
                    {
                        Vector3 at = cut.Polyline[0];
                        Vector3 away = -(cut.Polyline[1] - cut.Polyline[0]);
                        at.y = TerrainManager.instance.SampleRawHeightSmooth(at);
                        var lead = DivideLeadIn(at, away, false, waiting.Full);
                        string why = lead == null || lead.Plan == null ? "the " + LeadIn.ToString("F0") + " m of " + Title(waiting.Full) + " can't be drawn there"
                            : !lead.Ok || !lead.Plan.Ok ? "the " + LeadIn.ToString("F0") + " m of " + Title(waiting.Full) + ": " +
                                                          (lead.Plan.Ok ? "it bends too sharply" : string.Join("; ", lead.Plan.Problems.ToArray()))
                            : null;
                        if (why != null)
                        {
                            FailMedian(waiting, why);
                            return;
                        }
                        Mod.Log("Median cut, next: building " + LeadIn.ToString("F0") + " m of " + waiting.Full.name + " to where the gap begins");
                        waiting.LeadAt = at;
                        NextMedian(waiting, MedianStage.CutLeadIn);
                        NetworkBuilder.BuildCurve(waiting.Full, lead.Plan, waiting.Group, built => waiting.State = built ? 1 : 2);
                    }
                    return;
                case MedianStage.CutSplit:
                    Mod.Log("Median cut, next: the road under it becomes " + waiting.Ground.name + " from node #" + waiting.Origin);
                    NextMedian(waiting, MedianStage.CutConvert);
                    NetworkBuilder.ConvertAlong(new List<Vector3>(cut.Polyline), waiting.Origin, waiting.Ground, lanes, waiting.Group, (stop, along, count) =>
                    {
                        waiting.Stop = stop;
                        waiting.StopAlong = along;
                        waiting.Converted = count;
                        waiting.State = 1;
                    });
                    return;
                case MedianStage.CutConvert:
                    if (waiting.Converted == 0)
                    {
                        FailMedian(waiting, "no stretch of the road under it could become its ground road");
                        return;
                    }
                    if (waiting.StopAlong < cut.Length - MinSpacing) BuildCutGround(waiting, waiting.Stop, waiting.StopAlong);
                    else CutStarts(waiting, waiting.Origin, MedianStage.CutStarts);
                    return;
                case MedianStage.CutLeadIn:
                {
                    ushort end = FullRoadEndAt(waiting.LeadAt, waiting.Full);
                    if (end == 0)
                    {
                        FailMedian(waiting, "the end of the " + LeadIn.ToString("F0") + " m of " + Title(waiting.Full) + " wasn't found");
                        return;
                    }
                    waiting.Origin = end;
                    waiting.Stop = 0;
                    BuildCutGround(waiting, end, 0f);
                    return;
                }
                case MedianStage.CutGround:
                    waiting.Stop = 0;   // its far end is the ground road's own (free, or on what it ends on)
                    CutStarts(waiting, waiting.Origin, MedianStage.CutStarts);
                    return;
                case MedianStage.CutStarts:
                    // Where the line ended in the road: there too.
                    if (waiting.Stop != 0 && waiting.Stop != waiting.Origin && GapEndsAt(waiting.Stop, waiting.Ground))
                    {
                        CutStarts(waiting, waiting.Stop, MedianStage.CutEndStarts);
                        return;
                    }
                    Mod.Log("Median cut, last: all parts built (one Ctrl+Z removes it all)");
                    return;
                case MedianStage.CutEndStarts:
                    Mod.Log("Median cut, last: all parts built (one Ctrl+Z removes it all)");
                    return;
            }
        }

        // The road with the gap along the line from 'from' ('fromAlong' along it).
        private void BuildCutGround(PendingParallel waiting, ushort from, float fromAlong)
        {
            var ground = DivideGround(waiting.Main, waiting.Ground, from, fromAlong);
            string why = ground == null || ground.Plan == null ? "its road beside the track can't be drawn there"
                : !ground.Ok ? "its road beside the track bends too sharply"
                : !ground.Plan.Ok ? "its road beside the track: " + string.Join("; ", ground.Plan.Problems.ToArray())
                : null;
            if (why != null)
            {
                FailMedian(waiting, why);
                return;
            }
            Mod.Log("Median cut, next: building " + waiting.Ground.name + " beside the track from node #" + from + ": " + ground.Length.ToString("F0") + " m in " +
                    (ground.Points.Count - 1) + " segment(s), " + ground.Crossings.Count + " crossing(s) on the ground");
            NextMedian(waiting, MedianStage.CutGround);
            NetworkBuilder.BuildCurve(waiting.Ground, ground.Plan, waiting.Group, built => waiting.State = built ? 1 : 2);
        }

        // Where the gap begins (or ends): the full road's last stretch (widening
        // into it) and the ground road's first are start pieces, as at a median
        // ramp's node (no junction pieces there: the roads' own surfaces meet).
        private void CutStarts(PendingParallel waiting, ushort node, MedianStage stage)
        {
            var full = SplitRoads.FullStartFor(waiting.Full);
            var ground = SplitRoads.StartFor(waiting.Ground);
            if (full == null || ground == null)
            {
                FailMedian(waiting, "its start pieces weren't made when the city loaded (the log's 'Median ramp roads' lines say why)");
                return;
            }
            if (!GapEndsAt(node, waiting.Ground))
            {
                Mod.Log("Median cut: node #" + node + " isn't only the road and its ground road (a junction): left as it is");
                Mod.Log("Median cut, last: all parts built (one Ctrl+Z removes it all)");
                return;
            }
            Mod.Log("Median cut, next: the start pieces at node #" + node + " (" + full.name + ", " + ground.name + ")");
            NextMedian(waiting, stage);
            NetworkBuilder.MedianTransition(node, full, ground, waiting.Group, (ok, why) =>
            {
                waiting.StartsWhy = why;
                waiting.State = ok ? 1 : 2;
            });
        }

        // A node with only two roads: the ground road and another (the full road).
        private static bool GapEndsAt(ushort node, NetInfo ground)
        {
            if (node == 0) return false;
            var net = NetManager.instance;
            int roads = 0, grounds = 0;
            for (int i = 0; i < 8; i++)
            {
                ushort s = net.m_nodes.m_buffer[node].GetSegment(i);
                if (s == 0) continue;
                roads++;
                if (SplitRoads.IsSplit(net.m_segments.m_buffer[s].Info)) grounds++;
            }
            return roads == 2 && grounds == 1;
        }
    }
}
