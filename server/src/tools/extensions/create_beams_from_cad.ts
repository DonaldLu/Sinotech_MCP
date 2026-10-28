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

// 自動建梁：依結構平面圖的梁雙線與標註建立梁，只建梁，可要求一併建柱（會修改模型）
export function registerCreateBeamsFromCadTool(server: McpServer) {
  server.registerTool(
    "create_beams_from_cad",
    {
      description:
        "【自動建梁】讀取目前視圖連結的結構平面圖（DWG），由梁的平行雙線求梁中心線與梁寬，並直接讀取 DWG 的梁標註文字取得梁深" +
        "（RC 梁「B23(50X70)」為寬×深 cm；鋼梁「H500X200」為深×翼寬 mm），梁頂齊指定樓層；梁端會延伸到相交的梁或柱心。" +
        "沒有標註的梁沿用同一直線上的標註，仍無則梁深 = 梁寬 × defaultDepthRatio。鋼梁需專案中已有相同斷面的結構構架類型，否則略過並回報。" +
        "預設只建梁；使用者明確要求一起建柱時才設 placeColumns（下一層到此樓層）。一張 DWG 有多個樓層平面時，只處理目前視圖「裁剪範圍」內的圖形，" +
        "結果中若處理到多層平面，請提醒使用者把視圖裁剪到要建的那一層。先呼叫 get_cad_layers：beamLikeName 為 true 且有雙線（pairedRatio）的圖層是梁圖層，" +
        "由你判定即可，與自動判定相同時可省略 layers。直接執行，不要詢問使用者；執行結果請如實轉告使用者。",
      inputSchema: {
        layers: z
          .array(z.string())
          .optional()
          .describe("梁的 CAD 圖層（取自 get_cad_layers）；省略則依圖層名稱（B、J、SB、SJ、BEAM、梁…）與雙線特徵自動判定"),
        level: z.string().optional().describe("梁頂所在樓層名稱；省略則使用目前平面視圖的樓層"),
        defaultDepthRatio: z
          .number()
          .default(1.4)
          .describe("沒有標註的梁：梁深 = 梁寬 × 此倍數（預設 1.4，四捨五入到 5 cm）"),
        placeColumns: z.boolean().default(false).describe("使用者明確要求一起建柱時才設 true（預設 false）"),
        columnLayers: z.array(z.string()).optional().describe("柱的 CAD 圖層；省略則自動判定"),
        importIds: z
          .array(z.number())
          .optional()
          .describe("只使用指定的連結 CAD（ImportInstance 的 ElementId）；省略則使用目前視圖中全部連結 CAD"),
      },
      annotations: { readOnlyHint: false, destructiveHint: false },
    },
    async (args) => {
      const params = {
        layers: args.layers ?? [],
        level: args.level,
        defaultDepthRatio: args.defaultDepthRatio ?? 1.4,
        placeColumns: args.placeColumns ?? false,
        columnLayers: args.columnLayers ?? [],
        importIds: args.importIds ?? [],
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_beams_from_cad", params);
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
              text: `自動建梁失敗：${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
