// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using System.IO;
using System.Text.RegularExpressions;
using ACadSharp;
using ACadSharp.IO;
using Autodesk.Revit.DB;
using Point = Sinotech.Revit.Extensions.AutoBuild.WallRegionGeometry.Point;

namespace Sinotech.Revit.Extensions.AutoBuild
{
    /// <summary>
    /// Revit API 不提供連結 CAD 中的文字，改以 ACadSharp 直接讀取連結的 DWG 檔（模型空間的 TEXT、MTEXT），
    /// 換算到模型座標。換算比例由 DWG 線段與 Revit 讀到的線段比對決定（不依賴匯入單位設定）
    /// </summary>
    internal static class DwgTextReader
    {
        // DWG 單位到英尺的候選比例：mm、cm、m、in、ft
        private static readonly double[] Scales = { 1 / 304.8, 1 / 30.48, 1 / 0.3048, 1 / 12.0, 1.0 };

        internal static List<StructureDetector.CadText> Read(Document doc, CadLayerReader reader, List<string> notes)
        {
            var texts = new List<StructureDetector.CadText>();
            foreach (var import in reader.Imports)
            {
                if (!reader.ImportTransforms.TryGetValue(import.Id, out var transform)) continue;
                var type = doc.GetElement((doc.GetElement(import.Id) as ImportInstance)?.GetTypeId() ?? ElementId.InvalidElementId) as CADLinkType;
                string path = null;
                try
                {
                    if (type != null && type.IsExternalFileReference())
                        path = ModelPathUtils.ConvertModelPathToUserVisiblePath(type.GetExternalFileReference().GetAbsolutePath());
                }
                catch (Exception) { }
                if (path == null || !File.Exists(path) || !path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
                {
                    notes.Add($"找不到「{import.Name}」的 DWG 檔，無法讀取標註文字。");
                    continue;
                }
                CadDocument cad;
                try
                {
                    // Revit 可能仍開著連結檔，以共用讀取方式整個讀進記憶體
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        var bytes = new byte[stream.Length];
                        int read = 0;
                        while (read < bytes.Length) { int n = stream.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                        using (var dwg = new DwgReader(new MemoryStream(bytes))) cad = dwg.Read();
                    }
                }
                catch (Exception ex)
                {
                    notes.Add($"讀取「{Path.GetFileName(path)}」失敗：{ex.Message}");
                    continue;
                }
                double scale = MatchScale(cad, transform, reader.Lines.Where(l => l.ImportId == import.Id).ToList(), out XYZ offset);
                if (scale <= 0)
                {
                    notes.Add($"「{Path.GetFileName(path)}」的座標無法與 Revit 中的線段對齊，未讀取標註文字。");
                    continue;
                }
                foreach (var entity in cad.Entities)
                {
                    string value; CSMath.XYZ insert; double rotation, height;
                    if (entity is ACadSharp.Entities.MText mtext)
                    {
                        value = PlainText(mtext.Value); insert = mtext.InsertPoint; height = mtext.Height;
                        rotation = Math.Atan2(mtext.AlignmentPoint.Y, mtext.AlignmentPoint.X);
                    }
                    else if (entity is ACadSharp.Entities.TextEntity text)
                    {
                        value = text.Value; insert = text.InsertPoint; rotation = text.Rotation; height = text.Height;
                    }
                    else continue;
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    var p = transform.OfPoint(new XYZ(insert.X * scale, insert.Y * scale, 0) + offset);
                    if (reader.Bounds != null && !reader.Bounds.Contains(p)) continue;
                    var dir = transform.OfVector(new XYZ(Math.Cos(rotation), Math.Sin(rotation), 0));
                    texts.Add(new StructureDetector.CadText
                    {
                        Position = new Point(p.X, p.Y),
                        Angle = Math.Atan2(dir.Y, dir.X),
                        Height = height * scale * transform.Scale,
                        Text = value
                    });
                }
            }
            return texts;
        }

        // DWG 座標 × 比例 + 位移 = Revit 連結內部座標。對每個候選比例，以「長度與方向相同的線段」配對投票求位移，
        // 再檢查換算後的 DWG 線段端點有多少落在 Revit 線段端點上（3 mm 內）；命中率最高且達 60% 者採用
        private static double MatchScale(CadDocument cad, Transform transform, List<CadLayerReader.CadLine> revitLines, out XYZ offset)
        {
            offset = XYZ.Zero;
            if (revitLines.Count == 0) return 0;
            const double cell = 1.0, tolerance = 3 / 304.8;
            var inverse = transform.Inverse;
            var local = revitLines.Select(l => Tuple.Create(inverse.OfPoint(l.Start), inverse.OfPoint(l.End))).ToList();
            var grid = new Dictionary<Tuple<long, long>, List<XYZ>>();
            var byLength = new Dictionary<long, List<Tuple<XYZ, XYZ>>>();
            foreach (var line in local)
            {
                foreach (var p in new[] { line.Item1, line.Item2 })
                {
                    var key = Tuple.Create((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<XYZ>();
                    list.Add(p);
                }
                long lk = (long)Math.Round(line.Item1.DistanceTo(line.Item2) / tolerance);
                if (!byLength.TryGetValue(lk, out var same)) byLength[lk] = same = new List<Tuple<XYZ, XYZ>>();
                same.Add(line);
            }
            var samples = cad.Entities.OfType<ACadSharp.Entities.Line>().Where(l => l.StartPoint.Z == l.EndPoint.Z).Take(300).ToList();
            if (samples.Count == 0) return 0;
            double best = 0, bestRatio = 0;
            foreach (double scale in Scales)
            {
                // 位移投票（以 1 cm 為格）
                var votes = new Dictionary<Tuple<long, long>, int>();
                foreach (var line in samples.Take(80))
                {
                    var a = new XYZ(line.StartPoint.X * scale, line.StartPoint.Y * scale, 0);
                    var b = new XYZ(line.EndPoint.X * scale, line.EndPoint.Y * scale, 0);
                    long lk = (long)Math.Round(a.DistanceTo(b) / tolerance);
                    for (long k = lk - 1; k <= lk + 1; k++)
                        if (byLength.TryGetValue(k, out var candidates))
                            foreach (var c in candidates.Take(50))
                                foreach (var pair in new[] { Tuple.Create(c.Item1, c.Item2), Tuple.Create(c.Item2, c.Item1) })
                                {
                                    if (!(pair.Item2 - pair.Item1).IsAlmostEqualTo(b - a, tolerance * 2)) continue;
                                    var d = pair.Item1 - a;
                                    var key = Tuple.Create((long)Math.Round(d.X * 30.48), (long)Math.Round(d.Y * 30.48));
                                    votes[key] = (votes.TryGetValue(key, out int v) ? v : 0) + 1;
                                }
                }
                var shift = votes.Count == 0 ? XYZ.Zero : new XYZ(votes.OrderByDescending(v => v.Value).First().Key.Item1 / 30.48,
                    votes.OrderByDescending(v => v.Value).First().Key.Item2 / 30.48, 0);
                foreach (var candidate in new[] { XYZ.Zero, shift }.Distinct())
                {
                    int hit = samples.Count(line => Near(grid, new XYZ(line.StartPoint.X * scale, line.StartPoint.Y * scale, 0) + candidate, cell, tolerance * 4));
                    double ratio = (double)hit / samples.Count;
                    if (ratio > bestRatio) { bestRatio = ratio; best = scale; offset = candidate; }
                }
            }
            return bestRatio >= 0.6 ? best : 0;
        }

        private static bool Near(Dictionary<Tuple<long, long>, List<XYZ>> grid, XYZ p, double cell, double tolerance)
        {
            long cx = (long)Math.Floor(p.X / cell), cy = (long)Math.Floor(p.Y / cell);
            for (long x = cx - 1; x <= cx + 1; x++)
                for (long y = cy - 1; y <= cy + 1; y++)
                    if (grid.TryGetValue(Tuple.Create(x, y), out var list) &&
                        list.Any(q => Math.Abs(q.X - p.X) <= tolerance && Math.Abs(q.Y - p.Y) <= tolerance)) return true;
            return false;
        }

        // 去掉 MTEXT 的格式碼（\P 換行、\f…; 字型、{ } 群組等）
        private static string PlainText(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            value = Regex.Replace(value, @"\\[Pp]", " ");
            value = Regex.Replace(value, @"\\[A-Za-z][^;\\{}]*;", "");
            return value.Replace("{", "").Replace("}", "").Trim();
        }
    }
}
