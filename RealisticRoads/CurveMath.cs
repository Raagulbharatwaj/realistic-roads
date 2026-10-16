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
