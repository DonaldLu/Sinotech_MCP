# SinotechExtensions：中興自行開發命令集

> 使用前須經開發者授權，詳見 [LICENSE.md](LICENSE.md)。

這是 Sinotech_MCP 的**獨立命令集**，和原本衍生自開源專案的 `RevitMCPCommandSet` 分開編譯、分開部署。安裝後 Revit 啟動時即自動載入，不需要另外啟用。

## 功能一覽

| 功能 | MCP 工具 | 說明 | 會修改模型 |
|---|---|---|---|
| 自動建牆 | `get_cad_layers` | 列出目前視圖連結 CAD 的圖層、基本牆類型、樓層 | 否 |
| 自動建牆 | `create_walls_from_cad` | 依所選圖層的封閉雙線區域求中心線並建牆 | 是（執行前會跳出確認） |

### 自動建牆的使用方式

1. 在 Revit 開啟含有**連結 DWG**的平面視圖（用「連結 CAD」放入，不支援「匯入 CAD」）。
2. 開啟功能區「中興MCP → AI助手」，輸入例如：「用連結的 CAD 自動建牆」。
3. AI 會先列出 CAD 圖層、牆類型與樓層，請選擇牆線圖層，以及門窗開口圖層（如 `WALL`、`DOOR`、`WINDOW`）、牆類型、基準樓層與頂部樓層。
4. AI 呼叫建牆前，面板會顯示完整參數，按確認後才開始建牆，可以用 Revit 的「復原」還原。

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
