using Newtonsoft.Json;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace revit_mcp_plugin.Configuration
{
    public class ConfigurationManager
    {
        private readonly ILogger _logger;

        public FrameworkConfig Config { get; private set; } = new FrameworkConfig();

        public ConfigurationManager(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// <para>掃描 Commands\*\command.json，啟用所有已安裝命令集的全部命令（不再使用 commandRegistry.json）。</para>
        /// <para>只要命令的 DLL 存在於對應的 Revit 版本子資料夾，就會載入；新版本增加的命令安裝後即可使用。</para>
        /// <para>Enable every command of every installed command set whose DLL exists for a Revit version.</para>
        /// </summary>
        public void LoadConfiguration()
        {
            var commands = new List<CommandConfig>();
            try
            {
                foreach (string directory in Directory.GetDirectories(PathManager.GetCommandsDirectoryPath()))
                {
                    string commandJsonPath = Path.Combine(directory, "command.json");
                    if (Path.GetFileName(directory).StartsWith(".") || !File.Exists(commandJsonPath))
                        continue;

                    var commandSet = JsonConvert.DeserializeObject<CommandJson>(File.ReadAllText(commandJsonPath));
                    if (commandSet?.Commands == null)
                        continue;

                    var versions = Directory.GetDirectories(directory)
                        .Select(Path.GetFileName)
                        .Where(name => int.TryParse(name, out _))
                        .ToList();

                    foreach (var command in commandSet.Commands)
                    {
                        if (string.IsNullOrEmpty(command.AssemblyPath) ||
                            commands.Any(c => c.CommandName == command.CommandName))
                            continue;

                        var supported = versions
                            .Where(v => File.Exists(Path.Combine(directory, v, command.AssemblyPath)))
                            .ToArray();
                        if (supported.Length == 0)
                            continue;

                        commands.Add(new CommandConfig
                        {
                            CommandName = command.CommandName,
                            // 使用資料夾名稱，與 CommandManager 解析相對路徑的方式一致
                            // Use the folder name, matching how CommandManager resolves relative paths.
                            AssemblyPath = Path.Combine(Path.GetFileName(directory), "{VERSION}", command.AssemblyPath),
                            Enabled = true,
                            Description = command.Description,
                            SupportedRevitVersions = supported,
                            Developer = commandSet.Developer ?? new DeveloperInfo()
                        });
                    }
                }
                _logger.Info("已啟用所有已安裝命令，共 {0} 個\nEnabled all installed commands: {0}.", commands.Count);
            }
            catch (Exception ex)
            {
                _logger.Error("讀取已安裝命令失敗: {0}\nFailed to read installed commands: {0}", ex.Message);
            }
            Config.Commands = commands;
        }
    }
}
