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
    /// 自動建柱：依連結 CAD 的柱輪廓（封閉小矩形或圓）建立結構柱，只建柱
    /// 柱圖層依模型中已有的牆、梁判斷（沒有時改用 CAD 牆線；都沒有時只採名稱像柱的圖層）
    /// </summary>
    public class CreateColumnsFromCadEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public CadColumnCreationInfo Info { get; private set; }

        public ExtensionResult<CadStructureResult> Result { get; private set; }

        public void SetParameters(CadColumnCreationInfo info)
        {
            Info = info;
            _resetEvent.Reset();
        }

        public void Execute(UIApplication uiapp)
        {
            try
            {
                Result = CreateColumns(uiapp.ActiveUIDocument.Document);
            }
            catch (Exception ex)
            {
                Result = Fail("自動建柱失敗: " + ex.Message);
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        private ExtensionResult<CadStructureResult> CreateColumns(Document doc)
        {
            CadInputs.Levels(doc, Info.BaseLevel, Info.TopLevel, out Level bottom, out Level top);
            if (bottom == null || top == null || top.Elevation <= bottom.Elevation)
                return Fail("請指定有效的基準／頂部樓層，且頂部樓層須高於基準樓層（省略時使用目前平面視圖的樓層與其上一層）。");
            var reader = CadInputs.Read(doc, Info.ImportIds);
            if (reader.Imports.Count == 0)
                return Fail("目前視圖沒有連結的 CAD，請切換到含有 DWG 連結的平面視圖。");
            var stats = new CadStructureResult { BaseLevel = bottom.Name, TopLevel = top.Name, CroppedToView = reader.Bounds != null };
            var supports = CadInputs.ModelSupports(doc);
            if (supports.Count == 0) supports = CadInputs.CadWallAxes(reader, 1500 / 304.8);
            var columns = CadInputs.Columns(reader, CadInputs.LayerLines(reader), Info.ColumnLayers, supports, out var used);
            stats.ColumnLayers = used;
            if (columns.Count == 0)
                return new ExtensionResult<CadStructureResult>
                {
                    Success = false,
                    Message = Info.ColumnLayers != null && Info.ColumnLayers.Count > 0
                        ? "指定的圖層中找不到柱輪廓（邊長 20～150 cm 的封閉矩形或圓）。"
                        : "找不到柱圖層（封閉小矩形或圓、尺寸重複且貼著牆或梁）。請用 get_cad_layers 查看 columnShapes 後指定 columnLayers。",
                    Response = stats
                };

            using (var transaction = new Transaction(doc, "自動建柱"))
            {
                WarningSwallower.Attach(transaction, new WarningSwallower());
                transaction.Start();
                var placer = new StructurePlacer(doc, stats.Notes);
                stats.ColumnsPlaced = placer.PlaceColumns(columns, bottom, top).Count;
                stats.TypeCounts = placer.TypeCounts;
                stats.CreatedTypes = placer.CreatedTypes;
                stats.Failed = placer.Failed;
                stats.Errors = placer.Errors;
                if (transaction.Commit() != TransactionStatus.Committed)
                    return Fail("建柱交易未成功提交，請檢查 Revit 的錯誤訊息。");
            }

            string message = $"已建立結構柱 {stats.ColumnsPlaced} 支（{bottom.Name} → {top.Name}，圖層：{string.Join("、", stats.ColumnLayers)}）。" +
                (stats.CroppedToView ? "\n（只處理目前視圖裁剪範圍內的 CAD 圖形）" : "");
            if (stats.TypeCounts.Count > 0) message += "\n類型：" + string.Join("、", stats.TypeCounts.Select(p => $"{p.Key} {p.Value}"));
            if (stats.CreatedTypes.Count > 0) message += "\n新建類型：" + string.Join("、", stats.CreatedTypes);
            if (stats.Failed > 0) message += $"\n放置失敗 {stats.Failed} 處" + (stats.Errors.Count == 0 ? "" : "：" + string.Join("；", stats.Errors));
            foreach (var note in stats.Notes) message += "\n" + note;
            return new ExtensionResult<CadStructureResult> { Success = stats.ColumnsPlaced > 0, Message = message, Response = stats };
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
            return "自動建柱";
        }
    }
}
