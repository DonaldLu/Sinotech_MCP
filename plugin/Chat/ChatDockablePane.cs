using System;
using Autodesk.Revit.UI;

namespace revit_mcp_plugin.Chat
{
    internal sealed class ChatDockablePane : IDockablePaneProvider
    {
        public static readonly DockablePaneId Id = new DockablePaneId(new Guid("5B7D9A2E-3C41-4F6A-9E85-2D1A7C60B3F4"));
        public ChatPanel Panel { get; }
        public ChatDockablePane() { Panel = new ChatPanel(); }
        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = Panel;
            data.VisibleByDefault = false;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
        }
    }
}
