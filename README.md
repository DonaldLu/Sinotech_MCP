# Sinotech_MCP

讓 Claude 等 AI 助手透過 Model Context Protocol (MCP) 操作 Autodesk Revit（Revit 2020–2026）。

```
MCP Client ⇄(stdio)⇄ server/ (TypeScript) ⇄(WebSocket)⇄ src/Sinotech.Revit.Addin → src/Sinotech.Revit.Commands → Revit API
```

| 目錄 | 說明 |
| --- | --- |
| `server/` | MCP Server（Node.js 20+），將工具呼叫轉為 WebSocket 訊息 |
| `Sinotech.MCP.sln` | Visual Studio 解決方案（Add-in、Commands、Setup） |
| `src/Sinotech.Revit.Addin/` | Revit Add-in（`Sinotech_MCP.addin`），功能區「中興MCP」含 AI助手、開關與設定 |
| `src/Sinotech.Revit.Commands/` | 實際執行 Revit API 的命令集，根目錄 `command.json` 為命令清單 |
| `src/Sinotech.Revit.Extensions/` | **中興自行開發命令集**（自動建牆等），使用須經開發者授權 |
| `Sinotech_MCP_Setup/` | Revit 2024–2026 MSI 安裝檔產生流程，見 [Setup 說明](Sinotech_MCP_Setup/README.md) |

## 建置與安裝

```powershell
.\scripts\build.ps1 -Revit 2026 -Install   # 建置並安裝到 %AppData%\Autodesk\Revit\Addins\2026
cd server; npm install; npm run build        # 建置 MCP Server
.\Sinotech_MCP_Setup\Build-Msi.ps1             # 產生 Revit 2024–2026 MSI 安裝檔
```

Claude Code 註冊（路徑請依實際調整）：

```bash
claude mcp add sinotech-mcp -- node C:\Prj\Revit\Sinotech_MCP\server\build\index.js
```

啟動 Revit → 功能區「Sinotech MCP」→ 於 Settings 勾選要啟用的命令 → 按 Switch 開啟服務。

## 自行開發命令集（SinotechExtensions）

> 使用須經開發者授權，詳見 [`src/Sinotech.Revit.Extensions/LICENSE.md`](src/Sinotech.Revit.Extensions/LICENSE.md)。

中興自行開發的 Revit 功能獨立放在下列位置，和衍生自開源專案的原始命令集分開：

| 位置 | 內容 |
| --- | --- |
| `src/Sinotech.Revit.Extensions/` | C# 命令集，編譯為 `SinotechExtensions.dll`，部署到 `Commands\SinotechExtensions\` |
| `server/src/tools/extensions/` | 對應的 MCP 工具 |

目前功能：**自動建牆**（移植自 TYBIM 自動翻牆）。在 AI助手中輸入「用連結的 CAD 自動建牆」，AI 會先列出 CAD 圖層、牆類型與樓層讓你選擇，確認後再建牆。詳見 [`src/Sinotech.Revit.Extensions/README.md`](src/Sinotech.Revit.Extensions/README.md)。

## 授權

衍生自 MIT 授權專案，詳見 `LICENSE` 與 `NOTICE.md`。`src/Sinotech.Revit.Extensions/` 與 `server/src/tools/extensions/` 為自行開發部分，不適用 MIT 授權。
