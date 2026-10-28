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
        /// 空白時自動判定：使用雙線特徵像牆的圖層，加上全部開口圖層
        /// </summary>
        public List<string> Layers { get; set; } = new List<string>();

        /// <summary>
        /// 基本牆類型名稱（與 WallTypeId 擇一）；兩者皆空白時，每道牆依 CAD 牆厚選用最接近的基本牆類型
        /// </summary>
        public string WallTypeName { get; set; }

        /// <summary>
        /// 基本牆類型 ElementId（與 WallTypeName 擇一）
        /// </summary>
        public long? WallTypeId { get; set; }

        /// <summary>
        /// 基準樓層名稱；空白時使用目前平面視圖的樓層
        /// </summary>
        public string BaseLevel { get; set; }

        /// <summary>
        /// 頂部樓層名稱；空白時使用基準樓層的上一層
        /// </summary>
        public string TopLevel { get; set; }

        /// <summary>
        /// 是否依中間樓層逐層分段建牆
        /// </summary>
        public bool ByLevel { get; set; }

        /// <summary>
        /// 可辨識的最大牆厚（mm）
        /// </summary>
        public double MaxThickness { get; set; } = 1500;

        /// <summary>
        /// 只讀取指定的連結 CAD（ImportInstance ElementId）；空白表示目前視圖中全部連結 CAD
        /// </summary>
        public List<long> ImportIds { get; set; } = new List<long>();

        /// <summary>
        /// 建牆時依 CAD 放置門（預設 false：只建牆）
        /// </summary>
        public bool PlaceDoors { get; set; }

        /// <summary>
        /// 建牆時依 CAD 放置窗（預設 false：只建牆）
        /// </summary>
        public bool PlaceWindows { get; set; }

        /// <summary>
        /// 舊版開關：同時放置門與窗（預設 false）
        /// </summary>
        public bool PlaceOpenings { get; set; }

        /// <summary>
        /// 窗台高度（mm）；空白時使用窗類型的預設值
        /// </summary>
        public double? WindowSillHeight { get; set; }

        /// <summary>
        /// 建牆時一併依 CAD 放置結構柱（預設 false；單獨建柱用 create_columns_from_cad）
        /// </summary>
        public bool PlaceColumns { get; set; }

        /// <summary>
        /// 柱的 CAD 圖層；空白時自動判定（封閉小矩形或圓、尺寸重複且貼著牆）
        /// </summary>
        public List<string> ColumnLayers { get; set; } = new List<string>();

        /// <summary>
        /// 建牆時一併每個房間建一塊樓板（預設 false；單獨建板用 create_floors_from_cad）
        /// </summary>
        public bool PlaceFloors { get; set; }

        /// <summary>
        /// 樓板類型名稱；空白時使用專案預設的樓板類型
        /// </summary>
        public string FloorTypeName { get; set; }
    }

    /// <summary>
    /// 自動建梁（create_beams_from_cad）輸入參數
    /// </summary>
    public class CadBeamCreationInfo
    {
        /// <summary>梁的 CAD 圖層；空白時依圖層名稱與雙線特徵自動判定</summary>
        public List<string> Layers { get; set; } = new List<string>();
        /// <summary>梁頂所在樓層；空白時使用目前平面視圖的樓層</summary>
        public string Level { get; set; }
        /// <summary>沒有標註時，梁深 = 梁寬 × 此倍數（預設 1.4，四捨五入到 5 cm）</summary>
        public double DefaultDepthRatio { get; set; } = 1.4;
        /// <summary>一併依 CAD 放置結構柱（下一層到此樓層；預設 false）</summary>
        public bool PlaceColumns { get; set; }
        /// <summary>柱的 CAD 圖層；空白時自動判定</summary>
        public List<string> ColumnLayers { get; set; } = new List<string>();
        /// <summary>只讀取指定的連結 CAD；空白表示目前視圖中全部連結 CAD</summary>
        public List<long> ImportIds { get; set; } = new List<long>();
    }

    /// <summary>
    /// 自動建柱（create_columns_from_cad）輸入參數
    /// </summary>
    public class CadColumnCreationInfo
    {
        /// <summary>柱的 CAD 圖層；空白時自動判定</summary>
        public List<string> ColumnLayers { get; set; } = new List<string>();
        /// <summary>基準樓層；空白時使用目前平面視圖的樓層</summary>
        public string BaseLevel { get; set; }
        /// <summary>頂部樓層；空白時使用基準樓層的上一層</summary>
        public string TopLevel { get; set; }
        public List<long> ImportIds { get; set; } = new List<long>();
    }

    /// <summary>
    /// 自動建板（create_floors_from_cad）輸入參數
    /// </summary>
    public class CadFloorCreationInfo
    {
        /// <summary>樓板所在樓層；空白時使用目前平面視圖的樓層</summary>
        public string Level { get; set; }
        /// <summary>樓板類型名稱；空白時使用專案預設的樓板類型</summary>
        public string FloorTypeName { get; set; }
        public List<long> ImportIds { get; set; } = new List<long>();
    }

    /// <summary>
    /// 自動建柱、建板結果統計
    /// </summary>
    public class CadStructureResult
    {
        public string BaseLevel { get; set; }
        public string TopLevel { get; set; }
        public bool CroppedToView { get; set; }
        public int ColumnsPlaced { get; set; }
        public List<string> ColumnLayers { get; set; } = new List<string>();
        public int FloorsCreated { get; set; }
        public int FloorsSkippedShafts { get; set; }
        public int FloorsSkippedStairs { get; set; }
        public int FloorsSkippedOutside { get; set; }
        public int FloorFailures { get; set; }
        public string FloorType { get; set; }
        public Dictionary<string, int> TypeCounts { get; set; } = new Dictionary<string, int>();
        public List<string> CreatedTypes { get; set; } = new List<string>();
        public int Failed { get; set; }
        public List<string> Notes { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// 自動建梁結果統計
    /// </summary>
    public class CadBeamCreationResult
    {
        public List<string> UsedLayers { get; set; } = new List<string>();
        public bool LayersAutoDetected { get; set; }
        public string Level { get; set; }
        public bool CroppedToView { get; set; }
        public int BeamsDetected { get; set; }
        public int BeamsPlaced { get; set; }
        /// <summary>有標註、沿用同線標註、無標註（梁深採預設）的梁數</summary>
        public int Labeled { get; set; }
        public int LabelInherited { get; set; }
        public int Unlabeled { get; set; }
        /// <summary>專案中沒有相符鋼梁類型而略過的鋼梁數</summary>
        public int SkippedSteel { get; set; }
        public int ColumnsPlaced { get; set; }
        public List<string> ColumnLayers { get; set; } = new List<string>();
        public Dictionary<string, int> TypeCounts { get; set; } = new Dictionary<string, int>();
        public List<string> CreatedTypes { get; set; } = new List<string>();
        public int Failed { get; set; }
        public List<string> Notes { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
        public List<long> ElementIds { get; set; } = new List<long>();
    }

    /// <summary>
    /// 自動建牆結果統計
    /// </summary>
    public class CadWallCreationResult
    {
        public int CreatedCount { get; set; }
        public List<long> WallIds { get; set; } = new List<long>();
        /// <summary>實際使用的 CAD 圖層</summary>
        public List<string> UsedLayers { get; set; } = new List<string>();
        public bool LayersAutoDetected { get; set; }
        public string BaseLevel { get; set; }
        public string TopLevel { get; set; }
        /// <summary>各牆類型建立的牆數</summary>
        public Dictionary<string, int> WallTypeCounts { get; set; } = new Dictionary<string, int>();
        /// <summary>CAD 牆厚與所用牆類型厚度相差超過 10 mm 的牆數（自動選類型時）</summary>
        public int ThicknessMismatches { get; set; }
        /// <summary>已放置的單開門、雙開門、推拉門、窗數量</summary>
        public int DoorsPlaced { get; set; }
        public int DoubleDoorsPlaced { get; set; }
        public int SlidingDoorsPlaced { get; set; }
        public int WindowsPlaced { get; set; }
        /// <summary>牆上有缺口但無法判斷為門或窗的位置（未放置），座標 mm</summary>
        public List<string> UnknownOpenings { get; set; } = new List<string>();
        /// <summary>判斷為門窗但放置失敗的數量</summary>
        public int OpeningFailures { get; set; }
        /// <summary>各門窗類型放置數量</summary>
        public Dictionary<string, int> OpeningTypeCounts { get; set; } = new Dictionary<string, int>();
        /// <summary>為符合 CAD 開口寬度而新建的門窗類型</summary>
        public List<string> CreatedOpeningTypes { get; set; } = new List<string>();
        /// <summary>門的開啟方向無法自動調整時的說明</summary>
        public string OrientationNote { get; set; }
        /// <summary>結構柱</summary>
        public int ColumnsPlaced { get; set; }
        public List<string> ColumnLayers { get; set; } = new List<string>();
        /// <summary>樓板：建立數、略過的電梯井／管道間與樓梯、失敗數</summary>
        public int FloorsCreated { get; set; }
        public int FloorsSkippedShafts { get; set; }
        public int FloorsSkippedStairs { get; set; }
        public int FloorsSkippedOutside { get; set; }
        public int FloorFailures { get; set; }
        public string FloorType { get; set; }
        /// <summary>柱、樓板的類型統計與新建類型、族群載入等說明</summary>
        public Dictionary<string, int> StructureTypeCounts { get; set; } = new Dictionary<string, int>();
        public List<string> CreatedStructureTypes { get; set; } = new List<string>();
        public List<string> StructureNotes { get; set; } = new List<string>();
        public int JoinedOpenings { get; set; }
        public int CenterLines { get; set; }
        /// <summary>排除的平行雙線：等間距重複（樓梯、填充線）、有對角線的開口符號框、不與牆相連的小框或短線（柱、設備）</summary>
        public int SkippedPatterns { get; set; }
        public int SkippedSymbols { get; set; }
        public int SkippedIsolated { get; set; }
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
        /// <summary>專案中的樓板類型（含預設者）</summary>
        public List<string> FloorTypes { get; set; } = new List<string>();
        public string DefaultFloorType { get; set; }
        /// <summary>依雙線特徵自動判定的牆線圖層（create_walls_from_cad 省略 layers 時使用，另加全部開口圖層）</summary>
        public List<string> RecommendedLayers { get; set; } = new List<string>();
        /// <summary>省略樓層時的預設基準樓層（目前平面視圖的樓層）與頂部樓層（上一層）</summary>
        public string DefaultBaseLevel { get; set; }
        public string DefaultTopLevel { get; set; }
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
        /// <summary>有平行雙線夥伴（間距在牆厚範圍內且互相重疊）的線段長度比例 0～1</summary>
        public double PairedRatio { get; set; }
        /// <summary>有雙線夥伴的線段總長（m）</summary>
        public double PairedLength { get; set; }
        /// <summary>常見雙線間距（mm），由多到少，可視為牆厚</summary>
        public List<double> TypicalThicknesses { get; set; } = new List<double>();
        /// <summary>由雙線求得的牆中心線總長（m）</summary>
        public double WallLength { get; set; }
        /// <summary>屬於牆網（5 道以上相連的牆）的牆長（m）；真正的牆線圖層會連成牆網</summary>
        public double NetworkLength { get; set; }
        /// <summary>平均每道牆長（m）；樓梯、填充線、設備等多半很短</summary>
        public double MeanWallLength { get; set; }
        /// <summary>依求得的牆（牆網、牆長、與主要牆相接）判定為牆線圖層</summary>
        public bool Recommended { get; set; }
        /// <summary>柱形狀（封閉小矩形或圓）數量</summary>
        public int ColumnShapes { get; set; }
        /// <summary>圖層名稱像梁圖層（B、J、SB、SJ、BEAM、梁、樑…）</summary>
        public bool BeamLikeName { get; set; }
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
