# Sinotech_MCP MSI（1.0.0）

流程比照 TYBIM_MCP_Setup：Revit 2024、2025、2026 各自編譯，產生獨立的 x64 目前使用者 MSI，具有不同 ProductCode／UpgradeCode，可以並存。

## 產生安裝檔

```powershell
cd C:\Prj\Revit\Sinotech_MCP
.\Sinotech_MCP_Setup\Build-Msi.ps1                 # 編譯 + 產生三版 MSI
.\Sinotech_MCP_Setup\Build-Msi.ps1 -RevitVersion 2025 -SkipBuild   # 只重新打包
```

需要 Visual Studio 2022 與 Microsoft Visual Studio Installer Projects 擴充、Node.js 20 以上（x64）。輸出在 `Release/Sinotech_MCP_Revit<年份>_<版本>.msi`，版本取自 `server/package.json`。

| 腳本 | 用途 |
|---|---|
| `Build-Msi.ps1` | 編譯 Add-in／Commands／server，依序呼叫下列腳本與 devenv |
| `Prepare-Payload.ps1` | 組成 `payloads/<年份>`：DLL、命令集、`runtime`（server build + 正式相依 + node.exe）、`chat.config.json` |
| `Generate-Setup.mjs` | 由 `Setup.Template.vdproj` 產生每個檔案明確列出的 `generated/<年份>` 安裝專案與 `.addin` |
| `Set-MsiRegistration.ps1` | 設定目前使用者安裝、可在 Windows「已安裝的應用程式」解除安裝 |
| `Verify-Msi.ps1` | 唯讀檢查 MSI 屬性、路徑、內嵌 CAB 全部檔案 SHA-256 |

`payloads/`、`generated/`、`registration/`、`Release/` 是產生物，不進版本控制。

## 安裝與路徑

請先儲存模型並關閉 Revit，再以實際使用 Revit 的 Windows 帳號執行對應年份 MSI。以 2025 為例：

```text
%APPDATA%\Autodesk\Revit\Addins\
  2025\
    Sinotech_MCP.addin            ← Assembly 指向 ..\Sinotech_MCP\2025\RevitMCPPlugin.dll
  Sinotech_MCP\
    2025\
      RevitMCPPlugin.dll、RevitMCPSDK.dll、Newtonsoft.Json.dll …
      Commands\RevitMCPCommandSet\command.json
      Commands\RevitMCPCommandSet\2025\RevitMCPCommandSet.dll …
      chat.config.json
      runtime\build\…、runtime\node_modules\…、runtime\node\node.exe
```

安裝後在 Windows「設定 → 應用程式」搜尋 `Sinotech_MCP` 可解除安裝。**完整解除安裝**時會一併刪除該年份的整個 `Sinotech_MCP\<年份>` 資料夾（包含執行時產生的 `Logs`、`runtime\revit-data.db`）；空的上層資料夾也會移除，其他年份與其他外掛不受影響。**升級**（安裝較新版本）時不會執行這項清理。請先關閉 Revit 再解除安裝，執行中的檔案無法刪除。

### AI助手登入資料

登入資料存在 `%LOCALAPPDATA%\Sinotech\McpChat\<年份>`，只屬於目前的 Windows 帳號：

- `codex\auth.json`：Codex（ChatGPT 帳號）登入權杖，由 Codex 管理。
- `ui.settings.json`：引擎、模型選擇與 Claude／Gemini API Key（以 Windows DPAPI 加密，只有同一個 Windows 帳號能解密）。

自 1.1.6 起，**解除安裝不會刪除登入資料**，重新安裝或升級後不需再登入；要清除請在 AI助手面板按「登出／清除金鑰」。
（1.1.5 以前的安裝檔在完整解除安裝時會刪除登入資料；要保留登入，請直接安裝新版覆蓋升級，不要先解除安裝。）

這個資料夾不在打包來源內。`Verify-Msi.ps1` 會擋下 `auth.json`、`ui.settings.json` 等檔名，並把 MSI 內每個檔案的實際內容與打包者本機的登入權杖、金鑰逐一比對，只要出現就讓建置失敗（只回報檔名，不輸出秘密）。

## 注意

- 開發時用 `Debug R2x` 組態建置，或執行 `scripts/build.ps1 -Install`，會把 `Sinotech_MCP.addin` 與 `revit_mcp_plugin\` 複製到 `Addins\<年份>`。這個 `.addin` 與 MSI 安裝的檔案同名、ClientId 也相同，**同一年份不要兩種方式混用**；改用 MSI 前，請先刪除開發版的 `Addins\<年份>\Sinotech_MCP.addin` 與 `revit_mcp_plugin\`。
- 功能區只有「AI助手」一個按鈕。Revit 啟動時會載入所有已安裝命令集的全部命令（新版本增加的命令也會自動載入，沒有停用選項）；MCP 服務只在 AI助手面板開啟時執行，關閉面板即停止，且只接受本機（127.0.0.1:8080）連線。
- 已通過 MSI 內容驗證，但尚未在實際 Revit 中驗證安裝結果；每個年份都需要開啟 Revit 確認功能區與 AI助手 面板。
