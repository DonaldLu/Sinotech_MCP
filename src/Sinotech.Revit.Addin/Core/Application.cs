using System;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using revit_mcp_plugin.Chat;



namespace revit_mcp_plugin.Core
{
    public class Application : IExternalApplication
    {
        private ChatDockablePane chat;
        // Revit 啟動完成前，AI助手面板可能已被還原為顯示狀態
        // The AI assistant pane may be restored as shown before Revit finishes starting.
        private bool paneShown;

        public Result OnStartup(UIControlledApplication application)
        {
            chat = new ChatDockablePane();
            application.RegisterDockablePane(ChatDockablePane.Id, "中興 AI 助手", chat);

            // Revit 啟動完成後載入所有已安裝命令；MCP 服務則隨 AI助手面板開啟／關閉而啟動／停止
            // Load every installed command once Revit is ready; the MCP service follows the AI assistant pane.
            application.ControlledApplication.ApplicationInitialized += OnApplicationInitialized;
            application.DockableFrameVisibilityChanged += OnDockableFrameVisibilityChanged;

            RibbonPanel mcpPanel = RibbonUi.GetOrCreatePanel(application);
            RibbonUi.AddAiAssistantButton(mcpPanel);

            return Result.Succeeded;
        }

        private void OnApplicationInitialized(object sender, Autodesk.Revit.DB.Events.ApplicationInitializedEventArgs e)
        {
            try
            {
                var uiApp = new UIApplication(sender as Autodesk.Revit.ApplicationServices.Application);
                SocketService.Instance.Initialize(uiApp);
                if (paneShown || uiApp.GetDockablePane(ChatDockablePane.Id).IsShown())
                    SocketService.Instance.Start();
            }
            catch (Exception ex)
            {
                // 失敗時按 AI助手 會再嘗試初始化
                // If this fails, clicking the AI assistant button initializes again.
                System.Diagnostics.Trace.WriteLine("Sinotech MCP initialization failed: " + ex.Message);
            }
        }

        private void OnDockableFrameVisibilityChanged(object sender, DockableFrameVisibilityChangedEventArgs e)
        {
            if (e.PaneId.Guid != ChatDockablePane.Id.Guid) return;
            paneShown = e.DockableFrameShown;
            try
            {
                var service = SocketService.Instance;
                if (!paneShown) service.Stop();
                else if (service.IsInitialized) service.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine("Sinotech MCP service toggle failed: " + ex.Message);
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.DockableFrameVisibilityChanged -= OnDockableFrameVisibilityChanged;
            try
            {
                if (SocketService.Instance.IsRunning)
                {
                    SocketService.Instance.Stop();
                }
            }
            catch { }

            chat?.Panel.Dispose();
            return Result.Succeeded;
        }
    }
}
