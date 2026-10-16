using System.Collections.Generic;
using UnityEngine;

namespace RealisticRoads
{
    // Pure curve maths: no game types beyond Vector3, so it is unit-tested
    // outside the game (see tests/CurveMath.Tests).
    //
    // Heights are carried along but all distances and angles are measured on
    // the ground plane (x/z), the way roads are laid out.
    public static class CurveMath
    {
        public const int SamplesPerSpan = 32;

        // A smooth curve through every control point (centripetal Catmull-Rom,
        // which never loops or overshoots on unevenly spaced points), returned
        // as a dense polyline that starts and ends exactly at the end points.
        public static List<Vector3> SampleSpline(IList<Vector3> points)
        {
            return SampleSpline(points, Vector3.zero, Vector3.zero);
        }

        // As above, but the curve leaves the first point heading 'startDir' and
        // reaches the last heading 'endDir' (Vector3.zero = no preference).
        // Combine with ShapeEnds so the curve also bends gently into them.
        public static List<Vector3> SampleShaped(IList<Vector3> controls, Vector3 startDir, Vector3 endDir)
        {
            return SampleSpline(ShapeEnds(controls, startDir, endDir), startDir, endDir);
        }

        public static List<Vector3> SampleSpline(IList<Vector3> points, Vector3 startDir, Vector3 endDir)
        {
            var result = new List<Vector3>();
            if (points == null || points.Count == 0) return result;
            if (points.Count == 1)
            {
                result.Add(points[0]);
                return result;
            }

            result.Add(points[0]);
            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector3 p1 = points[i];
                Vector3 p2 = points[i + 1];
                // Past the ends, continue the curve's own bend (quadratic
                // extrapolation) so the road leaves its first point already
                // curving; with only two points, just mirror (a straight line).
                // A fixed end direction puts the phantom point straight behind
                // the end, which makes the curve leave exactly that way.
                Vector3 startN = FlatNormal(startDir), endN = FlatNormal(endDir);
                float span = FlatDistance(p1, p2);
                Vector3 p0 = i > 0 ? points[i - 1]
                    : startN != Vector3.zero ? p1 - startN * span
                    : points.Count > 2 ? p1 * 3f - p2 * 3f + points[2]
                    : p1 + (p1 - p2);
                Vector3 p3 = i + 2 < points.Count ? points[i + 2]
                    : endN != Vector3.zero ? p2 + endN * span
                    : points.Count > 2 ? p2 * 3f - p1 * 3f + points[i - 1]
                    : p2 + (p2 - p1);

                for (int s = 1; s <= SamplesPerSpan; s++)
                {
                    float u = s / (float)SamplesPerSpan;
                    result.Add(s == SamplesPerSpan ? p2 : CatmullRom(p0, p1, p2, p3, u));
                }
            }
            return result;
        }

        // Adds guide points so the curve leaves its first point heading
        // 'startDir' and arrives at its last point heading 'endDir' (pass
        // Vector3.zero for "no preference"). Used to continue an existing road
        // smoothly instead of meeting it at an angle.
        public static List<Vector3> ShapeEnds(IList<Vector3> controls, Vector3 startDir, Vector3 endDir)
        {
            var result = new List<Vector3>(controls);
            if (controls.Count < 2) return result;

            float firstSpan = FlatDistance(controls[0], controls[1]);
            float lastSpan = FlatDistance(controls[controls.Count - 2], controls[controls.Count - 1]);
            if (controls.Count == 2)
            {
                // One span shared by both guides: keep them from crossing.
                firstSpan *= 0.5f;
                lastSpan *= 0.5f;
            }

            Vector3 s = FlatNormal(startDir);
            if (s != Vector3.zero)
            {
                Vector3 guide = controls[0] + s * Mathf.Min(firstSpan * 0.35f, 60f);
                guide.y = controls[0].y;
                result.Insert(1, guide);
            }

            Vector3 e = FlatNormal(endDir);
            if (e != Vector3.zero)
            {
                Vector3 end = controls[controls.Count - 1];
                Vector3 guide = end - e * Mathf.Min(lastSpan * 0.35f, 60f);
                guide.y = end.y;
                result.Insert(result.Count - 1, guide);
            }
            return result;
        }

        // Ground length of a polyline.
        public static float Length(IList<Vector3> polyline)
        {
            float length = 0f;
            for (int i = 1; i < polyline.Count; i++) length += FlatDistance(polyline[i - 1], polyline[i]);
            return length;
        }

        // Splits a polyline into 'count' pieces of equal ground length.
        // Returns count + 1 points (first and last are the polyline's ends) and
        // the ground direction of the curve at each of them.
        public static void Divide(IList<Vector3> polyline, int count, out List<Vector3> points, out List<Vector3> tangents)
        {
            points = new List<Vector3>();
            tangents = new List<Vector3>();
            if (polyline.Count < 2 || count < 1) return;

            float total = Length(polyline);
            float step = total / count;
            int index = 1;
            float travelled = 0f;

            for (int k = 0; k <= count; k++)
            {
                float target = k == count ? total : step * k;
                while (index < polyline.Count - 1 && travelled + FlatDistance(polyline[index - 1], polyline[index]) < target)
                {
                    travelled += FlatDistance(polyline[index - 1], polyline[index]);
                    index++;
                }
                Vector3 a = polyline[index - 1];
                Vector3 b = polyline[index];
                float piece = FlatDistance(a, b);
                float t = piece > 0.0001f ? Mathf.Clamp01((target - travelled) / piece) : 0f;
                points.Add(Vector3.Lerp(a, b, t));
                tangents.Add(TangentAt(polyline, index, t));
            }
        }

        // Turn between two directions on the ground, in degrees (0..180).
        public static float BendAngle(Vector3 from, Vector3 to)
        {
            Vector3 a = FlatNormal(from);
            Vector3 b = FlatNormal(to);
            float dot = Mathf.Clamp(a.x * b.x + a.z * b.z, -1f, 1f);
            return Mathf.Acos(dot) * Mathf.Rad2Deg;
        }

        // Picks how many pieces to cut a curve into: about 'spacing' metres
        // each, but more (down to 'minSpacing') where needed to keep every
        // piece's bend within 'maxBend' degrees. 'ok' is false when even the
        // shortest pieces bend too much (the curve is too tight to build).
        public static int ChoosePieceCount(IList<Vector3> polyline, float spacing, float minSpacing, float maxBend, out bool ok)
        {
            float length = Length(polyline);
            int count = Mathf.Max(1, Mathf.RoundToInt(length / spacing));
            int maxCount = Mathf.Max(count, Mathf.FloorToInt(length / minSpacing));
            while (true)
            {
                List<Vector3> points, tangents;
                Divide(polyline, count, out points, out tangents);
                ok = MaxBend(tangents) <= maxBend;
                if (ok || count >= maxCount) return count;
                count++;
            }
        }

        // The largest bend of any single piece.
        public static float MaxBend(IList<Vector3> tangents)
        {
            float max = 0f;
            for (int i = 1; i < tangents.Count; i++) max = Mathf.Max(max, BendAngle(tangents[i - 1], tangents[i]));
            return max;
        }

        // --- points, slices and crossings on polylines ------------------------

        // Point and ground direction 'distance' metres along a polyline.
        public static void PointAt(IList<Vector3> polyline, float distance, out Vector3 point, out Vector3 tangent)
        {
            float travelled = 0f;
            for (int i = 1; i < polyline.Count; i++)
            {
                float step = FlatDistance(polyline[i - 1], polyline[i]);
                if (travelled + step >= distance || i == polyline.Count - 1)
                {
                    float t = step > 0.0001f ? Mathf.Clamp01((distance - travelled) / step) : 0f;
                    point = Vector3.Lerp(polyline[i - 1], polyline[i], t);
                    tangent = TangentAt(polyline, i, t);
                    return;
                }
                travelled += step;
            }
            point = polyline.Count > 0 ? polyline[0] : Vector3.zero;
            tangent = Vector3.zero;
        }

        // The part of a polyline between two distances along it.
        public static List<Vector3> Slice(IList<Vector3> polyline, float from, float to)
        {
            var result = new List<Vector3>();
            Vector3 p, t;
            PointAt(polyline, from, out p, out t);
            result.Add(p);
            float travelled = 0f;
            for (int i = 1; i < polyline.Count; i++)
            {
                travelled += FlatDistance(polyline[i - 1], polyline[i]);
                if (travelled > from + 0.01f && travelled < to - 0.01f) result.Add(polyline[i]);
            }
            PointAt(polyline, to, out p, out t);
            result.Add(p);
            return result;
        }

        // Where two ground line pieces a-b and c-d cross, as fractions along each.
        public static bool Intersect(Vector3 a, Vector3 b, Vector3 c, Vector3 d, out float ta, out float tc)
        {
            float rx = b.x - a.x, rz = b.z - a.z;
            float sx = d.x - c.x, sz = d.z - c.z;
            float denominator = rx * sz - rz * sx;
            ta = tc = 0f;
            if (Mathf.Abs(denominator) < 1e-6f) return false;
            float qx = c.x - a.x, qz = c.z - a.z;
            ta = (qx * sz - qz * sx) / denominator;
            tc = (qx * rz - qz * rx) / denominator;
            return ta >= 0f && ta <= 1f && tc >= 0f && tc <= 1f;
        }

        // A cubic Bezier and its derivative (a, d ends; b, c control points).
        public static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            float u = 1f - t;
            return a * (u * u * u) + b * (3f * u * u * t) + c * (3f * u * t * t) + d * (t * t * t);
        }

        public static Vector3 BezierDerivative(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            float u = 1f - t;
            return (b - a) * (3f * u * u) + (c - b) * (6f * u * t) + (d - c) * (3f * t * t);
        }

        // --- heights along a road ----------------------------------------------

        // Heights for the nodes of a road at ground distances 'distance':
        // each node starts on 'ground', is kept within [minH, maxH], and the
        // slope between neighbours stays within 'maxGrade' around lifted or
        // lowered nodes (plain ground keeps following the terrain). Fixed
        // nodes (minH == maxH and isFixed) never move. Returns false if the
        // limits cannot all be met, e.g. a flyover too close to a fixed end.
        public static bool SolveHeights(float[] distance, float[] ground, float[] minH, float[] maxH, bool[] isFixed,
            float maxGrade, out float[] heights)
        {
            int n = ground.Length;
            heights = new float[n];
            for (int i = 0; i < n; i++)
                heights[i] = isFixed[i] ? minH[i] : Mathf.Clamp(ground[i], minH[i], maxH[i]);

            for (int pass = 0; pass < 12; pass++)
            {
                bool changed = false;
                // Up and down, both directions: lifted/fixed nodes raise their neighbours,
                // lowered/fixed nodes lower theirs.
                for (int dir = 0; dir < 2; dir++)
                {
                    for (int k = 1; k < n; k++)
                    {
                        int i = dir == 0 ? k : n - 1 - k;
                        int j = dir == 0 ? k - 1 : n - k;
                        if (isFixed[i]) continue;
                        float run = Mathf.Abs(distance[i] - distance[j]);
                        float h = heights[i];
                        if (isFixed[j] || heights[j] > ground[j] + 0.5f)
                            h = Mathf.Max(h, heights[j] - maxGrade * run);
                        if (isFixed[j] || heights[j] < ground[j] - 0.5f)
                            h = Mathf.Min(h, heights[j] + maxGrade * run);
                        h = Mathf.Clamp(h, minH[i], maxH[i]);
                        if (Mathf.Abs(h - heights[i]) > 0.001f)
                        {
                            heights[i] = h;
                            changed = true;
                        }
                    }
                }
                if (!changed) break;
            }

            // Check: every limit met, and no lifted/lowered part steeper than allowed.
            for (int i = 0; i < n; i++)
            {
                if (heights[i] < minH[i] - 0.01f || heights[i] > maxH[i] + 0.01f) return false;
                if (i == 0) continue;
                bool shaped = isFixed[i] || isFixed[i - 1] ||
                              Mathf.Abs(heights[i] - ground[i]) > 0.5f || Mathf.Abs(heights[i - 1] - ground[i - 1]) > 0.5f;
                float run = Mathf.Abs(distance[i] - distance[i - 1]);
                if (shaped && Mathf.Abs(heights[i] - heights[i - 1]) > maxGrade * run + 0.05f) return false;
            }
            return true;
        }

        // The left-hand direction on the ground when facing 'd' (same side the
        // game calls leftSide for a segment going away from a node).
        public static Vector3 Left(Vector3 d)
        {
            return new Vector3(-d.z, 0f, d.x);
        }

        public static Vector3 FlatNormal(Vector3 v)
        {
            v.y = 0f;
            float length = Mathf.Sqrt(v.x * v.x + v.z * v.z);
            return length > 0.0001f ? v / length : Vector3.zero;
        }

        public static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = b.x - a.x, dz = b.z - a.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // Direction of the polyline around sample 'index', blending the
        // neighbouring pieces so it turns smoothly from piece to piece.
        private static Vector3 TangentAt(IList<Vector3> polyline, int index, float t)
        {
            Vector3 current = polyline[index] - polyline[index - 1];
            Vector3 neighbour = t < 0.5f
                ? (index >= 2 ? polyline[index - 1] - polyline[index - 2] : current)
                : (index + 1 < polyline.Count ? polyline[index + 1] - polyline[index] : current);
            float weight = t < 0.5f ? 0.5f - t : t - 0.5f;
            Vector3 blended = FlatNormal(current) * (1f - weight) + FlatNormal(neighbour) * weight;
            Vector3 result = FlatNormal(blended);
            return result == Vector3.zero ? FlatNormal(current) : result;
        }

        // Centripetal Catmull-Rom (alpha 0.5), Barry-Goldman form, between p1 and p2.
        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
        {
            float t0 = 0f;
            float t1 = t0 + Knot(p0, p1);
            float t2 = t1 + Knot(p1, p2);
            float t3 = t2 + Knot(p2, p3);
            float t = Mathf.Lerp(t1, t2, u);

            Vector3 a1 = Blend(p0, p1, t0, t1, t);
            Vector3 a2 = Blend(p1, p2, t1, t2, t);
            Vector3 a3 = Blend(p2, p3, t2, t3, t);
            Vector3 b1 = Blend(a1, a2, t0, t2, t);
            Vector3 b2 = Blend(a2, a3, t1, t3, t);
            return Blend(b1, b2, t1, t2, t);
        }

        private static float Knot(Vector3 a, Vector3 b)
        {
            // sqrt of the distance; a small floor keeps repeated points safe.
            return Mathf.Max(Mathf.Sqrt(FlatDistance(a, b)), 0.01f);
        }

        private static Vector3 Blend(Vector3 a, Vector3 b, float ta, float tb, float t)
        {
            float span = tb - ta;
            if (span < 0.0001f) return a;
            return a * ((tb - t) / span) + b * ((t - ta) / span);
        }
    }
}
