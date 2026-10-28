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
using RevitMCPSDK.API.Interfaces;

namespace Sinotech.Revit.Extensions.AutoBuild.Services
{
    /// <summary>
    /// 自動建梁：由結構平面圖的梁雙線求梁中心線與梁寬，從 DWG 的標註文字讀梁深（RC「(寬X深)」cm、鋼梁「H深X翼寬」mm），
    /// 梁頂齊指定樓層；可一併依柱輪廓放置結構柱。一張 DWG 有多個樓層平面時，只處理目前視圖裁剪範圍內的圖形
    /// </summary>
    public class CreateBeamsFromCadEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public CadBeamCreationInfo Info { get; private set; }

        public ExtensionResult<CadBeamCreationResult> Result { get; private set; }

        public void SetParameters(CadBeamCreationInfo info)
        {
            Info = info;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            try
            {
                Result = CreateBeams(uiapp.ActiveUIDocument.Document);
            }
            catch (Exception ex)
            {
                Result = new ExtensionResult<CadBeamCreationResult> { Success = false, Message = "自動建梁失敗: " + ex.Message };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private ExtensionResult<CadBeamCreationResult> CreateBeams(Document doc)
        {
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            CadLayerReader.DefaultLevels(doc, out Level defaultLevel, out _);
            Level level = string.IsNullOrWhiteSpace(Info.Level) ? defaultLevel : levels.FirstOrDefault(l => l.Name == Info.Level);
            if (level == null)
                return Fail("請指定梁所在樓層（level），或切換到該樓層的平面視圖。");
            var stats = new CadBeamCreationResult { Level = level.Name };
            var reader = new CadLayerReader(doc) { Bounds = CadLayerReader.CropRegion(doc.ActiveView) };
            stats.CroppedToView = reader.Bounds != null;
            reader.Read(Info.ImportIds);
            if (reader.Imports.Count == 0)
                return Fail("目前視圖沒有連結的 CAD，請切換到含有結構平面 DWG 連結的平面視圖。");

            double tolerance = 1 / 304.8;
            var layerLines = reader.Lines.Where(l => Math.Abs(l.Start.Z - l.End.Z) <= tolerance)
                .Select(l => new KeyValuePair<string, WallRegionGeometry.Segment>(l.Layer,
                    new WallRegionGeometry.Segment(new WallRegionGeometry.Point(l.Start.X, l.Start.Y), new WallRegionGeometry.Point(l.End.X, l.End.Y))))
                .Where(l => l.Value.Length > tolerance).ToList();
            stats.LayersAutoDetected = Info.Layers == null || Info.Layers.Count == 0;
            // 自動判定：名稱像梁圖層，且有足夠的平行雙線
            var beamLayers = new HashSet<string>(stats.LayersAutoDetected
                ? WallLayerDetector.Analyze(layerLines.Where(l => StructureDetector.LooksLikeBeamLayer(l.Key)), 120 / 30.48)
                    .Where(a => a.PairedRatio >= 0.3 && a.PairedLength >= 2 / 0.3048).Select(a => a.Layer)
                : Info.Layers);
            stats.UsedLayers = beamLayers.Where(reader.Layers.Contains).OrderBy(l => l).ToList();
            if (stats.UsedLayers.Count == 0)
                return Fail(stats.LayersAutoDetected
                    ? "無法自動判定梁圖層。請用 get_cad_layers 查看圖層後指定 layers。"
                    : "連結 CAD 中找不到指定的梁圖層：" + string.Join("、", Info.Layers));

            var texts = DwgTextReader.Read(doc, reader, stats.Notes);
            var beams = StructureDetector.FindBeams(layerLines.Where(l => beamLayers.Contains(l.Key)), texts, tolerance);
            stats.BeamsDetected = beams.Count;
            stats.Labeled = beams.Count(b => b.Label != null && !b.Label.EndsWith("（沿用）"));
            stats.LabelInherited = beams.Count(b => b.Label != null && b.Label.EndsWith("（沿用）"));
            stats.Unlabeled = beams.Count(b => b.Label == null);
            if (beams.Count == 0)
                return new ExtensionResult<CadBeamCreationResult> { Success = false, Message = "所選圖層找不到梁的雙線（間距 15～120 cm 的平行線）。", Response = stats };

            var columns = new List<StructureDetector.Column>();
            Level below = levels.LastOrDefault(l => l.Elevation < level.Elevation - 1e-6);
            if (Info.PlaceColumns && below != null)
            {
                var circles = reader.Circles.Select(c => new KeyValuePair<string, Tuple<WallRegionGeometry.Point, double>>(c.Layer,
                    Tuple.Create(new WallRegionGeometry.Point(c.Center.X, c.Center.Y), c.Radius)));
                var found = StructureDetector.FindColumns(layerLines, circles, tolerance, beams.Select(b => b.Axis).ToList());
                var chosen = Info.ColumnLayers != null && Info.ColumnLayers.Count > 0
                    ? found.Where(f => Info.ColumnLayers.Contains(f.Layer)).ToList()
                    : found.Where(f => f.Recommended && StructureDetector.LooksLikeColumnLayer(f.Layer)).ToList();
                stats.ColumnLayers = chosen.Select(f => f.Layer).OrderBy(l => l).ToList();
                columns = StructureDetector.Distinct(chosen.SelectMany(f => f.Columns), tolerance);
            }
            else if (Info.PlaceColumns) stats.Notes.Add("梁所在樓層下方沒有樓層，未放置柱。");

            using (var transaction = new Transaction(doc, columns.Count > 0 ? "自動建梁、柱" : "自動建梁"))
            {
                var failureOptions = transaction.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(new WarningSwallower());
                transaction.SetFailureHandlingOptions(failureOptions);
                transaction.Start();
                var placer = new StructurePlacer(doc, stats.Notes);
                stats.BeamsPlaced = placer.PlaceBeams(beams, level, Info.DefaultDepthRatio > 0 ? Info.DefaultDepthRatio : 1.4, out int skippedSteel);
                stats.SkippedSteel = skippedSteel;
                if (columns.Count > 0) stats.ColumnsPlaced = placer.PlaceColumns(columns, below, level).Count;
                stats.TypeCounts = placer.TypeCounts;
                stats.CreatedTypes = placer.CreatedTypes;
                stats.Failed = placer.Failed;
                stats.Errors = placer.Errors;
                if (transaction.Commit() != TransactionStatus.Committed)
                    return Fail("建梁交易未成功提交，請檢查 Revit 的錯誤訊息。");
            }

            string message = $"已建立梁 {stats.BeamsPlaced} 支（共辨識 {stats.BeamsDetected} 支，梁頂齊 {level.Name}）。\n" +
                $"使用圖層{(stats.LayersAutoDetected ? "（自動判定）" : "")}：{string.Join("、", stats.UsedLayers)}" +
                (stats.CroppedToView ? "（只處理目前視圖裁剪範圍內的圖形）" : "") + "\n" +
                $"梁尺寸：依標註 {stats.Labeled}、沿用同線標註 {stats.LabelInherited}、無標註 {stats.Unlabeled}（梁深採梁寬 × {Info.DefaultDepthRatio}）";
            if (stats.SkippedSteel > 0) message += $"\n鋼梁 {stats.SkippedSteel} 支：專案中沒有相同斷面（例如 H500X200）的結構構架類型，未放置，請先載入鋼梁族群。";
            if (Info.PlaceColumns && below != null)
                message += stats.ColumnLayers.Count == 0 ? "\n結構柱：此平面沒有柱輪廓，未放置。" :
                    $"\n結構柱 {stats.ColumnsPlaced} 支（{below.Name} → {level.Name}，圖層：{string.Join("、", stats.ColumnLayers)}）";
            if (stats.TypeCounts.Count > 0) message += "\n類型：" + string.Join("、", stats.TypeCounts.Select(p => $"{p.Key} {p.Value}"));
            if (stats.CreatedTypes.Count > 0) message += "\n新建類型：" + string.Join("、", stats.CreatedTypes);
            if (stats.Failed > 0) message += $"\n放置失敗 {stats.Failed} 處" + (stats.Errors.Count == 0 ? "" : "：" + string.Join("；", stats.Errors));
            foreach (var note in stats.Notes) message += "\n" + note;
            return new ExtensionResult<CadBeamCreationResult> { Success = stats.BeamsPlaced > 0, Message = message, Response = stats };
        }

        private static ExtensionResult<CadBeamCreationResult> Fail(string message)
        {
            return new ExtensionResult<CadBeamCreationResult> { Success = false, Message = message };
        }

        private class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                foreach (var failure in failuresAccessor.GetFailureMessages())
                    if (failure.GetSeverity() == FailureSeverity.Warning) failuresAccessor.DeleteWarning(failure);
                return FailureProcessingResult.Continue;
            }
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName()
        {
            return "自動建梁";
        }
    }
}
