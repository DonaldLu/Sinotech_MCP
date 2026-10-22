// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
namespace Sinotech.Revit.Extensions.AutoBuild.Models
{
    /// <summary>
    /// 自動建牆（create_walls_from_cad）輸入參數
    /// </summary>
    public class CadWallCreationInfo
    {
        /// <summary>
        /// 要建牆的 CAD 圖層名稱；名稱含 OPEN/DOOR/WINDOW/開口/門/窗 的圖層視為開口，用來連接牆
        /// </summary>
        public List<string> Layers { get; set; } = new List<string>();

        /// <summary>
        /// 基本牆類型名稱（與 WallTypeId 擇一）
        /// </summary>
        public string WallTypeName { get; set; }

        /// <summary>
        /// 基本牆類型 ElementId（與 WallTypeName 擇一）
        /// </summary>
        public long? WallTypeId { get; set; }

        /// <summary>
        /// 基準樓層名稱
        /// </summary>
        public string BaseLevel { get; set; }

        /// <summary>
        /// 頂部樓層名稱
        /// </summary>
        public string TopLevel { get; set; }

        /// <summary>
        /// 是否依中間樓層逐層分段建牆
        /// </summary>
        public bool ByLevel { get; set; }

        /// <summary>
        /// 可辨識的最大牆厚（mm）
        /// </summary>
        public double MaxThickness { get; set; } = 1000;

        /// <summary>
        /// 只讀取指定的連結 CAD（ImportInstance ElementId）；空白表示目前視圖中全部連結 CAD
        /// </summary>
        public List<long> ImportIds { get; set; } = new List<long>();
    }

    /// <summary>
    /// 自動建牆結果統計
    /// </summary>
    public class CadWallCreationResult
    {
        public int CreatedCount { get; set; }
        public List<long> WallIds { get; set; } = new List<long>();
        public int ClosedRegions { get; set; }
        public int JoinedOpenings { get; set; }
        public int CenterLines { get; set; }
        public int OpenEdges { get; set; }
        public int InvalidLoops { get; set; }
        public int NonPlanarLines { get; set; }
        public int UnsupportedCurves { get; set; }
        public int Failed { get; set; }
        public int SuppressedWarnings { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// 連結 CAD 圖層查詢（get_cad_layers）結果
    /// </summary>
    public class CadLayersResult
    {
        public string ActiveView { get; set; }
        public List<CadImportInfo> CadLinks { get; set; } = new List<CadImportInfo>();
        public List<CadLayerInfo> Layers { get; set; } = new List<CadLayerInfo>();
        public List<CadWallTypeInfo> WallTypes { get; set; } = new List<CadWallTypeInfo>();
        public List<CadLevelInfo> Levels { get; set; } = new List<CadLevelInfo>();
    }

    public class CadImportInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
    }

    public class CadLayerInfo
    {
        public string Name { get; set; }
        public int LineCount { get; set; }
        public int UnsupportedCurveCount { get; set; }
        public bool IsOpeningLayer { get; set; }
    }

    public class CadWallTypeInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        /// <summary>牆厚（mm）</summary>
        public double Width { get; set; }
    }

    public class CadLevelInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        /// <summary>高程（mm）</summary>
        public double Elevation { get; set; }
    }
}
