// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
namespace Sinotech.Revit.Extensions.Common
{
    /// <summary>
    /// 命令統一回傳格式（欄位與原命令集的 AIResult 相同，方便 AI 解讀）
    /// </summary>
    public class ExtensionResult<T>
    {
        /// <summary>是否成功</summary>
        public bool Success { get; set; }

        /// <summary>給使用者看的訊息（繁體中文）</summary>
        public string Message { get; set; }

        /// <summary>回傳資料</summary>
        public T Response { get; set; }
    }
}
