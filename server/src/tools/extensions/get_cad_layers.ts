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

// 自動建牆第一步：列出連結 CAD 的圖層（含像不像牆的雙線統計）、基本牆類型與樓層（唯讀）
export function registerGetCadLayersTool(server: McpServer) {
  server.registerTool(
    "get_cad_layers",
    {
      description:
        "【自動建牆】列出目前 Revit 視圖中連結 CAD（DWG）的圖層，以及可用的基本牆類型（牆厚 mm）與樓層（高程 mm）。" +
        "每個圖層附有實際由平行雙線求得的牆統計：wallLength（牆中心線總長 m）、networkLength（連成牆網的牆長 m，真正的牆會互相連接）、" +
        "meanWallLength（平均每道牆長 m，樓梯踏步、填充線、設備多半很短）、typicalThicknesses（常見牆厚 mm）、pairedRatio、pairedLength；" +
        "recommended 為依上述統計判定的牆線圖層（已排除樓梯、填充線、開口符號框、柱、設備、電扶梯等），recommendedLayers 為判定結果，" +
        "defaultBaseLevel／defaultTopLevel 為預設樓層。只讀取目前視圖裁剪範圍內的圖形。不要請使用者選圖層。" +
        "圖層名稱常是無意義的編號（例如由 MicroStation 轉出的「圖層 27」），請以 recommendedLayers 為準，不要只看 pairedRatio 自行挑圖層；" +
        "除非使用者指定圖層，呼叫 create_walls_from_cad 時省略 layers（會自動判定並加入門窗開口圖層）。" +
        "使用者另外指定某圖層也要建牆時，layers 填 recommendedLayers 加上該圖層。columnShapes 為封閉小矩形或圓（可能是柱）的數量；beamLikeName 表示名稱像梁圖層（結構平面圖用 create_beams_from_cad）；柱用 create_columns_from_cad、板用 create_floors_from_cad；" +
        "floorTypes／defaultFloorType 為專案中的樓板類型。向使用者簡短說明你選了哪些圖層與理由。",
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
