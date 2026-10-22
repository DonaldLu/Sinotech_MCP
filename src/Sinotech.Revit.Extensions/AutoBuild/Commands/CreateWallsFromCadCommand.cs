// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using Sinotech.Revit.Extensions.AutoBuild.Models;
using Sinotech.Revit.Extensions.AutoBuild.Services;

namespace Sinotech.Revit.Extensions.AutoBuild.Commands
{
    /// <summary>
    /// 自動建牆：依連結 CAD 所選圖層的封閉雙線區域求中心線建牆
    /// </summary>
    public class CreateWallsFromCadCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private CreateWallsFromCadEventHandler _handler => (CreateWallsFromCadEventHandler)Handler;

        public override string CommandName => "create_walls_from_cad";

        public CreateWallsFromCadCommand(UIApplication uiApp)
            : base(new CreateWallsFromCadEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    var info = parameters?.ToObject<CadWallCreationInfo>();
                    if (info?.Layers == null || info.Layers.Count == 0)
                        throw new ArgumentException("請至少指定一個 CAD 圖層（layers）");
                    if (string.IsNullOrWhiteSpace(info.WallTypeName) && !info.WallTypeId.HasValue)
                        throw new ArgumentException("請指定牆類型（wallTypeName 或 wallTypeId）");
                    if (string.IsNullOrWhiteSpace(info.BaseLevel) || string.IsNullOrWhiteSpace(info.TopLevel))
                        throw new ArgumentException("請指定基準樓層（baseLevel）與頂部樓層（topLevel）");
                    if (info.BaseLevel == info.TopLevel)
                        throw new ArgumentException("基準樓層與頂部樓層相同，請重新選擇");
                    if (info.MaxThickness <= 0) info.MaxThickness = 1000;

                    _handler.SetParameters(info);

                    if (RaiseAndWaitForCompletion(60000)) // 逾時 60 秒
                    {
                        return _handler.Result;
                    }
                    else
                    {
                        throw new TimeoutException("自動建牆逾時（Revit 可能仍在處理，請稍後查看模型）");
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"自動建牆失敗: {ex.Message}");
                }
            }
        }
    }
}
