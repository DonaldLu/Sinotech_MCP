// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Sinotech.Revit.Extensions.AutoBuild.Models;
using Sinotech.Revit.Extensions.Common;
using Sinotech.Revit.Extensions.AutoBuild;
using RevitMCPSDK.API.Interfaces;

namespace Sinotech.Revit.Extensions.AutoBuild.Services
{
    /// <summary>
    /// 列出目前視圖中連結 CAD 的圖層，以及自動建牆可用的基本牆類型與樓層
    /// </summary>
    public class GetCadLayersEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public List<long> ImportIds { get; private set; }

        public ExtensionResult<CadLayersResult> Result { get; private set; }

        public void SetParameters(List<long> importIds)
        {
            ImportIds = importIds;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            try
            {
                Document doc = uiapp.ActiveUIDocument.Document;
                var reader = CadInputs.Read(doc, ImportIds);
                var lineCounts = reader.Lines.GroupBy(l => l.Layer).ToDictionary(g => g.Key, g => g.Count());
                var analysis = reader.AnalyzeLayers(1000 / 304.8).ToDictionary(a => a.Layer);
                CadLayerReader.DefaultLevels(doc, out Level defaultBase, out Level defaultTop);
                double tolerance = 1 / 304.8;
                var columnCounts = StructureDetector.FindColumns(
                    reader.Lines.Where(l => Math.Abs(l.Start.Z - l.End.Z) <= tolerance && l.Start.DistanceTo(l.End) > tolerance)
                        .Select(l => new KeyValuePair<string, WallRegionGeometry.Segment>(l.Layer, new WallRegionGeometry.Segment(
                            new WallRegionGeometry.Point(l.Start.X, l.Start.Y), new WallRegionGeometry.Point(l.End.X, l.End.Y)))),
                    reader.Circles.Select(c => new KeyValuePair<string, Tuple<WallRegionGeometry.Point, double>>(c.Layer,
                        Tuple.Create(new WallRegionGeometry.Point(c.Center.X, c.Center.Y), c.Radius))), tolerance)
                    .ToDictionary(c => c.Layer, c => c.Columns.Count);
                var floorTypes = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>().Where(t => !t.IsFoundationSlab).ToList();
                var defaultFloor = doc.GetDefaultElementTypeId(ElementTypeGroup.FloorType);

                var result = new CadLayersResult
                {
                    ActiveView = doc.ActiveView.Name,
                    CadLinks = reader.Imports.Select(i => new CadImportInfo { Id = i.Id.ToLong(), Name = i.Name }).ToList(),
                    Layers = reader.Layers.Select(layer => new CadLayerInfo
                    {
                        Name = layer,
                        LineCount = lineCounts.TryGetValue(layer, out int lines) ? lines : 0,
                        UnsupportedCurveCount = reader.UnsupportedCurves.TryGetValue(layer, out int curves) ? curves : 0,
                        IsOpeningLayer = WallRegionGeometry.IsOpeningLayer(layer),
                        PairedRatio = analysis.TryGetValue(layer, out var a) ? Math.Round(a.PairedRatio, 2) : 0,
                        PairedLength = a == null ? 0 : Math.Round(a.PairedLength * 0.3048, 1),
                        TypicalThicknesses = a == null ? new List<double>() : a.TypicalThicknesses.Select(t => Math.Round(t * 304.8)).ToList(),
                        Recommended = a?.Recommended ?? false,
                        ColumnShapes = columnCounts.TryGetValue(layer, out int shapes) ? shapes : 0,
                        BeamLikeName = StructureDetector.LooksLikeBeamLayer(layer)
                    }).ToList(),
                    FloorTypes = floorTypes.Select(t => t.Name).OrderBy(n => n).ToList(),
                    DefaultFloorType = floorTypes.FirstOrDefault(t => t.Id == defaultFloor)?.Name ?? floorTypes.FirstOrDefault()?.Name,
                    WallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                        .Where(t => t.Kind == WallKind.Basic).OrderBy(t => t.Name)
                        .Select(t => new CadWallTypeInfo { Id = t.Id.ToLong(), Name = t.Name, Width = Math.Round(t.Width * 304.8, 1) })
                        .ToList(),
                    Levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation)
                        .Select(l => new CadLevelInfo { Id = l.Id.ToLong(), Name = l.Name, Elevation = Math.Round(l.Elevation * 304.8, 1) })
                        .ToList(),
                    RecommendedLayers = analysis.Values.Where(a => a.Recommended).Select(a => a.Layer).OrderBy(l => l).ToList(),
                    DefaultBaseLevel = defaultBase?.Name,
                    DefaultTopLevel = defaultTop?.Name
                };

                Result = new ExtensionResult<CadLayersResult>
                {
                    Success = result.CadLinks.Count > 0,
                    Message = result.CadLinks.Count > 0
                        ? $"目前視圖「{result.ActiveView}」找到 {result.CadLinks.Count} 個連結 CAD、{result.Layers.Count} 個圖層"
                        : $"目前視圖「{result.ActiveView}」沒有連結的 CAD，請先在平面視圖中連結 DWG",
                    Response = result
                };
            }
            catch (Exception ex)
            {
                Result = new ExtensionResult<CadLayersResult>
                {
                    Success = false,
                    Message = $"讀取 CAD 圖層失敗: {ex.Message}"
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName()
        {
            return "讀取 CAD 圖層";
        }
    }
}
