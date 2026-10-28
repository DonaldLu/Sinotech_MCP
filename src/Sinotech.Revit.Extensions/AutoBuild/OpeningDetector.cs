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
    /// 由牆中心線的缺口與附近的 CAD 圖形判斷門窗，不依賴圖層名稱：
    /// 缺口旁有半徑約等於缺口寬、圓心在門樘的圓弧為單開門（兩個半寬圓弧為雙開門）；
    /// 牆厚範圍內有兩條以上橫跨缺口的平行線為窗；其他只回報不放置
    /// 所有距離皆為 Revit 內部單位（英尺）；不依賴 Revit 執行環境
    /// </summary>
    internal static class OpeningDetector
    {
        internal enum Kind { Door, DoubleDoor, SlidingDoor, Window, Column, Unknown }

        internal class CadArc
        {
            public Point Center, Mid;
            public double Radius;
        }

        internal class Opening
        {
            public Kind Kind;
            public Point Start, End;
            public double WallWidth;
            public double Width => Length(End - Start);
            public Point Center => (Start + End) * .5;
            // 門扇開向的一側（單位法向量）；窗與未知開口為 (0,0)
            public Point SwingSide;
            // 單開門的鉸鏈端（門樘位置）
            public Point Hinge;
        }

        internal class Result
        {
            public List<Opening> Openings = new List<Opening>();
            // 已把門窗缺口補成連續牆的中心線（含牆厚）
            public List<Tuple<Segment, double>> CenterLines = new List<Tuple<Segment, double>>();
        }

        private const double MinWallWidth = 50 / 304.8;

        internal static Result Detect(List<Tuple<Segment, double>> centers, IEnumerable<WallRegionGeometry.Gap> bridged,
            List<Segment> lines, List<CadArc> arcs, double tolerance, double minWidth, double maxWidth, Func<Kind, bool> fill = null)
        {
            var result = new Result();
            var gaps = bridged.Where(g => g.Width >= MinWallWidth).Select(g => new Opening { Start = g.Start, End = g.End, WallWidth = g.Width }).ToList();
            var unbridged = FindGaps(centers, tolerance, minWidth, maxWidth);
            // 開口圖層已補齊的缺口不重複計算
            gaps.AddRange(unbridged.Where(u => !gaps.Any(g => Length(g.Center - u.Center) <= u.WallWidth)));
            foreach (var gap in gaps.Where(g => g.Width >= minWidth && g.Width <= maxWidth))
            {
                Classify(gap, lines, arcs, tolerance);
                // 柱子打斷的牆不是開口
                if (gap.Kind != Kind.Column) result.Openings.Add(gap);
            }
            // 要放置的門窗缺口補上相同牆厚的中心線，讓牆連續，門窗才有宿主牆（不放的種類保留缺口）
            var fillers = result.Openings.Where(o => o.Kind != Kind.Unknown && (fill == null || fill(o.Kind)) && unbridged.Contains(o))
                .Select(o => Tuple.Create(new Segment(o.Start, o.End), o.WallWidth));
            result.CenterLines = centers.Concat(fillers).GroupBy(c => Math.Round(c.Item2 * 304.8))
                .SelectMany(g => WallRegionGeometry.Merge(g.Select(c => c.Item1), tolerance).Select(s => Tuple.Create(s, g.First().Item2)))
                .ToList();
            return result;
        }

        // 同牆厚、共線、端點相對的兩段中心線之間的缺口；缺口中間不可有其他牆穿過（那是 T 接頭不是開口）
        private static List<Opening> FindGaps(List<Tuple<Segment, double>> centers, double tolerance, double minWidth, double maxWidth)
        {
            var gaps = new List<Opening>();
            for (int i = 0; i < centers.Count; i++)
            {
                Segment a = centers[i].Item1;
                double width = centers[i].Item2;
                if (width < MinWallWidth) continue;
                Point d = Direction(a);
                // a 兩端各找最近的下一段
                Opening ahead = null, behind = null;
                for (int j = 0; j < centers.Count; j++)
                {
                    Segment b = centers[j].Item1;
                    if (i == j || Math.Abs(centers[j].Item2 - width) > 3 * tolerance || Math.Abs(Cross(d, Direction(b))) > 1e-3 ||
                        Math.Abs(Cross(b.A - a.A, d)) > tolerance || Math.Abs(Cross(b.B - a.A, d)) > tolerance) continue;
                    double lo = Math.Min(Dot(b.A - a.A, d), Dot(b.B - a.A, d));
                    double hi = Math.Max(Dot(b.A - a.A, d), Dot(b.B - a.A, d));
                    if (lo > a.Length && (ahead == null || lo - a.Length < ahead.Width))
                        ahead = new Opening { Start = a.B, End = a.A + d * lo, WallWidth = width };
                    if (hi < 0 && (behind == null || -hi < behind.Width))
                        behind = new Opening { Start = a.A + d * hi, End = a.A, WallWidth = width };
                }
                foreach (var gap in new[] { ahead, behind })
                {
                    if (gap == null || gap.Width < minWidth - tolerance || gap.Width > maxWidth + tolerance) continue;
                    var probe = new Segment(gap.Start, gap.End);
                    bool crossed = centers.Any(c => !ReferenceEquals(c.Item1, a) && Crosses(c.Item1, probe, width));
                    if (!crossed && !gaps.Any(g => Length(g.Center - gap.Center) <= tolerance)) gaps.Add(gap);
                }
            }
            return gaps;
        }

        private static void Classify(Opening gap, List<Segment> lines, List<CadArc> arcs, double tolerance)
        {
            double g = gap.Width, half = gap.WallWidth / 2;
            Point d = Direction(new Segment(gap.Start, gap.End)), n = new Point(-d.Y, d.X), mid = gap.Center;
            double reach = gap.WallWidth + 0.15 * g;
            // 門：圓弧圓心靠近門樘，半徑約等於開口寬（雙開門為一半）
            var single = arcs.Where(a => a.Radius >= 0.8 * g && a.Radius <= 1.2 * g &&
                (Length(a.Center - gap.Start) <= reach || Length(a.Center - gap.End) <= reach)).ToList();
            var leaves = arcs.Where(a => a.Radius >= 0.4 * g && a.Radius <= 0.6 * g).ToList();
            var startLeaf = leaves.FirstOrDefault(a => Length(a.Center - gap.Start) <= reach);
            var endLeaf = leaves.FirstOrDefault(a => Length(a.Center - gap.End) <= reach);
            if (single.Count > 0)
            {
                var arc = single.OrderBy(a => Math.Min(Length(a.Center - gap.Start), Length(a.Center - gap.End))).First();
                gap.Kind = Kind.Door;
                gap.Hinge = Length(arc.Center - gap.Start) <= Length(arc.Center - gap.End) ? gap.Start : gap.End;
                gap.SwingSide = Dot(arc.Mid - mid, n) >= 0 ? n : n * -1;
                return;
            }
            if (startLeaf != null && endLeaf != null)
            {
                gap.Kind = Kind.DoubleDoor;
                gap.Hinge = gap.Start;
                gap.SwingSide = Dot(startLeaf.Mid - mid, n) >= 0 ? n : n * -1;
                return;
            }
            // 與牆平行、橫跨缺口大部分的線：全在牆厚範圍內且至少兩條為窗；
            // 兩側門樘處都有垂直於牆、伸出牆面的邊（柱的輪廓），或牆面兩側外都有平行邊，為柱；
            // 平行線有落在牆面外一個牆厚內（門片滑軌）的為推拉門
            var inside = new List<double>();
            var outside = new List<double>();
            bool beyondLeft = false, beyondRight = false, startSide = false, endSide = false;
            foreach (var line in lines)
            {
                if (Math.Abs(Cross(Direction(line), d)) > 1 - 1e-3)
                {
                    double sa = Dot(line.A - gap.Start, d), sb = Dot(line.B - gap.Start, d);
                    double na = Math.Abs(Dot(line.A - mid, n)), nb = Math.Abs(Dot(line.B - mid, n));
                    double reachOut = Math.Max(na, nb), reachIn = Math.Min(na, nb);
                    // 柱的側邊從牆面起、伸出牆外但不長；尺寸界線等長線離開牆面或過長，不算
                    if (reachOut > half + gap.WallWidth / 2 + tolerance && reachIn <= half + tolerance && line.Length <= 1.5 * g)
                    {
                        if (Math.Abs(sa) <= tolerance && Math.Abs(sb) <= tolerance) startSide = true;
                        if (Math.Abs(sa - g) <= tolerance && Math.Abs(sb - g) <= tolerance) endSide = true;
                    }
                    continue;
                }
                if (line.Length < 0.5 * g || Math.Abs(Cross(Direction(line), d)) > 1e-3) continue;
                double oa = Dot(line.A - mid, n), ob = Dot(line.B - mid, n);
                double ta = Dot(line.A - gap.Start, d), tb = Dot(line.B - gap.Start, d);
                double covered = Math.Min(g, Math.Max(ta, tb)) - Math.Max(0, Math.Min(ta, tb));
                if (covered < 0.7 * g) continue;
                double offset = (oa + ob) / 2;
                if (Math.Abs(offset) > half + tolerance && Math.Abs(offset) <= Math.Max(g, half + gap.WallWidth))
                {
                    if (offset > 0) beyondLeft = true; else beyondRight = true;
                }
                var target = Math.Abs(oa) <= half + tolerance && Math.Abs(ob) <= half + tolerance ? inside :
                    Math.Abs(offset) <= half + gap.WallWidth + tolerance ? outside : null;
                if (target != null && !target.Any(o => Math.Abs(o - offset) <= tolerance)) target.Add(offset);
            }
            gap.Kind = startSide && endSide ? Kind.Column : inside.Count >= 2 ? Kind.Window : beyondLeft && beyondRight ? Kind.Column :
                outside.Count >= 1 && inside.Count + outside.Count >= 2 ? Kind.SlidingDoor : Kind.Unknown;
        }

        // 另一段中心線是否穿過缺口內部（端點附近、牆厚範圍內的接觸不算）
        private static bool Crosses(Segment other, Segment gap, double width)
        {
            Point a = other.B - other.A, b = gap.B - gap.A;
            double det = Cross(a, b);
            if (Math.Abs(det) < 1e-9) return false;
            Point delta = gap.A - other.A;
            double t = Cross(delta, b) / det, u = Cross(delta, a) / det;
            double margin = width / gap.Length;
            return t >= 0 && t <= 1 && u > margin && u < 1 - margin;
        }

        private static double Dot(Point a, Point b) { return a.X * b.X + a.Y * b.Y; }
        private static double Cross(Point a, Point b) { return a.X * b.Y - a.Y * b.X; }
        private static double Length(Point p) { return Math.Sqrt(Dot(p, p)); }
        private static Point Direction(Segment s) { return (s.B - s.A) * (1 / s.Length); }
    }
}
