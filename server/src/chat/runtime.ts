import { mkdir } from "node:fs/promises";
import { ApiProvider } from "./api-providers.js";
import { CodexProvider } from "./codex-provider.js";
import { McpGateway, type Approve } from "./gateway.js";
import type { ChatProvider, EventSink, ModelOption, ProviderSettings } from "./types.js";

export class ChatRuntime {
  private gateway: McpGateway;
  private provider?: ChatProvider;
  private settings?: ProviderSettings;
  private turn?: AbortController;
  private active?: Promise<void>;
  private approvals = new Map<string, (accepted: boolean) => void>();
  constructor(private readonly stateDirectory: string, private readonly emit: EventSink) {
    const approve: Approve = (request, signal) => new Promise(resolve => {
      let timer: ReturnType<typeof setTimeout>;
      const finish = (accepted: boolean) => { clearTimeout(timer); this.approvals.delete(request.id); signal.removeEventListener("abort", cancel); this.emit({ event: "approvalClosed", id: request.id }); resolve(accepted); };
      const cancel = () => finish(false);
      timer = setTimeout(cancel, 300000);
      this.approvals.set(request.id, finish);
      signal.addEventListener("abort", cancel, { once: true });
      this.emit({ event: "approval", ...request });
      if (signal.aborted) cancel();
    });
    this.gateway = new McpGateway(emit, approve);
  }
  async start(): Promise<void> { await mkdir(this.stateDirectory, { recursive: true }); await this.gateway.connect(); }
  async configure(settings: ProviderSettings): Promise<unknown> {
    if (this.active) throw new Error("請先停止並等待目前回合完成，再切換引擎或模型。");
    if (!["codex", "claude", "gemini"].includes(settings.provider)) throw new Error("Unknown AI provider");
    await this.provider?.close();
    this.settings = settings;
    if (settings.provider === "codex") {
      const provider = new CodexProvider(settings, this.gateway, this.emit, this.stateDirectory);
      this.provider = provider;
      await provider.start();
    } else this.provider = new ApiProvider(settings, this.gateway, this.emit);
    const status = await this.provider.status();
    let models: ModelOption[] = [];
    if ((this.provider instanceof CodexProvider && this.provider.authenticated) || (settings.provider !== "codex" && settings.apiKey)) models = await this.provider.models();
    if (settings.provider !== "codex" && settings.apiKey) {
      if (!models.length) throw new Error("此 API Key 的清單未提供建議的文字／工具模型；請確認專案權限。取得清單不代表生成配額足夠。");
      const requested = settings.model?.replace(/^models\//, "");
      settings.model = models.some(model => model.id === requested) ? requested : models[0].id;
    }
    return { status, models, selectedModel: settings.model };
  }
  async handle(method: string, params: any = {}): Promise<unknown> {
    if (method === "configure") return this.configure(params);
    if (method === "cancel") { this.turn?.abort(); return { status: "stopping" }; }
    if (method === "approve") { this.approvals.get(params.id)?.(params.accepted === true); return {}; }
    if (!this.provider) throw new Error("請先按連線／取得模型。");
    if (method === "login") { if (this.active) throw new Error("回合執行中"); await this.provider.login(); return {}; }
    if (method === "logout") { if (this.active) throw new Error("回合執行中"); await this.provider.logout(); return { status: await this.provider.status() }; }
    if (method === "reset") { if (!this.settings) throw new Error("No provider"); return this.configure(this.settings); }
    if (method === "send") {
      if (this.active) throw new Error("已有回合執行中。");
      if (typeof params.text !== "string" || !params.text.trim() || params.text.length > 20000) throw new Error("訊息長度須為 1–20000 字。");
      this.turn = new AbortController();
      const signal = this.turn.signal;
      this.active = this.provider.send(params.text, signal);
      try { await this.active; return { status: signal.aborted ? "cancelled" : "completed" }; }
      finally { await this.gateway.idle(); this.active = undefined; this.turn = undefined; this.emit({ event: "turnCompleted", cancelled: signal.aborted }); }
    }
    throw new Error("Unknown chat method: " + method);
  }
  async close(): Promise<void> {
    this.turn?.abort();
    for (const finish of [...this.approvals.values()]) finish(false);
    await this.active?.catch(() => {});
    await this.provider?.close(); await this.gateway.close();
  }
}
