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
        "每個圖層附有雙線統計：pairedRatio（有平行雙線夥伴的線段比例）、pairedLength（m）、typicalThicknesses（常見雙線間距 mm，即可能的牆厚）、" +
        "recommended（依雙線特徵與名稱判定為牆線圖層）；recommendedLayers 為判定結果，defaultBaseLevel／defaultTopLevel 為預設樓層。" +
        "不要請使用者選圖層：由你依統計與圖層名稱自行判定牆線圖層（排除柱、樓梯、家具、標註等即使有雙線也不是牆的圖層），" +
        "isOpeningLayer 為 true 的門窗開口圖層一併選入，讓開口兩側的牆能相連。判定結果與 recommendedLayers 相同時，" +
        "呼叫 create_walls_from_cad 可直接省略 layers。columnShapes 為封閉小矩形或圓（可能是柱）的數量；beamLikeName 表示名稱像梁圖層（結構平面圖用 create_beams_from_cad）；" +
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
