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
                var reader = new CadLayerReader(doc).Read(ImportIds);
                var lineCounts = reader.Lines.GroupBy(l => l.Layer).ToDictionary(g => g.Key, g => g.Count());

                var result = new CadLayersResult
                {
                    ActiveView = doc.ActiveView.Name,
                    CadLinks = reader.Imports.Select(i => new CadImportInfo { Id = i.Id.ToLong(), Name = i.Name }).ToList(),
                    Layers = reader.Layers.Select(layer => new CadLayerInfo
                    {
                        Name = layer,
                        LineCount = lineCounts.TryGetValue(layer, out int lines) ? lines : 0,
                        UnsupportedCurveCount = reader.UnsupportedCurves.TryGetValue(layer, out int curves) ? curves : 0,
                        IsOpeningLayer = WallRegionGeometry.IsOpeningLayer(layer)
                    }).ToList(),
                    WallTypes = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                        .Where(t => t.Kind == WallKind.Basic).OrderBy(t => t.Name)
                        .Select(t => new CadWallTypeInfo { Id = t.Id.ToLong(), Name = t.Name, Width = Math.Round(t.Width * 304.8, 1) })
                        .ToList(),
                    Levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation)
                        .Select(l => new CadLevelInfo { Id = l.Id.ToLong(), Name = l.Name, Elevation = Math.Round(l.Elevation * 304.8, 1) })
                        .ToList()
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
