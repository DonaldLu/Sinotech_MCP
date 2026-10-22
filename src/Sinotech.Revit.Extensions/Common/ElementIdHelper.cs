// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
using Autodesk.Revit.DB;

namespace Sinotech.Revit.Extensions.Common
{
    /// <summary>
    /// ElementId 跨版本相容：Revit 2024 起改用 long（Value），之前為 int（IntegerValue）
    /// </summary>
    public static class ElementIdHelper
    {
#if REVIT2024_OR_GREATER
        public static long ToLong(this ElementId id) => id.Value;

        public static ElementId FromLong(long value) => new ElementId(value);
#else
        public static long ToLong(this ElementId id) => id.IntegerValue;

        public static ElementId FromLong(long value) => new ElementId((int)value);
#endif
    }
}
