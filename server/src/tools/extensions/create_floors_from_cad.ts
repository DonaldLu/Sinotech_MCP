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

// 自動建板：依已有的牆柱圍出的房間，每個房間建一塊樓板，只建板（會修改模型）
export function registerCreateFloorsFromCadTool(server: McpServer) {
  server.registerTool(
    "create_floors_from_cad",
    {
      description:
        "【自動建板】只建板：依模型中已有的牆、柱在指定樓層圍出的房間，每個房間建一塊樓板，邊界在牆與柱的內緣，使用專案預設樓板類型（或 floorTypeName）。" +
        "必須先建好牆、柱；專案原有的房間保留並一併建板。連結 CAD 只用來辨識電梯井（叉線）與樓梯（踏步），這些房間不建板。" +
        "視圖有裁剪範圍時，只處理完整位於範圍內的房間。單獨建板不會刪除既有樓板，重複執行會重複建立。" +
        "直接執行，不要詢問使用者；執行結果請如實轉告使用者。",
      inputSchema: {
        level: z.string().optional().describe("樓板所在樓層名稱；省略則使用目前平面視圖的樓層"),
        floorTypeName: z.string().optional().describe("樓板類型名稱（取自 get_cad_layers 的 floorTypes）；省略則使用專案預設樓板類型"),
        importIds: z
          .array(z.number())
          .optional()
          .describe("只使用指定的連結 CAD（ImportInstance 的 ElementId）；省略則使用目前視圖中全部連結 CAD"),
      },
      annotations: { readOnlyHint: false, destructiveHint: false },
    },
    async (args) => {
      const params = {
        level: args.level,
        floorTypeName: args.floorTypeName,
        importIds: args.importIds ?? [],
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_floors_from_cad", params);
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
              text: `自動建板失敗：${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
