using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using revit_mcp_plugin.Chat;

namespace revit_mcp_plugin.Core
{
    [Transaction(TransactionMode.Manual)]
    public class ShowAiAssistantCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                // AI助手開啟時啟動 MCP 服務、關閉時停止（面板事件也會處理，這裡再確保一次）
                // The MCP service runs only while the AI assistant pane is shown.
                var service = SocketService.Instance;
                service.Initialize(commandData.Application);
                var pane = commandData.Application.GetDockablePane(ChatDockablePane.Id);
                if (pane.IsShown())
                {
                    pane.Hide();
                    service.Stop();
                }
                else
                {
                    service.Start();
                    pane.Show();
                }
                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
