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

        internal static bool LooksLikeNonWall(string layer)
        {
            return NonWallWords.Any(word => layer.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
        }

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
