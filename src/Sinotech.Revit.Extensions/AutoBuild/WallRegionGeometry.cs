// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace Sinotech.Revit.Extensions.AutoBuild
{
    /// <summary>
    /// 由封閉雙線區域求牆中心線（移植自 TYBIM.AutoBuild.WallRegionGeometry）
    /// 所有距離皆為 Revit 內部單位（英尺）；不依賴 Revit 執行環境，可獨立做回歸測試
    /// </summary>
    internal static class WallRegionGeometry
    {
        internal struct Point
        {
            public double X, Y;
            public Point(double x, double y) { X = x; Y = y; }
            public static Point operator +(Point a, Point b) { return new Point(a.X + b.X, a.Y + b.Y); }
            public static Point operator -(Point a, Point b) { return new Point(a.X - b.X, a.Y - b.Y); }
            public static Point operator *(Point a, double t) { return new Point(a.X * t, a.Y * t); }
        }
        internal class Segment
        {
            public Point A, B;
            public Segment(Point a, Point b) { A = a; B = b; }
            public double Length { get { return LengthOf(B - A); } }
        }
        internal class Result
        {
            public List<List<Point>> Boundaries = new List<List<Point>>();
            public List<Segment> CenterLines = new List<Segment>();
            public int OpenEdges;
            public int JoinedOpenings;
            // 由開口圖層補齊的牆缺口（中心線上的起訖點與牆厚），供放置門窗使用
            public List<Gap> Gaps = new List<Gap>();
        }
        internal class Gap
        {
            public Point Start, End;
            public double Width;
        }
        internal static bool IsOpeningLayer(string name)
        {
            return new[] { "OPEN", "DOOR", "WINDOW", "開口", "門", "窗" }
                .Any(word => name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        // 開口符號只用來證明牆體相連，不會單獨產生牆中心線。
        // 先以確認過的開口範圍補齊區域，再求最終的中心線。
        internal static Result BuildWithOpenings(IEnumerable<Segment> wallSource, IEnumerable<Segment> openingSource,
            double tolerance, double maxThickness)
        {
            var walls = wallSource.ToList();
            var openings = openingSource.Where(s => s.Length > tolerance).ToList();
            var result = Build(walls, tolerance, maxThickness);
            if (openings.Count == 0 || walls.Count == 0) return result;
            // 部分 CAD 圖面以開口圖層的線條來封閉牆輪廓本身。
            if (result.Boundaries.Count == 0) return Build(walls.Concat(openings), tolerance, maxThickness);
            var footprints = new List<Segment>();
            var gaps = new List<Gap>();
            int joined = 0;
            for (int i = 0; i < result.CenterLines.Count; i++)
                for (int j = i + 1; j < result.CenterLines.Count; j++)
                {
                    Segment a = result.CenterLines[i], b = result.CenterLines[j];
                    Point d = Direction(a), n = Normal(d);
                    if (Math.Abs(Cross(d, Direction(b))) > 1e-6 ||
                        Math.Abs(Cross(b.A - a.A, d)) > tolerance || Math.Abs(Cross(b.B - a.A, d)) > tolerance) continue;
                    double lo = Math.Min(Dot(b.A - a.A, d), Dot(b.B - a.A, d));
                    double hi = Math.Max(Dot(b.A - a.A, d), Dot(b.B - a.A, d));
                    Point start, end;
                    if (lo > a.Length + tolerance) { start = a.B; end = a.A + d * lo; }
                    else if (hi < -tolerance) { start = a.A + d * hi; end = a.A; }
                    else continue;
                    double spanLength = LengthOf(end - start);
                    if (result.CenterLines.Any(s => !ReferenceEquals(s, a) && !ReferenceEquals(s, b) &&
                        Math.Abs(Cross(Direction(s), d)) <= 1e-6 && Math.Abs(Cross(s.A - start, d)) <= tolerance &&
                        Dot((s.A + s.B) * .5 - start, d) > tolerance &&
                        Dot((s.A + s.B) * .5 - start, d) < spanLength - tolerance)) continue;
                    double width = Thickness(a, result.Boundaries, tolerance);
                    double otherWidth = Thickness(b, result.Boundaries, tolerance);
                    if (width <= tolerance || width > maxThickness || Math.Abs(width - otherWidth) > tolerance) continue;
                    double gap = LengthOf(end - start);
                    if (!OpeningCoversGap(openings, start, d, gap, width, tolerance)) continue;
                    gaps.Add(new Gap { Start = start, End = end, Width = width });
                    // 在門窗邊框處稍微重疊，以吸收 CAD 端點的捨入誤差。
                    start = start - d * (tolerance * .5); end = end + d * (tolerance * .5);
                    Point p = start + n * (width / 2), q = end + n * (width / 2);
                    Point r = end - n * (width / 2), corner = start - n * (width / 2);
                    footprints.AddRange(new[] { new Segment(p, q), new Segment(q, r), new Segment(r, corner), new Segment(corner, p) });
                    joined++;
                }
            if (joined == 0) return result;
            result = Build(walls.Concat(footprints), tolerance, maxThickness);
            result.JoinedOpenings = joined;
            result.Gaps = gaps;
            return result;
        }
        // 線段圍出的封閉多邊形（已簡化共線頂點），供判斷柱等封閉輪廓
        internal static List<List<Point>> ClosedLoops(IEnumerable<Segment> source, double tolerance)
        {
            int open;
            return Faces(Merge(source, tolerance), tolerance, out open).Select(p => SimplifyBoundary(p, tolerance)).ToList();
        }
        // 中心線兩側對稱的牆厚；無法判定時回傳 0
        // 中點可能落在轉角或接頭處而量不到，依序改在其他位置量測
        internal static double Thickness(Segment axis, List<List<Point>> loops, double tolerance)
        {
            foreach (double t in new[] { .5, .25, .75, .1, .9 })
            {
                double width = ThicknessAt(axis, axis.A + (axis.B - axis.A) * t, loops, tolerance);
                if (width > 0) return width;
            }
            return 0;
        }
        private static double ThicknessAt(Segment axis, Point center, List<List<Point>> loops, double tolerance)
        {
            Point direction = Direction(axis), normal = Normal(direction);
            double left = double.MaxValue, right = double.MaxValue;
            foreach (var loop in loops)
                for (int i = 0; i < loop.Count; i++)
                {
                    var edge = new Segment(loop[i], loop[(i + 1) % loop.Count]);
                    if (edge.Length <= tolerance || Math.Abs(Cross(Direction(edge), direction)) > 1e-6) continue;
                    double lo = Dot(edge.A - center, direction), hi = Dot(edge.B - center, direction);
                    if (Math.Min(lo, hi) > tolerance || Math.Max(lo, hi) < -tolerance) continue;
                    double offset = Dot(edge.A - center, normal);
                    if (offset > tolerance) left = Math.Min(left, offset);
                    if (offset < -tolerance) right = Math.Min(right, -offset);
                }
            if (left == double.MaxValue || right == double.MaxValue || Math.Abs(left - right) > tolerance) return 0;
            return left + right;
        }
        private static bool Covers(List<Tuple<double, double>> spans, double start, double end, double tolerance)
        {
            double covered = start;
            foreach (var span in spans.OrderBy(s => s.Item1))
            {
                if (span.Item2 < covered - tolerance) continue;
                if (span.Item1 > covered + tolerance) return false;
                covered = Math.Max(covered, span.Item2);
                if (covered >= end - tolerance) return true;
            }
            return false;
        }
        internal static bool OpeningCoversGap(List<Segment> openings, Point start, Point direction,
            double gap, double width, double tolerance)
        {
            // 平行判斷容許約 0.06°（旋轉過的圖面座標有捨入誤差）
            const double parallel = 1e-3;
            Point normal = Normal(direction);
            var left = new List<Tuple<double, double>>();
            var right = new List<Tuple<double, double>>();
            var middle = new List<Tuple<double, double>>();
            var firstJamb = new List<Tuple<double, double>>();
            var lastJamb = new List<Tuple<double, double>>();
            foreach (var line in openings)
            {
                double a = Dot(line.A - start, direction), b = Dot(line.B - start, direction);
                double x = Dot(line.A - start, normal), y = Dot(line.B - start, normal);
                if (Math.Abs(Cross(Direction(line), direction)) <= parallel &&
                    Math.Abs(x) <= width / 2 + tolerance && Math.Abs(y) <= width / 2 + tolerance)
                {
                    var span = Tuple.Create(Math.Min(a, b), Math.Max(a, b));
                    if (Math.Abs(x) <= tolerance && Math.Abs(y) <= tolerance) middle.Add(span);
                    else if (x > tolerance && y > tolerance) left.Add(span);
                    else if (x < -tolerance && y < -tolerance) right.Add(span);
                }
                if (Math.Abs(Cross(Direction(line), normal)) <= parallel)
                {
                    var span = Tuple.Create(Math.Min(x, y), Math.Max(x, y));
                    if (Math.Abs(a) <= tolerance && Math.Abs(b) <= tolerance) firstJamb.Add(span);
                    if (Math.Abs(a - gap) <= tolerance && Math.Abs(b - gap) <= tolerance) lastJamb.Add(span);
                }
            }
            return Covers(middle, 0, gap, tolerance) ||
                (Covers(left, 0, gap, tolerance) && Covers(right, 0, gap, tolerance)) ||
                (Covers(firstJamb, -width / 2, width / 2, tolerance) && Covers(lastJamb, -width / 2, width / 2, tolerance));
        }
        private static double Dot(Point a, Point b) { return a.X * b.X + a.Y * b.Y; }
        private static double Cross(Point a, Point b) { return a.X * b.Y - a.Y * b.X; }
        private static double LengthOf(Point p) { return Math.Sqrt(Dot(p, p)); }
        private static Point Direction(Segment s) { return (s.B - s.A) * (1 / s.Length); }
        private static Point Normal(Point d) { return new Point(-d.Y, d.X); }
        private static double Area(List<Point> p)
        {
            double a = 0;
            for (int i = 0; i < p.Count; i++) a += Cross(p[i], p[(i + 1) % p.Count]);
            return a / 2;
        }
        private static bool Contains(List<Point> p, Point q)
        {
            bool inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
                if ((p[i].Y > q.Y) != (p[j].Y > q.Y) &&
                    q.X < (p[j].X - p[i].X) * (q.Y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X)
                    inside = !inside;
            return inside;
        }
        private static bool Inside(List<List<Point>> loops, Point q)
        {
            return loops.Count(p => Contains(p, q)) % 2 == 1;
        }
        private static List<Point> SimplifyBoundary(List<Point> source, double tolerance)
        {
            var points = source.ToList();
            bool changed = true;
            while (changed && points.Count > 3)
            {
                changed = false;
                for (int i = 0; i < points.Count && points.Count > 3; i++)
                {
                    Point a = points[i] - points[(i + points.Count - 1) % points.Count];
                    Point b = points[(i + 1) % points.Count] - points[i];
                    double la = LengthOf(a), lb = LengthOf(b);
                    if (la <= tolerance || lb <= tolerance ||
                        (Dot(a, b) > 0 && Math.Abs(Cross(a, b)) <= 1e-6 * la * lb))
                    {
                        points.RemoveAt(i--); changed = true;
                    }
                }
            }
            return points;
        }
        internal static List<Segment> Merge(IEnumerable<Segment> source, double tolerance)
        {
            var lines = source.Where(s => s.Length > tolerance).Select(s => new Segment(s.A, s.B)).ToList();
            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    Point d = Direction(lines[i]);
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        var b = lines[j];
                        if (Math.Abs(Cross(d, Direction(b))) > 1e-3 ||
                            Math.Abs(Cross(b.A - lines[i].A, d)) > tolerance ||
                            Math.Abs(Cross(b.B - lines[i].A, d)) > tolerance) continue;
                        double lo = Math.Min(Dot(b.A - lines[i].A, d), Dot(b.B - lines[i].A, d));
                        double hi = Math.Max(Dot(b.A - lines[i].A, d), Dot(b.B - lines[i].A, d));
                        if (lo > lines[i].Length + tolerance || hi < -tolerance) continue;
                        Point origin = lines[i].A;
                        lines[i] = new Segment(origin + d * Math.Min(0, lo), origin + d * Math.Max(lines[i].Length, hi));
                        lines.RemoveAt(j--);
                        changed = true;
                    }
                }
            } while (changed);
            return lines;
        }
        private static int Node(List<Point> nodes, Point p, double tolerance)
        {
            int i = nodes.FindIndex(q => LengthOf(q - p) <= tolerance);
            if (i >= 0) return i;
            nodes.Add(p);
            return nodes.Count - 1;
        }
        // 先在交點處切斷線段，再沿著「封閉面在左側」的有向邊走訪各個面。
        private static List<List<Point>> Faces(List<Segment> lines, double tolerance, out int openEdges)
        {
            var cuts = lines.Select(s => new List<double> { 0, 1 }).ToList();
            for (int i = 0; i < lines.Count; i++)
                for (int j = i + 1; j < lines.Count; j++)
                {
                    Point a = lines[i].B - lines[i].A, b = lines[j].B - lines[j].A;
                    double det = Cross(a, b);
                    if (Math.Abs(det) < 1e-10) continue;
                    Point delta = lines[j].A - lines[i].A;
                    double t = Cross(delta, b) / det, u = Cross(delta, a) / det;
                    if (t < -tolerance / lines[i].Length || t > 1 + tolerance / lines[i].Length ||
                        u < -tolerance / lines[j].Length || u > 1 + tolerance / lines[j].Length) continue;
                    cuts[i].Add(Math.Max(0, Math.Min(1, t)));
                    cuts[j].Add(Math.Max(0, Math.Min(1, u)));
                }
            var nodes = new List<Point>();
            var edges = new HashSet<Tuple<int, int>>();
            for (int i = 0; i < lines.Count; i++)
            {
                var values = cuts[i].Distinct().OrderBy(t => t).ToList();
                for (int k = 1; k < values.Count; k++)
                {
                    int a = Node(nodes, lines[i].A + (lines[i].B - lines[i].A) * values[k - 1], tolerance);
                    int b = Node(nodes, lines[i].A + (lines[i].B - lines[i].A) * values[k], tolerance);
                    if (a != b) edges.Add(Tuple.Create(Math.Min(a, b), Math.Max(a, b)));
                }
            }
            var adjacent = nodes.Select(p => new List<int>()).ToList();
            foreach (var e in edges) { adjacent[e.Item1].Add(e.Item2); adjacent[e.Item2].Add(e.Item1); }
            // 橋接邊（bridge）無法圍成區域，走訪前先移除，
            // 避免外輪廓與內孔之間的一條雜線把兩者併成同一個迴圈。
            int originalEdgeCount = edges.Count, clock = 0;
            var visited = new int[nodes.Count];
            var low = new int[nodes.Count];
            var bridges = new List<Tuple<int, int>>();
            Action<int, int> visit = null;
            visit = (node, parent) =>
            {
                visited[node] = low[node] = ++clock;
                foreach (int next in adjacent[node])
                {
                    if (next == parent) continue;
                    if (visited[next] == 0)
                    {
                        visit(next, node); low[node] = Math.Min(low[node], low[next]);
                        if (low[next] > visited[node]) bridges.Add(Tuple.Create(Math.Min(node, next), Math.Max(node, next)));
                    }
                    else low[node] = Math.Min(low[node], visited[next]);
                }
            };
            for (int i = 0; i < nodes.Count; i++) if (visited[i] == 0) visit(i, -1);
            foreach (var bridge in bridges)
            {
                edges.Remove(bridge);
                adjacent[bridge.Item1].Remove(bridge.Item2); adjacent[bridge.Item2].Remove(bridge.Item1);
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                int n = i;
                adjacent[i] = adjacent[i].OrderBy(j => Math.Atan2(nodes[j].Y - nodes[n].Y, nodes[j].X - nodes[n].X)).ToList();
            }
            var used = new HashSet<Tuple<int, int>>();
            var closedEdges = new HashSet<Tuple<int, int>>();
            var faces = new List<List<Point>>();
            foreach (var e in edges)
                foreach (var start in new[] { e, Tuple.Create(e.Item2, e.Item1) })
                {
                    if (used.Contains(start)) continue;
                    var walk = new List<Tuple<int, int>>();
                    var current = start;
                    while (!used.Contains(current))
                    {
                        used.Add(current); walk.Add(current);
                        var neighbors = adjacent[current.Item2];
                        int reverse = neighbors.IndexOf(current.Item1);
                        current = Tuple.Create(current.Item2, neighbors[(reverse + neighbors.Count - 1) % neighbors.Count]);
                    }
                    var polygon = walk.Select(w => nodes[w.Item1]).ToList();
                    if (!current.Equals(start) || Area(polygon) <= tolerance * tolerance) continue;
                    // 懸空的線段會在走訪中出現兩次；直接移除，不自行補成封閉。
                    var boundary = walk.Where(w => !walk.Contains(Tuple.Create(w.Item2, w.Item1))).ToList();
                    polygon = boundary.Select(w => nodes[w.Item1]).ToList();
                    if (polygon.Count < 3) continue;
                    faces.Add(polygon);
                    foreach (var w in boundary) closedEdges.Add(Tuple.Create(Math.Min(w.Item1, w.Item2), Math.Max(w.Item1, w.Item2)));
                }
            openEdges = originalEdgeCount - closedEdges.Count;
            return faces;
        }
        internal static Result Build(IEnumerable<Segment> source, double tolerance, double maxThickness)
        {
            var result = new Result();
            int open;
            var faces = Faces(Merge(source, tolerance), tolerance, out open);
            result.OpenEdges = open;
            // 抵銷相鄰封閉區域的共用分隔邊，取得整體外輪廓。
            var perimeter = new List<Segment>();
            foreach (var p in faces)
                for (int i = 0; i < p.Count; i++) perimeter.Add(new Segment(p[i], p[(i + 1) % p.Count]));
            var external = perimeter.Where(s => !perimeter.Any(t => !ReferenceEquals(s, t) &&
                LengthOf(s.A - t.B) <= tolerance && LengthOf(s.B - t.A) <= tolerance)).ToList();
            result.Boundaries = Faces(external, tolerance, out open).Select(p => SimplifyBoundary(p, tolerance)).ToList();
            var boundaries = new List<Segment>();
            foreach (var p in result.Boundaries)
            {
                // 巢狀迴圈為內孔；調整每條邊方向，使牆體材料位於左側。
                Point d = Direction(new Segment(p[0], p[1]));
                Point probe = (p[0] + p[1]) * .5 + Normal(d) * (tolerance * .1);
                int depth = result.Boundaries.Count(q => !ReferenceEquals(p, q) && Contains(q, probe));
                var oriented = depth % 2 == 0 ? p : p.AsEnumerable().Reverse().ToList();
                for (int i = 0; i < oriented.Count; i++) boundaries.Add(new Segment(oriented[i], oriented[(i + 1) % oriented.Count]));
            }
            var centers = new List<Segment>();
            var widths = new List<double>();
            var shortCandidates = new List<Tuple<Segment, double>>();
            for (int i = 0; i < boundaries.Count; i++)
                for (int j = i + 1; j < boundaries.Count; j++)
                {
                    Segment a = boundaries[i], b = boundaries[j];
                    Point d = Direction(a), n = Normal(d);
                    if (Dot(d, Direction(b)) > -1 + 1e-6) continue;
                    double width = Dot(b.A - a.A, n);
                    if (width <= tolerance || width > maxThickness) continue;
                    double lo = Math.Max(0, Math.Min(Dot(b.A - a.A, d), Dot(b.B - a.A, d)));
                    double hi = Math.Min(a.Length, Math.Max(Dot(b.A - a.A, d), Dot(b.B - a.A, d)));
                    // 牆端的封口線不可變成垂直的短牆。
                    if (hi - lo <= tolerance) continue;
                    bool material = true;
                    for (int k = 0; k <= 8 && material; k++)
                        for (int h = 1; h <= 3 && material; h++)
                            material = Inside(result.Boundaries, a.A + d * (lo + (hi - lo) * (k + .5) / 9) + n * (width * h / 4));
                    // 以精確裁切找出取樣可能漏掉的微小孔洞。
                    if (material && boundaries.Any(edge => CrossesStrip(edge, a.A, d, lo, hi, width, tolerance))) material = false;
                    if (material)
                    {
                        var axis = new Segment(a.A + d * lo + n * (width / 2), a.A + d * hi + n * (width / 2));
                        if (hi - lo > width + tolerance) { centers.Add(axis); widths.Add(width); }
                        else shortCandidates.Add(Tuple.Create(axis, width));
                    }
                }
            // 短分支須有相同厚度的較長牆段相連，才視為有效。
            var shortBranches = shortCandidates.Where(c => widths.Any(w => Math.Abs(w - c.Item2) <= tolerance) &&
                centers.Any(s => CanJoin(c.Item1, s, result.Boundaries, c.Item2 / 2 + tolerance))).Select(c => c.Item1).ToList();
            centers.AddRange(shortBranches);
            centers = Merge(centers, tolerance);
            // 只在牆體區域內延伸，不可跨越開口或其他區域。
            var original = centers.Select(s => new Segment(s.A, s.B)).ToList();
            for (int i = 0; i < original.Count; i++)
                for (int j = i + 1; j < original.Count; j++)
                {
                    Point a = Direction(original[i]), b = Direction(original[j]);
                    double det = Cross(a, b);
                    if (Math.Abs(det) < 1e-6) continue;
                    Point delta = original[j].A - original[i].A;
                    double t = Cross(delta, b) / det, u = Cross(delta, a) / det;
                    if (DistanceOutside(t, original[i].Length) > maxThickness / 2 + tolerance ||
                        DistanceOutside(u, original[j].Length) > maxThickness / 2 + tolerance) continue;
                    Point intersection = original[i].A + a * t;
                    if (!Inside(result.Boundaries, intersection)) continue;
                    Extend(centers[i], intersection, result.Boundaries, tolerance);
                    Extend(centers[j], intersection, result.Boundaries, tolerance);
                }
            result.CenterLines = Merge(centers, tolerance);
            return result;
        }
        private static double DistanceOutside(double t, double length) { return Math.Max(0, Math.Max(-t, t - length)); }
        private static bool CanJoin(Segment first, Segment second, List<List<Point>> loops, double reach)
        {
            Point a = Direction(first), b = Direction(second);
            double det = Cross(a, b);
            if (Math.Abs(det) < 1e-6) return false;
            Point delta = second.A - first.A;
            double t = Cross(delta, b) / det, u = Cross(delta, a) / det;
            return DistanceOutside(t, first.Length) <= reach && DistanceOutside(u, second.Length) <= reach &&
                Inside(loops, first.A + a * t);
        }
        private static bool Clip(double a, double b, double low, double high, ref double start, ref double end)
        {
            double delta = b - a;
            if (Math.Abs(delta) < 1e-12) return a > low && a < high;
            double t = (low - a) / delta, u = (high - a) / delta;
            start = Math.Max(start, Math.Min(t, u)); end = Math.Min(end, Math.Max(t, u));
            return start < end;
        }
        private static bool CrossesStrip(Segment edge, Point origin, Point direction, double low, double high, double width, double tolerance)
        {
            Point normal = Normal(direction), a = edge.A - origin, b = edge.B - origin;
            double start = 0, end = 1, inset = tolerance * .01;
            return Clip(Dot(a, direction), Dot(b, direction), low + inset, high - inset, ref start, ref end) &&
                Clip(Dot(a, normal), Dot(b, normal), inset, width - inset, ref start, ref end);
        }
        private static void Extend(Segment s, Point p, List<List<Point>> loops, double tolerance)
        {
            double t = Dot(p - s.A, Direction(s));
            if (t >= -tolerance && t <= s.Length + tolerance) return;
            Point endpoint = t < 0 ? s.A : s.B;
            var parameters = new List<double> { 0, 1 };
            Point vector = p - endpoint;
            foreach (var loop in loops)
                for (int i = 0; i < loop.Count; i++)
                {
                    Point edge = loop[(i + 1) % loop.Count] - loop[i];
                    double det = Cross(vector, edge);
                    if (Math.Abs(det) < 1e-12) continue;
                    Point delta = loop[i] - endpoint;
                    double a = Cross(delta, edge) / det, b = Cross(delta, vector) / det;
                    if (a > 0 && a < 1 && b >= 0 && b <= 1) parameters.Add(a);
                }
            parameters.Sort();
            for (int i = 1; i < parameters.Count; i++)
                if (!Inside(loops, endpoint + vector * ((parameters[i - 1] + parameters[i]) / 2))) return;
            if (t < 0) s.A = p; else s.B = p;
        }
    }
}
