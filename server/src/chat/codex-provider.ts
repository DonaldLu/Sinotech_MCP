import { mkdir, writeFile, access } from "node:fs/promises";
import { join } from "node:path";
import { RpcProcess } from "./rpc-process.js";
import { instructions, textResult, type ChatProvider, type EventSink, type ModelOption, type ProviderSettings, type ToolGateway } from "./types.js";

export class CodexProvider implements ChatProvider {
  authenticated = false;
  private rpc!: RpcProcess;
  private threadId?: string;
  private turnId?: string;
  private signal?: AbortSignal;
  private finish?: (error?: Error) => void;
  private streamedItems = new Set<string>();
  constructor(private readonly settings: ProviderSettings, private readonly gateway: ToolGateway, private readonly emit: EventSink, private readonly stateDirectory: string) { }
  async start(): Promise<void> {
    const home = join(this.stateDirectory, "codex");
    const workspace = join(this.stateDirectory, "workspace");
    await mkdir(home, { recursive: true }); await mkdir(workspace, { recursive: true });
    const config = join(home, "config.toml");
    try { await access(config); } catch { await writeFile(config, '# Dedicated Sinotech Codex profile; no desktop credentials are copied.\n'); }
    const env = { ...process.env, CODEX_HOME: home } as NodeJS.ProcessEnv;
    // This adapter explicitly offers ChatGPT login, not inherited API billing.
    for (const name of ["OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_ACCESS_TOKEN"]) delete env[name];
    this.rpc = new RpcProcess(this.settings.codexExecutable || "codex", ["app-server", "--listen", "stdio://",
      // Dynamic Revit tools also run through Code Mode; disabling its host leaves
      // text generation working but makes every tool call fail at runtime.
      "-c", 'forced_login_method="chatgpt"', "-c", "features.shell_tool=false", "-c", "features.unified_exec=false", "-c", "features.code_mode_host=true", "-c", "features.daemon_auto_start=false", "-c", 'web_search="disabled"', "-c", "mcp_servers={}"], env, workspace);
    this.rpc.onRequest = async (method, params) => {
      if (method === "item/tool/call") {
        if (params.threadId !== this.threadId || !this.signal) throw new Error("Unexpected tool call outside current conversation");
        let result;
        try { result = await this.gateway.call(params.tool, params.arguments ?? {}, this.signal); }
        catch (error) { result = textResult(error instanceof Error ? error.message : String(error), true); }
        return { contentItems: result.content.map(c => c.type === 'text' ? ({ type: "inputText", text: c.text }) : ({ type: 'inputImage', imageUrl: `data:${c.mimeType};base64,${c.data}` })), success: !result.isError };
      }
      if (method === "item/commandExecution/requestApproval" || method === "item/fileChange/requestApproval") return { decision: "decline" };
      if (method === "item/permissions/requestApproval") return { permissions: {}, scope: "turn" };
      if (method === "mcpServer/elicitation/request") return { action: "decline", content: null };
      if (method === "item/tool/requestUserInput" || method === "tool/requestUserInput") return { answers: {} };
      throw new Error("此面板僅開放已註冊的 Revit 工具，請直接向使用者提問。");
    };
    this.rpc.onNotification = (method, p) => {
      if (method === "account/login/completed") {
        this.emit({ event: "loginCompleted", success: p.success === true });
        this.emit({ event: "status", text: p.success ? "Codex 登入完成，請按重新連線／取得模型。" : "Codex 登入失敗：" + (p.error ?? "未知錯誤") });
      }
      if (p.threadId && p.threadId !== this.threadId) return;
      if (method === "turn/started") this.turnId = p.turn?.id;
      if (method === "item/agentMessage/delta") { this.streamedItems.add(p.itemId); this.emit({ event: "textDelta", text: p.delta }); }
      if (method === "item/completed" && p.item?.type === "agentMessage" && !this.streamedItems.has(p.item.id)) this.emit({ event: "textDelta", text: p.item.text ?? "" });
      if (method === "turn/completed") {
        const turn = p.turn;
        this.finish?.(turn.status === "completed" ? undefined : new Error(turn.error?.message ?? (turn.status === "interrupted" ? "已停止" : "Codex 回合失敗")));
      }
    };
    this.rpc.onExit = error => this.finish?.(error);
    await this.rpc.request("initialize", { clientInfo: { name: "sinotech_revit", title: "Sinotech Revit AI", version: "1.0.0" }, capabilities: { experimentalApi: true } });
    this.rpc.notify("initialized");
  }
  async status(): Promise<string> {
    const { account } = await this.rpc.request("account/read", { refreshToken: false });
    this.authenticated = !!account;
    return account ? `Codex 已登入（${account.type}${account.planType ? " / " + account.planType : ""}）` : "Codex 尚未登入，請按登入 Codex。";
  }
  async login(): Promise<void> {
    const result = await this.rpc.request("account/login/start", { type: "chatgpt" });
    if (result.authUrl) this.emit({ event: "loginUrl", url: result.authUrl });
  }
  async logout(): Promise<void> { await this.rpc.request("account/logout"); this.threadId = undefined; this.authenticated = false; }
  async models(): Promise<ModelOption[]> {
    const result = await this.rpc.request("model/list", { includeHidden: false, limit: 100 });
    return (result.data ?? []).map((m: any) => ({ id: m.model ?? m.id, name: m.displayName ?? m.model ?? m.id }));
  }
  async send(text: string, signal: AbortSignal): Promise<void> {
    if (!this.settings.model) throw new Error("請先選擇模型。");
    if (!this.threadId) {
      const result = await this.rpc.request("thread/start", { model: this.settings.model, sandbox: "read-only", approvalPolicy: "on-request", developerInstructions: instructions,
        dynamicTools: this.gateway.tools.map(t => ({ type: "function", name: t.name, description: t.description ?? t.name, inputSchema: t.inputSchema })) });
      this.threadId = result.thread.id;
    }
    signal.throwIfAborted();
    this.signal = signal;
    this.streamedItems.clear();
    const abort = () => { if (this.turnId) void this.rpc.request("turn/interrupt", { threadId: this.threadId, turnId: this.turnId }).catch(() => {}); };
    signal.addEventListener("abort", abort, { once: true });
    let timer: ReturnType<typeof setTimeout>;
    const completed = new Promise<void>((resolve, reject) => {
      this.finish = error => error ? reject(error) : resolve();
      timer = setTimeout(() => reject(new Error("Codex 回合超過 10 分鐘，請停止後重新連線。")), 600000);
    });
    // Attach a handler immediately: a turn can finish before turn/start returns.
    void completed.catch(() => {});
    try {
      const started = await this.rpc.request("turn/start", { threadId: this.threadId, input: [{ type: "text", text, text_elements: [] }] });
      this.turnId = started.turn.id;
      if (signal.aborted) abort();
      await completed;
    } finally {
      clearTimeout(timer!); signal.removeEventListener("abort", abort); this.finish = undefined; this.signal = undefined; this.turnId = undefined;
    }
  }
  async close(): Promise<void> { await this.rpc?.close(); this.threadId = undefined; }
}




