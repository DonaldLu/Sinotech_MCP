// ============================================================================
//  Sinotech_MCP 自行開發命令集（SinotechExtensions）
//  開發者：Donald（donaldlu@mail.sinotech.com.tw）
//  Copyright © 2026 開發者保留所有權利。
//  本檔案不適用專案根目錄之 MIT 授權；未經開發者書面授權，
//  不得使用、複製、修改或散布。詳見 src/Sinotech.Revit.Extensions/LICENSE.md
// ============================================================================
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../../utils/ConnectionManager.js";

// 自動建牆第一步：列出連結 CAD 的圖層、基本牆類型與樓層（唯讀）
export function registerGetCadLayersTool(server: McpServer) {
  server.registerTool(
    "get_cad_layers",
    {
      description:
        "【自動建牆】列出目前 Revit 視圖中連結 CAD（DWG）的圖層，以及可用的基本牆類型（牆厚 mm）與樓層（高程 mm）。" +
        "使用者要依 CAD 自動建牆時先呼叫此工具，把圖層列給使用者選擇，並確認牆類型、基準樓層與頂部樓層後，再呼叫 create_walls_from_cad。" +
        "isOpeningLayer 為 true 的圖層（名稱含 OPEN／DOOR／WINDOW／開口／門／窗）是門窗開口圖層，建議一併選入，讓開口兩側的牆能相連。",
      inputSchema: {
        importIds: z
          .array(z.number())
          .optional()
          .describe("只讀取指定的連結 CAD（ImportInstance 的 ElementId）；省略則讀取目前視圖中全部連結 CAD"),
      },
      annotations: { readOnlyHint: true },
    },
    async (args) => {
      const params = {
        importIds: args.importIds ?? [],
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_cad_layers", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response, null, 2),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `讀取 CAD 圖層失敗：${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
