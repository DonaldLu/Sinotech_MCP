// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using Autodesk.Revit.DB;
using Sinotech.Revit.Extensions.Common;

namespace Sinotech.Revit.Extensions.AutoBuild
{
    /// <summary>
    /// 讀取目前視圖中連結 CAD（DWG）的線條，依圖層分組
    /// 移植自 TYBIM.AutoBuild.LayersForm.GetCADLayerLines / ReadCadGeometry
    /// </summary>
    public class CadLayerReader
    {
        public class CadLine
        {
            public string Layer;
            public ElementId ImportId;
            public XYZ Start, End;
        }

        public class CadArc
        {
            public string Layer;
            public ElementId ImportId;
            public XYZ Center, Mid;
            public double Radius;
        }

        public class CadCircle
        {
            public string Layer;
            public ElementId ImportId;
            public XYZ Center;
            public double Radius;
        }

        /// <summary>平面上的矩形範圍（模型座標）</summary>
        public class Region
        {
            public double MinX, MinY, MaxX, MaxY;
            public bool Contains(XYZ p) { return p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY; }
        }

        public class CadImport
        {
            public ElementId Id;
            public string Name;
        }

        private readonly Document _doc;
        private readonly Options _options = new Options
        {
            ComputeReferences = true,
            IncludeNonVisibleObjects = true,
            DetailLevel = ViewDetailLevel.Fine
        };

        public List<CadImport> Imports { get; } = new List<CadImport>();
        public List<CadLine> Lines { get; } = new List<CadLine>();
        // 圓弧（門的開啟弧線），用來判斷門
        public List<CadArc> Arcs { get; } = new List<CadArc>();
        // 整圓（圓柱）
        public List<CadCircle> Circles { get; } = new List<CadCircle>();
        // 每個連結 CAD 的轉換（DWG 座標換算到模型座標，讀取 DWG 文字時使用）
        public Dictionary<ElementId, Transform> ImportTransforms { get; } = new Dictionary<ElementId, Transform>();
        // 只讀取此範圍內的線條（一張 DWG 有多個樓層平面時，用視圖裁剪範圍限定）；null 表示全部
        public Region Bounds { get; set; }
        // 各圖層中無法用來建牆的曲線（圓弧、雲形線等）數量
        public Dictionary<string, int> UnsupportedCurves { get; } = new Dictionary<string, int>();
        public SortedSet<string> Layers { get; } = new SortedSet<string>();

        public CadLayerReader(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// 讀取目前視圖中的連結 CAD；importIds 不為空時只讀取指定的 CAD
        /// </summary>
        public CadLayerReader Read(ICollection<long> importIds = null)
        {
            var importInstances = new FilteredElementCollector(_doc, _doc.ActiveView.Id)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .Where(x => x.Category != null && x.IsLinked);

            foreach (ImportInstance importInstance in importInstances)
            {
                if (importIds != null && importIds.Count > 0 && !importIds.Contains(importInstance.Id.ToLong())) continue;
                Imports.Add(new CadImport { Id = importInstance.Id, Name = importInstance.Category.Name });
                var geometry = importInstance.get_Geometry(_options);
                var top = geometry?.OfType<GeometryInstance>().FirstOrDefault();
                if (top != null) ImportTransforms[importInstance.Id] = top.Transform;
                ReadCadGeometry(geometry, Transform.Identity, null, importInstance.Id);
            }
            return this;
        }

        /// <summary>
        /// 依雙線特徵統計每個圖層像不像牆（只用水平面上的直線）
        /// </summary>
        internal List<WallLayerDetector.LayerStats> AnalyzeLayers(double maxThickness)
        {
            double tolerance = 1 / 304.8;
            return WallLayerDetector.AnalyzeWalls(Lines.Where(l => Math.Abs(l.Start.Z - l.End.Z) <= tolerance)
                .Select(l => new KeyValuePair<string, WallRegionGeometry.Segment>(l.Layer, new WallRegionGeometry.Segment(
                    new WallRegionGeometry.Point(l.Start.X, l.Start.Y), new WallRegionGeometry.Point(l.End.X, l.End.Y)))), maxThickness);
        }

        /// <summary>
        /// 視圖有啟用裁剪範圍時回傳該範圍（模型座標的外接矩形），否則 null
        /// </summary>
        public static Region CropRegion(View view)
        {
            if (view == null || !view.CropBoxActive) return null;
            var box = view.CropBox;
            var corners = new[] { new XYZ(box.Min.X, box.Min.Y, 0), new XYZ(box.Max.X, box.Min.Y, 0), new XYZ(box.Min.X, box.Max.Y, 0), new XYZ(box.Max.X, box.Max.Y, 0) }
                .Select(box.Transform.OfPoint).ToList();
            return new Region { MinX = corners.Min(c => c.X), MinY = corners.Min(c => c.Y), MaxX = corners.Max(c => c.X), MaxY = corners.Max(c => c.Y) };
        }

        /// <summary>
        /// 預設樓層：基準為目前平面視圖的樓層，頂部為高程更高的下一個樓層
        /// </summary>
        public static void DefaultLevels(Document doc, out Level baseLevel, out Level topLevel)
        {
            baseLevel = (doc.ActiveView as ViewPlan)?.GenLevel;
            double elevation = baseLevel?.Elevation ?? double.MaxValue;
            topLevel = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .Where(l => l.Elevation > elevation + 1e-6).OrderBy(l => l.Elevation).FirstOrDefault();
        }

        private bool Inside(XYZ p) { return Bounds == null || Bounds.Contains(p); }

        private void ReadCadGeometry(GeometryElement geometry, Transform transform, string inheritedLayer, ElementId importId)
        {
            if (geometry == null) return;
            foreach (GeometryObject obj in geometry)
            {
                GraphicsStyle style = _doc.GetElement(obj.GraphicsStyleId) as GraphicsStyle;
                string layer = style?.GraphicsStyleCategory?.Name ?? inheritedLayer;
                // 與 AutoCAD 相同：圖塊內 0 圖層的物件隨插入圖塊所在的圖層
                if (layer == "0" && !string.IsNullOrEmpty(inheritedLayer)) layer = inheritedLayer;
                if (obj is GeometryInstance instance)
                {
                    ReadCadGeometry(instance.GetSymbolGeometry(), transform.Multiply(instance.Transform), layer, importId);
                    continue;
                }
                if (string.IsNullOrEmpty(layer)) continue;
                if (obj is PolyLine polyline)
                {
                    var points = polyline.GetCoordinates().Select(transform.OfPoint).ToList();
                    for (int i = 1; i < points.Count; i++)
                        if (Inside(points[i - 1]) && Inside(points[i]))
                            Lines.Add(new CadLine { Layer = layer, ImportId = importId, Start = points[i - 1], End = points[i] });
                    Layers.Add(layer);
                }
                else if (obj is Line line && line.IsBound)
                {
                    XYZ start = transform.OfPoint(line.GetEndPoint(0)), end = transform.OfPoint(line.GetEndPoint(1));
                    if (Inside(start) && Inside(end))
                        Lines.Add(new CadLine { Layer = layer, ImportId = importId, Start = start, End = end });
                    Layers.Add(layer);
                }
                else if (obj is Arc circle && !circle.IsBound)
                {
                    var center = transform.OfPoint(circle.Center);
                    if (Inside(center))
                        Circles.Add(new CadCircle { Layer = layer, ImportId = importId, Center = center, Radius = circle.Radius * transform.Scale });
                    Layers.Add(layer);
                }
                else if (obj is Arc arc && arc.IsBound && Inside(transform.OfPoint(arc.Center)))
                {
                    Arcs.Add(new CadArc
                    {
                        Layer = layer,
                        ImportId = importId,
                        Center = transform.OfPoint(arc.Center),
                        Mid = transform.OfPoint(arc.Evaluate(0.5, true)),
                        Radius = arc.Radius * transform.Scale
                    });
                    if (!UnsupportedCurves.ContainsKey(layer)) UnsupportedCurves[layer] = 0;
                    UnsupportedCurves[layer]++;
                    Layers.Add(layer);
                }
                else if (obj is Arc)
                {
                    Layers.Add(layer);
                }
                else if (obj is Curve)
                {
                    if (!UnsupportedCurves.ContainsKey(layer)) UnsupportedCurves[layer] = 0;
                    UnsupportedCurves[layer]++;
                    Layers.Add(layer);
                }
            }
        }
    }
}
