// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Point = Sinotech.Revit.Extensions.AutoBuild.WallRegionGeometry.Point;
using Segment = Sinotech.Revit.Extensions.AutoBuild.WallRegionGeometry.Segment;

namespace Sinotech.Revit.Extensions.AutoBuild
{
    /// <summary>
    /// 由 CAD 線條判斷柱與梁（不依賴 Revit 執行環境）。所有距離皆為 Revit 內部單位（英尺）
    /// 柱：圖層中封閉的小矩形（邊長 20～150 cm）或圓（直徑 20～150 cm），且尺寸重複出現
    /// 梁：圖層中平行、互相重疊、間距 15～120 cm 的雙線，取中心線；端點延伸到相交的梁或柱心；尺寸由附近的標註文字讀取
    /// </summary>
    internal static class StructureDetector
    {
        private const double Cm = 1 / 30.48;
        private const double MinColumn = 20 * Cm, MaxColumn = 150 * Cm;
        private const double MinBeam = 15 * Cm, MaxBeam = 120 * Cm;

        internal class Column
        {
            public Point Center;
            // 矩形柱的兩邊長（B 沿 Direction）；圓柱 Round 為 true、B 為直徑
            public double B, H;
            public bool Round;
            // B 邊的方向（單位向量）
            public Point Direction = new Point(1, 0);
        }

        internal class Beam
        {
            public Segment Axis;
            public double Width;
            // 由標註讀到的梁寬、梁深（無標註為 0）與標註文字
            public double LabelWidth, Depth;
            public string Label;
            public bool Steel;
        }

        internal class CadText
        {
            public Point Position;
            // 文字方向（弧度）與字高
            public double Angle, Height;
            public string Text;
        }

        // 名稱看起來不是柱的圖層（門窗、衛浴、標註、文字、家具、符號、軸線），即使有封閉小矩形也不採用
        private static readonly string[] NonColumnWords =
        {
            "DOOR", "WIN", "OPEN", "門", "窗", "TOIL", "SANI", "廁", "衛", "DIM", "標註", "尺寸", "TEXT", "文字",
            "FURN", "家具", "SYMB", "符號", "GRID", "軸", "HATCH", "填充", "TAG", "編號", "STAIR", "樓梯", "EQUIP", "設備"
        };

        private static readonly string[] BeamTokens = { "B", "J", "G", "SB", "SJ", "SG", "BM", "BEAM", "BEAMS", "GIRDER", "JOIST" };

        // 圖層名稱以 - _ 空白等分隔後，有一段是梁代號（B、J、G、SB、SJ…）或含「梁／樑」；
        // 例如 0S-B、0S-J、0S-SB、S-BEAM；1S-BN（梁編號文字）不算
        internal static bool LooksLikeBeamLayer(string layer)
        {
            if (layer.Contains("梁") || layer.Contains("樑")) return true;
            return Regex.Split(layer.ToUpperInvariant(), "[^A-Z0-9]+").Select(t => t.TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'))
                .Any(t => BeamTokens.Contains(t) || (t.StartsWith("S") && BeamTokens.Contains(t.Substring(1))));
        }

        // 結構平面圖上柱頭、底板螺栓等符號也是封閉小圓或矩形，自動判定時只採用名稱像柱的圖層
        // （含 COL／柱，排除 HEAD／頭／帽、擋土）；例如 0S-COLUMN、0S-COL-H（下層柱虛線）
        internal static bool LooksLikeColumnLayer(string layer)
        {
            string upper = layer.ToUpperInvariant();
            return (upper.Contains("COL") || layer.Contains("柱")) &&
                !upper.Contains("HEAD") && !layer.Contains("頭") && !layer.Contains("帽") && !layer.Contains("擋土");
        }

        internal static bool LooksLikeNonColumn(string layer)
        {
            // 0、DEFPOINTS 是通用圖層，不自動採用
            return layer == "0" || layer.Equals("DEFPOINTS", StringComparison.OrdinalIgnoreCase) ||
                NonColumnWords.Any(w => layer.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) ||
                WallRegionGeometry.IsOpeningLayer(layer);
        }

        internal class LayerColumns
        {
            public string Layer;
            public List<Column> Columns = new List<Column>();
            // 封閉輪廓總數（含不像柱的）
            public int ClosedShapes;
            public bool Recommended;
        }

        /// <summary>
        /// 逐圖層找柱；Recommended 表示這個圖層像柱圖層：柱形狀至少 3 個、佔封閉輪廓 60% 以上、最常見尺寸佔 30% 以上，
        /// 且有提供 supports（牆或梁中心線）時，一半以上的柱貼著牆或梁（房間名稱框等浮在房間中的方框不算）
        /// </summary>
        internal static List<LayerColumns> FindColumns(IEnumerable<KeyValuePair<string, Segment>> lines,
            IEnumerable<KeyValuePair<string, Tuple<Point, double>>> circles, double tolerance, List<Segment> supports = null)
        {
            var circleGroups = circles.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToList());
            var lineGroups = lines.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.Select(l => l.Value).ToList());
            var result = new List<LayerColumns>();
            foreach (var layer in lineGroups.Keys.Union(circleGroups.Keys))
            {
                var entry = new LayerColumns { Layer = layer };
                if (lineGroups.TryGetValue(layer, out var segments) && segments.Count <= 20000)
                    foreach (var loop in WallRegionGeometry.ClosedLoops(segments, tolerance))
                    {
                        entry.ClosedShapes++;
                        var column = Rectangle(loop, tolerance);
                        if (column != null) entry.Columns.Add(column);
                    }
                if (circleGroups.TryGetValue(layer, out var list))
                    foreach (var c in list)
                    {
                        entry.ClosedShapes++;
                        if (2 * c.Item2 >= MinColumn && 2 * c.Item2 <= MaxColumn)
                            entry.Columns.Add(new Column { Center = c.Item1, B = 2 * c.Item2, H = 2 * c.Item2, Round = true });
                    }
                entry.Columns = Distinct(entry.Columns, tolerance);
                int common = entry.Columns.GroupBy(c => Key(c)).Select(g => g.Count()).DefaultIfEmpty(0).Max();
                int attached = supports == null || supports.Count == 0 ? entry.Columns.Count : entry.Columns.Count(c => Attached(c, supports));
                entry.Recommended = !LooksLikeNonColumn(layer) && entry.Columns.Count >= 3 &&
                    entry.Columns.Count >= 0.6 * entry.ClosedShapes && common >= 0.3 * entry.Columns.Count && attached >= 0.5 * entry.Columns.Count;
                if (entry.ClosedShapes > 0) result.Add(entry);
            }
            return result;
        }

        // 柱心到牆或梁中心線的距離不超過柱的半個長邊加 30 cm
        internal static bool Attached(Column c, List<Segment> supports)
        {
            double reach = Math.Max(c.B, c.H) / 2 + 30 * Cm;
            return supports.Any(s =>
            {
                Point d = Direction(s);
                double t = Math.Max(0, Math.Min(s.Length, Dot(c.Center - s.A, d)));
                return Length(c.Center - (s.A + d * t)) <= reach;
            });
        }

        internal static string Key(Column c)
        {
            double b = Math.Round(c.B / Cm), h = Math.Round(c.H / Cm);
            return c.Round ? $"D{b}" : $"{Math.Min(b, h)}x{Math.Max(b, h)}";
        }

        // 同一位置重複的柱（外框與填充、不同圖層）只留一個
        internal static List<Column> Distinct(IEnumerable<Column> columns, double tolerance)
        {
            var kept = new List<Column>();
            foreach (var c in columns.OrderByDescending(c => c.B * c.H))
                if (!kept.Any(k => Length(k.Center - c.Center) <= Math.Max(5 * Cm, tolerance))) kept.Add(c);
            return kept;
        }

        private static Column Rectangle(List<Point> loop, double tolerance)
        {
            if (loop.Count != 4) return null;
            Point a = loop[1] - loop[0], b = loop[2] - loop[1], c = loop[3] - loop[2], d = loop[0] - loop[3];
            double la = Length(a), lb = Length(b);
            if (Math.Abs(la - Length(c)) > tolerance || Math.Abs(lb - Length(d)) > tolerance) return null;
            if (Math.Abs(Dot(a, b)) > 1e-3 * la * lb) return null;
            if (la < MinColumn || la > MaxColumn || lb < MinColumn || lb > MaxColumn) return null;
            var center = (loop[0] + loop[2]) * .5;
            // 以接近水平的一邊為 B 方向，避免旋轉 90 度
            var dir = a * (1 / la);
            if (Math.Abs(dir.Y) > Math.Abs(dir.X)) return new Column { Center = center, B = lb, H = la, Direction = Canonical(b * (1 / lb)) };
            return new Column { Center = center, B = la, H = lb, Direction = Canonical(dir) };
        }

        private static Point Canonical(Point d)
        {
            return d.X < -1e-9 || (Math.Abs(d.X) <= 1e-9 && d.Y < 0) ? d * -1 : d;
        }

        /// <summary>
        /// 由梁圖層的雙線求梁中心線與梁寬，再延伸端點、讀取標註
        /// </summary>
        internal static List<Beam> FindBeams(IEnumerable<KeyValuePair<string, Segment>> lines, List<CadText> texts, double tolerance)
        {
            // 雙線只在同一圖層內配對，避免大梁與相鄰小梁的線互相配對
            var beams = new List<Beam>();
            foreach (var layer in lines.GroupBy(l => l.Key))
                beams.AddRange(Pair(layer.Select(l => l.Value).Where(l => l.Length > MinBeam).ToList(), tolerance));
            // 相同梁寬、共線重疊的合併
            beams = beams.GroupBy(b => Math.Round(b.Width / Cm))
                .SelectMany(g => WallRegionGeometry.Merge(g.Select(b => b.Axis), tolerance).Select(s => new Beam { Axis = s, Width = g.First().Width }))
                .ToList();
            JoinAtColumns(beams, tolerance);
            ExtendToSupports(beams, tolerance);
            ReadLabels(beams, texts);
            return beams;
        }

        private static List<Beam> Pair(List<Segment> items, double tolerance)
        {
            var beams = new List<Beam>();
            for (int i = 0; i < items.Count; i++)
            {
                var a = items[i];
                Point d = Direction(a), n = new Point(-d.Y, d.X);
                Segment partner = null; double best = double.MaxValue;
                for (int j = 0; j < items.Count; j++)
                {
                    if (i == j) continue;
                    var b = items[j];
                    if (Math.Abs(Cross(d, Direction(b))) > 1e-3) continue;
                    double gap = Math.Abs(Dot((b.A + b.B) * .5 - a.A, n));
                    if (gap < MinBeam || gap > MaxBeam || gap >= best) continue;
                    double lo = Math.Max(0, Math.Min(Dot(b.A - a.A, d), Dot(b.B - a.A, d)));
                    double hi = Math.Min(a.Length, Math.Max(Dot(b.A - a.A, d), Dot(b.B - a.A, d)));
                    if (hi - lo < 0.5 * Math.Min(a.Length, b.Length) || hi - lo < 2 * gap) continue;
                    best = gap; partner = b;
                }
                if (partner == null) continue;
                double offset = Dot((partner.A + partner.B) * .5 - a.A, n);
                double s = Math.Max(0, Math.Min(Dot(partner.A - a.A, d), Dot(partner.B - a.A, d)));
                double e = Math.Min(a.Length, Math.Max(Dot(partner.A - a.A, d), Dot(partner.B - a.A, d)));
                var axis = new Segment(a.A + d * s + n * (offset / 2), a.A + d * e + n * (offset / 2));
                // 兩條線互為夥伴時只留一次
                if (!beams.Any(x => Math.Abs(x.Width - best) <= tolerance && Same(x.Axis, axis, tolerance)))
                    beams.Add(new Beam { Axis = axis, Width = best });
            }
            return beams;
        }

        // 同梁寬、共線、中間斷開不超過 150 cm（柱的位置）的兩段梁，各延伸到斷開處的中點（柱心）
        private static void JoinAtColumns(List<Beam> beams, double tolerance)
        {
            foreach (var a in beams)
                foreach (var b in beams)
                {
                    if (ReferenceEquals(a, b) || Math.Abs(a.Width - b.Width) > tolerance) continue;
                    Point d = Direction(a.Axis);
                    if (Math.Abs(Cross(d, Direction(b.Axis))) > 1e-3 || Math.Abs(Cross(b.Axis.A - a.Axis.A, d)) > a.Width / 2 ||
                        Math.Abs(Cross(b.Axis.B - a.Axis.A, d)) > a.Width / 2) continue;
                    double lo = Math.Min(Dot(b.Axis.A - a.Axis.A, d), Dot(b.Axis.B - a.Axis.A, d));
                    double gap = lo - a.Axis.Length;
                    if (gap <= tolerance || gap > 150 * Cm) continue;
                    var mid = a.Axis.A + d * (a.Axis.Length + gap / 2);
                    a.Axis = new Segment(a.Axis.A, mid);
                    if (Length(b.Axis.A - mid) < Length(b.Axis.B - mid)) b.Axis = new Segment(mid, b.Axis.B);
                    else b.Axis = new Segment(b.Axis.A, mid);
                }
        }

        // 梁端延伸到相交的另一根梁中心線（只延伸，不超過對方梁寬一半加 75 cm）
        private static void ExtendToSupports(List<Beam> beams, double tolerance)
        {
            foreach (var beam in beams)
                foreach (bool atStart in new[] { true, false })
                {
                    Point end = atStart ? beam.Axis.A : beam.Axis.B, d = Direction(beam.Axis) * (atStart ? -1 : 1);
                    double bestT = double.MaxValue;
                    foreach (var other in beams)
                    {
                        if (ReferenceEquals(other, beam)) continue;
                        Point od = Direction(other.Axis);
                        double det = Cross(d, od);
                        if (Math.Abs(det) < 0.2) continue;
                        Point delta = other.Axis.A - end;
                        double t = Cross(delta, od) / det, u = Cross(delta, d) / det;
                        double reach = other.Width / 2 + 75 * Cm;
                        if (t < -tolerance || t > reach || u < -reach || u > other.Axis.Length + reach) continue;
                        bestT = Math.Min(bestT, t);
                    }
                    if (bestT == double.MaxValue || bestT <= tolerance) continue;
                    var p = end + d * bestT;
                    beam.Axis = atStart ? new Segment(p, beam.Axis.B) : new Segment(beam.Axis.A, p);
                }
        }

        private static readonly Regex RcSize = new Regex(@"\(\s*(\d+(?:\.\d+)?)\s*[xX×*]\s*(\d+(?:\.\d+)?)\s*\)");
        private static readonly Regex SteelSize = new Regex(@"H\s*(\d+)\s*[xX×*]\s*(\d+)", RegexOptions.IgnoreCase);

        // 與梁平行、位於梁旁（梁寬 2.5 倍加 2 倍字高內）且落在梁跨內的文字視為該梁的標註
        // RC 梁「(寬X深)」單位 cm；鋼梁「H深X翼寬」單位 mm
        private static void ReadLabels(List<Beam> beams, List<CadText> texts)
        {
            if (texts == null) return;
            var sized = texts.Select(t => new { t, rc = RcSize.Match(t.Text), steel = SteelSize.Match(t.Text) })
                .Where(x => x.rc.Success || x.steel.Success).ToList();
            foreach (var beam in beams)
            {
                Point d = Direction(beam.Axis), n = new Point(-d.Y, d.X);
                var match = sized.Select(x =>
                {
                    double angle = Math.Abs(Math.IEEERemainder(x.t.Angle - Math.Atan2(d.Y, d.X), Math.PI));
                    double along = Dot(x.t.Position - beam.Axis.A, d), across = Math.Abs(Dot(x.t.Position - beam.Axis.A, n));
                    // 文字插入點在左下角，跨內判斷容許一段文字長度
                    double textLength = x.t.Text.Length * x.t.Height * 0.8;
                    bool ok = angle <= 3 * Math.PI / 180 && across <= 2.5 * beam.Width + 2 * x.t.Height &&
                        along >= -textLength && along <= beam.Axis.Length;
                    return new { x, ok, across };
                }).Where(m => m.ok).OrderBy(m => m.across).FirstOrDefault();
                if (match == null) continue;
                beam.Label = match.x.t.Text.Trim();
                if (match.x.rc.Success)
                {
                    beam.LabelWidth = Number(match.x.rc.Groups[1].Value) * Cm;
                    beam.Depth = Number(match.x.rc.Groups[2].Value) * Cm;
                }
                else
                {
                    beam.Steel = true;
                    beam.Depth = Number(match.x.steel.Groups[1].Value) / 304.8;
                    beam.LabelWidth = Number(match.x.steel.Groups[2].Value) / 304.8;
                }
            }
            // 沒有標註的梁：沿用同一直線上、同梁寬且有標註的梁
            foreach (var beam in beams.Where(b => b.Label == null))
            {
                Point d = Direction(beam.Axis);
                var sibling = beams.Where(b => b.Label != null && Math.Abs(b.Width - beam.Width) <= 1 * Cm &&
                    Math.Abs(Cross(d, Direction(b.Axis))) <= 1e-3 && Math.Abs(Cross(b.Axis.A - beam.Axis.A, d)) <= beam.Width / 2)
                    .OrderBy(b => Length((b.Axis.A + b.Axis.B) * .5 - (beam.Axis.A + beam.Axis.B) * .5)).FirstOrDefault();
                if (sibling == null) continue;
                beam.Label = sibling.Label + "（沿用）"; beam.Depth = sibling.Depth; beam.LabelWidth = sibling.LabelWidth; beam.Steel = sibling.Steel;
            }
        }

        private static double Number(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
        private static bool Same(Segment a, Segment b, double tolerance)
        {
            return (Length(a.A - b.A) <= tolerance && Length(a.B - b.B) <= tolerance) || (Length(a.A - b.B) <= tolerance && Length(a.B - b.A) <= tolerance);
        }
        private static double Dot(Point a, Point b) { return a.X * b.X + a.Y * b.Y; }
        private static double Cross(Point a, Point b) { return a.X * b.Y - a.Y * b.X; }
        private static double Length(Point p) { return Math.Sqrt(Dot(p, p)); }
        private static Point Direction(Segment s) { return (s.B - s.A) * (1 / s.Length); }
    }
}
