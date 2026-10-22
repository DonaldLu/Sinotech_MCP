# Sinotech_MCP

讓 Claude 等 AI 助手透過 Model Context Protocol (MCP) 操作 Autodesk Revit（Revit 2020–2026）。

```
MCP Client ⇄(stdio)⇄ server/ (TypeScript) ⇄(WebSocket)⇄ plugin/ (Revit Add-in) → commandset/ → Revit API
```

| 目錄 | 說明 |
| --- | --- |
| `server/` | MCP Server（Node.js 20+），將工具呼叫轉為 WebSocket 訊息 |
| `plugin/` | Revit Add-in（`Sinotech_MCP.addin`），功能區「Sinotech MCP」含開關與設定 |
| `commandset/` | 實際執行 Revit API 的命令集，`command.json` 為命令清單 |
| `Sinotech_MCP_Setup/` | Visual Studio Installer 安裝專案 |

## 建置與安裝

```powershell
.\scripts\build.ps1 -Revit 2026 -Install   # 建置並安裝到 %AppData%\Autodesk\Revit\Addins\2026
cd server; npm install; npm run build        # 建置 MCP Server
```

Claude Code 註冊（路徑請依實際調整）：

```bash
claude mcp add sinotech-mcp -- node C:\Prj\Revit\Sinotech_MCP\server\build\index.js
```

啟動 Revit → 功能區「Sinotech MCP」→ 於 Settings 勾選要啟用的命令 → 按 Switch 開啟服務。

## 授權

衍生自 MIT 授權專案，詳見 `LICENSE` 與 `NOTICE.md`。
