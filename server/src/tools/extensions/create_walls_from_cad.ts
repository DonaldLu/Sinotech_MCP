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

// 自動建牆第二步：依 CAD 圖層（可自動判定）的封閉雙線區域求中心線並建牆，並依 CAD 放置門窗（會修改模型）
export function registerCreateWallsFromCadTool(server: McpServer) {
  server.registerTool(
    "create_walls_from_cad",
    {
      description:
        "【自動建牆】讀取目前視圖連結 CAD（DWG）的線條，重建封閉的雙線牆區域，求出牆中心線後，在基準樓層與頂部樓層之間建立基本牆。" +
        "只支援直線輪廓，圓弧會略過。使用者只要說「用連結的 CAD 建牆」即可：先呼叫 get_cad_layers，由你自行判定圖層，不需詢問使用者；" +
        "layers、牆類型、樓層都可省略——省略 layers 時自動判定牆線圖層並加入全部開口圖層；省略牆類型時每道牆依 CAD 牆厚選用最接近的基本牆類型；" +
        "省略樓層時使用目前平面視圖的樓層與其上一層。只有使用者明確指定時才填牆類型或樓層。執行前面板會顯示參數供使用者確認。" +
        "預設會同時放置門窗（placeOpenings）：依牆的缺口與 CAD 圖形判斷——有開門弧線為門（兩個半寬弧線為雙開門）、牆厚內有平行線為窗、" +
        "牆面外有門片線為推拉門；使用專案中寬度最接近的門窗類型，必要時複製等寬類型，並依 CAD 調整開門方向。無法判斷的缺口只回報不放置。" +
        "預設也會放置結構柱（placeColumns：封閉小矩形或圓、尺寸重複且貼著牆的圖層，依尺寸選用或新建 b×h 類型），" +
        "並建立樓板（placeFloors：每個房間一塊、邊界在牆與柱的內緣，使用專案預設樓板類型；電梯井、樓梯間不建板）。" +
        "所有長度單位為公釐（mm）。執行結果會回報使用的圖層、各牆類型數量、厚度不符的牆數、門窗數量與類型、無法判斷的缺口與略過的線段統計，請如實轉告使用者。",
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
        placeOpenings: z
          .boolean()
          .default(true)
          .describe("建牆後是否依 CAD 判斷並放置門窗（預設 true）；使用者只要牆時設為 false"),
        windowSillHeight: z
          .number()
          .optional()
          .describe("窗台高度（mm）；省略則使用窗類型的預設值"),
        placeColumns: z.boolean().default(true).describe("是否依 CAD 放置結構柱（預設 true）"),
        columnLayers: z
          .array(z.string())
          .optional()
          .describe("柱的 CAD 圖層（get_cad_layers 中 columnShapes 多的圖層）；省略則自動判定"),
        placeFloors: z.boolean().default(true).describe("是否每個房間建一塊樓板（預設 true）"),
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
        placeOpenings: args.placeOpenings ?? true,
        windowSillHeight: args.windowSillHeight,
        placeColumns: args.placeColumns ?? true,
        columnLayers: args.columnLayers ?? [],
        placeFloors: args.placeFloors ?? true,
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
