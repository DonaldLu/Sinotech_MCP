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
                ReadCadGeometry(importInstance.get_Geometry(_options), Transform.Identity, null, importInstance.Id);
            }
            return this;
        }

        private void ReadCadGeometry(GeometryElement geometry, Transform transform, string inheritedLayer, ElementId importId)
        {
            if (geometry == null) return;
            foreach (GeometryObject obj in geometry)
            {
                GraphicsStyle style = _doc.GetElement(obj.GraphicsStyleId) as GraphicsStyle;
                string layer = style?.GraphicsStyleCategory?.Name ?? inheritedLayer;
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
                        Lines.Add(new CadLine { Layer = layer, ImportId = importId, Start = points[i - 1], End = points[i] });
                    Layers.Add(layer);
                }
                else if (obj is Line line && line.IsBound)
                {
                    Lines.Add(new CadLine
                    {
                        Layer = layer,
                        ImportId = importId,
                        Start = transform.OfPoint(line.GetEndPoint(0)),
                        End = transform.OfPoint(line.GetEndPoint(1))
                    });
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
