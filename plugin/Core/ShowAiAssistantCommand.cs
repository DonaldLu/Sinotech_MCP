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
                // The chat runtime reaches Revit through the same socket service as the Switch button.
                var service = SocketService.Instance;
                if (!service.IsRunning)
                {
                    service.Initialize(commandData.Application);
                    service.Start();
                }
                var pane = commandData.Application.GetDockablePane(ChatDockablePane.Id);
                if (pane.IsShown()) pane.Hide(); else pane.Show();
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
