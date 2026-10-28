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
using Point = Sinotech.Revit.Extensions.AutoBuild.WallRegionGeometry.Point;
using Segment = Sinotech.Revit.Extensions.AutoBuild.WallRegionGeometry.Segment;

namespace Sinotech.Revit.Extensions.AutoBuild
{
    /// <summary>
    /// 以平行雙線配對求牆中心線，不要求牆輪廓封閉（門口、T 字交接處的牆線常是開口的）：
    /// 1. 同圖層、同方向（0.5° 內）的線先接成連續線
    /// 2. 相鄰兩條平行線之間沒有其他線、間距在牆厚範圍內的部分視為牆段
    /// 3. 排除等間距重複的線（樓梯踏步、填充線）、有對角線的矩形（開口、管道間符號）
    /// 4. 同一道牆的牆段接起來（一側牆線連續、另一側被交接牆打斷時也接起來；窗戶處兩側線都在牆內，不接），牆端延伸到相交牆的中心線
    /// 5. 不與其他牆相連的短小矩形（柱、設備）不算牆
    /// 所有距離皆為 Revit 內部單位（英尺）；不依賴 Revit 執行環境
    /// </summary>
    internal static class WallPairBuilder
    {
        internal class Wall
        {
            public string Layer;
            public Segment Axis;
            public double Thickness;
            public bool Connected;
            // 與此牆相連的整組牆（牆網）的牆數與總長
            public int NetworkSize;
            public double NetworkLength;
            internal double Ux, Uy, Offset, Lo, Hi;
            internal bool Closed;
            internal List<Wall> Links = new List<Wall>();
        }

        internal class Result
        {
            public List<Wall> Walls = new List<Wall>();
            // 由開口圖層的線補齊的牆缺口（中心線上的起訖點與牆厚），供放置門窗使用
            public List<WallRegionGeometry.Gap> Gaps = new List<WallRegionGeometry.Gap>();
            // 排除的牆段數：等間距重複（樓梯、填充線）、開口符號框、獨立小矩形（柱、設備）
            public int Patterns, Symbols, Isolated;
        }

        private const double MinThickness = 50 / 304.8;
        private const double MinLineLength = 20 / 304.8;
        private const double AngleTolerance = 0.5 * Math.PI / 180;
        private const double Collinear = 3 / 304.8;

        private class Line
        {
            public int Id;
            public double Offset, Lo, Hi;
        }

        private class Strip
        {
            public Line I, J;
            public double Lo, Hi, Gap;
            public bool Pattern, Closed, Symbol;
            public double Center => (I.Offset + J.Offset) / 2;
        }

        private class Cluster
        {
            public double Ux, Uy;
            public List<Line> Lines = new List<Line>();
            public List<Strip> Strips = new List<Strip>();
        }

        /// <summary>
        /// 依圖層各自求牆，再把不同圖層的牆接在一起；重疊的牆只保留牆總長較多的圖層。
        /// openings 為開口圖層的線：只用來把同一道牆在門窗處的缺口補成連續牆，不會單獨產生牆
        /// </summary>
        internal static Result Build(IEnumerable<KeyValuePair<string, Segment>> lines, double maxThickness, double tolerance,
            List<Segment> openings = null)
        {
            var result = new Result();
            var byLayer = lines.GroupBy(l => l.Key).Select(g => BuildLayer(g.Key, g.Select(l => l.Value).ToList(), maxThickness, result))
                .OrderByDescending(w => w.Sum(x => x.Hi - x.Lo)).ToList();
            var walls = new List<Wall>();
            foreach (var layer in byLayer)
                walls.AddRange(layer.Where(w => !walls.Any(o => Duplicate(o, w))));
            Join(walls, tolerance);
            Bridge(walls, null, null);
            if (openings != null && openings.Count > 0)
            {
                Bridge(walls, openings, result.Gaps);
                Embedded(walls, openings, result.Gaps);
            }
            foreach (var w in walls)
            {
                double length = w.Hi - w.Lo;
                bool keep = w.Connected ? length >= Math.Max(w.Thickness * .5, tolerance)
                    : length >= 2.5 * w.Thickness && !(w.Closed && length < 6 * w.Thickness);
                if (!keep) { result.Isolated++; continue; }
                w.Axis = new Segment(At(w, w.Lo), At(w, w.Hi));
                result.Walls.Add(w);
            }
            var kept = new HashSet<Wall>(result.Walls);
            var seen = new HashSet<Wall>();
            foreach (var w in result.Walls)
            {
                if (!seen.Add(w)) continue;
                var network = new List<Wall> { w };
                for (int i = 0; i < network.Count; i++)
                    foreach (var next in network[i].Links)
                        if (kept.Contains(next) && seen.Add(next)) network.Add(next);
                double total = network.Sum(x => x.Axis.Length);
                foreach (var x in network) { x.NetworkSize = network.Count; x.NetworkLength = total; }
            }
            return result;
        }

        private static Point At(Wall w, double t)
        {
            // 方向座標 t、法向位移 Offset 換回平面座標
            return new Point(w.Ux * t - w.Uy * w.Offset, w.Uy * t + w.Ux * w.Offset);
        }

        private static List<Wall> BuildLayer(string layer, List<Segment> segments, double maxThickness, Result result)
        {
            var walls = new List<Wall>();
            var ends = new EndpointIndex(segments);
            foreach (var cluster in Clusters(segments))
            {
                Pair(cluster, maxThickness);
                MarkPatterns(cluster.Strips);
                var strips = KeepThinner(cluster.Strips.Where(s => !s.Pattern).ToList());
                result.Patterns += cluster.Strips.Count(s => s.Pattern);
                foreach (var s in strips)
                {
                    Point a0 = Frame(cluster, s.Lo, s.I.Offset), b0 = Frame(cluster, s.Lo, s.J.Offset);
                    Point a1 = Frame(cluster, s.Hi, s.I.Offset), b1 = Frame(cluster, s.Hi, s.J.Offset);
                    s.Closed = ends.Has(a0, b0) && ends.Has(a1, b1);
                    s.Symbol = s.Closed && (ends.Has(a0, b1) || ends.Has(b0, a1));
                }
                result.Symbols += strips.Count(s => s.Symbol);
                walls.AddRange(Merge(cluster, strips.Where(s => !s.Symbol).ToList(), maxThickness, layer, ends));
            }
            return walls;
        }

        private static Point Frame(Cluster c, double t, double offset)
        {
            return new Point(c.Ux * t - c.Uy * offset, c.Uy * t + c.Ux * offset);
        }

        // 依方向分組（相鄰角度相差 0.5° 內串成同一組，0° 與 180° 相接），每組內共線且相接的線段接成一條
        private static List<Cluster> Clusters(List<Segment> segments)
        {
            var items = segments.Where(s => s.Length >= MinLineLength).Select(s =>
            {
                double angle = Math.Atan2(s.B.Y - s.A.Y, s.B.X - s.A.X);
                if (angle < 0) angle += Math.PI;
                if (angle >= Math.PI) angle -= Math.PI;
                return Tuple.Create(angle, s);
            }).OrderBy(t => t.Item1).ToList();
            var groups = new List<List<Tuple<double, Segment>>>();
            foreach (var item in items)
            {
                if (groups.Count == 0 || item.Item1 - groups[groups.Count - 1].Last().Item1 > AngleTolerance)
                    groups.Add(new List<Tuple<double, Segment>>());
                groups[groups.Count - 1].Add(item);
            }
            if (groups.Count > 1 && groups[0][0].Item1 + Math.PI - groups[groups.Count - 1].Last().Item1 <= AngleTolerance)
            {
                groups[groups.Count - 1].AddRange(groups[0].Select(t => Tuple.Create(t.Item1 + Math.PI, t.Item2)));
                groups.RemoveAt(0);
            }
            var clusters = new List<Cluster>();
            int id = 0;
            foreach (var group in groups)
            {
                double angle = group.Sum(t => t.Item1 * t.Item2.Length) / group.Sum(t => t.Item2.Length);
                var cluster = new Cluster { Ux = Math.Cos(angle), Uy = Math.Sin(angle) };
                var raw = group.Select(t =>
                {
                    double a = t.Item2.A.X * cluster.Ux + t.Item2.A.Y * cluster.Uy, b = t.Item2.B.X * cluster.Ux + t.Item2.B.Y * cluster.Uy;
                    double offset = ((t.Item2.A.Y + t.Item2.B.Y) * cluster.Ux - (t.Item2.A.X + t.Item2.B.X) * cluster.Uy) / 2;
                    return new Line { Offset = offset, Lo = Math.Min(a, b), Hi = Math.Max(a, b) };
                }).OrderBy(l => l.Offset).ToList();
                // 法向位移相近（3 mm 內）的線為同一直線，再依方向座標接起相接或重疊的線段
                for (int i = 0; i < raw.Count;)
                {
                    int j = i + 1;
                    while (j < raw.Count && raw[j].Offset - raw[j - 1].Offset <= Collinear) j++;
                    var row = raw.GetRange(i, j - i).OrderBy(l => l.Lo).ToList();
                    Line current = null; double weight = 0, sum = 0;
                    foreach (var l in row)
                    {
                        if (current != null && l.Lo <= current.Hi + Collinear)
                        {
                            current.Hi = Math.Max(current.Hi, l.Hi);
                            sum += l.Offset * (l.Hi - l.Lo); weight += l.Hi - l.Lo;
                            continue;
                        }
                        if (current != null) { current.Offset = sum / weight; cluster.Lines.Add(current); }
                        current = new Line { Id = id++, Lo = l.Lo, Hi = l.Hi };
                        sum = l.Offset * (l.Hi - l.Lo); weight = l.Hi - l.Lo;
                    }
                    current.Offset = sum / Math.Max(weight, 1e-12);
                    cluster.Lines.Add(current);
                    i = j;
                }
                cluster.Lines = cluster.Lines.OrderBy(l => l.Offset).ToList();
                clusters.Add(cluster);
            }
            return clusters;
        }

        // 每條線往法向正側找相鄰的平行線：較近的線會遮住較遠的線，只有中間沒有其他線的部分才配對
        private static void Pair(Cluster cluster, double maxThickness)
        {
            var lines = cluster.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                var open = new List<Tuple<double, double>> { Tuple.Create(lines[i].Lo, lines[i].Hi) };
                for (int j = i + 1; j < lines.Count && open.Count > 0; j++)
                {
                    double gap = lines[j].Offset - lines[i].Offset;
                    if (gap <= Collinear) continue;
                    if (gap > maxThickness) break;
                    double lo = lines[j].Lo, hi = lines[j].Hi;
                    var next = new List<Tuple<double, double>>();
                    foreach (var span in open)
                    {
                        double a = Math.Max(span.Item1, lo), b = Math.Min(span.Item2, hi);
                        if (b - a > Collinear)
                        {
                            if (gap >= MinThickness) cluster.Strips.Add(new Strip { I = lines[i], J = lines[j], Lo = a, Hi = b, Gap = gap });
                            if (a - span.Item1 > Collinear) next.Add(Tuple.Create(span.Item1, a));
                            if (span.Item2 - b > Collinear) next.Add(Tuple.Create(b, span.Item2));
                        }
                        else next.Add(span);
                    }
                    open = next;
                }
            }
        }

        private static bool Similar(double a, double b) { return Math.Abs(a - b) <= Math.Max(0.15 * Math.Min(a, b), 5 / 304.8); }

        private static bool Overlaps(Strip a, Strip b)
        {
            return Math.Min(a.Hi, b.Hi) - Math.Max(a.Lo, b.Lo) > 0.5 * Math.Min(a.Hi - a.Lo, b.Hi - b.Lo);
        }

        // 等間距重複三次以上（四條以上的平行線）視為樓梯踏步或填充線，不是牆
        private static void MarkPatterns(List<Strip> strips)
        {
            var byI = strips.ToLookup(s => s.I.Id);
            var byJ = strips.ToLookup(s => s.J.Id);
            foreach (var s in strips)
            {
                var before = byJ[s.I.Id].FirstOrDefault(t => Similar(t.Gap, s.Gap) && Overlaps(t, s));
                var after = byI[s.J.Id].FirstOrDefault(t => Similar(t.Gap, s.Gap) && Overlaps(t, s));
                if (before == null || after == null) continue;
                s.Pattern = before.Pattern = after.Pattern = true;
            }
        }

        // 一條線兩側都有配對時，屬於較薄的那一側（較厚的多半是兩道牆之間的窄空間）
        private static List<Strip> KeepThinner(List<Strip> strips)
        {
            var byI = strips.ToLookup(s => s.I.Id);
            var byJ = strips.ToLookup(s => s.J.Id);
            var kept = new List<Strip>();
            foreach (var s in strips)
            {
                var spans = new List<Tuple<double, double>> { Tuple.Create(s.Lo, s.Hi) };
                foreach (var t in byJ[s.I.Id].Concat(byI[s.J.Id]))
                {
                    if (t.Gap >= s.Gap - 5 / 304.8) continue;
                    var next = new List<Tuple<double, double>>();
                    foreach (var span in spans)
                    {
                        double a = Math.Max(span.Item1, t.Lo), b = Math.Min(span.Item2, t.Hi);
                        if (b - a <= Collinear) { next.Add(span); continue; }
                        if (a - span.Item1 > Collinear) next.Add(Tuple.Create(span.Item1, a));
                        if (span.Item2 - b > Collinear) next.Add(Tuple.Create(b, span.Item2));
                    }
                    spans = next;
                }
                foreach (var span in spans)
                    kept.Add(span.Item1 == s.Lo && span.Item2 == s.Hi ? s :
                        new Strip { I = s.I, J = s.J, Lo = span.Item1, Hi = span.Item2, Gap = s.Gap });
            }
            return kept;
        }

        // 同一軸線、同厚度的牆段接成一道牆：相接，或一側共用同一條連續牆線（另一側被交接的牆打斷）
        private static IEnumerable<Wall> Merge(Cluster cluster, List<Strip> strips, double maxThickness, string layer, EndpointIndex ends)
        {
            var sorted = strips.OrderBy(s => s.Center).ToList();
            for (int i = 0; i < sorted.Count;)
            {
                int j = i + 1;
                while (j < sorted.Count && sorted[j].Center - sorted[j - 1].Center <= Collinear) j++;
                foreach (var thickness in sorted.GetRange(i, j - i).GroupBy(s => Math.Round(s.Gap * 304.8 / 5)))
                {
                    Wall wall = null; Strip last = null; double weight = 0, sum = 0, gap = 0;
                    foreach (var s in thickness.OrderBy(s => s.Lo))
                    {
                        bool join = wall != null && (s.Lo <= wall.Hi + Collinear ||
                            (s.Lo - wall.Hi <= maxThickness && (s.I == last.I || s.J == last.J) &&
                             (s.I == last.I ? s.I : s.J).Lo <= wall.Hi && (s.I == last.I ? s.I : s.J).Hi >= s.Lo &&
                             Branches(cluster, ends, s.I == last.I ? last.J : last.I, s.I == last.I ? s.J : s.I,
                                 s.I == last.I ? s.I : s.J, wall.Hi, s.Lo)));
                        if (join)
                        {
                            wall.Hi = Math.Max(wall.Hi, s.Hi); wall.Closed = false;
                            sum += s.Center * (s.Hi - s.Lo); gap += s.Gap * (s.Hi - s.Lo); weight += s.Hi - s.Lo;
                            last = s;
                            continue;
                        }
                        if (wall != null) { wall.Offset = sum / weight; wall.Thickness = gap / weight; yield return wall; }
                        wall = new Wall { Layer = layer, Ux = cluster.Ux, Uy = cluster.Uy, Lo = s.Lo, Hi = s.Hi, Closed = s.Closed };
                        sum = s.Center * (s.Hi - s.Lo); gap = s.Gap * (s.Hi - s.Lo); weight = s.Hi - s.Lo;
                        last = s;
                    }
                    wall.Offset = sum / weight; wall.Thickness = gap / weight;
                    yield return wall;
                }
                i = j;
            }
        }

        // 同軸線、同厚度的兩段牆之間 3.6 m 內的缺口，若被開口圖層的線（門窗框、窗線）蓋住，接成一道牆並記錄缺口；
        // openings 為 null 時只接起首尾相接（3 mm 內）的牆段（例如不同圖層畫的同一道牆）
        private static void Bridge(List<Wall> walls, List<Segment> openings, List<WallRegionGeometry.Gap> gaps)
        {
            double reach = openings == null ? Collinear : 3600 / 304.8;
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var a in walls.OrderBy(w => w.Lo))
                {
                    Wall next = null; double nextLo = 0, nextHi = 0;
                    foreach (var b in walls)
                    {
                        if (ReferenceEquals(a, b) || Math.Abs(a.Ux * b.Uy - a.Uy * b.Ux) > Math.Sin(AngleTolerance) ||
                            Math.Abs(a.Thickness - b.Thickness) > 5 / 304.8) continue;
                        Point p = At(b, b.Lo), q = At(b, b.Hi);
                        double op = p.Y * a.Ux - p.X * a.Uy, oq = q.Y * a.Ux - q.X * a.Uy;
                        if (Math.Abs(op - a.Offset) > Collinear || Math.Abs(oq - a.Offset) > Collinear) continue;
                        double lo = Math.Min(p.X * a.Ux + p.Y * a.Uy, q.X * a.Ux + q.Y * a.Uy);
                        double hi = Math.Max(p.X * a.Ux + p.Y * a.Uy, q.X * a.Ux + q.Y * a.Uy);
                        if (lo < a.Lo || hi <= a.Hi || (openings != null && lo <= a.Hi) || lo - a.Hi > reach || (next != null && lo >= nextLo)) continue;
                        next = b; nextLo = lo; nextHi = hi;
                    }
                    if (next == null) continue;
                    Point start = At(a, a.Hi), direction = new Point(a.Ux, a.Uy);
                    if (openings != null)
                    {
                        if (!WallRegionGeometry.OpeningCoversGap(openings, start, direction, nextLo - a.Hi, a.Thickness, 2 / 304.8)) continue;
                        gaps.Add(new WallRegionGeometry.Gap { Start = start, End = At(a, nextLo), Width = a.Thickness });
                    }
                    a.Hi = Math.Max(a.Hi, nextHi);
                    a.Connected |= next.Connected;
                    a.Closed = false;
                    foreach (var link in next.Links) { a.Links.Add(link); link.Links.Add(a); }
                    walls.Remove(next);
                    changed = true;
                    break;
                }
            }
        }

        // 被打斷那一側的斷口有往牆外延伸的線（交接牆的牆線）才是 T 字交接；窗戶、開口的框線在牆厚內，不算
        private static bool Branches(Cluster c, EndpointIndex ends, Line before, Line after, Line shared, double lo, double hi)
        {
            double sign = before.Offset > shared.Offset ? 1 : -1;
            var outward = new Point(-c.Uy * sign, c.Ux * sign);
            return ends.Outward(Frame(c, lo, before.Offset), outward, 30 / 304.8) || ends.Outward(Frame(c, hi, after.Offset), outward, 30 / 304.8);
        }

        // 牆線連續、門窗只以開口圖層的框畫在牆厚內（例如帶狀窗）：開口圖層與牆平行、位於牆厚內的線段範圍 40 cm～3.6 m 視為門窗位置
        private static void Embedded(List<Wall> walls, List<Segment> openings, List<WallRegionGeometry.Gap> gaps)
        {
            double near = 5 / 304.8;
            foreach (var w in walls)
            {
                var spans = new List<Tuple<double, double>>();
                foreach (var s in openings)
                {
                    double length = s.Length;
                    if (length <= near || Math.Abs(((s.B.X - s.A.X) * w.Uy - (s.B.Y - s.A.Y) * w.Ux) / length) > Math.Sin(AngleTolerance)) continue;
                    double oa = s.A.Y * w.Ux - s.A.X * w.Uy - w.Offset, ob = s.B.Y * w.Ux - s.B.X * w.Uy - w.Offset;
                    if (Math.Abs(oa) > w.Thickness / 2 + 2 / 304.8 || Math.Abs(ob) > w.Thickness / 2 + 2 / 304.8) continue;
                    double a = s.A.X * w.Ux + s.A.Y * w.Uy, b = s.B.X * w.Ux + s.B.Y * w.Uy;
                    double lo = Math.Max(Math.Min(a, b), w.Lo), hi = Math.Min(Math.Max(a, b), w.Hi);
                    if (hi - lo > near) spans.Add(Tuple.Create(lo, hi));
                }
                double start = 0, end = double.MinValue;
                foreach (var span in spans.OrderBy(x => x.Item1).Concat(new[] { Tuple.Create(double.MaxValue, double.MaxValue) }))
                {
                    if (span.Item1 <= end + near) { end = Math.Max(end, span.Item2); continue; }
                    double width = end - start;
                    if (width >= 400 / 304.8 && width <= 3600 / 304.8 && start > w.Lo + near && end < w.Hi - near)
                    {
                        Point a = At(w, start), b = At(w, end), mid = (a + b) * .5;
                        if (!gaps.Any(g => Math.Abs((g.Start.X + g.End.X) / 2 - mid.X) + Math.Abs((g.Start.Y + g.End.Y) / 2 - mid.Y) <= w.Thickness))
                            gaps.Add(new WallRegionGeometry.Gap { Start = a, End = b, Width = w.Thickness });
                    }
                    start = span.Item1; end = span.Item2;
                }
            }
        }

        // 平行、軸線相距在半牆厚內、且一半以上重疊：視為同一道牆
        private static bool Duplicate(Wall a, Wall b)
        {
            if (Math.Abs(a.Ux * b.Uy - a.Uy * b.Ux) > Math.Sin(1 * Math.PI / 180)) return false;
            // b 的軸線換到 a 的座標
            Point p = At(b, b.Lo), q = At(b, b.Hi);
            double offset = (p.Y * a.Ux - p.X * a.Uy + q.Y * a.Ux - q.X * a.Uy) / 2;
            if (Math.Abs(offset - a.Offset) > Math.Max(a.Thickness, b.Thickness) / 2) return false;
            double lo = Math.Min(p.X * a.Ux + p.Y * a.Uy, q.X * a.Ux + q.Y * a.Uy), hi = Math.Max(p.X * a.Ux + p.Y * a.Uy, q.X * a.Ux + q.Y * a.Uy);
            return Math.Min(hi, a.Hi) - Math.Max(lo, a.Lo) > 0.5 * (hi - lo);
        }

        // 牆端延伸到相交牆的中心線（T 字與 L 形交接）：牆端到相交牆中心線的距離不超過其半牆厚
        private static void Join(List<Wall> walls, double tolerance)
        {
            var updates = new List<Tuple<Wall, bool, double>>();
            foreach (var a in walls)
                foreach (bool atHi in new[] { false, true })
                {
                    double end = atHi ? a.Hi : a.Lo, best = double.MaxValue, target = 0;
                    Wall partner = null;
                    foreach (var b in walls)
                    {
                        if (ReferenceEquals(a, b)) continue;
                        double sin = a.Ux * b.Uy - a.Uy * b.Ux;
                        if (Math.Abs(sin) < Math.Sin(15 * Math.PI / 180)) continue;
                        // a 的軸線（t）與 b 的中心線交點
                        Point origin = At(a, 0);
                        double bOffsetOfOrigin = origin.Y * b.Ux - origin.X * b.Uy;
                        double t = (bOffsetOfOrigin - b.Offset) / sin;
                        double reach = (b.Thickness / 2) / Math.Abs(sin) + tolerance * 5;
                        double outward = atHi ? t - end : end - t;
                        if (outward < -tolerance * 5 || outward > reach) continue;
                        Point p = At(a, t);
                        double along = p.X * b.Ux + p.Y * b.Uy;
                        double margin = (a.Thickness / 2) / Math.Abs(sin) + tolerance * 5;
                        if (along < b.Lo - margin || along > b.Hi + margin) continue;
                        if (Math.Abs(outward) < best) { best = Math.Abs(outward); target = t; partner = b; }
                    }
                    if (partner == null) continue;
                    updates.Add(Tuple.Create(a, atHi, target));
                    a.Connected = partner.Connected = true;
                    a.Links.Add(partner); partner.Links.Add(a);
                }
            foreach (var u in updates)
            {
                if (u.Item2) u.Item1.Hi = Math.Max(u.Item1.Hi, u.Item3);
                else u.Item1.Lo = Math.Min(u.Item1.Lo, u.Item3);
            }
        }

        // 線段端點索引：查兩點之間是否有一條線段（容許 5 mm）
        private class EndpointIndex
        {
            private const double Cell = 10 / 304.8, Near = 5 / 304.8;
            private readonly Dictionary<long, List<Segment>> _grid = new Dictionary<long, List<Segment>>();

            public EndpointIndex(List<Segment> segments)
            {
                foreach (var s in segments)
                    foreach (var p in new[] { s.A, s.B })
                    {
                        long key = Key(p);
                        if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<Segment>();
                        if (!list.Contains(s)) list.Add(s);
                    }
            }

            private static long Key(Point p) { return ((long)Math.Floor(p.X / Cell) << 32) ^ (long)Math.Floor(p.Y / Cell) & 0xffffffffL; }

            private static bool Close(Point a, Point b) { return Math.Abs(a.X - b.X) <= Near && Math.Abs(a.Y - b.Y) <= Near; }

            public bool Has(Point a, Point b) { return From(a, b) || From(b, a); }

            // p 有一條不平行於牆的線段，另一端往 outward 方向離開至少 distance
            public bool Outward(Point p, Point outward, double distance)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (_grid.TryGetValue(Key(new Point(p.X + dx * Cell, p.Y + dy * Cell)), out var list))
                            foreach (var s in list)
                            {
                                Point q = Close(s.A, p) ? s.B : Close(s.B, p) ? s.A : new Point(double.NaN, 0);
                                if (double.IsNaN(q.X)) continue;
                                double away = (q.X - p.X) * outward.X + (q.Y - p.Y) * outward.Y;
                                if (away >= distance && away >= 0.5 * s.Length) return true;
                            }
                return false;
            }

            private bool From(Point p, Point q)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (_grid.TryGetValue(Key(new Point(p.X + dx * Cell, p.Y + dy * Cell)), out var list) &&
                            list.Any(s => Close(s.A, p) && Close(s.B, q))) return true;
                return false;
            }
        }
    }
}
