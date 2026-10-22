import { createInterface } from "node:readline";
import { ChatRuntime } from "./runtime.js";

const keys = new Set<string>();
function output(value: unknown) {
  let line = JSON.stringify(value);
  for (const key of keys) if (key.length > 5) line = line.split(key).join("[REDACTED]");
  process.stdout.write(line + "\n");
}
async function main() {
  const state = process.env.SINOTECH_CHAT_STATE;
  if (!state) throw new Error("Missing SINOTECH_CHAT_STATE");
  const runtime = new ChatRuntime(state, output);
  await runtime.start();
  output({ event: "ready", version: "1.0.0" });
  const reader = createInterface({ input: process.stdin, crlfDelay: Infinity });
  let closing = false;
  const shutdown = async () => { if (closing) return; closing = true; await runtime.close(); process.exit(0); };
  reader.on("line", line => {
    void (async () => {
      let request: any;
      try {
        if (line.length > 1024 * 1024) throw new Error("Chat request too large");
        request = JSON.parse(line);
        if (request.method === "shutdown") { await shutdown(); return; }
        if (closing) return;
        if (request.method === "configure" && typeof request.params?.apiKey === "string") keys.add(request.params.apiKey);
        output({ id: request.id, result: await runtime.handle(request.method, request.params ?? {}) });
      } catch (error) { output({ id: request?.id, error: error instanceof Error ? error.message : String(error) }); }
    })();
  });
  reader.on("close", () => { void shutdown(); });
  process.on("SIGTERM", () => { void shutdown(); });
}
main().catch(error => { output({ event: "fatal", text: error instanceof Error ? error.message : String(error) }); process.exitCode = 1; });
