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
    /// 自動建板：依模型中已有的牆、柱圍出的房間，每個房間建一塊樓板，邊界在牆柱內緣；只建板
    /// 連結 CAD 只用來辨識電梯井、樓梯（不建板），沒有連結 CAD 也可執行
    /// </summary>
    public class CreateFloorsFromCadEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public CadFloorCreationInfo Info { get; private set; }

        public ExtensionResult<CadStructureResult> Result { get; private set; }

        public void SetParameters(CadFloorCreationInfo info)
        {
            Info = info;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            try
            {
                Result = CreateFloors(uiapp.ActiveUIDocument.Document);
            }
            catch (Exception ex)
            {
                Result = Fail("自動建板失敗: " + ex.Message);
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private ExtensionResult<CadStructureResult> CreateFloors(Document doc)
        {
            CadInputs.Levels(doc, Info.Level, null, out Level level, out _);
            if (level == null)
                return Fail("請指定樓板所在樓層（level），或切換到該樓層的平面視圖。");
            var floorType = CadInputs.FloorType(doc, Info.FloorTypeName);
            if (floorType == null)
                return Fail(string.IsNullOrWhiteSpace(Info.FloorTypeName) ? "專案中沒有樓板類型。" : "找不到樓板類型：" + Info.FloorTypeName);
            var reader = CadInputs.Read(doc, Info.ImportIds);
            var stats = new CadStructureResult { BaseLevel = level.Name, FloorType = floorType.Name, CroppedToView = reader.Bounds != null };
            if (reader.Imports.Count == 0) stats.Notes.Add("目前視圖沒有連結 CAD，無法辨識電梯井與樓梯，所有房間都會建板。");

            using (var transaction = new Transaction(doc, "自動建板"))
            {
                WarningSwallower.Attach(transaction, new WarningSwallower());
                transaction.Start();
                var builder = new FloorBuilder();
                builder.Build(doc, level, floorType, CadInputs.LayerLines(reader).Select(l => l.Value).ToList(), reader.Bounds);
                stats.FloorsCreated = builder.Created;
                stats.FloorsSkippedShafts = builder.SkippedShafts;
                stats.FloorsSkippedStairs = builder.SkippedStairs;
                stats.FloorsSkippedOutside = builder.SkippedOutside;
                stats.FloorFailures = builder.Failed;
                stats.Errors = builder.Errors;
                if (transaction.Commit() != TransactionStatus.Committed)
                    return Fail("建板交易未成功提交，請檢查 Revit 的錯誤訊息。");
            }

            string message = stats.FloorsCreated > 0
                ? $"已建立樓板 {stats.FloorsCreated} 塊（{level.Name}，{floorType.Name}，每個房間一塊、邊界在牆柱內緣）。"
                : $"{level.Name} 沒有建立樓板：找不到由牆、柱圍成的房間，請先建牆與柱。";
            if (stats.FloorsSkippedShafts + stats.FloorsSkippedStairs > 0)
                message += $"\n略過電梯井／管道間 {stats.FloorsSkippedShafts}、樓梯 {stats.FloorsSkippedStairs} 處。";
            if (stats.FloorsSkippedOutside > 0) message += $"\n不完全在視圖裁剪範圍內的房間 {stats.FloorsSkippedOutside} 個未建板。";
            if (stats.FloorFailures > 0) message += $"\n失敗 {stats.FloorFailures} 處" + (stats.Errors.Count == 0 ? "" : "：" + string.Join("；", stats.Errors));
            foreach (var note in stats.Notes) message += "\n" + note;
            return new ExtensionResult<CadStructureResult> { Success = stats.FloorsCreated > 0, Message = message, Response = stats };
        }

        private static ExtensionResult<CadStructureResult> Fail(string message)
        {
            return new ExtensionResult<CadStructureResult> { Success = false, Message = message };
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 10000)
        {
            _resetEvent.Reset();
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName()
        {
            return "自動建板";
        }
    }
}
