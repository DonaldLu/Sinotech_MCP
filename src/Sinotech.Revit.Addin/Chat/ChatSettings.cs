using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace revit_mcp_plugin.Chat
{
    public sealed class ChatLaunchSettings
    {
        public string NodeExecutable { get; set; }
        public string RuntimeScript { get; set; }
        public string CodexExecutable { get; set; }
        public string StateDirectory { get; set; }

        /// <summary>
        /// Optional chat.config.json beside the add-in DLL may set nodeExecutable, runtimeScript,
        /// codexExecutable and stateDirectory. Without it the runtime is auto-detected:
        /// first runtime/build/chat/index.js beside the DLL (installed layout), then server/build/chat/index.js
        /// found by walking up from the DLL (development layout).
        /// </summary>
        public static ChatLaunchSettings Load()
        {
            var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var configPath = Path.Combine(directory, "chat.config.json");
            var settings = File.Exists(configPath)
                ? JsonConvert.DeserializeObject<ChatLaunchSettings>(File.ReadAllText(configPath)) ?? new ChatLaunchSettings()
                : new ChatLaunchSettings();
            settings.NodeExecutable = Resolve(directory, settings.NodeExecutable) ?? FindNode();
            settings.RuntimeScript = Resolve(directory, settings.RuntimeScript) ?? FindRuntime(directory);
            settings.StateDirectory = Environment.ExpandEnvironmentVariables(settings.StateDirectory ?? "%LOCALAPPDATA%\\Sinotech\\McpChat");
            if (string.IsNullOrEmpty(settings.NodeExecutable) || !File.Exists(settings.NodeExecutable))
                throw new FileNotFoundException("找不到 Node.js（需要 20 以上），請安裝 Node.js 或在 chat.config.json 指定 nodeExecutable。");
            if (string.IsNullOrEmpty(settings.RuntimeScript) || !File.Exists(settings.RuntimeScript))
                throw new FileNotFoundException("找不到對話 runtime（server/build/chat/index.js），請先在 server 資料夾執行 npm run build。");
            Directory.CreateDirectory(settings.StateDirectory);
            return settings;
        }

        private static string Resolve(string directory, string path) =>
            string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(Path.Combine(directory, path));

        private static string FindNode()
        {
            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try
                {
                    var candidate = Path.Combine(entry.Trim('"'), "node.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
            return File.Exists(fallback) ? fallback : null;
        }

        private static string FindRuntime(string directory)
        {
            var installed = Path.Combine(directory, "runtime", "build", "chat", "index.js");
            if (File.Exists(installed)) return installed;
            for (var dir = new DirectoryInfo(directory); dir != null; dir = dir.Parent)
            {
                var dev = Path.Combine(dir.FullName, "server", "build", "chat", "index.js");
                if (File.Exists(dev)) return dev;
            }
            return null;
        }

        /// <summary>
        /// 本機所有找得到的 codex.exe（依序：上次選擇、chat.config.json、PATH、Codex 安裝目錄新到舊），供下拉選單選擇
        /// Every codex.exe found on this machine, in preference order, for the read-only drop-down.
        /// </summary>
        public List<string> FindCodexCandidates(string preferred)
        {
            var found = new List<string>();
            void Add(string candidate)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) return;
                var full = Path.GetFullPath(candidate);
                if (!found.Contains(full, StringComparer.OrdinalIgnoreCase)) found.Add(full);
            }
            Add(preferred);
            Add(CodexExecutable);
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                try { Add(Path.Combine(directory.Trim('"'), "codex.exe")); }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(root))
                foreach (var file in Directory.GetFiles(root, "codex.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc))
                    Add(file);
            return found;
        }
    }

    public sealed class ChatPreferences
    {
        public string Provider { get; set; } = "codex";
        public string CodexExecutable { get; set; }
        public Dictionary<string, string> Models { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> ProtectedKeys { get; set; } = new Dictionary<string, string>();
        public static ChatPreferences Load(string directory)
        {
            var path = Path.Combine(directory, "ui.settings.json");
            return File.Exists(path) ? JsonConvert.DeserializeObject<ChatPreferences>(File.ReadAllText(path)) ?? new ChatPreferences() : new ChatPreferences();
        }
        public string Key(string provider)
        {
            if (!ProtectedKeys.TryGetValue(provider, out var encoded)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encoded), null, DataProtectionScope.CurrentUser)); }
            catch (CryptographicException) { return ""; }
            catch (FormatException) { return ""; }
        }
        public void SetKey(string provider, string key)
        {
            if (string.IsNullOrWhiteSpace(key)) ProtectedKeys.Remove(provider);
            else ProtectedKeys[provider] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
        }
        public void Save(string directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "ui.settings.json"), JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
        }
    }
}
