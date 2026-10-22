import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import type { Tool } from "@modelcontextprotocol/sdk/types.js";
import { fileURLToPath } from "node:url";
import { randomUUID } from "node:crypto";
import { textResult, type EventSink, type ToolGateway, type ToolResult } from "./types.js";

export type Approve = (request: { id: string; name: string; arguments: Record<string, unknown>; preview: unknown }, signal: AbortSignal) => Promise<boolean>;

// The original Sinotech server tools carry no readOnlyHint annotations, so reads are listed explicitly.
// Tools that declare readOnlyHint (e.g. tools/extensions) are also reads.
// Anything else is treated as a write and needs user confirmation.
const readOnlyTools = new Set([
  "ai_element_filter", "analyze_model_statistics", "export_room_data", "get_available_family_types",
  "get_current_view_elements", "get_current_view_info", "get_material_quantities", "get_selected_elements",
  "query_stored_data", "say_hello"
]);

export class McpGateway implements ToolGateway {
  tools: Tool[] = [];
  private inFlight = new Set<Promise<ToolResult>>();
  private tail: Promise<unknown> = Promise.resolve();
  constructor(private readonly emit: EventSink, private readonly approve: Approve,
    private readonly client: Pick<Client, "connect" | "listTools" | "callTool" | "close"> = new Client({ name: "sinotech-revit-chat", version: "1.0.0" })) { }
  async connect(): Promise<void> {
    // Spawn the existing Sinotech MCP server (build/index.js, one level above build/chat).
    await this.client.connect(new StdioClientTransport({ command: process.execPath, args: [fileURLToPath(new URL("../index.js", import.meta.url))], env: process.env as Record<string, string>, stderr: "pipe" }));
    this.tools = (await this.client.listTools()).tools;
  }
  call(name: string, args: Record<string, unknown>, signal: AbortSignal): Promise<ToolResult> {
    // Serialize across providers/tool batches so a cancelled turn cannot leave concurrent writes.
    const work = this.tail.then(() => this.perform(name, args, signal));
    this.tail = work.catch(() => {});
    this.inFlight.add(work);
    void work.finally(() => this.inFlight.delete(work)).catch(() => {});
    return work;
  }
  private async perform(name: string, args: Record<string, unknown>, signal: AbortSignal): Promise<ToolResult> {
    signal.throwIfAborted();
    const tool = this.tools.find(t => t.name === name);
    if (!tool) return textResult("UNKNOWN_TOOL", true);
    if (!readOnlyTools.has(name) && tool.annotations?.readOnlyHint !== true) {
      const preview = { title: "Revit 模型修改", tool: name, arguments: args, note: "以下是本次執行參數，請核對元素 ID、尺寸、範圍或完整程式碼後確認。" };
      if (!await this.approve({ id: randomUUID(), name, arguments: args, preview }, signal)) return textResult("使用者已取消此次操作，停止後續寫入。", true);
    }
    signal.throwIfAborted();
    this.emit({ event: "toolStarted", name, arguments: args });
    // Once dispatched, wait for the real result even when the AI request is cancelled;
    // abandoning the call could hide a committed Revit transaction.
    let result: ToolResult;
    try {
      const response = await this.client.callTool({ name, arguments: args }, undefined, { timeout: 65000 });
      result = { isError: !!response.isError, content: (response.content as any[]).filter(c => c.type === "text" || c.type === "image").map(c => c.type === "text" ? { type: "text", text: c.text } : { type: "image", data: c.data, mimeType: c.mimeType }) };
    } catch (error) { result = textResult(error instanceof Error ? error.message : String(error), true); }
    let data: unknown;
    try { data = JSON.parse(result.content.find(c => c.type === "text")?.text ?? ""); } catch { /* plain text result */ }
    this.emit({ event: "toolCompleted", name, result, data });
    return result;
  }
  async idle(): Promise<void> { await Promise.allSettled([...this.inFlight]); }
  async close(): Promise<void> { await this.idle(); await this.client.close(); }
}
