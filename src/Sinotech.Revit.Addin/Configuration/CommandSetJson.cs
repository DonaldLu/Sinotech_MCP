using System.Collections.Generic;

namespace revit_mcp_plugin.Configuration
{
    /// <summary>
    /// <para>命令集資料夾中的 command.json（Commands\&lt;命令集&gt;\command.json）</para>
    /// <para>The command.json of an installed command set.</para>
    /// </summary>
    public class CommandJson
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public List<CommandItemJson> Commands { get; set; } = new List<CommandItemJson>();
        public DeveloperInfo Developer { get; set; }
        public List<string> SupportedRevitVersions { get; set; } = new List<string>();
    }

    public class CommandItemJson
    {
        public string CommandName { get; set; }
        public string Description { get; set; }
        public string AssemblyPath { get; set; }
    }
}
