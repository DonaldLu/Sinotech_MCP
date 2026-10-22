using System;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace revit_mcp_plugin.Core
{
    internal static class RibbonUi
    {
        internal const string TabName = "中興MCP";
        internal const string PanelName = "MCP";
        // Internal ID is separate from the displayed name.
        internal const string AiAssistantButtonId = "Sinotech.MCP.ShowAiAssistant";

        /// <summary>Creates the tab/panel (shared if they already exist) and returns the panel.</summary>
        internal static RibbonPanel GetOrCreatePanel(UIControlledApplication app)
        {
            try { app.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Tab already exists (another Sinotech add-in); reuse it.
            }
            return app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == PanelName)
                ?? app.CreateRibbonPanel(TabName, PanelName);
        }

        internal static void AddAiAssistantButton(RibbonPanel panel)
        {
            if (panel.GetItems().Any(item => item.Name == AiAssistantButtonId)) return;

            var assembly = Assembly.GetExecutingAssembly();
            var data = new PushButtonData(AiAssistantButtonId, "AI助手", assembly.Location,
                typeof(ShowAiAssistantCommand).FullName)
            {
                // The supplied 32px PNG is used as LargeImage only (no 16px Image).
                LargeImage = RibbonImages.LoadImage("Sinotech.Mcp.Images.AiAssistant.png"),
                ToolTip = "【功能介紹】\n在 Revit 內使用 Codex、Claude 或 Gemini 與 AI 助手對話，透過 MCP 查詢並編修目前的 Revit 模型。",
                LongDescription = "【使用步驟】\n1. 開啟 Revit 專案，點選「AI助手」開啟對話面板。\n2. 選擇 AI 引擎並登入（Codex／Claude／Gemini）。\n3. 輸入需求，AI 透過 MCP 工具讀取或修改模型。\n4. 修改模型前請先核對預覽內容；需要時可使用 Revit Undo 還原，並自行儲存模型。",
                ToolTipImage = RibbonImages.LoadImage("Sinotech.Mcp.Images.AiAssistant.Original.png", 250)
            };
            panel.AddItem(data);
        }
    }

    internal static class RibbonImages
    {
        internal static BitmapSource LoadImage(string resource, int width = 0)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Missing ribbon image: " + resource))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                if (width > 0) image.DecodePixelWidth = width;
                image.StreamSource = stream;
                image.EndInit();
                // The PNG carries 300 DPI: 32 pixels would then occupy only ~10 WPF
                // units in the ribbon. Normalize the in-memory DPI to 96 without
                // touching the file or its pixels, so it shows at 32x32 units.
                var pixelsSource = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                int stride = pixelsSource.PixelWidth * 4;
                var pixels = new byte[stride * pixelsSource.PixelHeight];
                pixelsSource.CopyPixels(pixels, stride, 0);
                var source = BitmapSource.Create(pixelsSource.PixelWidth, pixelsSource.PixelHeight,
                    96, 96, PixelFormats.Bgra32, null, pixels, stride);
                source.Freeze();
                return source;
            }
        }
    }
}
