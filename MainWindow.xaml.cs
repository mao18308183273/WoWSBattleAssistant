using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WoWSBattleAssistant.Models;
using WoWSBattleAssistant.Services;
using WoWSBattleAssistant.Services.AI;
using WoWSBattleAssistant.Services.Shinoaki;
using WoWSBattleAssistant.Views;

namespace WoWSBattleAssistant;

/// <summary>
/// 悬浮窗主窗口。三步式流程：
/// ① 截阵容 → AI 识别舰船名 → 用户指定自己的船（可修正）
/// ② 截小地图
/// ③ 分析（用识别到的名字查知识库 + 小地图图 → 调 AI 分析）
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ShipDatabase _database = new();
    private CancellationTokenSource? _cts;

    // 步骤①②的状态
    private BitmapSource? _lineupImage;
    private BitmapSource? _minimapImage;
    private bool _lineupReady;   // 阵容识别成功（或手动填了名字）
    private bool _minimapReady;  // 小地图已截

    /// <summary>阵容识别得到的"玩家名+舰船名"配对（用于分析阶段查 shinoaki 战绩判真人/人机）</summary>
    private List<PlayerShipPair> _playerShipPairs = new();

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        Loaded += MainWindow_Loaded;
        LocationChanged += MainWindow_LocationChanged;
        SizeChanged += MainWindow_LocationChanged;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Left = _settings.WindowLeft;
        Top = _settings.WindowTop;
        if (_settings.WindowWidth > 0) Width = _settings.WindowWidth;
        if (_settings.WindowHeight > 0) Height = _settings.WindowHeight;

        UpdateStatusBar();
        await LoadDatabaseAsync();
    }

    private async Task LoadDatabaseAsync()
    {
        var path = _settings.ShipDataPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            TxtStatus.Text = "知识库未配置，请点⚙设置数据路径";
            return;
        }
        try
        {
            TxtStatus.Text = "加载知识库中...";
            var progress = new Progress<string>(s => TxtStatus.Text = s);
            await _database.LoadAsync(path, progress);
            TxtStatus.Text = $"已加载 {_database.TotalCount} 艘战舰";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "知识库加载失败: " + ex.Message;
        }
    }

    private void MainWindow_LocationChanged(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        SettingsStore.Save(_settings);
    }

    private void UpdateStatusBar()
    {
        var provider = _settings.AiProvider switch
        {
            AiProvider.Glm => $"智谱 {_settings.GlmModel}",
            AiProvider.Qwen => $"通义 {_settings.QwenModel}",
            AiProvider.DeepSeek => "DeepSeek 视觉",
            _ => "未知"
        };
        var keyOk = _settings.AiProvider switch
        {
            AiProvider.Glm => !string.IsNullOrWhiteSpace(_settings.GlmApiKey),
            AiProvider.Qwen => !string.IsNullOrWhiteSpace(_settings.QwenApiKey),
            AiProvider.DeepSeek => !string.IsNullOrWhiteSpace(_settings.DeepSeekToken),
            _ => false
        };
        TxtModel.Text = keyOk ? $"模型: {provider}" : $"模型: {provider} (未填 Key)";
    }

    // ===== 标题栏拖拽 =====
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1) DragMove();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings, _database) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            UpdateStatusBar();
            if (!_database.IsLoaded)
            {
                _ = LoadDatabaseAsync();
            }
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _lineupImage = null;
        _minimapImage = null;
        _lineupReady = false;
        _minimapReady = false;
        _playerShipPairs = new();

        TxtMyShip.Text = "";
        TxtAllShips.Text = "";
        CboMyShip.Items.Clear();

        ImgLineupPreview.Source = null;
        TxtLineupPlaceholder.Visibility = Visibility.Visible;
        TxtLineupStatus.Text = "未截取";

        ImgMinimapPreview.Source = null;
        TxtMinimapPlaceholder.Visibility = Visibility.Visible;
        TxtMinimapStatus.Text = "未截取";

        TxtResult.Text = "等待分析...";
        TxtFooter.Text = "";
        TxtStatus.Text = "已清空";
        UpdateAnalyzeButton();
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TxtResult.Text); TxtFooter.Text = "已复制结果到剪贴板"; }
        catch { }
    }

    // ===== 折叠/展开详情 =====
    private bool _collapsed;
    private void BtnCollapse_Click(object sender, RoutedEventArgs e)
    {
        _collapsed = !_collapsed;
        var vis = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.Visibility = vis;
        Step1Details.Visibility = vis;
        Step2Details.Visibility = vis;
        BtnCollapse.Content = _collapsed ? "▼" : "▲";
        TxtFooter.Visibility = vis; // 折叠时也隐藏底部用时信息
    }

    // ===== 屏蔽主窗口一切按键(只放行 TextBox 文本输入) =====
    // 用户常按住游戏内的 TAB(显示阵容)同时操作本程序,若不屏蔽,TAB 会在按钮间循环焦点。
    // 只允许在 TextBox 内打字;其余按键一律吞掉,避免焦点乱跳、按钮被空格/回车误触发。
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
        e.Handled = true;
    }

    // ===== 步骤①：截阵容 → AI 识别 =====
    private async void BtnCaptureLineup_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            TxtStatus.Text = "上一次操作仍在进行中";
            return;
        }

        if (!_database.IsLoaded)
        {
            MessageBox.Show("战舰知识库未加载，请检查数据文件路径。", "提示");
            return;
        }
        var hasKey = _settings.AiProvider switch
        {
            AiProvider.Glm => !string.IsNullOrWhiteSpace(_settings.GlmApiKey),
            AiProvider.Qwen => !string.IsNullOrWhiteSpace(_settings.QwenApiKey),
            AiProvider.DeepSeek => !string.IsNullOrWhiteSpace(_settings.DeepSeekToken),
            _ => false
        };
        if (!hasKey)
        {
            MessageBox.Show("未配置 API Key，请点⚙设置。", "提示");
            return;
        }

        _cts = new CancellationTokenSource();
        BtnCaptureLineup.IsEnabled = false;
        TxtStatus.Text = "请框选双方阵容面板...";

        BitmapSource? shot = null;
        try
        {
            // 1. 隐藏主窗口 → 框选 → 截图
            this.Hide();
            await Task.Delay(80);
            try
            {
                var sel = new RegionSelectorWindow();
                if (sel.ShowDialog() != true || sel.SelectedRegion.IsEmpty)
                {
                    TxtStatus.Text = "已取消框选";
                    return;
                }
                shot = ScreenCaptureService.CaptureRegion(sel.SelectedRegion);
            }
            finally
            {
                this.Show();
                this.Activate();
            }

            // 2. 预览
            _lineupImage = shot;
            ImgLineupPreview.Source = shot;
            TxtLineupPlaceholder.Visibility = shot == null ? Visibility.Visible : Visibility.Collapsed;
            TxtLineupStatus.Text = "截图完成，正在调 AI 识别舰船名...";

            // 3. 调 AI 识别
            TxtStatus.Text = "AI 识别阵容中（可能需 10-30 秒）...";
            var analyzer = AIAnalyzerFactory.Create(_settings);
            var rec = await analyzer.RecognizeShipsAsync(shot!, _cts.Token);

            if (!rec.Success)
            {
                TxtLineupStatus.Text = "❌ 识别失败：" + rec.Error;
                TxtStatus.Text = "识别失败，可手动填写舰船名";
                UpdateAnalyzeButton();
                return;
            }

            // 用数据库过滤：只保留真实存在的舰船，并把名字归一化为数据库标准名
            // （AI 常把用户名误认成舰船，必须剔除）
            var recognized = rec.Ships;
            // 保存识别到的玩家名+舰船名配对，供分析阶段查 shinoaki 战绩
            _playerShipPairs = rec.PlayerShipPairs ?? new();
            List<string> filtered;
            if (_database.IsLoaded)
            {
                filtered = FilterToKnownShips(recognized, out int dropped);
                if (filtered.Count == 0 && recognized.Count > 0)
                {
                    // 全被过滤掉——可能是数据未命中或识别全是用户名，保留原名单让用户手动修
                    filtered = recognized;
                    TxtLineupStatus.Text = $"⚠️ 识别到 {recognized.Count} 个名称但无一命中知识库，已保留原值供你手动修正。";
                }
                else
                {
                    TxtLineupStatus.Text = $"✅ 识别 {recognized.Count} 项，命中 {filtered.Count} 艘真实舰船" +
                        (dropped > 0 ? $"（剔除 {dropped} 个非舰船名/用户名）" : "") + "。请从下拉框指定你的战舰。";
                }
            }
            else
            {
                filtered = recognized;
                TxtLineupStatus.Text = $"✅ 识别到 {recognized.Count} 项（知识库未加载，未做过滤）。请从下拉框指定你的战舰。";
            }

            CboMyShip.Items.Clear();
            foreach (var name in filtered.Distinct(StringComparer.OrdinalIgnoreCase)) CboMyShip.Items.Add(name);

            TxtAllShips.Text = string.Join("、", filtered);
            TxtMyShip.Text = "";

            TxtStatus.Text = "阵容识别完成，请指定你的战舰";
            _lineupReady = true;
            UpdateAnalyzeButton();
        }
        catch (Exception ex)
        {
            TxtLineupStatus.Text = "❌ 异常：" + ex.Message;
            TxtStatus.Text = "异常";
            this.Show();
        }
        finally
        {
            BtnCaptureLineup.IsEnabled = true;
            _cts = null; // ★ 必须复位,否则下次点击永远判定"上一次仍在进行中"
        }
    }

    /// <summary>下拉框选中 → 自动把选中船设为"我的战舰"，并从 Allies 框移除</summary>
    private void CboMyShip_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CboMyShip.SelectedItem == null) return;
        ApplyMyShipSelection(CboMyShip.SelectedItem.ToString() ?? "");
    }

    /// <summary>"设为我的"按钮：兜底确保下拉选中项移入 MyShip</summary>
    private void BtnMoveToMy_Click(object sender, RoutedEventArgs e)
    {
        if (CboMyShip.SelectedItem == null)
        {
            MessageBox.Show("请先在下拉框中选择一艘舰船。", "提示");
            return;
        }
        ApplyMyShipSelection(CboMyShip.SelectedItem.ToString() ?? "");
    }

    private void ApplyMyShipSelection(string shipName)
    {
        if (string.IsNullOrWhiteSpace(shipName)) return;
        TxtMyShip.Text = shipName;
        // 注意：扁平列表 TxtAllShips 保持完整（含用户自己的船），不删除。
        // 它用于构建知识库；用户自己的船名作为锚点让 AI 在阵容图中定位我方阵营。
        TxtLineupStatus.Text = $"✅ 已指定你的战舰: {shipName}（敌我由分析阶段 AI 看阵容图自行判断）";
        _lineupReady = true;
        UpdateAnalyzeButton();
    }

    // ===== 步骤②：截小地图 =====
    private void BtnCaptureMinimap_Click(object sender, RoutedEventArgs e)
    {
        TxtStatus.Text = "请框选小地图区域...";

        try
        {
            this.Hide();
            try
            {
                var sel = new RegionSelectorWindow();
                if (sel.ShowDialog() != true || sel.SelectedRegion.IsEmpty)
                {
                    TxtStatus.Text = "已取消框选";
                    return;
                }
                var shot = ScreenCaptureService.CaptureRegion(sel.SelectedRegion);
                _minimapImage = shot;
                ImgMinimapPreview.Source = shot;
                TxtMinimapPlaceholder.Visibility = shot == null ? Visibility.Visible : Visibility.Collapsed;
                TxtMinimapStatus.Text = $"✅ 已截取小地图 ({(int)sel.SelectedRegion.Width}×{(int)sel.SelectedRegion.Height})";
                TxtStatus.Text = "小地图已就绪";
                _minimapReady = true;
                UpdateAnalyzeButton();
            }
            finally
            {
                this.Show();
                this.Activate();
            }
        }
        catch (Exception ex)
        {
            TxtMinimapStatus.Text = "❌ 异常：" + ex.Message;
            TxtStatus.Text = "异常";
            this.Show();
        }
    }

    private void UpdateAnalyzeButton()
    {
        // ①②都完成（识别成功或手动填了船名 + 小地图已截 + 我的船非空）才能点分析
        var myShipOk = !string.IsNullOrWhiteSpace(TxtMyShip.Text.Trim());
        var lineupTouched = _lineupReady
            || !string.IsNullOrWhiteSpace(TxtMyShip.Text.Trim())
            || !string.IsNullOrWhiteSpace(TxtAllShips.Text.Trim());
        BtnAnalyze.IsEnabled = lineupTouched && _minimapReady && myShipOk;
    }

    /// <summary>用户手动编辑输入框时刷新分析按钮状态</summary>
    private void TxtInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateAnalyzeButton();
    }

    // ===== 步骤③：分析 =====
    private async void BtnAnalyze_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null && !_cts.IsCancellationRequested)
        {
            TxtStatus.Text = "上一次分析仍在进行中";
            return;
        }

        var myShip = TxtMyShip.Text.Trim();
        if (string.IsNullOrEmpty(myShip))
        {
            MessageBox.Show("请指定你的战舰。", "提示");
            return;
        }
        if (_minimapImage == null)
        {
            MessageBox.Show("请先截取小地图。", "提示");
            return;
        }
        if (!_database.IsLoaded)
        {
            MessageBox.Show("战舰知识库未加载。", "提示");
            return;
        }

        _cts = new CancellationTokenSource();
        BtnAnalyze.IsEnabled = false;
        TxtStatus.Text = "构建知识库并调用 AI 分析中...";
        TxtResult.Text = "";

        try
        {
            // 1. 构建知识库（用扁平列表里所有舰船名，不分敌我）
            var allNames = ParseNames(TxtAllShips.Text);
            // 确保用户自己的船也在名单里（作为锚点）
            if (!string.IsNullOrWhiteSpace(myShip) &&
                !allNames.Any(n => string.Equals(n, myShip, StringComparison.OrdinalIgnoreCase)))
            {
                allNames.Insert(0, myShip);
            }
            var kbText = _database.BuildKnowledgeText(allNames);

            // 1.5 查询玩家战绩（shinoaki）：判真人/人机 + 提取 PR/胜率/伤害，供 AI 威胁评估
            string playerThreatText = "";
            if (_playerShipPairs.Count > 0)
            {
                TxtStatus.Text = "查询玩家战绩中（判真人/人机）...";
                try
                {
                    var progress = new Progress<string>(s => TxtStatus.Text = s);
                    var infos = await ShinoakiApiClient.AssessPlayersAsync(
                        _playerShipPairs, _settings.Server, progress, _cts.Token);
                    playerThreatText = BuildPlayerThreatText(infos);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 战绩查询失败不阻塞主流程，降级为无战绩数据（AI 回退看玩家名判断）
                    playerThreatText = "";
                    TxtStatus.Text = "玩家战绩查询失败（已降级）: " + ex.Message;
                }
            }

            // 2. 调 AI 分析（阵容图 + 小地图两张图一起发，敌我由 AI 看阵容图判断）
            var analyzer = AIAnalyzerFactory.Create(_settings);
            var req = new BattleAnalysisRequest
            {
                MinimapImage = _minimapImage,
                ImageBase64 = ScreenCaptureService.EncodeToBase64(_minimapImage),
                LineupImage = _lineupImage,
                LineupImageBase64 = _lineupImage != null ? ScreenCaptureService.EncodeToBase64(_lineupImage) : "",
                MyShip = myShip,
                AllShips = string.Join("、", allNames),
                KnowledgeBaseText = kbText,
                PlayerThreatText = playerThreatText,
                SystemPrompt = _settings.SystemPrompt
            };

            var result = await analyzer.AnalyzeAsync(req, _cts.Token);

            if (result.Success)
            {
                TxtResult.Text = result.Content;
                TxtStatus.Text = $"分析完成 · {result.ProviderName}";
                TxtFooter.Text = $"用时 {result.Elapsed.TotalSeconds:0.0}s · 知识库命中 {HitCount(kbText)} 艘 · 模型 {result.ProviderName}";
            }
            else
            {
                TxtResult.Text = "❌ 分析失败：" + result.Error;
                TxtStatus.Text = "分析失败";
                TxtFooter.Text = "";
            }
        }
        catch (Exception ex)
        {
            TxtResult.Text = "❌ 发生异常：" + ex.Message;
            TxtStatus.Text = "异常";
        }
        finally
        {
            UpdateAnalyzeButton();
            _cts = null; // ★ 必须复位
        }
    }

    /// <summary>解析输入的舰船名称（支持 逗号/顿号/换行/空格分隔）</summary>
    private static List<string> ParseNames(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.Split(new[] { ',', '，', '、', '\n', '\r', ' ' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private static int HitCount(string kbText)
    {
        return kbText.Count(c => c == '【');
    }

    /// <summary>把玩家威胁评估结果格式化为给 AI 的文本块</summary>
    private static string BuildPlayerThreatText(List<PlayerThreatInfo> infos)
    {
        if (infos.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("=== 玩家威胁评估（来自 shinoaki 联网查询，提供搜索结果与战绩数据供 AI 判断）===");
        foreach (var info in infos)
            sb.AppendLine(info.ToAiLine());
        return sb.ToString();
    }

    /// <summary>
    /// 把识别到的舰船名用知识库过滤：只保留真实存在的舰船，并归一化为数据库标准名。
    /// 不去重——双方同型舰会出现两次，保留以反映真实阵容（知识库构建时会自行去重参数）。
    /// 带长度校验：若匹配靠"包含"且长度差过大，视为用户名误匹配而剔除
    /// （例如用户名"YamatoFan"碰巧包含船名"Yamato"，会被排除）。
    /// 保留等级前缀（如"VII 沙恩霍斯特"）以区分重名舰船。
    /// </summary>
    private List<string> FilterToKnownShips(List<string> recognized, out int dropped)
    {
        dropped = 0;
        var result = new List<string>();
        foreach (var raw in recognized)
        {
            if (string.IsNullOrWhiteSpace(raw)) { dropped++; continue; }
            var ship = _database.TryGetShip(raw);
            if (ship == null) { dropped++; continue; }
            var name = ship["name"]?.ToString()?.Trim() ?? raw.Trim();
            var vlevel = ship["vlevel"]?.ToString()?.Trim() ?? "";

            // 归一化：如果原始输入带等级前缀，保留"vlevel name"格式以区分重名舰船
            var canonical = !string.IsNullOrEmpty(vlevel) && raw.Trim().StartsWith(vlevel, StringComparison.OrdinalIgnoreCase)
                ? $"{vlevel} {name}"
                : name;

            // 长度差校验：去掉等级前缀后比较，防止"包含"误匹配
            var rawPure = Regex.Replace(raw.Trim(), @"^[IVXLCDM]+\s+", "");
            if (Math.Abs(name.Length - rawPure.Length) > 2) { dropped++; continue; }
            result.Add(canonical); // 保留重复（双方同型舰）
        }
        return result;
    }
}
