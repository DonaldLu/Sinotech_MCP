# 第三方授權聲明

本專案之 Revit Add-in（`plugin/`）、命令集（`commandset/`）與 MCP Server（`server/`）
衍生自 [mcp-servers-for-revit](https://github.com/mcp-servers-for-revit/mcp-servers-for-revit)
（Copyright (c) 2026 sparx-fire、mcp-servers-for-revit），採 **MIT License** 授權，
允許商業使用、修改與散布，惟須保留原著作權與授權聲明（見 `LICENSE`）。

主要相依套件：Nice3point.Revit.*（MIT）、Newtonsoft.Json（MIT）、
Microsoft.CodeAnalysis.CSharp（MIT）、@modelcontextprotocol/sdk（MIT）、ws（MIT）、zod（MIT）、
better-sqlite3（MIT）。RevitMCPSDK 為 NuGet 相依，發佈前請確認其授權條款。

## 自行開發部分（不適用 MIT 授權）

下列檔案由開發者自行開發，**不屬於**上述 MIT 授權範圍。任何使用、複製、修改或散布都須先取得開發者書面授權，詳見 `src/Sinotech.Revit.Extensions/LICENSE.md`：

- `src/Sinotech.Revit.Extensions/`（編譯產物 `SinotechExtensions.dll`）
- `server/src/tools/extensions/`
