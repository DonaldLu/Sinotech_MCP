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
    /// 自動建牆：讀取連結 CAD 所選圖層的封閉雙線區域，求中心線後建立基本牆
    /// 移植自 TYBIM.AutoBuild.CreateWalls
    /// </summary>
    public class CreateWallsFromCadEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public CadWallCreationInfo Info { get; private set; }

        public ExtensionResult<CadWallCreationResult> Result { get; private set; }

        public void SetParameters(CadWallCreationInfo info)
        {
            Info = info;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            try
            {
                Result = CreateWalls(uiapp);
            }
            catch (Exception ex)
            {
                Result = Fail("自動建牆失敗: " + ex.Message);
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private ExtensionResult<CadWallCreationResult> CreateWalls(UIApplication uiapp)
        {
            Document doc = uiapp.ActiveUIDocument.Document;
            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
            Level bottom = levels.FirstOrDefault(l => l.Name == Info.BaseLevel);
            Level top = levels.FirstOrDefault(l => l.Name == Info.TopLevel);
            WallType type = FindWallType(doc);
            if (type == null || type.Kind != WallKind.Basic)
                return Fail("找不到指定的基本牆類型，請先用 get_cad_layers 查詢可用的牆類型。");
            if (bottom == null || top == null || top.Elevation <= bottom.Elevation)
                return Fail("請指定有效的基準／頂部樓層，且頂部樓層須高於基準樓層。");

            var reader = new CadLayerReader(doc).Read(Info.ImportIds);
            if (reader.Imports.Count == 0)
                return Fail("目前視圖沒有連結的 CAD，請切換到含有 DWG 連結的平面視圖。");
            var selected = new HashSet<string>(Info.Layers);
            var missing = selected.Where(l => !reader.Layers.Contains(l)).ToList();
            if (missing.Count == selected.Count)
                return Fail("連結 CAD 中找不到指定的圖層：" + string.Join("、", missing));

            double tolerance = 1 / 304.8; // 1 mm
            double minimumLength = Math.Max(uiapp.Application.ShortCurveTolerance, tolerance);
            double maxThickness = Info.MaxThickness / 304.8;
            var stats = new CadWallCreationResult
            {
                UnsupportedCurves = reader.UnsupportedCurves.Where(p => selected.Contains(p.Key)).Sum(p => p.Value)
            };
            var centerLines = new List<WallRegionGeometry.Segment>();
            // 不同的 DWG 連結分開重建區域，避免互相干擾。
            foreach (var import in reader.Lines.Where(l => selected.Contains(l.Layer)).GroupBy(l => l.ImportId))
            {
                var input = new List<WallRegionGeometry.Segment>();
                var openings = new List<WallRegionGeometry.Segment>();
                foreach (var line in import)
                {
                    if (Math.Abs(line.Start.Z - line.End.Z) > tolerance) { stats.NonPlanarLines++; continue; }
                    var target = WallRegionGeometry.IsOpeningLayer(line.Layer) ? openings : input;
                    target.Add(new WallRegionGeometry.Segment(new WallRegionGeometry.Point(line.Start.X, line.Start.Y),
                        new WallRegionGeometry.Point(line.End.X, line.End.Y)));
                }
                var geometry = input.Count == 0 ? WallRegionGeometry.Build(openings, tolerance, maxThickness) :
                    WallRegionGeometry.BuildWithOpenings(input, openings, tolerance, maxThickness);
                stats.JoinedOpenings += geometry.JoinedOpenings;
                stats.OpenEdges += geometry.OpenEdges;
                bool valid = true;
                foreach (var boundary in geometry.Boundaries)
                {
                    try
                    {
                        var curves = new List<Curve>();
                        for (int i = 0; i < boundary.Count; i++)
                        {
                            var a = boundary[i]; var b = boundary[(i + 1) % boundary.Count];
                            curves.Add(Line.CreateBound(new XYZ(a.X, a.Y, bottom.Elevation), new XYZ(b.X, b.Y, bottom.Elevation)));
                        }
                        CurveLoop loop = CurveLoop.Create(curves);
                        if (loop.IsOpen() || !loop.HasPlane()) throw new InvalidOperationException("邊界不封閉或不共平面");
                        stats.ClosedRegions++;
                    }
                    catch (Exception) { valid = false; stats.InvalidLoops++; }
                }
                if (valid) centerLines.AddRange(geometry.CenterLines);
            }
            centerLines = WallRegionGeometry.Merge(centerLines, tolerance).Where(s => s.Length > minimumLength).ToList();
            stats.CenterLines = centerLines.Count;
            if (centerLines.Count == 0)
                return new ExtensionResult<CadWallCreationResult>
                {
                    Success = false,
                    Message = $"所選圖層未找到可建牆的封閉雙線區域。請確認邊界封閉、牆厚不超過 {Info.MaxThickness} mm，且為直線輪廓。\n" +
                        Summary(stats),
                    Response = stats
                };

            var stories = Info.ByLevel ? levels.Where(l => l.Elevation >= bottom.Elevation && l.Elevation <= top.Elevation)
                .GroupBy(l => l.Elevation).Select(g => g.First()).ToList() : new List<Level> { bottom, top };
            stories[0] = bottom; stories[stories.Count - 1] = top;

            using (var transaction = new Transaction(doc, "自動建牆"))
            {
                // 略過警告（例如牆重疊），避免跳出對話框卡住 MCP 呼叫。
                var warnings = new WarningSwallower();
                var failureOptions = transaction.GetFailureHandlingOptions();
                failureOptions.SetFailuresPreprocessor(warnings);
                transaction.SetFailureHandlingOptions(failureOptions);
                transaction.Start();
                var created = new List<Wall>();
                for (int i = 0; i < stories.Count - 1; i++)
                    foreach (var s in centerLines)
                    {
                        using (var step = new SubTransaction(doc))
                        {
                            step.Start();
                            try
                            {
                                Line axis = Line.CreateBound(new XYZ(s.A.X, s.A.Y, stories[i].Elevation), new XYZ(s.B.X, s.B.Y, stories[i].Elevation));
                                Wall wall = Wall.Create(doc, axis, type.Id, stories[i].Id,
                                    stories[i + 1].Elevation - stories[i].Elevation, 0, false, false);
                                wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM).Set(0);
                                wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE).Set(stories[i + 1].Id);
                                step.Commit();
                                created.Add(wall);
                            }
                            catch (Exception ex)
                            {
                                step.RollBack(); stats.Failed++;
                                if (stats.Errors.Count < 3) stats.Errors.Add(ex.Message);
                            }
                        }
                    }
                if (transaction.Commit() != TransactionStatus.Committed)
                    return Fail("建牆交易未成功提交，請檢查 Revit 的錯誤訊息。");
                stats.SuppressedWarnings = warnings.Count;
                stats.WallIds = created.Where(w => w.IsValidObject).Select(w => w.Id.ToLong()).ToList();
                stats.CreatedCount = stats.WallIds.Count;
            }

            string message = $"已生成 {stats.CreatedCount} 道牆（{type.Name}，{bottom.Name} → {top.Name}）。\n" + Summary(stats);
            if (missing.Count > 0) message += "\n找不到的圖層：" + string.Join("、", missing);
            return new ExtensionResult<CadWallCreationResult> { Success = stats.CreatedCount > 0, Message = message, Response = stats };
        }

        private WallType FindWallType(Document doc)
        {
            if (Info.WallTypeId.HasValue)
            {
                return doc.GetElement(ElementIdHelper.FromLong(Info.WallTypeId.Value)) as WallType;
            }
            return new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .FirstOrDefault(t => t.Name == Info.WallTypeName);
        }

        private static string Summary(CadWallCreationResult s)
        {
            return $"封閉邊界 {s.ClosedRegions} 個；已連接開口 {s.JoinedOpenings} 處；略過未封閉線段 {s.OpenEdges}；無效邊界 {s.InvalidLoops}；" +
                $"非水平線段 {s.NonPlanarLines}；不支援的曲線 {s.UnsupportedCurves}；建牆失敗 {s.Failed}" +
                (s.Errors.Count == 0 ? "" : "\n" + string.Join("\n", s.Errors));
        }

        private static ExtensionResult<CadWallCreationResult> Fail(string message)
        {
            return new ExtensionResult<CadWallCreationResult> { Success = false, Message = message };
        }

        private class WarningSwallower : IFailuresPreprocessor
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
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName()
        {
            return "自動建牆";
        }
    }
}
