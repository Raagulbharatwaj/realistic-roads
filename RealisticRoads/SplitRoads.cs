using System;
using System.Collections.Generic;
using UnityEngine;

namespace RealisticRoads
{
    // The first step towards ramps in a road's middle, VOP-style: a "split"
    // copy of a wide road with its middle cut out, the ramp rising in the gap.
    // Before building that, this finds out in game what the game lets us do
    // with a road, logged once per road: its lanes, whether its 3D model can
    // be read and cut, and whether a copy of the road can be made. Nothing is
    // kept, registered or saved.
    public static class SplitRoadProbe
    {
        private static readonly HashSet<string> probed = new HashSet<string>();

        // Main thread. 'road': the road a ramp starts on; 'ramp': the ramp's road.
        public static void Probe(NetInfo road, NetInfo ramp)
        {
            if (road == null || !probed.Add(road.name + "|" + (ramp != null ? ramp.name : ""))) return;
            Mod.Log("Split road probe: " + road.name + " (half width " + road.m_halfWidth.ToString("F1") + " m), ramp " +
                    (ramp != null ? ramp.name + " (half width " + ramp.m_halfWidth.ToString("F1") + " m)" : "none"));
            try
            {
                LogLanes(road);
                if (ramp != null) LogLanes(ramp);
                float cut = MiddleCut(road);
                Mod.Log("  Middle to cut out: " + (cut > 0f ? "within " + cut.ToString("F1") + " m of the centre" : "none found"));
                if (road.m_segments != null)
                    for (int i = 0; i < road.m_segments.Length; i++)
                        LogMesh("  Segment model " + i, road.m_segments[i] != null ? road.m_segments[i].m_mesh : null, cut);
                TryCopy(road);
            }
            catch (Exception e)
            {
                Mod.LogError("Split road probe failed", e);
            }
        }

        private static void LogLanes(NetInfo info)
        {
            if (info.m_lanes == null) return;
            var parts = new List<string>();
            foreach (var lane in info.m_lanes)
                if (lane != null)
                    parts.Add(lane.m_laneType + " " + lane.m_direction + " at " + lane.m_position.ToString("F1") + " (" + lane.m_width.ToString("F1") + " m)");
            Mod.Log("  " + info.name + " lanes: " + string.Join("; ", parts.ToArray()));
        }

        // How far from the centre the middle (the lanes nearest it on each
        // side, and anything between them) reaches: what the ramp replaces.
        private static float MiddleCut(NetInfo info)
        {
            if (info.m_lanes == null) return 0f;
            float left = float.MaxValue, right = float.MaxValue;
            NetInfo.Lane leftLane = null, rightLane = null;
            foreach (var lane in info.m_lanes)
            {
                if (lane == null || (lane.m_laneType & NetInfo.LaneType.Vehicle) == NetInfo.LaneType.None) continue;
                float d = Mathf.Abs(lane.m_position);
                if (lane.m_position < 0f && d < left) { left = d; leftLane = lane; }
                if (lane.m_position >= 0f && d < right) { right = d; rightLane = lane; }
            }
            if (leftLane == null || rightLane == null) return 0f;
            return Mathf.Max(left + leftLane.m_width * 0.5f, right + rightLane.m_width * 0.5f);
        }

        private static void LogMesh(string what, Mesh mesh, float cut)
        {
            if (mesh == null)
            {
                Mod.Log(what + ": none");
                return;
            }
            try
            {
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                if (vertices == null || vertices.Length == 0 || triangles == null || triangles.Length == 0)
                {
                    Mod.Log(what + " '" + mesh.name + "': can't be read (no vertices)");
                    return;
                }
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                foreach (var v in vertices)
                {
                    minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                    minY = Mathf.Min(minY, v.y); maxY = Mathf.Max(maxY, v.y);
                }
                int middle = 0;
                var kept = new List<int>();
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    float x = (vertices[triangles[i]].x + vertices[triangles[i + 1]].x + vertices[triangles[i + 2]].x) / 3f;
                    if (cut > 0f && Mathf.Abs(x) < cut) middle++;
                    else { kept.Add(triangles[i]); kept.Add(triangles[i + 1]); kept.Add(triangles[i + 2]); }
                }
                // Making the cut model works (then thrown away).
                string made = "not tried";
                if (middle > 0)
                {
                    var copy = new Mesh();
                    copy.vertices = vertices;
                    copy.normals = mesh.normals;
                    copy.uv = mesh.uv;
                    copy.triangles = kept.ToArray();
                    copy.RecalculateBounds();
                    made = "made";
                    UnityEngine.Object.Destroy(copy);
                }
                Mod.Log(what + " '" + mesh.name + "': " + vertices.Length + " vertices, " + triangles.Length / 3 + " triangles, x " +
                        minX.ToString("F1") + " to " + maxX.ToString("F1") + ", y " + minY.ToString("F1") + " to " + maxY.ToString("F1") +
                        "; " + middle + " triangle(s) in the middle; cut model " + made);
            }
            catch (Exception e)
            {
                Mod.Log(what + " '" + mesh.name + "': can't be read (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }

        // Whether the road can be copied (then thrown away at once).
        private static void TryCopy(NetInfo road)
        {
            try
            {
                var copy = UnityEngine.Object.Instantiate(road.gameObject);
                var info = copy != null ? copy.GetComponent<NetInfo>() : null;
                Mod.Log("  Copy of the road: " + (info != null ? "made (" + (info.m_lanes != null ? info.m_lanes.Length : 0) + " lanes)" : "failed"));
                if (copy != null) UnityEngine.Object.Destroy(copy);
            }
            catch (Exception e)
            {
                Mod.Log("  Copy of the road: failed (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }
    }
}
