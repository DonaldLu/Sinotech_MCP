import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createInterface } from "node:readline";

export class RpcProcess {
  private child: ChildProcessWithoutNullStreams;
  private sequence = 0;
  private pending = new Map<number, { resolve: (v: any) => void; reject: (e: Error) => void; timer: ReturnType<typeof setTimeout> }>();
  onNotification: (method: string, params: any) => void = () => {};
  onRequest: (method: string, params: any) => Promise<unknown> = async () => { throw new Error("Unsupported server request"); };
  onExit: (error: Error) => void = () => {};
  constructor(executable: string, args: string[], env: NodeJS.ProcessEnv, cwd: string) {
    this.child = spawn(executable, args, { env, cwd, windowsHide: true, stdio: ["pipe", "pipe", "pipe"] });
    const reader = createInterface({ input: this.child.stdout, crlfDelay: Infinity });
    reader.on("line", line => { void this.receive(line); });
    // Native stderr can include private paths; keep it off UI/stdout.
    this.child.stderr.resume();
    this.child.on("error", error => this.fail(error));
    this.child.on("exit", () => this.fail(new Error("Codex 背景程序已結束，請重新連線。")));
    this.child.stdin.on("error", error => this.fail(error));
  }
  private fail(error: Error) {
    for (const entry of this.pending.values()) { clearTimeout(entry.timer); entry.reject(error); }
    this.pending.clear(); this.onExit(error);
  }
  private async receive(line: string) {
    let message: any;
    try { message = JSON.parse(line); } catch { return; }
    if (message.method && message.id !== undefined) {
      try { this.write({ id: message.id, result: await this.onRequest(message.method, message.params ?? {}) }); }
      catch (error) { this.write({ id: message.id, error: { code: -32601, message: error instanceof Error ? error.message : String(error) } }); }
    } else if (message.method) this.onNotification(message.method, message.params ?? {});
    else if (this.pending.has(message.id)) {
      const entry = this.pending.get(message.id)!; this.pending.delete(message.id); clearTimeout(entry.timer);
      if (message.error) entry.reject(new Error(message.error.message ?? "Codex RPC error")); else entry.resolve(message.result);
    }
  }
  private write(value: unknown) { if (!this.child.stdin.destroyed) this.child.stdin.write(JSON.stringify(value) + "\n"); }
  notify(method: string, params: unknown = {}) { this.write({ method, params }); }
  request(method: string, params: unknown = {}, timeoutMs = 45000): Promise<any> {
    return new Promise((resolve, reject) => {
      const id = ++this.sequence;
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error("Codex RPC timeout: " + method)); }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer }); this.write({ id, method, params });
    });
  }
  async close(): Promise<void> {
    this.fail(new Error("Connection closed"));
    if (!this.child.pid || this.child.exitCode !== null || this.child.signalCode !== null) return;
    await new Promise<void>(resolve => {
      const timer = setTimeout(resolve, 4000);
      this.child.once("exit", () => { clearTimeout(timer); resolve(); });
      this.child.stdin.end(); this.child.kill();
    });
  }
}
