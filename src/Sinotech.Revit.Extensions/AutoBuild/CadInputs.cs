// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using Autodesk.Revit.DB;
using Point = Sinotech.Revit.Extensions.AutoBuild.WallRegionGeometry.Point;
using Segment = Sinotech.Revit.Extensions.AutoBuild.WallRegionGeometry.Segment;

namespace Sinotech.Revit.Extensions.AutoBuild
{
    /// <summary>
    /// 建柱、樑、板、牆共用的 CAD 讀取與換算
    /// </summary>
    internal static class CadInputs
    {
        internal const double Tolerance = 1 / 304.8; // 1 mm

        /// <summary>讀取目前視圖的連結 CAD；視圖有裁剪範圍時只讀範圍內的圖形（一張 DWG 有多個樓層平面時用）</summary>
        internal static CadLayerReader Read(Document doc, ICollection<long> importIds)
        {
            return new CadLayerReader(doc) { Bounds = CadLayerReader.CropRegion(doc.ActiveView) }.Read(importIds);
        }

        // 全部圖層的水平直線
        internal static List<KeyValuePair<string, Segment>> LayerLines(CadLayerReader reader)
        {
            return reader.Lines.Where(l => Math.Abs(l.Start.Z - l.End.Z) <= Tolerance)
                .Select(l => new KeyValuePair<string, Segment>(l.Layer, new Segment(new Point(l.Start.X, l.Start.Y), new Point(l.End.X, l.End.Y))))
                .Where(l => l.Value.Length > Tolerance).ToList();
        }

        internal static List<KeyValuePair<string, Tuple<Point, double>>> Circles(CadLayerReader reader)
        {
            return reader.Circles.Select(c => new KeyValuePair<string, Tuple<Point, double>>(c.Layer, Tuple.Create(new Point(c.Center.X, c.Center.Y), c.Radius))).ToList();
        }

        internal static List<OpeningDetector.CadArc> Arcs(CadLayerReader reader)
        {
            return reader.Arcs.Select(a => new OpeningDetector.CadArc
            {
                Center = new Point(a.Center.X, a.Center.Y),
                Mid = new Point(a.Mid.X, a.Mid.Y),
                Radius = a.Radius
            }).ToList();
        }

        /// <summary>
        /// 模型中已有的牆、梁中心線（平面），作為判斷柱是否「貼著牆或梁」的依據
        /// </summary>
        internal static List<Segment> ModelSupports(Document doc)
        {
            var supports = new List<Segment>();
            var elements = new FilteredElementCollector(doc).OfClass(typeof(Wall)).Cast<Element>()
                .Concat(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_StructuralFraming).WhereElementIsNotElementType());
            foreach (var element in elements)
                if (element.Location is LocationCurve location && location.Curve is Line line && line.IsBound)
                {
                    XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
                    var segment = new Segment(new Point(a.X, a.Y), new Point(b.X, b.Y));
                    if (segment.Length > Tolerance) supports.Add(segment);
                }
            return supports;
        }

        /// <summary>
        /// 依 CAD 自動判定的牆線圖層求牆中心線（沒有已建的牆時，作為柱的依據）
        /// </summary>
        internal static List<Segment> CadWallAxes(CadLayerReader reader, double maxThickness)
        {
            var selected = new HashSet<string>(reader.AnalyzeLayers(maxThickness).Where(a => a.Recommended).Select(a => a.Layer));
            if (selected.Count == 0) return new List<Segment>();
            selected.UnionWith(reader.Layers.Where(WallRegionGeometry.IsOpeningLayer));
            var axes = new List<Segment>();
            foreach (var import in reader.Lines.Where(l => selected.Contains(l.Layer) && Math.Abs(l.Start.Z - l.End.Z) <= Tolerance).GroupBy(l => l.ImportId))
            {
                var input = new List<KeyValuePair<string, Segment>>();
                var openings = new List<Segment>();
                foreach (var line in import)
                {
                    var segment = new Segment(new Point(line.Start.X, line.Start.Y), new Point(line.End.X, line.End.Y));
                    if (WallRegionGeometry.IsOpeningLayer(line.Layer)) openings.Add(segment);
                    else input.Add(new KeyValuePair<string, Segment>(line.Layer, segment));
                }
                if (input.Count > 0) axes.AddRange(WallPairBuilder.Build(input, maxThickness, Tolerance, openings).Walls.Select(w => w.Axis));
            }
            return axes;
        }

        /// <summary>
        /// 找柱：指定圖層時直接採用；否則採用像柱圖層的（有牆或梁可依據時須貼著牆或梁，否則另須圖層名稱像柱）
        /// </summary>
        internal static List<StructureDetector.Column> Columns(CadLayerReader reader, List<KeyValuePair<string, Segment>> layerLines,
            List<string> explicitLayers, List<Segment> supports, out List<string> usedLayers)
        {
            bool hasSupports = supports != null && supports.Count > 0;
            var found = StructureDetector.FindColumns(layerLines, Circles(reader), Tolerance, hasSupports ? supports : null);
            var chosen = explicitLayers != null && explicitLayers.Count > 0
                ? found.Where(f => explicitLayers.Contains(f.Layer)).ToList()
                : found.Where(f => f.Recommended && (hasSupports || StructureDetector.LooksLikeColumnLayer(f.Layer))).ToList();
            usedLayers = chosen.Select(f => f.Layer).OrderBy(l => l).ToList();
            return StructureDetector.Distinct(chosen.SelectMany(f => f.Columns), Tolerance);
        }

        /// <summary>
        /// 樓層：指定名稱優先；否則基準為目前平面視圖的樓層，頂部為其上一層
        /// </summary>
        internal static void Levels(Document doc, string baseName, string topName, out Level bottom, out Level top)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            CadLayerReader.DefaultLevels(doc, out Level defaultBase, out _);
            bottom = string.IsNullOrWhiteSpace(baseName) ? defaultBase : levels.FirstOrDefault(l => l.Name == baseName);
            var b = bottom;
            top = !string.IsNullOrWhiteSpace(topName) ? levels.FirstOrDefault(l => l.Name == topName) :
                b == null ? null : levels.FirstOrDefault(l => l.Elevation > b.Elevation + 1e-6);
        }

        internal static FloorType FloorType(Document doc, string name)
        {
            var types = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t => !t.IsFoundationSlab).ToList();
            if (!string.IsNullOrWhiteSpace(name)) return types.FirstOrDefault(t => t.Name == name);
            var id = doc.GetDefaultElementTypeId(ElementTypeGroup.FloorType);
            return types.FirstOrDefault(t => t.Id == id) ?? types.FirstOrDefault();
        }
    }

    /// <summary>略過警告（例如重疊），避免跳出對話框卡住 MCP 呼叫</summary>
    internal class WarningSwallower : IFailuresPreprocessor
    {
        public int Count;

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() != FailureSeverity.Warning) continue;
                failuresAccessor.DeleteWarning(failure);
                Count++;
            }
            return FailureProcessingResult.Continue;
        }

        internal static void Attach(Transaction transaction, WarningSwallower swallower)
        {
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(swallower);
            transaction.SetFailureHandlingOptions(options);
        }
    }
}
