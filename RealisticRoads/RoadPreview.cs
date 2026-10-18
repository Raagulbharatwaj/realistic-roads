using System;
using System.Reflection;
using ColossalFramework.Math;
using UnityEngine;

namespace RealisticRoads
{
    // The planned road drawn the way the game's own road tool previews a road:
    // the real road pieces (ground, elevated, bridge) at their planned heights,
    // so a flyover, a ramp or a cutting is seen in 3D instead of as a coloured
    // band lying on the ground. Uses the road tool's own (private)
    // NetTool.RenderSegment, as Move It does for its previews. If a game update
    // removes it, the curve tool falls back to the band alone. Main thread.
    public static class RoadPreview
    {
        // The road tool's segment preview, however this game version declares
        // it: found by name, its parameters filled in by type (the road, the
        // flags, then the Vector3s as start, end, start direction, end
        // direction, the bools as smooth start / end, anything else left at
        // its default). Static or not (then called on the game's NetTool).
        private static MethodInfo renderSegment;
        private static ParameterInfo[] parameters;
        private static bool lookedUp;
        private static bool failed;

        public static bool Available
        {
            get
            {
                if (!lookedUp) LookUp();
                return renderSegment != null && !failed;
            }
        }

        private static void LookUp()
        {
            lookedUp = true;
            try
            {
                const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var candidates = new System.Text.StringBuilder();
                foreach (var method in typeof(NetTool).GetMethods(all))
                {
                    if (!method.Name.Contains("Render")) continue;
                    candidates.Append("\n  ").Append(Patcher.Describe(method)).Append(method.IsStatic ? " (static)" : "");
                    if (method.Name != "RenderSegment" || renderSegment != null) continue;
                    var p = method.GetParameters();
                    int vectors = 0;
                    foreach (var parameter in p) if (parameter.ParameterType == typeof(Vector3)) vectors++;
                    if (p.Length == 0 || p[0].ParameterType != typeof(NetInfo) || vectors < 4) continue;
                    renderSegment = method;
                    parameters = p;
                }
                Mod.Log(renderSegment != null
                    ? "3D preview ready: " + Patcher.Describe(renderSegment)
                    : "3D preview not available: no usable NetTool.RenderSegment. NetTool's render methods are:" + candidates);
            }
            catch (Exception e)
            {
                renderSegment = null;
                Mod.LogError("3D preview not available; the curve is shown as a band only", e);
            }
        }

        private static object[] Arguments(NetInfo info, Vector3 startPosition, Vector3 endPosition, Vector3 startDirection, Vector3 endDirection)
        {
            var args = new object[parameters.Length];
            var vectors = new[] { startPosition, endPosition, startDirection, endDirection };
            int vector = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                var type = parameters[i].ParameterType;
                if (type == typeof(NetInfo)) args[i] = info;
                else if (type == typeof(NetSegment.Flags)) args[i] = NetSegment.Flags.All;
                else if (type == typeof(Vector3)) args[i] = vector < vectors.Length ? vectors[vector++] : Vector3.zero;
                else if (type == typeof(bool)) args[i] = false;
                else args[i] = type.IsValueType ? Activator.CreateInstance(type) : null;
            }
            return args;
        }

        // Draws every piece of the plan with the road version it will be built
        // with (from RoadPlanner.SegmentInfo), between its planned node heights.
        public static void Render(NetInfo info, Plan plan)
        {
            if (info == null || plan == null || plan.Points == null || plan.Kinds == null || !Available) return;
            try
            {
                object target = renderSegment.IsStatic ? null : ToolsModifierControl.GetTool<NetTool>();
                if (!renderSegment.IsStatic && target == null) return;
                for (int i = 1; i < plan.Points.Count; i++)
                {
                    NetInfo piece = RoadPlanner.SegmentInfo(info, plan.Kinds[i - 1], plan.Kinds[i]) ?? info;
                    if (piece.m_segments == null || piece.m_segments.Length == 0) continue;
                    Vector3 start, end;
                    Directions(plan, i, out start, out end);
                    // A tunnel entrance is built from its underground end up (see
                    // NetworkBuilder), so its portal faces the surface: draw it so too.
                    // The flags passed (All, like the game's own preview) include Invert.
                    if (plan.Kinds[i] == NodeKind.Tunnel && plan.Kinds[i - 1] != NodeKind.Tunnel)
                        renderSegment.Invoke(target, Arguments(piece, plan.Points[i], plan.Points[i - 1], -end, -start));
                    else
                        renderSegment.Invoke(target, Arguments(piece, plan.Points[i - 1], plan.Points[i], start, end));
                }
            }
            catch (Exception e)
            {
                failed = true;
                Mod.LogError("Drawing the 3D preview failed; the curve is shown as a band only", e.InnerException ?? e);
            }
        }

        // Piece i's directions (forward at both ends) with the slope between
        // its nodes, as the builder gives the game.
        public static void Directions(Plan plan, int i, out Vector3 start, out Vector3 end)
        {
            Vector3 a = plan.Points[i - 1], d = plan.Points[i];
            float run = CurveMath.FlatDistance(a, d);
            float slope = run > 0.01f ? (d.y - a.y) / run : 0f;
            start = new Vector3(plan.Tangents[i - 1].x, slope, plan.Tangents[i - 1].z).normalized;
            end = new Vector3(plan.InTangent(i).x, slope, plan.InTangent(i).z).normalized;
        }

        // Piece i as the game will build it, at its planned heights.
        public static Bezier3 PieceBezier(Plan plan, int i)
        {
            Vector3 start, end;
            Directions(plan, i, out start, out end);
            var bezier = new Bezier3 { a = plan.Points[i - 1], d = plan.Points[i] };
            NetSegment.CalculateMiddlePoints(bezier.a, start, bezier.d, -end, false, false, out bezier.b, out bezier.c);
            return bezier;
        }
    }
}
