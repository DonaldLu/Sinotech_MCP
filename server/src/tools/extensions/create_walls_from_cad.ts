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

// 自動建牆：依 CAD 圖層（可自動判定）的封閉雙線區域求中心線並建牆；門、窗、柱、板須明確要求（會修改模型）
export function registerCreateWallsFromCadTool(server: McpServer) {
  server.registerTool(
    "create_walls_from_cad",
    {
      description:
        "【自動建牆】讀取目前視圖連結 CAD（DWG）的線條，重建封閉的雙線牆區域，求出牆中心線後，在基準樓層與頂部樓層之間建立基本牆。" +
        "只支援直線輪廓，圓弧會略過。使用者只要說「用連結的 CAD 建牆」即可：先呼叫 get_cad_layers，由你自行判定圖層，不需詢問使用者；" +
        "layers、牆類型、樓層都可省略——省略 layers 時自動判定牆線圖層並加入全部開口圖層；省略牆類型時每道牆依 CAD 牆厚選用最接近的基本牆類型；" +
        "省略樓層時使用目前平面視圖的樓層與其上一層。只有使用者明確指定時才填牆類型或樓層。只處理視圖裁剪範圍內的圖形。" +
        "預設只建牆：placeDoors、placeWindows、placeColumns、placeFloors 皆為 false，CAD 有門窗圖層不代表要放門窗。" +
        "使用者明確要求門才設 placeDoors（有開門弧線為門、兩個半寬弧線為雙開門、牆面外有門片線為推拉門，並依 CAD 調整開門方向）；" +
        "明確要求窗才設 placeWindows（牆厚內有平行線為窗）；只要求其中一種時不可順便放另一種。門窗使用寬度最接近的類型，必要時複製等寬類型，無法判斷的缺口只回報。" +
        "明確要求一起建柱、板時才設 placeColumns、placeFloors（單獨建柱、建板請用 create_columns_from_cad、create_floors_from_cad）。" +
        "直接執行，不要詢問使用者。" +
        "所有長度單位為公釐（mm）。執行結果會回報使用的圖層、各牆類型數量、厚度不符的牆數、（有要求時）門窗柱板數量與略過的線段統計，請如實轉告使用者。",
      inputSchema: {
        layers: z
          .array(z.string())
          .optional()
          .describe("要建牆的 CAD 圖層名稱（取自 get_cad_layers），應包含門窗開口圖層；省略則自動判定牆線圖層並加入全部開口圖層"),
        wallTypeName: z
          .string()
          .optional()
          .describe("基本牆類型名稱，全部牆使用此類型；與 wallTypeId 擇一。皆省略時每道牆依 CAD 牆厚自動選類型"),
        wallTypeId: z
          .number()
          .optional()
          .describe("基本牆類型 ElementId；與 wallTypeName 擇一"),
        baseLevel: z.string().optional().describe("基準樓層名稱；省略則使用目前平面視圖的樓層"),
        topLevel: z.string().optional().describe("頂部樓層名稱（須高於基準樓層）；省略則使用基準樓層的上一層"),
        byLevel: z
          .boolean()
          .default(false)
          .describe("是否在基準與頂部樓層之間的每個樓層把牆分段（預設 false）"),
        maxThickness: z
          .number()
          .default(1000)
          .describe("可辨識的最大牆厚（mm，預設 1000）"),
        placeDoors: z.boolean().default(false).describe("使用者明確要求放門時才設 true（預設 false）"),
        placeWindows: z.boolean().default(false).describe("使用者明確要求放窗時才設 true（預設 false）"),
        placeOpenings: z
          .boolean()
          .default(false)
          .describe("舊版開關：同時放門與窗（預設 false）；請改用 placeDoors、placeWindows"),
        windowSillHeight: z
          .number()
          .optional()
          .describe("窗台高度（mm）；省略則使用窗類型的預設值"),
        placeColumns: z.boolean().default(false).describe("使用者明確要求一起建柱時才設 true（預設 false）"),
        columnLayers: z
          .array(z.string())
          .optional()
          .describe("柱的 CAD 圖層（get_cad_layers 中 columnShapes 多的圖層）；省略則自動判定"),
        placeFloors: z.boolean().default(false).describe("使用者明確要求一起建板時才設 true（預設 false）；每個房間一塊、邊界在牆柱內緣"),
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
        layers: args.layers ?? [],
        wallTypeName: args.wallTypeName,
        wallTypeId: args.wallTypeId,
        baseLevel: args.baseLevel,
        topLevel: args.topLevel,
        byLevel: args.byLevel ?? false,
        maxThickness: args.maxThickness ?? 1000,
        importIds: args.importIds ?? [],
        placeDoors: args.placeDoors ?? false,
        placeWindows: args.placeWindows ?? false,
        placeOpenings: args.placeOpenings ?? false,
        windowSillHeight: args.windowSillHeight,
        placeColumns: args.placeColumns ?? false,
        columnLayers: args.columnLayers ?? [],
        placeFloors: args.placeFloors ?? false,
        floorTypeName: args.floorTypeName,
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
