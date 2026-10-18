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
                    : points.Count > 2 ? Extrapolate(p1, p2, points[2])
                    : p1 + (p1 - p2);
                Vector3 p3 = i + 2 < points.Count ? points[i + 2]
                    : endN != Vector3.zero ? p2 + endN * span
                    : points.Count > 2 ? Extrapolate(p2, p1, points[i - 1])
                    : p2 + (p2 - p1);

                for (int s = 1; s <= SamplesPerSpan; s++)
                {
                    float u = s / (float)SamplesPerSpan;
                    result.Add(s == SamplesPerSpan ? p2 : CatmullRom(p0, p1, p2, p3, u));
                }
            }
            return result;
        }

        // A phantom point one span before 'a', continuing the parabola through
        // a, b, c parameterised by their real distances (the even-spacing
        // formula 3a - 3b + c throws the point far off, even ahead of 'a', when
        // a-b is short and b-c long). Falls back to mirroring b if the
        // parabola would still turn the start round.
        public static Vector3 Extrapolate(Vector3 a, Vector3 b, Vector3 c)
        {
            float ab = Mathf.Max(FlatDistance(a, b), 0.01f);
            float bc = Mathf.Max(FlatDistance(b, c), 0.01f);
            // Lagrange weights of the parabola through (0, a), (ab, b), (ab + bc, c) at -ab.
            float wa = 2f * (2f * ab + bc) / (ab + bc);
            float wb = -(2f * ab + bc) / bc;
            float wc = 2f * ab * ab / ((ab + bc) * bc);
            Vector3 phantom = a * wa + b * wb + c * wc;
            Vector3 mirror = a + (a - b);

            Vector3 back = FlatNormal(a - b);
            Vector3 offset = phantom - a;
            float along = offset.x * back.x + offset.z * back.z;
            float flat = Mathf.Sqrt(offset.x * offset.x + offset.z * offset.z);
            if (flat < 0.01f || along < flat * 0.5f) return mirror;   // turned more than 60 degrees
            if (flat > ab * 2f) phantom = a + offset * (ab * 2f / flat); // keep it about one span away
            return phantom;
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

        // Cuts a curve into pieces of about 'spacing' metres on straight
        // stretches and shorter ones (down to 'minSpacing') only where it bends,
        // so one tight corner doesn't chop the whole road into short pieces.
        // Returns the points (first and last are the polyline's ends) and the
        // ground direction at each; 'ok' is false if a piece still bends more
        // than 'maxBend'. Builds its distance and turn tables once (no
        // searching by trial division).
        public static void DivideAdaptive(IList<Vector3> polyline, float spacing, float minSpacing, float maxBend,
            out List<Vector3> points, out List<Vector3> tangents, out bool ok)
        {
            points = new List<Vector3>();
            tangents = new List<Vector3>();
            ok = true;
            int n = polyline.Count;
            if (n < 2) return;

            // Distance along the curve and total turning up to each sample.
            var along = new float[n];
            var turned = new float[n];
            for (int k = 1; k < n; k++)
            {
                along[k] = along[k - 1] + FlatDistance(polyline[k - 1], polyline[k]);
                turned[k] = turned[k - 1];
                if (k + 1 < n) turned[k] += BendAngle(polyline[k] - polyline[k - 1], polyline[k + 1] - polyline[k]);
            }
            float total = along[n - 1];
            if (total < 0.01f) return;

            // Leave room in each piece's bend allowance for the blended end directions.
            float budget = maxBend * 0.8f;
            var cuts = new List<float> { 0f };
            float s = 0f;
            // Keep cutting while what is left is too long, or still bends too much.
            while (total - s > spacing || (total - s > minSpacing && TurnBetween(along, turned, s, total) > budget))
            {
                float length = spacing;
                while (length > minSpacing && TurnBetween(along, turned, s, s + length) > budget)
                    length = Mathf.Max(minSpacing, length * 0.9f);
                s += length;
                cuts.Add(s);
            }
            // A short last piece shares the length with the one before it.
            float rest = total - s;
            if (cuts.Count > 1 && rest < spacing * 0.5f)
            {
                float previous = cuts[cuts.Count - 1] - cuts[cuts.Count - 2];
                float even = (previous + rest) * 0.5f;
                float start = cuts[cuts.Count - 2];
                if (even >= minSpacing && TurnBetween(along, turned, start, start + even) <= budget &&
                    TurnBetween(along, turned, start + even, total) <= budget)
                    cuts[cuts.Count - 1] = start + even;
            }
            // Still a stub at the end (a tight bend there kept the pieces from
            // sharing): give the last piece minSpacing, pushing the cuts before it
            // back while they would leave a piece shorter than that, if the bends
            // still fit.
            if (cuts.Count > 1 && total - cuts[cuts.Count - 1] < minSpacing)
            {
                var moved = new List<float>(cuts);
                float end = total;
                int k = moved.Count - 1;
                while (k > 0 && end - moved[k] < minSpacing)
                {
                    moved[k] = end - minSpacing;
                    end = moved[k];
                    k--;
                }
                bool fits = k >= 0 && moved[k + 1] - moved[k] >= minSpacing;
                for (int c = k < 0 ? 0 : k; c < moved.Count && fits; c++)
                {
                    float to = c + 1 < moved.Count ? moved[c + 1] : total;
                    fits = TurnBetween(along, turned, moved[c], to) <= maxBend * 0.95f;
                }
                if (fits) cuts = moved;
            }
            cuts.Add(total);

            for (int c = 0; c < cuts.Count; c++)
            {
                Vector3 point, tangent;
                PointAt(polyline, cuts[c], out point, out tangent);
                if (c == 0) point = polyline[0];
                if (c == cuts.Count - 1) point = polyline[n - 1];
                points.Add(point);
                tangents.Add(tangent);
            }
            ok = MaxBend(tangents) <= maxBend;
        }

        // Total turning of the curve between two distances along it.
        private static float TurnBetween(float[] along, float[] turned, float from, float to)
        {
            return TurnedAt(along, turned, to) - TurnedAt(along, turned, from);
        }

        private static float TurnedAt(float[] along, float[] turned, float distance)
        {
            int low = 0, high = along.Length - 1;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (along[mid] <= distance) low = mid;
                else high = mid - 1;
            }
            return turned[low];
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
        // lowered nodes (plain ground keeps following the terrain, also next to
        // a fixed node on the ground). Fixed nodes (minH == maxH and isFixed)
        // never move. Returns false if the
        // limits cannot all be met, e.g. a flyover too close to a fixed end.
        public static bool SolveHeights(float[] distance, float[] ground, float[] minH, float[] maxH, bool[] isFixed,
            float maxGrade, out float[] heights)
        {
            int n = ground.Length;
            heights = new float[n];
            for (int i = 0; i < n; i++)
                heights[i] = isFixed[i] ? minH[i] : Mathf.Clamp(ground[i], minH[i], maxH[i]);

            for (int pass = 0; pass < 24; pass++)
            {
                bool changed = false;
                // Up and down, both directions: lifted nodes raise their neighbours,
                // lowered nodes lower theirs.
                for (int dir = 0; dir < 2; dir++)
                {
                    for (int k = 1; k < n; k++)
                    {
                        int i = dir == 0 ? k : n - 1 - k;
                        int j = dir == 0 ? k - 1 : n - k;
                        if (isFixed[i]) continue;
                        float run = Mathf.Abs(distance[i] - distance[j]);
                        float h = heights[i];
                        // A lifted node also follows higher ground next to it (a bridge
                        // meets a cliff top instead of dipping below it), and a lowered
                        // node follows lower ground (a tunnel comes out into a valley).
                        bool lifted = heights[i] > ground[i] + 0.5f;
                        bool lowered = heights[i] < ground[i] - 0.5f;
                        // A fixed node only shapes its neighbours when it is itself off the
                        // ground (an elevated road end, a deck): a ground junction on a hill
                        // must not drag the next node off the hillside.
                        if (heights[j] > ground[j] + 0.5f || (lifted && heights[j] > heights[i]))
                            h = Mathf.Max(h, heights[j] - maxGrade * run);
                        if (heights[j] < ground[j] - 0.5f || (lowered && heights[j] < heights[i]))
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
                bool shaped = Mathf.Abs(heights[i] - ground[i]) > 0.5f || Mathf.Abs(heights[i - 1] - ground[i - 1]) > 0.5f;
                float run = Mathf.Abs(distance[i] - distance[i - 1]);
                if (shaped && Mathf.Abs(heights[i] - heights[i - 1]) > maxGrade * run + 0.05f) return false;
            }
            return true;
        }

        // Dry gorges and steep valleys: where the road would follow the ground
        // down a slope steeper than 'maxSlope' (the road's own limit) into a
        // dip it has to climb out of again, the bottom node gets a lower limit
        // so it becomes a viaduct from the rim; LevelSpans then levels it rim to
        // rim. A slope that only goes one way is left alone (the planner
        // reports it as too steep). Returns true if any limit was raised.
        public static bool LiftValleys(float[] distance, float[] heights, bool[] isFixed, float maxSlope, float maxGrade,
            float rimReach, float[] minH)
        {
            int n = heights.Length;
            bool raised = false;
            for (int i = 0; i < n; i++)
            {
                if (isFixed[i]) continue;
                bool steep = false;
                int rim = -1;
                for (int j = i - 1; j <= i + 1; j += 2)
                {
                    if (j < 0 || j >= n) continue;
                    float run = Mathf.Max(Mathf.Abs(distance[j] - distance[i]), 0.01f);
                    if ((heights[j] - heights[i]) / run > maxSlope)
                    {
                        steep = true;
                        if (rim < 0 || heights[j] > heights[rim]) rim = j;
                    }
                }
                if (!steep) continue;
                // A dip: the ground rises on both sides within reach.
                if (!RisesWithin(distance, heights, i, -1, rimReach) || !RisesWithin(distance, heights, i, 1, rimReach)) continue;
                float lift = heights[rim] - maxGrade * Mathf.Abs(distance[rim] - distance[i]);
                if (lift > minH[i] + 0.01f && lift > heights[i] + 0.5f)
                {
                    minH[i] = lift;
                    raised = true;
                }
            }
            return raised;
        }

        private static bool RisesWithin(float[] distance, float[] heights, int from, int step, float reach)
        {
            for (int k = from + step; k >= 0 && k < heights.Length; k += step)
            {
                if (Mathf.Abs(distance[k] - distance[from]) > reach) return false;
                if (heights[k] > heights[from] + 2f) return true;
            }
            return false;
        }

        // Embankments and cuttings, CS2 style: nodes on plain ground may move
        // up or down by at most 'maxDeviation' so the road between them is no
        // steeper than 'maxSlope' (the road's own limit). Lifted, lowered and
        // fixed nodes don't move. Marks the nodes moved off the ground in
        // 'embanked'. Returns the steepest grade left over (0 if none).
        public static float SmoothGrades(float[] distance, float[] ground, float[] heights, bool[] isFixed,
            float[] minH, float[] maxH, float maxSlope, float maxDeviation, bool[] embanked)
        {
            int n = heights.Length;
            var free = new bool[n];
            for (int i = 0; i < n; i++) free[i] = !isFixed[i] && Mathf.Abs(heights[i] - ground[i]) <= 0.5f;

            for (int pass = 0; pass < 24; pass++)
            {
                bool changed = false;
                for (int dir = 0; dir < 2; dir++)
                {
                    for (int k = 1; k < n; k++)
                    {
                        int i = dir == 0 ? k : n - 1 - k;
                        int j = dir == 0 ? k - 1 : n - k;
                        if (!free[i]) continue;
                        float run = Mathf.Abs(distance[i] - distance[j]);
                        float h = Mathf.Clamp(heights[i], heights[j] - maxSlope * run, heights[j] + maxSlope * run);
                        h = Mathf.Clamp(h, ground[i] - maxDeviation, ground[i] + maxDeviation);
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

            float steepest = 0f;
            for (int i = 0; i < n; i++)
            {
                if (free[i] && Mathf.Abs(heights[i] - ground[i]) > 0.5f) embanked[i] = true;
                if (i == 0) continue;
                float run = Mathf.Max(Mathf.Abs(distance[i] - distance[i - 1]), 0.01f);
                float grade = Mathf.Abs(heights[i] - heights[i - 1]) / run;
                if ((free[i] || free[i - 1]) && grade > maxSlope + 0.001f) steepest = Mathf.Max(steepest, grade);
            }
            return steepest;
        }

        // Bridges span: every run of lifted nodes between two nodes on the
        // ground (or fixed) gets a lower limit on the straight line between
        // those two, so a deck crosses a gorge or valley level with its banks
        // instead of sagging down to the water and climbing back out. Returns
        // true if any limit was raised (solve the heights again then).
        public static bool LevelSpans(float[] distance, float[] ground, float[] heights, bool[] isFixed, float[] minH)
        {
            int n = heights.Length;
            bool raised = false;
            int i = 0;
            while (i < n)
            {
                if (isFixed[i] || heights[i] <= ground[i] + 0.5f) { i++; continue; }
                int first = i;
                while (i < n && !isFixed[i] && heights[i] > ground[i] + 0.5f) i++;
                int a = first - 1, b = i;     // the anchors on both sides
                if (a < 0 || b >= n) continue; // runs off the end of the curve: nothing to span to
                float span = distance[b] - distance[a];
                if (span < 0.01f) continue;
                for (int k = first; k < b; k++)
                {
                    float line = heights[a] + (heights[b] - heights[a]) * (distance[k] - distance[a]) / span;
                    if (line > minH[k] + 0.01f)
                    {
                        minH[k] = line;
                        raised = true;
                    }
                }
            }
            return raised;
        }

        // --- the bridge put into a road over an underpass -----------------------

        public enum BridgeEnd
        {
            Cut,        // a new node where the bridge ends
            Keep,       // the bridge runs on to the existing node just past its end
            SlideIn,    // the existing node past the end is slid in to it
            SlideOut,   // the existing node before the end is slid out to it, into the road beyond
            Continue,   // the bridge carries on into the road beyond, cut there
        }

        // How one end of that bridge is made, with as few new nodes as possible.
        // 'toNode': metres from the crossing to the crossed segment's end node on
        // this side; 'reach': how far the bridge must reach. 'movable': that node
        // may be slid (a plain node of this road only); 'beyond': length of the
        // segment past it (hasBeyond false at a dead end). A slide is at most
        // 'maxSlide', leaves the segment beyond at least 'minPiece' long and at
        // most 'maxLength'. 'slide' gets how far the node moves.
        public static BridgeEnd ChooseBridgeEnd(float toNode, float reach, bool movable, bool hasBeyond, float beyond,
            float keepSlack, float maxSlide, float minPiece, float maxLength, out float slide)
        {
            slide = 0f;
            if (toNode >= reach)
            {
                float gap = toNode - reach;
                if (gap <= keepSlack) return BridgeEnd.Keep;
                if (movable && gap <= maxSlide && (!hasBeyond || beyond + gap <= maxLength))
                {
                    slide = gap;
                    return BridgeEnd.SlideIn;
                }
                // Never a sliver of road between the cut and the node.
                return gap < minPiece ? BridgeEnd.Keep : BridgeEnd.Cut;
            }
            float over = reach - toNode;
            if (!hasBeyond) return BridgeEnd.Keep;   // a dead end: the bridge just runs to it
            if (movable && over <= maxSlide && beyond - over >= minPiece)
            {
                slide = over;
                return BridgeEnd.SlideOut;
            }
            return BridgeEnd.Continue;
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
