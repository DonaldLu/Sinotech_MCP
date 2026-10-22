using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Chat
{
    public sealed class ChatProcessClient : IDisposable
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JToken>> pending = new ConcurrentDictionary<int, TaskCompletionSource<JToken>>();
        private readonly object writeGate = new object();
        private Process process;
        private int sequence;
        private bool disposed;
        private readonly TaskCompletionSource<bool> ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<JObject> Event;

        public async Task Start(ChatLaunchSettings settings)
        {
            var info = new ProcessStartInfo(settings.NodeExecutable, "\"" + settings.RuntimeScript + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = settings.StateDirectory
            };
            info.EnvironmentVariables["SINOTECH_CHAT_STATE"] = settings.StateDirectory;
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.Exited += (_, __) => Fail(new InvalidOperationException("AI 背景程序已結束，請重新連線。"));
            process.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data)) return;
                try
                {
                    var message = JObject.Parse(args.Data);
                    if (message["id"]?.Type == JTokenType.Integer && pending.TryRemove(message.Value<int>("id"), out var completion))
                    {
                        if (message["error"] != null) completion.TrySetException(new InvalidOperationException(message.Value<string>("error")));
                        else completion.TrySetResult(message["result"] ?? JValue.CreateNull());
                    }
                    else
                    {
                        if (message.Value<string>("event") == "ready") ready.TrySetResult(true);
                        if (message.Value<string>("event") == "fatal") Fail(new InvalidOperationException(message.Value<string>("text")));
                        Event?.Invoke(message);
                    }
                }
                catch (JsonException) { Fail(new InvalidOperationException("AI 背景程序回傳無效訊息。")); }
            };
            // Do not forward raw stderr or credentials into the conversation transcript.
            process.ErrorDataReceived += (_, __) => { };
            if (!process.Start()) throw new InvalidOperationException("無法啟動 Node.js。");
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            if (await Task.WhenAny(ready.Task, Task.Delay(20000)).ConfigureAwait(false) != ready.Task) { Dispose(); throw new TimeoutException("AI runtime 啟動逾時。"); }
            await ready.Task.ConfigureAwait(false);
        }
        private void Fail(Exception error)
        {
            ready.TrySetException(error);
            foreach (var pair in pending) if (pending.TryRemove(pair.Key, out var completion)) completion.TrySetException(error);
            if (!disposed) Event?.Invoke(new JObject { ["event"] = "disconnected", ["text"] = error.Message });
        }
        public async Task<JToken> Request(string method, object parameters = null, int timeoutMs = 60000)
        {
            if (disposed || process == null || process.HasExited) throw new InvalidOperationException("請先連線 AI。");
            int id = Interlocked.Increment(ref sequence);
            var completion = new TaskCompletionSource<JToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.TryAdd(id, completion);
            try
            {
                WriteLine(JsonConvert.SerializeObject(new { id, method, @params = parameters ?? new { } }));
                if (await Task.WhenAny(completion.Task, Task.Delay(timeoutMs)).ConfigureAwait(false) != completion.Task)
                    throw new TimeoutException("AI 回應逾時；模型修改請先確認 Revit 內的實際結果，避免重複建立。");
                return await completion.Task.ConfigureAwait(false);
            }
            finally { pending.TryRemove(id, out _); }
        }
        private void WriteLine(string line)
        {
            // .NET Framework may use a legacy console encoding for StandardInput.
            // Write bytes explicitly so Traditional Chinese is always UTF-8.
            var bytes = new UTF8Encoding(false).GetBytes(line + "\n");
            lock (writeGate)
            {
                var stream = process.StandardInput.BaseStream;
                stream.Write(bytes, 0, bytes.Length); stream.Flush();
            }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (process != null && !process.HasExited)
                {
                    WriteLine("{\"method\":\"shutdown\"}");
                    process.StandardInput.Close();
                    if (!process.WaitForExit(1500))
                    {
#if NET8_0_OR_GREATER
                        process.Kill(true);
#else
                        process.Kill();
#endif
                    }
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (System.IO.IOException) { }
            Fail(new ObjectDisposedException(nameof(ChatProcessClient)));
            process?.Dispose();
        }
    }
}
