# SinotechExtensions：中興自行開發命令集

> 使用前須經開發者授權，詳見 [LICENSE.md](LICENSE.md)。

這是 Sinotech_MCP 的**獨立命令集**，和原本衍生自開源專案的 `RevitMCPCommandSet` 分開編譯、分開部署。安裝後 Revit 啟動時即自動載入，不需要另外啟用。

## 功能一覽

| 功能 | MCP 工具 | 說明 | 會修改模型 |
|---|---|---|---|
| 自動建牆 | `get_cad_layers` | 列出目前視圖連結 CAD 的圖層（含雙線統計與自動判定結果）、基本牆類型、樓層 | 否 |
| 自動建牆 | `create_walls_from_cad` | 依圖層（可自動判定）的封閉雙線區域求中心線並建牆，並依 CAD 放置門窗、結構柱，每個房間建樓板 | 是（執行前會跳出確認） |
| 自動建梁 | `create_beams_from_cad` | 依結構平面圖的梁雙線與標註建立梁，可一併放置結構柱 | 是（執行前會跳出確認） |

### 自動建牆的使用方式

1. 在 Revit 開啟含有**連結 DWG**的平面視圖（用「連結 CAD」放入，不支援「匯入 CAD」）。
2. 開啟功能區「中興MCP → AI助手」，輸入例如：「用連結的 CAD 自動建牆」。
3. 不需要選圖層：AI 依各圖層的雙線統計與名稱自行判定牆線圖層，門窗開口圖層會一併加入。也可以直接指定，例如「只用 A-WALL 圖層、牆類型用 RC 20cm」。
4. 未指定牆類型時，每道牆依 CAD 量到的牆厚選用最接近的基本牆類型（厚度相差超過 10 mm 會在結果中提醒）；未指定樓層時，使用目前平面視圖的樓層到上一層。
5. AI 呼叫建牆前，面板會顯示完整參數，按確認後才開始建牆，可以用 Revit 的「復原」還原。

### 門窗（`OpeningDetector.cs`、`OpeningPlacer.cs`）

建牆時預設一併放置門窗（`placeOpenings: false` 可關閉）。判斷只看幾何，不看圖層名稱，牆、門、窗分圖層或畫在一起都適用：

1. 找開口：同牆厚、共線的兩段牆之間 40～360 cm 的缺口，以及開口圖層線條補齊的缺口。
2. 判斷種類：
   - 圓心在門樘、半徑約等於開口寬的圓弧 → 單開門；兩個半寬圓弧 → 雙開門
   - 牆厚範圍內有兩條以上橫跨開口的平行線 → 窗
   - 牆面外一個牆厚內有門片線 → 推拉門
   - 兩側門樘有伸出牆面的短邊 → 柱（不是開口，略過）
   - 其他 → 只回報位置，不放置
3. 門窗缺口補成連續牆，再放置專案中寬度最接近的門窗類型（雙開門找名稱含 Double／雙、推拉門找 Sliding／推拉 的族群）；寬度相差超過 10 mm 時複製一個「原類型名 寬度mm」的類型。
4. 依族群在平面視圖中自己的開門弧線，翻轉開門方向與鉸鏈側以符合 CAD。窗台高度可用 `windowSillHeight` 指定。
5. 門窗只放在基準樓層的牆上（逐層建牆時亦同）。

### 結構柱（`StructureDetector.cs`、`StructurePlacer.cs`）

建牆時預設一併放置（`placeColumns: false` 可關閉）：

- 柱圖層：封閉小矩形（邊長 20～150 cm）或圓佔該圖層封閉輪廓 60% 以上、最常見尺寸佔 30% 以上，且一半以上貼著牆（或梁）；`0`、`DEFPOINTS` 與名稱含門窗、衛浴、標註等字樣的圖層不採用。也可用 `columnLayers` 指定。
- 結構柱高度為基準樓層到頂部樓層，依矩形方向旋轉；類型選用 b×h 相同者，沒有就複製「寬x深cm」（圓柱「D直徑cm」）。專案中沒有混凝土柱族群時從 Revit 族庫載入。
- 結構柱設為房間邊界，樓板才能沿柱與牆圍出房間。

### 樓板（`FloorBuilder.cs`）

建牆時預設一併建立（`placeFloors: false` 可關閉）：在交易中暫時自動放置房間、取得各房間在牆與柱內緣（面層）的邊界後撤銷這些房間，每個房間建一塊樓板（專案預設樓板類型或 `floorTypeName`）。房間內有電梯井叉線，或 6 條以上間距 20～35 cm 的平行線（樓梯踏步）時不建板。門窗補成連續牆，所以房間會在門處分開；無法判斷的牆缺口會讓兩個空間連成一個房間。

### 自動建梁（`create_beams_from_cad`）

用於結構平面圖：

1. 梁圖層：名稱有梁代號（B、J、G、SB、SJ、BEAM、梁、樑，例如 `0S-B`、`0S-J`）且有平行雙線的圖層，也可用 `layers` 指定。
2. 同一圖層內平行、重疊、間距 15～120 cm 的雙線取中心線，梁寬為間距；斷在柱位置（共線、間隔 150 cm 內）的兩段延伸到柱心，梁端延伸到相交的梁。
3. 梁尺寸由連結 DWG 的標註文字讀取（`DwgTextReader.cs`，以 ACadSharp 直接讀檔，因為 Revit API 不提供 CAD 文字）：RC 梁「B23(50X70)」為寬×深 cm，鋼梁「H500X200」為深×翼寬 mm。沒有標註的沿用同一直線上的標註，仍無則梁深 = 梁寬 × 1.4。
4. RC 梁使用混凝土矩形梁族群（b×h，沒有就複製類型、必要時從族庫載入）；鋼梁須專案中已有名稱含相同斷面的結構構架類型，否則略過並回報。梁頂齊指定樓層。
5. 可一併放置結構柱（下一層到此樓層），自動判定時只採用名稱含 COL／柱 的圖層（例如 `0S-COL-H` 下層柱虛線），排除柱頭、擋土柱。
6. **一張 DWG 有多個樓層平面時，只處理目前視圖裁剪範圍內的圖形**：請先在該樓層的平面視圖開啟「裁剪視圖」，把範圍框到要建的那一層。

圖層自動判定（`WallLayerDetector.cs`）：統計每個圖層有多少線段能找到「平行、間距在牆厚範圍內、互相重疊」的雙線夥伴。
雙線比例 ≥ 40%、雙線總長達最像牆圖層的 15% 以上（且至少 2 m）的圖層判定為牆線；名稱含柱、樓梯、家具、標註、文字、填充、軸線、衛浴等字樣，或常見間距小於 5 cm 的圖層不會被自動選入。雙線重疊長度須達間距的 2.5 倍以上才算（排除柱等方形輪廓）。

辨識規則（與 TYBIM 自動翻牆相同）：

- 只處理**直線**輪廓。圓弧等曲線會略過並計入「不支援的曲線」。
- 牆線必須圍成**封閉的雙線區域**，牆厚上限預設為 1000 mm，可用 `maxThickness` 調整。
- 圖層名稱含 `OPEN`、`DOOR`、`WINDOW`、`開口`、`門`、`窗` 的圖層視為開口圖層，用來把門窗兩側的牆連接成同一道牆。
- 勾選「逐層」（`byLevel`）時，會在基準與頂部樓層之間的每個樓層把牆分段。

## 資料夾結構

```
src/Sinotech.Revit.Extensions/
├─ Sinotech.Revit.Extensions.csproj   建置後部署到 revit_mcp_plugin\Commands\SinotechExtensions\
├─ command.json                       命令清單與開發者資訊（Revit 啟動時讀取）
├─ LICENSE.md                         授權聲明
├─ Common/                            共用：回傳格式、ElementId 跨版本相容
└─ AutoBuild/                         自動建牆
   ├─ CadLayerReader.cs               讀取連結 CAD 的線條與圖層
   ├─ WallRegionGeometry.cs           封閉雙線區域 → 牆中心線（移植自 TYBIM）
   ├─ Models/CadWallInfo.cs           參數與回傳資料
   ├─ Commands/                       MCP 命令：解析參數、觸發外部事件
   └─ Services/                       Revit API 實作（在 Revit 主執行緒執行）

server/src/tools/extensions/          對應的 MCP 工具定義（TypeScript）
```

## 新增其他自行開發的功能

1. 在 `src/Sinotech.Revit.Extensions/<功能>/` 新增 `Commands/XxxCommand.cs`（繼承 `ExternalEventCommandBase`）與 `Services/XxxEventHandler.cs`。
2. 在本資料夾的 `command.json` 加入命令。
3. 在 `server/src/tools/extensions/` 新增 `xxx.ts`，命名規則為 `registerXxxTool`。唯讀工具請加上 `annotations: { readOnlyHint: true }`，AI 助手就不會在執行前要求確認。
4. 每個新檔案開頭都要保留著作權標頭。
5. 重新建置：`.\scripts\build.ps1 -Revit 2025 -Install`，以及 `cd server; npm run build`。
