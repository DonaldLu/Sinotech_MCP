import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { textResult, type ToolResult } from "./types.js";

export const searchToolName = "sinotech_search_revit_tools";
const searchTool: Tool = {
  name: searchToolName,
  description: "搜尋並載入其他 Revit 工具。請用功能關鍵字（如 create walls、房間、相機）查詢；結果工具於下一次回應即可呼叫，不需執行程式碼。",
  inputSchema: { type: "object", properties: { query: { type: "string", description: "功能名稱或關鍵字" } }, required: ["query"], additionalProperties: false },
  annotations: { readOnlyHint: true }
};
// Sinotech exposes a small tool set (~26), so every tool is loaded up front.
// The search tool remains as a fallback if the set grows large.
const preloadLimit = 40;

// Local discovery keeps large, unrelated schemas out of every API request.
// Execution still goes through the same gateway and its write approvals.
export class ApiTools {
  private loaded = new Map<string, Tool>();
  constructor(private readonly all: Tool[]) {
    for (const tool of all.slice(0, preloadLimit)) this.loaded.set(tool.name, tool);
  }
  definitions(): Tool[] { return [...this.loaded.values(), searchTool]; }
  has(name: string): boolean { return this.loaded.has(name); }
  load(query: string): Tool[] {
    const normalized = query.toLowerCase();
    const terms = new Set(normalized.split(/[^a-z0-9\u3400-\u9fff]+/u).filter(term => term.length > 1));
    for (const [pattern, words] of [
      [/樓層|標高|level|storey/i, ["level"]], [/房間|room/i, ["room"]],
      [/牆|wall/i, ["wall"]], [/cad|dwg|圖層|layer/i, ["cad"]], [/門|door/i, ["door"]], [/相機|camera/i, ["camera"]],
      [/梁|樑|beam/i, ["beam", "cad"]], [/柱|column/i, ["column", "cad"]],
      [/建立|新增|create|add/i, ["create"]], [/刪除|delete/i, ["delete"]],
      [/查詢|讀取|query|list|get/i, ["query", "get"]], [/樓板|floor/i, ["floor"]]
    ] as const) if (pattern.test(normalized)) for (const word of words) terms.add(word);
    const ranked = this.all.map(tool => {
      const name = tool.name.toLowerCase();
      const description = (tool.description ?? "").toLowerCase();
      let score = name === normalized ? 10000 : 0;
      for (const term of terms) score += name.includes(term) ? 12 : description.includes(term) ? 2 : 0;
      return { tool, score };
    }).filter(match => match.score > 0).sort((a, b) => b.score - a.score || a.tool.name.localeCompare(b.tool.name)).slice(0, 8);
    for (const { tool } of ranked) this.loaded.set(tool.name, tool);
    return ranked.map(match => match.tool);
  }
  search(args: Record<string, unknown>): ToolResult {
    if (typeof args.query !== "string" || !args.query.trim() || args.query.length > 200) return textResult("query 必須是 1–200 字的功能關鍵字。", true);
    const tools = this.load(args.query);
    return textResult(JSON.stringify({ tools: tools.map(tool => ({ name: tool.name, description: tool.description })),
      note: tools.length ? "以上工具已載入，下一次回應可使用其完整參數定義。" : "找不到符合工具，請換用較短的中文或英文關鍵字。" }));
  }
}
