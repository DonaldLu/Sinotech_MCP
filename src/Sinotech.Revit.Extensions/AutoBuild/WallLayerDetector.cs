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
    /// 不依賴圖層名稱判斷哪些 CAD 圖層像牆：統計每個圖層「平行、間距在牆厚範圍內、互相重疊」的雙線比例與常見間距
    /// 所有距離皆為 Revit 內部單位（英尺）；不依賴 Revit 執行環境
    /// </summary>
    internal static class WallLayerDetector
    {
        internal class LayerStats
        {
            public string Layer;
            public double TotalLength;
            // 找得到雙線夥伴的線段總長
            public double PairedLength;
            public double PairedRatio => TotalLength > 0 ? PairedLength / TotalLength : 0;
            // 依重疊長度加權、以 10 mm 為級距的常見雙線間距（英尺），由多到少
            public List<double> TypicalThicknesses = new List<double>();
            public bool Recommended;
            // AnalyzeWalls：依雙線求得的牆中心線總長、屬於牆網（5 道以上相連的牆）的長度、平均每道牆長
            public double WallLength, NetworkLength, MeanWallLength;
            // 牆端接在主要牆線圖層的牆上（交接或隔著門窗缺口共線）的牆長比例
            public double AttachedRatio;
        }

        private const double MinThickness = 50 / 304.8;
        private const double MinLineLength = 50 / 304.8;
        private const int Bins = 360;
        private const double AngleStep = Math.PI / Bins; // 0.5°

        // 名稱看起來明顯不是牆的圖層（柱、家具、標註、文字、填充線、軸線、樓梯、設備、衛浴），即使有雙線也不推薦
        private static readonly string[] NonWallWords =
        {
            "COL", "柱", "FURN", "家具", "DIM", "標註", "尺寸", "TEXT", "文字", "HATCH", "PATT", "填充",
            "AXIS", "GRID", "軸", "STAIR", "樓梯", "EQUIP", "設備", "SYMB", "符號", "ANNO", "TITLE", "圖框",
            "TOIL", "SANI", "PLUMB", "廁", "衛"
        };

        internal static bool LooksLikeWallName(string layer)
        {
            return layer.IndexOf("WALL", StringComparison.OrdinalIgnoreCase) >= 0 || layer.IndexOf("牆", StringComparison.Ordinal) >= 0;
        }

        internal static bool LooksLikeNonWall(string layer)
        {
            // 梁圖層（結構平面圖的梁雙線）也不是牆
            if (StructureDetector.LooksLikeBeamLayer(layer) && !LooksLikeWallName(layer)) return true;
            return NonWallWords.Any(word => layer.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// 依各圖層實際求得的牆判斷牆線圖層：牆會互相連成牆網（5 道以上相連），且每道牆有一定長度；
        /// 樓梯踏步、填充線、開口符號、柱、設備、電扶梯等雖有平行雙線，但不成牆網或牆段很短。
        /// 另外，牆大多接在主要牆線圖層上的圖層（例如窗邊牆墩另畫一層）也視為牆線圖層
        /// </summary>
        internal static List<LayerStats> AnalyzeWalls(IEnumerable<KeyValuePair<string, WallRegionGeometry.Segment>> lines, double maxThickness)
        {
            double tolerance = 1 / 304.8;
            var stats = new List<LayerStats>();
            var wallsOf = new Dictionary<string, List<WallPairBuilder.Wall>>();
            foreach (var g in lines.GroupBy(l => l.Key))
            {
                var s = new LayerStats { Layer = g.Key, TotalLength = g.Sum(l => l.Value.Length) };
                if (!WallRegionGeometry.IsOpeningLayer(g.Key))
                {
                    var walls = wallsOf[g.Key] = WallPairBuilder.Build(g, maxThickness, tolerance).Walls;
                    s.WallLength = walls.Sum(w => w.Axis.Length);
                    s.NetworkLength = walls.Where(w => w.NetworkSize >= 5).Sum(w => w.Axis.Length);
                    s.MeanWallLength = walls.Count == 0 ? 0 : s.WallLength / walls.Count;
                    s.PairedLength = Math.Min(s.TotalLength, 2 * s.WallLength);
                    s.TypicalThicknesses = walls.GroupBy(w => Math.Round(w.Thickness * 304.8 / 10))
                        .OrderByDescending(t => t.Sum(w => w.Axis.Length)).Take(3).Select(t => t.Key * 10 / 304.8).ToList();
                }
                stats.Add(s);
            }
            double best = stats.Where(s => !LooksLikeNonWall(s.Layer)).Select(s => s.NetworkLength).DefaultIfEmpty(0).Max();
            foreach (var s in stats)
                s.Recommended = !WallRegionGeometry.IsOpeningLayer(s.Layer) && !LooksLikeNonWall(s.Layer) &&
                    s.NetworkLength >= Math.Max(5 / 0.3048, best * 0.2) && s.MeanWallLength >= 2 / 0.3048;
            // 次要牆圖層：牆有三成以上接在主要牆上、平均牆長 1.5 m 以上、主要厚度與某個主要牆圖層相同（±10 mm）；
            // 或名稱含 WALL／牆 且平均牆長 1 m 以上
            var primaryLayers = stats.Where(s => s.Recommended).ToList();
            var primary = primaryLayers.SelectMany(s => wallsOf[s.Layer]).ToList();
            foreach (var s in stats.Where(s => !s.Recommended && wallsOf.ContainsKey(s.Layer) && s.WallLength >= 1 / 0.3048))
            {
                if (LooksLikeNonWall(s.Layer)) continue;
                var walls = wallsOf[s.Layer];
                s.AttachedRatio = walls.Where(w => Attached(w, primary)).Sum(w => w.Axis.Length) / s.WallLength;
                bool sameThickness = s.TypicalThicknesses.Count > 0 && primaryLayers.Any(p => p.TypicalThicknesses.Count > 0 &&
                    Math.Abs(p.TypicalThicknesses[0] - s.TypicalThicknesses[0]) <= 10 / 304.8);
                s.Recommended = (s.AttachedRatio >= 0.3 && s.MeanWallLength >= 1.5 / 0.3048 && sameThickness) ||
                    (LooksLikeWallName(s.Layer) && s.MeanWallLength >= 1 / 0.3048);
            }
            return stats;
        }

        // 牆端接在主要牆上：端點離主要牆中心線不超過兩者半牆厚之和，或與同厚度的主要牆共線、相距 3.6 m 內（門窗缺口）；
        // 與主要牆重疊（重複描繪）的不算
        private static bool Attached(WallPairBuilder.Wall w, List<WallPairBuilder.Wall> primary)
        {
            var a = w.Axis;
            double length = a.Length;
            double ux = (a.B.X - a.A.X) / length, uy = (a.B.Y - a.A.Y) / length;
            bool attached = false;
            foreach (var p in primary)
            {
                var b = p.Axis;
                double vx = (b.B.X - b.A.X) / b.Length, vy = (b.B.Y - b.A.Y) / b.Length;
                bool parallel = Math.Abs(ux * vy - uy * vx) < 0.01;
                double offset = Math.Abs((a.A.X - b.A.X) * -vy + (a.A.Y - b.A.Y) * vx);
                if (parallel && offset <= Math.Max(w.Thickness, p.Thickness) / 2)
                {
                    double lo = Math.Min((a.A.X - b.A.X) * vx + (a.A.Y - b.A.Y) * vy, (a.B.X - b.A.X) * vx + (a.B.Y - b.A.Y) * vy);
                    double hi = Math.Max((a.A.X - b.A.X) * vx + (a.A.Y - b.A.Y) * vy, (a.B.X - b.A.X) * vx + (a.B.Y - b.A.Y) * vy);
                    double overlap = Math.Min(hi, b.Length) - Math.Max(lo, 0);
                    if (overlap > 0.5 * length) return false;
                    if (offset <= 5 / 304.8 && Math.Abs(w.Thickness - p.Thickness) <= 5 / 304.8 &&
                        Math.Max(lo - b.Length, -hi) <= 3600 / 304.8) attached = true;
                }
                double reach = (w.Thickness + p.Thickness) / 2 + 10 / 304.8;
                if (Distance(a.A, b) <= reach || Distance(a.B, b) <= reach) attached = true;
            }
            return attached;
        }

        private static double Distance(WallRegionGeometry.Point p, WallRegionGeometry.Segment s)
        {
            double dx = s.B.X - s.A.X, dy = s.B.Y - s.A.Y, l2 = dx * dx + dy * dy;
            double t = l2 <= 0 ? 0 : Math.Max(0, Math.Min(1, ((p.X - s.A.X) * dx + (p.Y - s.A.Y) * dy) / l2));
            double x = s.A.X + dx * t - p.X, y = s.A.Y + dy * t - p.Y;
            return Math.Sqrt(x * x + y * y);
        }

        // 平行雙線統計（梁圖層判斷用）
        internal static List<LayerStats> Analyze(IEnumerable<KeyValuePair<string, WallRegionGeometry.Segment>> lines, double maxThickness)
        {
            var stats = lines.GroupBy(l => l.Key).Select(g => Analyze(g.Key, g.Select(l => l.Value).ToList(), maxThickness)).ToList();
            double best = stats.Where(s => !WallRegionGeometry.IsOpeningLayer(s.Layer) && !LooksLikeNonWall(s.Layer))
                .Select(s => s.PairedLength).DefaultIfEmpty(0).Max();
            foreach (var s in stats)
                s.Recommended = !WallRegionGeometry.IsOpeningLayer(s.Layer) && !LooksLikeNonWall(s.Layer) &&
                    s.PairedRatio >= 0.4 && s.PairedLength >= Math.Max(2 / 0.3048, best * 0.15) &&
                    s.TypicalThicknesses.Count > 0 && s.TypicalThicknesses[0] >= MinThickness;
            return stats;
        }

        private class Item
        {
            public WallRegionGeometry.Point A, B;
            public double Length;
            public int Bin;
        }

        private class Projected
        {
            public Item Source;
            public double Offset, Lo, Hi;
        }

        private static LayerStats Analyze(string layer, List<WallRegionGeometry.Segment> segments, double maxThickness)
        {
            var result = new LayerStats { Layer = layer };
            var items = new List<Item>();
            foreach (var s in segments)
            {
                double length = s.Length;
                result.TotalLength += length;
                if (length < MinLineLength) continue;
                double angle = Math.Atan2(s.B.Y - s.A.Y, s.B.X - s.A.X);
                if (angle < 0) angle += Math.PI;
                items.Add(new Item { A = s.A, B = s.B, Length = length, Bin = (int)(angle / AngleStep) % Bins });
            }
            var histogram = new Dictionary<int, double>();
            // 依角度分組（0.5° 一組，連同相鄰組，0° 與 180° 視為相鄰），投影到該組的方向座標後
            // 依法向位移排序，往兩側找最近且互相重疊的平行線
            var bins = items.GroupBy(i => i.Bin).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var bin in bins)
            {
                double angle = (bin.Key + 0.5) * AngleStep, ux = Math.Cos(angle), uy = Math.Sin(angle);
                var candidates = new List<Projected>();
                foreach (int key in new[] { bin.Key, (bin.Key + 1) % Bins, (bin.Key + Bins - 1) % Bins }.Distinct())
                    if (bins.TryGetValue(key, out var members))
                        foreach (var m in members)
                        {
                            double a = m.A.X * ux + m.A.Y * uy, b = m.B.X * ux + m.B.Y * uy;
                            double offset = ((m.A.Y + m.B.Y) * ux - (m.A.X + m.B.X) * uy) / 2;
                            candidates.Add(new Projected { Source = m, Offset = offset, Lo = Math.Min(a, b), Hi = Math.Max(a, b) });
                        }
                candidates.Sort((x, y) => x.Offset.CompareTo(y.Offset));
                foreach (var item in candidates.Where(c => c.Source.Bin == bin.Key))
                {
                    double nearest = double.MaxValue, overlapOfNearest = 0;
                    int start = LowerBound(candidates, item.Offset - maxThickness);
                    for (int k = start; k < candidates.Count && candidates[k].Offset <= item.Offset + maxThickness; k++)
                    {
                        var other = candidates[k];
                        double gap = Math.Abs(other.Offset - item.Offset);
                        if (ReferenceEquals(other.Source, item.Source) || gap < MinThickness || gap >= nearest) continue;
                        double overlap = Math.Min(item.Hi, other.Hi) - Math.Max(item.Lo, other.Lo);
                        // 牆是細長的：重疊長度須明顯大於間距，排除柱、設備等方形輪廓
                        if (overlap < 0.5 * Math.Min(item.Source.Length, other.Source.Length) || overlap < 2.5 * gap) continue;
                        nearest = gap; overlapOfNearest = overlap;
                    }
                    if (nearest == double.MaxValue) continue;
                    result.PairedLength += item.Source.Length;
                    int key = (int)Math.Round(nearest * 304.8 / 10);
                    histogram[key] = (histogram.TryGetValue(key, out double weight) ? weight : 0) + overlapOfNearest;
                }
            }
            result.TypicalThicknesses = histogram.OrderByDescending(p => p.Value).Take(3).Select(p => p.Key * 10 / 304.8).ToList();
            return result;
        }

        private static int LowerBound(List<Projected> sorted, double offset)
        {
            int lo = 0, hi = sorted.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (sorted[mid].Offset < offset) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }
}
