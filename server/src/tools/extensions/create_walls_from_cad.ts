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

// 自動建牆第二步：依所選 CAD 圖層的封閉雙線區域求中心線並建牆（會修改模型）
export function registerCreateWallsFromCadTool(server: McpServer) {
  server.registerTool(
    "create_walls_from_cad",
    {
      description:
        "【自動建牆】讀取目前視圖連結 CAD（DWG）中所選圖層的線條，重建封閉的雙線牆區域，求出牆中心線後，在基準樓層與頂部樓層之間建立基本牆。" +
        "只支援直線輪廓，圓弧會略過。呼叫前請先用 get_cad_layers 取得正確的圖層名稱、牆類型與樓層名稱，並與使用者確認要使用的圖層。" +
        "所有長度單位為公釐（mm）。執行結果會回報建立的牆數量與略過的線段統計，請如實轉告使用者。",
      inputSchema: {
        layers: z
          .array(z.string())
          .min(1)
          .describe("要建牆的 CAD 圖層名稱（取自 get_cad_layers）；建議一併包含門窗開口圖層，讓開口兩側的牆能相連"),
        wallTypeName: z
          .string()
          .optional()
          .describe("基本牆類型名稱；與 wallTypeId 擇一提供"),
        wallTypeId: z
          .number()
          .optional()
          .describe("基本牆類型 ElementId；與 wallTypeName 擇一提供"),
        baseLevel: z.string().describe("基準樓層名稱"),
        topLevel: z.string().describe("頂部樓層名稱（須高於基準樓層）"),
        byLevel: z
          .boolean()
          .default(false)
          .describe("是否在基準與頂部樓層之間的每個樓層把牆分段（預設 false）"),
        maxThickness: z
          .number()
          .default(1000)
          .describe("可辨識的最大牆厚（mm，預設 1000）"),
        importIds: z
          .array(z.number())
          .optional()
          .describe("只使用指定的連結 CAD（ImportInstance 的 ElementId）；省略則使用目前視圖中全部連結 CAD"),
      },
      annotations: { readOnlyHint: false, destructiveHint: false },
    },
    async (args) => {
      const params = {
        layers: args.layers,
        wallTypeName: args.wallTypeName,
        wallTypeId: args.wallTypeId,
        baseLevel: args.baseLevel,
        topLevel: args.topLevel,
        byLevel: args.byLevel ?? false,
        maxThickness: args.maxThickness ?? 1000,
        importIds: args.importIds ?? [],
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_walls_from_cad", params);
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
              text: `自動建牆失敗：${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
