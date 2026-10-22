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
using Sinotech.Revit.Extensions.AutoBuild.Services;

namespace Sinotech.Revit.Extensions.AutoBuild.Commands
{
    /// <summary>
    /// 列出目前視圖連結 CAD 的圖層、基本牆類型與樓層（自動建牆的前置查詢）
    /// </summary>
    public class GetCadLayersCommand : ExternalEventCommandBase
    {
        private static readonly object _executionLock = new object();
        private GetCadLayersEventHandler _handler => (GetCadLayersEventHandler)Handler;

        public override string CommandName => "get_cad_layers";

        public GetCadLayersCommand(UIApplication uiApp)
            : base(new GetCadLayersEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            lock (_executionLock)
            {
                try
                {
                    var importIds = parameters?["importIds"]?.ToObject<List<long>>() ?? new List<long>();
                    _handler.SetParameters(importIds);

                    if (RaiseAndWaitForCompletion(30000)) // 逾時 30 秒
                    {
                        return _handler.Result;
                    }
                    else
                    {
                        throw new TimeoutException("讀取 CAD 圖層逾時");
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"讀取 CAD 圖層失敗: {ex.Message}");
                }
            }
        }
    }
}
