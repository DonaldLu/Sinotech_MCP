using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core;

namespace revit_mcp_plugin.Chat
{
    public sealed class ChatPanel : UserControl, IDisposable
    {
        private readonly ComboBox provider = new ComboBox { MinHeight = 30 };
        private readonly ComboBox model = new ComboBox { IsEditable = true, MinHeight = 30 };
        private readonly PasswordBox apiKey = new PasswordBox { MinHeight = 30 };
        private readonly TextBox codexPath = new TextBox { MinHeight = 28 };
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
        private readonly TextBlock authHint = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBox input = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly StackPanel messages = new StackPanel();
        private readonly ScrollViewer scroll;
        private readonly StackPanel approval = new StackPanel { Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromRgb(255, 247, 222)), Margin = new Thickness(0, 6, 0, 6) };
        private readonly Button connect = Button("連線／取得模型");
        private readonly Button login = Button("登入 Codex");
        private readonly Button reopenLogin = Button("開啟 Codex 登入頁");
        private readonly Action<string> launchUrl;
        private bool loginStarting;
        private readonly Button logout = Button("登出／清除金鑰");
        private readonly Button send = Button("送出");
        private readonly Button stop = Button("停止");
        private readonly Button reset = Button("新對話");
        private ChatLaunchSettings launch;
        private ChatPreferences preferences;
        private ChatProcessClient client;
        private TextBox currentAnswer;
        private bool answerPending;
        private readonly Stopwatch turnWatch = new Stopwatch();
        private readonly DispatcherTimer progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private string progressPhase;
        private string connectedProvider;
        private string connectedModel;
        private string approvalId;
        private string loginUrl;
        private bool busy;
        private bool changing;
        private bool disposed;
        private string Provider => new[] { "codex", "claude", "gemini" }[Math.Max(0, provider.SelectedIndex)];
        private static Button Button(string caption) => new Button { Content = caption, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 6, 4), MinHeight = 30 };
        private static TextBlock Label(string text) => new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 4), Foreground = Brushes.DimGray };

        public ChatPanel() : this(OpenUrl) { }
        internal ChatPanel(Action<string> launchUrl)
        {
            this.launchUrl = launchUrl;
            progressTimer.Tick += (_, __) => { if (progressPhase != null) UpdateAnswerProgress(progressPhase); };
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));
            FontFamily = new FontFamily("Microsoft JhengHei UI"); FontSize = 13;
            MinWidth = 300;
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;
            var header = new StackPanel(); root.Children.Add(header);
            header.Children.Add(new TextBlock { Text = "中興 AI 助手", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(25, 56, 86)), Margin = new Thickness(0, 0, 0, 8) });
            provider.Items.Add("Codex · ChatGPT 登入"); provider.Items.Add("Claude · API Key"); provider.Items.Add("Gemini · API Key");
            header.Children.Add(provider);
            var settings = new StackPanel();
            settings.Children.Add(Label("模型（連線後取得，也可輸入模型 ID）")); settings.Children.Add(model);
            settings.Children.Add(Label("Claude／Gemini API Key（本機加密儲存）")); settings.Children.Add(apiKey);
            settings.Children.Add(Label("Codex 執行檔（codex.exe）")); settings.Children.Add(codexPath);
            header.Children.Add(new Expander { Header = "連線設定", IsExpanded = true, Content = settings, Margin = new Thickness(0, 6, 0, 6) });
            var controls = new WrapPanel(); controls.Children.Add(connect); controls.Children.Add(login); controls.Children.Add(logout); controls.Children.Add(reset); header.Children.Add(controls);
            header.Children.Add(authHint); header.Children.Add(status);
            reopenLogin.Visibility = Visibility.Collapsed;
            header.Children.Add(reopenLogin);
            reopenLogin.Click += (_, e) =>
            {
                e.Handled = true;
                try { if (loginUrl != null) this.launchUrl(loginUrl); }
                catch (Exception ex) { status.Text = ex.Message; }
            };
            scroll = new ScrollViewer { Content = messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 8, 0, 8) };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            Grid.SetRow(approval, 2); root.Children.Add(approval);
            var footer = new StackPanel(); Grid.SetRow(footer, 3); root.Children.Add(footer);
            footer.Children.Add(input);
            var sendButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) }; sendButtons.Children.Add(send); sendButtons.Children.Add(stop); footer.Children.Add(sendButtons);
            footer.Children.Add(new TextBlock { Text = "Enter 送出 · Shift+Enter 換行｜修改模型前會顯示參數供確認，完成後請自行儲存模型。", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray });
            connect.Click += async (_, __) => await Run(Connect);
            login.Click += async (_, e) => { e.Handled = true; await Run(Login); };
            logout.Click += async (_, __) => await Run(async () =>
            {
                if (busy || launch == null) return;
                if (Provider == "codex" && (client == null || connectedProvider != Provider)) await Connect();
                if (client != null && connectedProvider == Provider) await client.Request("logout");
                ClearLoginLink();
                apiKey.Password = ""; preferences.SetKey(Provider, ""); preferences.Save(launch.StateDirectory);
                model.Items.Clear(); connectedProvider = null;
                status.Text = Provider == "codex" ? "已登出此面板的 Codex 帳號。" : "已清除此面板儲存的 API Key。";
            });
            send.Click += async (_, __) => await Run(Send);
            stop.Click += async (_, __) => await Run(async () => { status.Text = "停止後續動作；已送出的 Revit 交易會等待結果。"; if (client != null) await client.Request("cancel"); });
            reset.Click += async (_, __) => await Run(async () => { if (client != null) await client.Request("reset"); messages.Children.Clear(); currentAnswer = null; status.Text = "已開始新對話。"; });
            input.PreviewKeyDown += async (_, e) => { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; await Run(Send); } };
            provider.SelectionChanged += (_, __) => SwitchProvider();
            try
            {
                launch = ChatLaunchSettings.Load();
                preferences = ChatPreferences.Load(launch.StateDirectory);
                codexPath.Text = launch.FindCodex(preferences.CodexExecutable);
                provider.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "codex", "claude", "gemini" }, preferences.Provider));
                status.Text = "請連線並選擇模型。Codex 首次使用須另完成官方登入。";
            }
            catch (Exception ex) { status.Text = ex.Message; connect.IsEnabled = false; login.IsEnabled = false; }
            SetBusy(false);
        }
        private void SwitchProvider()
        {
            if (changing || preferences == null) return;
            changing = true;
            try
            {
                ClearLoginLink();
                if (connectedProvider != null) { AddMessage("系統", "已切換 AI 引擎；下次送出會開始新的對話。"); connectedProvider = null; }
                preferences.Provider = Provider;
                apiKey.Password = preferences.Key(Provider);
                model.Items.Clear(); model.Text = preferences.Models.TryGetValue(Provider, out var saved) ? saved : "";
                model.IsEditable = Provider == "codex";
                apiKey.IsEnabled = Provider != "codex";
                codexPath.IsEnabled = Provider == "codex";
                login.Content = Provider == "codex" ? "登入 Codex" : "取得 API Key";
                authHint.Text = Provider == "codex" ? "使用 ChatGPT 帳號登入；背景引擎在 Revit 外獨立執行，不需開啟 Codex 視窗。" : "使用開發者 API Key；API 用量依供應商計費，與一般聊天訂閱分開。";
                preferences.Save(launch.StateDirectory);
            }
            finally { changing = false; }
        }
        private void SavePreferences()
        {
            preferences.Provider = Provider; preferences.CodexExecutable = codexPath.Text.Trim();
            preferences.Models[Provider] = model.Text.Trim();
            if (Provider != "codex") preferences.SetKey(Provider, apiKey.Password);
            preferences.Save(launch.StateDirectory);
        }
        private async Task EnsureClient()
        {
            if (client != null) return;
            var created = new ChatProcessClient();
            created.Event += message =>
            {
                if (!disposed) Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (disposed) return;
                    try { HandleEvent(message); }
                    catch (Exception ex) { status.Text = "面板訊息處理失敗：" + ex.Message; }
                }));
            };
            try { await created.Start(launch); client = created; }
            catch { created.Dispose(); throw; }
        }
        private async Task Connect()
        {
            if (busy || launch == null) return;
            SetBusy(true);
            try
            {
                SavePreferences(); await EnsureClient();
                status.Text = "連線中…";
                var wanted = model.Text.Trim();
                var response = await Configure(wanted);
                model.Items.Clear();
                foreach (var entry in response["models"] ?? new JArray()) model.Items.Add(entry.Value<string>("id"));
                model.Text = response.Value<string>("selectedModel") ?? wanted;
                SavePreferences();
                status.Text = response.Value<string>("status") + " 請選擇模型後送出。";
            }
            finally { SetBusy(false); }
        }
        private async Task<JToken> Configure(string selectedModel)
        {
            ClearLoginLink(); // Reconfiguration replaces the old Codex login session.
            if (Provider == "codex" && !File.Exists(codexPath.Text.Trim())) throw new FileNotFoundException("找不到 codex.exe，請在連線設定指定本機 Codex 執行檔。");
            var response = await client.Request("configure", new { provider = Provider, model = selectedModel, apiKey = Provider == "codex" ? "" : apiKey.Password, codexExecutable = codexPath.Text.Trim() }, 90000);
            connectedProvider = Provider; connectedModel = response.Value<string>("selectedModel") ?? selectedModel;
            model.Text = connectedModel;
            SavePreferences();
            return response;
        }
        private async Task Login()
        {
            if (busy || loginStarting || launch == null) return;
            if (Provider != "codex") { launchUrl(Provider == "claude" ? "https://console.anthropic.com/settings/keys" : "https://aistudio.google.com/apikey"); return; }
            if (loginUrl != null) { launchUrl(loginUrl); return; }
            loginStarting = true;
            try
            {
                if (client == null || connectedProvider != Provider) await Connect();
                if (client != null) await client.Request("login");
            }
            finally { loginStarting = false; }
        }
        private async Task Send()
        {
            if (busy || launch == null || string.IsNullOrWhiteSpace(input.Text)) return;
            if (string.IsNullOrWhiteSpace(model.Text)) { status.Text = "請先連線並選擇模型。"; return; }
            if (!SocketService.Instance.IsRunning) { status.Text = "Revit MCP 服務未啟動，請先按功能區「AI助手」按鈕（或 Switch）開啟服務。"; return; }
            var text = input.Text.Trim();
            SetBusy(true);
            try
            {
                SavePreferences(); await EnsureClient();
                if (connectedProvider != Provider || connectedModel != model.Text.Trim()) await Configure(model.Text.Trim());
                AddMessage("你", text); input.Clear(); currentAnswer = AddMessage("AI", "等待 AI 回應…尚未收到文字或工具指令。");
                answerPending = true;
                turnWatch.Restart(); progressTimer.Start();
                UpdateAnswerProgress("等待 AI 回應…尚未收到文字或工具指令。");
                var result = await client.Request("send", new { text }, 660000);
                status.Text = result.Value<string>("status") == "cancelled" ? "已停止。" : "本回合完成。";
            }
            finally
            {
                progressTimer.Stop(); turnWatch.Stop(); progressPhase = null;
                if (answerPending && currentAnswer != null) currentAnswer.Text = "此回合已結束，未收到文字回覆；請查看狀態與工具結果。";
                currentAnswer = null; answerPending = false; SetBusy(false);
            }
        }
        private void UpdateAnswerProgress(string text)
        {
            progressPhase = text;
            var display = turnWatch.IsRunning ? text + "（本回合 " + (int)turnWatch.Elapsed.TotalSeconds + " 秒）" : text;
            status.Text = display;
            if (answerPending && currentAnswer != null) currentAnswer.Text = display;
        }
        private void HandleEvent(JObject message)
        {
            switch (message.Value<string>("event"))
            {
                case "textDelta":
                    if (string.IsNullOrEmpty(message.Value<string>("text"))) break;
                    if (currentAnswer == null) currentAnswer = AddMessage("AI", "");
                    if (answerPending) { currentAnswer.Clear(); answerPending = false; }
                    UpdateAnswerProgress("正在接收 AI 回覆…");
                    currentAnswer.AppendText(message.Value<string>("text")); scroll.ScrollToEnd(); break;
                case "status": status.Text = message.Value<string>("text"); UpdateAnswerProgress(status.Text); break;
                case "loginUrl":
                    if (Provider != "codex") break;
                    var nextUrl = message.Value<string>("url");
                    if (string.IsNullOrWhiteSpace(nextUrl)) break;
                    bool changedUrl = nextUrl != loginUrl;
                    loginUrl = nextUrl;
                    reopenLogin.Visibility = Visibility.Visible;
                    status.Text = "請在瀏覽器完成 Codex 登入，再按連線取得模型。";
                    if (changedUrl) launchUrl(loginUrl);
                    break;
                case "loginCompleted": ClearLoginLink(); break;
                case "toolStarted": status.Text = "工具執行中：" + message.Value<string>("name"); UpdateAnswerProgress(status.Text); break;
                case "toolCompleted": AddToolResult(message); break;
                case "approval": ShowApproval(message); break;
                case "approvalClosed": if (message.Value<string>("id") == approvalId) { approval.Visibility = Visibility.Collapsed; approvalId = null; } break;
                case "fatal": case "disconnected":
                    ClearLoginLink();
                    status.Text = message.Value<string>("text"); connectedProvider = null;
                    var old = client; client = null; old?.Dispose(); break;
            }
        }
        private void AddToolResult(JObject message)
        {
            var data = message["data"];
            var caption = message.Value<string>("name");
            if (message["result"]?.Value<bool?>("isError") == true) caption += " · 錯誤";
            var body = new StackPanel();
            var images = (message["result"]?["content"] as JArray)?.OfType<JObject>().Where(item => item.Value<string>("type") == "image").ToArray() ?? Array.Empty<JObject>();
            var summary = (data ?? message["result"])?.DeepClone();
            if (summary is JObject summaryObject && summaryObject["content"] is JArray content)
                foreach (var item in content.OfType<JObject>().Where(item => item.Value<string>("type") == "image")) item["data"] = "[影像內容見下方]";
            body.Children.Add(new TextBox { Text = summary?.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            foreach (var item in images)
            {
                try
                {
                    using (var stream = new MemoryStream(Convert.FromBase64String(item.Value<string>("data"))))
                    {
                        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1200;
                        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                        body.Children.Add(new Image { Source = bitmap, MaxHeight = 420, Stretch = Stretch.Uniform, Margin = new Thickness(0, 6, 0, 0) });
                    }
                }
                catch (Exception) { body.Children.Add(new TextBlock { Text = "影像無法顯示，請查看工具結果。" }); }
            }
            messages.Children.Add(new Expander { Header = caption, Margin = new Thickness(0, 4, 0, 4), Content = body });
            scroll.ScrollToEnd();
        }
        private void ShowApproval(JObject message)
        {
            approvalId = message.Value<string>("id");
            approval.Children.Clear();
            var preview = message["preview"];
            approval.Children.Add(new TextBlock { Text = "確認執行操作", FontWeight = FontWeights.Bold, Margin = new Thickness(8) });
            approval.Children.Add(new TextBlock { Text = $"工具：{preview?["tool"]}\n{preview?["note"]}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) });
            approval.Children.Add(new TextBox { Text = preview?["arguments"]?.ToString(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(8) });
            var actions = new WrapPanel { Margin = new Thickness(8, 0, 8, 4) };
            var accept = Button("執行此操作"); var decline = Button("取消");
            var id = approvalId;
            accept.Click += async (_, __) => await Run(async () => { approval.Visibility = Visibility.Collapsed; await client.Request("approve", new { id, accepted = true }); });
            decline.Click += async (_, __) => await Run(async () => { approval.Visibility = Visibility.Collapsed; await client.Request("approve", new { id, accepted = false }); });
            actions.Children.Add(accept); actions.Children.Add(decline); approval.Children.Add(actions); approval.Visibility = Visibility.Visible;
            status.Text = "等待你確認操作參數。";
            UpdateAnswerProgress(status.Text);
        }
        private TextBox AddMessage(string role, string text)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };
            stack.Children.Add(new TextBlock { Text = role, FontWeight = FontWeights.SemiBold, Foreground = Brushes.SteelBlue });
            var body = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0, 3, 0, 0) };
            stack.Children.Add(body); messages.Children.Add(stack); scroll.ScrollToEnd(); return body;
        }
        private void ClearLoginLink()
        {
            loginUrl = null;
            reopenLogin.Visibility = Visibility.Collapsed;
        }
        private static void OpenUrl(string url)
        {
            var uri = new Uri(url);
            var allowed = new[] { "auth.openai.com", "chatgpt.com", "platform.claude.com", "console.anthropic.com", "aistudio.google.com" };
            if (uri.Scheme != "https" || !allowed.Contains(uri.Host)) throw new InvalidOperationException("登入網址不在允許的供應商網域。");
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        private async Task Run(Func<Task> action)
        {
            try { await action(); }
            catch (Exception ex) { status.Text = ex.Message; AddMessage("系統", ex.Message); }
        }
        private void SetBusy(bool value)
        {
            busy = value; provider.IsEnabled = !value; model.IsEnabled = !value; apiKey.IsEnabled = !value && Provider != "codex";
            codexPath.IsEnabled = !value && Provider == "codex"; connect.IsEnabled = !value && launch != null;
            login.IsEnabled = !value && launch != null; send.IsEnabled = !value && launch != null; reset.IsEnabled = !value && launch != null;
            logout.IsEnabled = !value && launch != null;
            stop.IsEnabled = value;
        }
        public void Dispose() { if (disposed) return; disposed = true; progressTimer.Stop(); client?.Dispose(); }
    }
}
