import { setTimeout as delay } from "node:timers/promises";

// Retry only rejected HTTP requests, never a consumed stream or a Revit command.
export async function fetchWithRetry(fetcher: typeof fetch, url: string, init: RequestInit,
  onRetry: (status: number, attempt: number, seconds: number) => void,
  sleep = (milliseconds: number, signal?: AbortSignal) => delay(milliseconds, undefined, { signal })): Promise<Response> {
  for (let attempt = 0; ; attempt++) {
    init.signal?.throwIfAborted();
    const response = await fetcher(url, init);
    const retryHeader = response.headers.get("retry-after");
    const requested = retryHeader === null ? NaN : /^\d+(?:\.\d+)?$/.test(retryHeader)
      ? Number(retryHeader) * 1000 : Date.parse(retryHeader) - Date.now();
    const temporary = [500, 502, 503, 504, 529].includes(response.status)
      || (response.status === 429 && Number.isFinite(requested) && requested >= 0 && requested <= 15000);
    // Long Retry-After values must not be ignored or shortened.
    if (!temporary || attempt >= 2 || (Number.isFinite(requested) && requested > 15000)) return response;
    const wait = Math.max(2000 * 2 ** attempt + Math.floor(Math.random() * 500), Number.isFinite(requested) ? requested : 0);
    await response.body?.cancel().catch(() => {});
    onRetry(response.status, attempt + 1, Math.ceil(wait / 1000));
    await sleep(wait, init.signal ?? undefined);
  }
}

export async function checkResponse(response: Response): Promise<Response> {
  if (response.ok) return response;
  // Keep headers, API keys and large response bodies out of UI/logs.
  let message = response.statusText;
  try { const body = await response.json() as { error?: { message?: string } }; message = body.error?.message ?? message; } catch { /* no JSON */ }
  const explanation: Record<number, string> = {
    400: "請求參數或模型設定不相容", 401: "API Key 無效或已失效", 402: "API 帳戶額度或付款設定不足",
    403: "此金鑰或專案沒有存取權限", 404: "模型或端點不可用", 429: "請求頻率或 Token／使用額度已達限制",
    500: "供應商服務發生錯誤", 502: "供應商服務暫時異常", 503: "供應商服務忙碌，暫時無法回應",
    504: "供應商處理請求逾時", 529: "Claude 服務過載，暫時無法回應"
  };
  throw new Error(`AI HTTP ${response.status}: ${explanation[response.status] ?? "請求失敗"}。${message.slice(0, 1000)}`);
}

export async function* sseJson(response: Response): AsyncGenerator<any> {
  await checkResponse(response);
  if (!response.body) throw new Error("Empty AI response");
  const decoder = new TextDecoder();
  let buffer = "";
  let data: string[] = [];
  const processLine = (line: string): any | undefined => {
    if (line === "") {
      const payload = data.join("\n"); data = [];
      if (!payload || payload === "[DONE]") return undefined;
      return JSON.parse(payload);
    }
    if (line.startsWith("data:")) data.push(line.slice(5).trimStart());
    return undefined;
  };
  for await (const chunk of response.body) {
    buffer += decoder.decode(chunk, { stream: true });
    if (buffer.length > 8 * 1024 * 1024) throw new Error("AI stream frame too large");
    let end: number;
    while ((end = buffer.indexOf("\n")) >= 0) {
      const value = processLine(buffer.slice(0, end).replace(/\r$/, ""));
      buffer = buffer.slice(end + 1);
      if (value !== undefined) yield value;
    }
  }
  buffer += decoder.decode();
  if (buffer) processLine(buffer.replace(/\r$/, ""));
  const final = processLine("");
  if (final !== undefined) yield final;
}

export function requestSignal(signal?: AbortSignal): AbortSignal {
  return signal ? AbortSignal.any([signal, AbortSignal.timeout(180000)]) : AbortSignal.timeout(30000);
}
