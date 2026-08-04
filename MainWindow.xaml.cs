using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WoWSBattleAssistant.Models;
using WoWSBattleAssistant.Services;
using WoWSBattleAssistant.Services.AI;
using WoWSBattleAssistant.Services.Shinoaki;
using WoWSBattleAssistant.Views;

namespace WoWSBattleAssistant;

/// <summary>
/// 悬浮窗主窗口。融合式流程：
/// ① 阵容自动检测（读取 tempArenaInfo.json，100%准确，零延迟）
///    └ 降级方案：手动截阵容 + AI 视觉识别
/// ② 截小地图（手动，游戏不暴露地图数据）
/// ③ AI 综合分析（知识库 + 战绩查询 + 小地图图）
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ShipDatabase _database = new();
    private readonly GameFileMonitor _fileMonitor = new();
    private DispatcherTimer? _pollingTimer;
    private CancellationTokenSource? _cts;

    // 步骤①②的状态
    private BitmapSource? _lineupImage;
    private BitmapSource? _minimapImage;
    private bool _lineupReady;
    private bool _minimapReady;

    /// <summary>阵容数据（来自自动检测或 AI 识别）</summary>
    private List<PlayerShipPair> _playerShipPairs = new();

    /// <summary>是否由自动检测填充了阵容（true=无需 AI 验证，数据 100%准确）</summary>
    private bool _lineupFromAutoDetect;

    /// <summary>当前是否为自动模式</summary>
    private bool _isAutoMode = true;

    /// <summary>精简模式（仅显示结果区和必要按钮）</summary>
    private bool _compactMode;

    /// <summary>自动检测到的新对局暂存（等待知识库加载后再处理）</summary>
    private BattleDetectionResult? _pendingDetection;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        _isAutoMode = _settings.AutoDetectLineup;
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

        AppLog.Info($"WoWS Battle Assistant V2.1 启动 | 模式: {(_isAutoMode ? "自动" : "手动")} | 游戏路径: {_settings.GamePath}");

        UpdateStatusBar();
        UpdateModeUI();
        await LoadDatabaseAsync();

        if (_isAutoMode) StartAutoDetection();
    }

    // ===== 模式切换 =====

    private void UpdateModeUI()
    {
        TglAuto.IsChecked = _isAutoMode;
        TglManual.IsChecked = !_isAutoMode;

        if (_isAutoMode)
        {
            AutoLineupPanel.Visibility = Visibility.Visible;
            BtnCaptureLineup.Visibility = Visibility.Collapsed;
            TxtLineupTitle.Text = "双方阵容（自动检测）";
            TxtModeHint.Text = string.IsNullOrWhiteSpace(_settings.GamePath)
                ? "⚠ 未配置游戏目录" : "";
            TxtModeHint.Foreground = string.IsNullOrWhiteSpace(_settings.GamePath)
                ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.Green;
        }
        else
        {
            AutoLineupPanel.Visibility = Visibility.Collapsed;
            BtnCaptureLineup.Visibility = Visibility.Visible;
            TxtLineupTitle.Text = "双方阵容（手动截取）";
            TxtModeHint.Text = "手动模式：截图+AI识别";
        }

        // 小地图按钮状态
        var hasRegion = !_settings.MinimapRegion.IsEmpty;
        TxtMinimapHint.Text = hasRegion
            ? "✅ 区域已设，点击按钮即可截取（战局变化时可反复截取重新分析）"
            : "⚠ 请先在⚙设置中框选小地图位置（仅需设置一次）";
    }

    private void TglAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_isAutoMode) { TglAuto.IsChecked = true; return; }
        _isAutoMode = true;
        _settings.AutoDetectLineup = true;
        SettingsStore.Save(_settings);
        UpdateModeUI();
        StartAutoDetection();
    }

    private void TglManual_Click(object sender, RoutedEventArgs e)
    {
        if (!_isAutoMode) { TglManual.IsChecked = true; return; }
        _isAutoMode = false;
        _settings.AutoDetectLineup = false;
        SettingsStore.Save(_settings);
        // 彻底停止并清理定时器
        if (_pollingTimer != null)
        {
            _pollingTimer.Stop();
            _pollingTimer.Tick -= OnPollingTimer;
            _pollingTimer = null;
        }
        UpdateModeUI();
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

            // 知识库加载完成后，如果有待处理的自动检测结果，立即处理
            if (_pendingDetection != null)
            {
                ApplyLineupDetection(_pendingDetection);
                _pendingDetection = null;
            }
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

    // ===== 自动检测 =====

    /// <summary>启动自动检测定时器（每秒轮询 tempArenaInfo.json）</summary>
    private void StartAutoDetection()
    {
        if (!_isAutoMode) return;

        if (string.IsNullOrWhiteSpace(_settings.GamePath) || !Directory.Exists(_settings.GamePath))
        {
            TxtAutoStatus.Text = "⚠ 未配置游戏目录，请点⚙设置 → 填入游戏安装路径（如 C:\\Games\\World_of_Warships_CN）";
            TxtStatus.Text = "请先设置游戏目录";
            return;
        }

        // 检查是否已在运行（用 IsEnabled 判断，因为 Stop() 后对象还在但不为 null）
        if (_pollingTimer?.IsEnabled == true) return;

        // 清理旧的停用定时器
        if (_pollingTimer != null)
        {
            _pollingTimer.Stop();
            _pollingTimer.Tick -= OnPollingTimer;
        }

        _pollingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollingTimer.Tick += OnPollingTimer;
        _pollingTimer.Start();

        TxtAutoStatus.Text = "🔄 自动检测中…等待进入对局";
        TxtStatus.Text = "自动检测已启动（500ms 轮询）";
    }

    private int _pollCount;

    private void OnPollingTimer(object? sender, EventArgs e)
    {
        // 如果正在手动操作中，跳过自动检测
        if (_cts != null && !_cts.IsCancellationRequested) return;

        try
        {
            var latestFile = _fileMonitor.GetLatestTempArenaInfoFile(_settings.GamePath);
            if (string.IsNullOrEmpty(latestFile))
            {
                // 心跳指示：仅在没有任何检测结果（成功或失败）时才轮转
                _pollCount++;
                if (!_lineupReady && _pollCount % 4 == 0 &&
                    TxtAutoStatus.Text.StartsWith("🔄"))
                    TxtAutoStatus.Text = "🔄 自动检测中…等待进入对局";
                return;
            }

            var result = _fileMonitor.ParseTempArenaInfo(latestFile);
            if (!result.Success)
            {
                if (!_lineupReady)
                    TxtAutoStatus.Text = $"⚠ 检测到对局文件但解析失败：{result.Error ?? "未知"}";
                return;
            }

            // 知识库未加载完则暂存
            if (!_database.IsLoaded)
            {
                _pendingDetection = result;
                TxtAutoStatus.Text = "⏳ 检测到对局，等待知识库加载完成…";
                return;
            }

            ApplyLineupDetection(result);
        }
        catch (Exception ex)
        {
            TxtAutoStatus.Text = $"⚠ 自动检测异常: {ex.Message}";
        }
    }

    /// <summary>将自动检测到的阵容数据填充到 UI</summary>
    private void ApplyLineupDetection(BattleDetectionResult detection)
    {
        // 自动检测游戏所在服务器
        var autoServer = GameFileMonitor.AutoDetectServer(_settings.GamePath);
        if (!string.IsNullOrEmpty(autoServer) && _settings.Server != autoServer)
        {
            _settings.Server = autoServer;
            SettingsStore.Save(_settings);
        }

        // 构建 PlayerShipPair 列表
        _playerShipPairs = new List<PlayerShipPair>();
        var allShipNames = new List<string>();
        string? myShip = null;

        foreach (var p in detection.Players)
        {
            var displayName = _database.GetShipDisplayName(p.ShipId);
            var pair = new PlayerShipPair
            {
                Player = p.PlayerName,
                Ship = displayName,
                Relation = p.Relation
            };
            _playerShipPairs.Add(pair);
            allShipNames.Add(displayName);

            // relation=0 就是玩家自己
            if (p.Relation == 0)
            {
                myShip = displayName;
            }
        }

        // 填充 UI
        var uniqueShips = allShipNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        CboMyShip.Items.Clear();
        foreach (var name in uniqueShips) CboMyShip.Items.Add(name);

        TxtAllShips.Text = string.Join("、", uniqueShips);

        if (myShip != null)
        {
            TxtMyShip.Text = myShip;
            CboMyShip.SelectedItem = myShip;
        }
        else
        {
            TxtMyShip.Text = "";
        }

        _lineupReady = true;
        _lineupFromAutoDetect = true;
        _lineupImage = null;

        AppLog.Info($"自动检测到新对局: {detection.BattleType}, {detection.Players.Count} 名玩家, {uniqueShips.Count} 种舰船{(myShip != null ? $", 我的船={myShip}" : "")}");

        TxtAutoStatus.Text = $"✅ 自动检测到新对局！{uniqueShips.Count} 种舰船，{detection.Players.Count} 名玩家" +
            (myShip != null ? $" | 你的船: {myShip}" : " | 未识别到自己的船");
        TxtLineupStatus.Text = $"✅ 自动检测 {detection.Players.Count} 名玩家，{uniqueShips.Count} 种舰船" +
            (myShip != null ? $"（已自动选中: {myShip}）" : "（请从下拉框选你的船）");
        TxtStatus.Text = $"检测到新对局 ({detection.BattleType})";
        ImgLineupPreview.Source = null;
        TxtLineupPlaceholder.Visibility = Visibility.Visible;

        UpdateAnalyzeButton();
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
        _pollingTimer?.Stop();
        Close();
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        _pollingTimer?.Stop();
        var dlg = new SettingsWindow(_settings, _database) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            UpdateStatusBar();
            UpdateModeUI();
            if (!_database.IsLoaded)
            {
                _ = LoadDatabaseAsync();
            }
        }
        if (_isAutoMode) StartAutoDetection();
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _lineupImage = null;
        _minimapImage = null;
        _lineupReady = false;
        _minimapReady = false;
        _lineupFromAutoDetect = false;
        _playerShipPairs = new();
        _pendingDetection = null;

        TxtMyShip.Text = "";
        TxtAllShips.Text = "";
        CboMyShip.Items.Clear();

        ImgLineupPreview.Source = null;
        TxtLineupPlaceholder.Visibility = Visibility.Visible;
        TxtLineupStatus.Text = "未获取";

        ImgMinimapPreview.Source = null;
        TxtMinimapPlaceholder.Visibility = Visibility.Visible;
        TxtMinimapStatus.Text = "未截取";

        TxtResult.Document.Blocks.Clear();
        TxtResult.Document.Blocks.Add(new Paragraph(new Run("等待分析...") { Foreground = System.Windows.Media.Brushes.Gray }));
        TxtFooter.Text = "";
        TxtStatus.Text = "已清空";
        UpdateAnalyzeButton();

        if (_isAutoMode && _pollingTimer != null)
            TxtAutoStatus.Text = "🔄 自动检测中…等待进入对局";
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(GetResultPlainText()); TxtFooter.Text = "已复制结果到剪贴板"; }
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
        TxtFooter.Visibility = vis;
    }

    private void BtnCompact_Click(object sender, RoutedEventArgs e)
    {
        _compactMode = !_compactMode;
        BtnCompact.Content = _compactMode ? "🗖" : "🗜";
        BtnCompact.ToolTip = _compactMode ? "完整模式" : "精简模式";

        if (_compactMode)
        {
            ModeBar.Visibility = Visibility.Collapsed;
            LineupCard.Visibility = Visibility.Collapsed;
            MinimapCard.Visibility = Visibility.Collapsed;
            Height = 380;
            MinHeight = 280;
        }
        else
        {
            ModeBar.Visibility = Visibility.Visible;
            LineupCard.Visibility = Visibility.Visible;
            MinimapCard.Visibility = Visibility.Visible;
            Height = 760;
            MinHeight = 600;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return;
        e.Handled = true;
    }

    // ===== 步骤①：手动截阵容（降级方案）=====
    private async void BtnCaptureLineup_Click(object sender, RoutedEventArgs e)
    {
        if (_lineupFromAutoDetect)
        {
            // 已有自动检测数据，提示用户
            MessageBox.Show("阵容已通过自动检测获取（100%准确），无需手动截图。\n如需重新识别，请先点「清空」。", "提示");
            return;
        }

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
            // 暂停自动检测
            _pollingTimer?.Stop();

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

            _lineupImage = shot;
            ImgLineupPreview.Source = shot;
            TxtLineupPlaceholder.Visibility = shot == null ? Visibility.Visible : Visibility.Collapsed;
            TxtLineupStatus.Text = "截图完成，正在调 AI 识别舰船名...";

            // 调 AI 识别（降级方案）
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

            _lineupFromAutoDetect = false;
            _playerShipPairs = rec.PlayerShipPairs ?? new();
            var recognized = rec.Ships;
            List<string> filtered;
            if (_database.IsLoaded)
            {
                filtered = FilterToKnownShips(recognized, out int dropped);
                if (filtered.Count == 0 && recognized.Count > 0)
                {
                    filtered = recognized;
                    TxtLineupStatus.Text = $"⚠️ 识别到 {recognized.Count} 个名称但无一命中知识库，已保留原值供你手动修正。";
                }
                else
                {
                    TxtLineupStatus.Text = $"✅ 手动识别 {recognized.Count} 项，命中 {filtered.Count} 艘真实舰船" +
                        (dropped > 0 ? $"（剔除 {dropped} 个非舰船名/用户名）" : "") + "。请从下拉框指定你的战舰。";
                }
            }
            else
            {
                filtered = recognized;
                TxtLineupStatus.Text = $"✅ 手动识别到 {recognized.Count} 项。请从下拉框指定你的战舰。";
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
            _cts = null;
            if (_isAutoMode) StartAutoDetection();
        }
    }

    private void CboMyShip_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (CboMyShip.SelectedItem == null) return;
        ApplyMyShipSelection(CboMyShip.SelectedItem.ToString() ?? "");
    }

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
        var detectTag = _lineupFromAutoDetect ? "（自动检测，100%准确）" : "（由 AI 看阵容图判断）";
        TxtLineupStatus.Text = $"✅ 已指定你的战舰: {shipName}{detectTag}";
        _lineupReady = true;
        UpdateAnalyzeButton();
    }

    // ===== 步骤②：小地图（手动触发，使用已保存的区域）=====
    private void BtnCaptureMinimap_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.MinimapRegion.IsEmpty)
        {
            // 区域未设置，引导到设置面板
            var result = MessageBox.Show("小地图区域尚未设置。\n\n是否现在去设置中框选？（只需设置一次）", "提示",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
                BtnSettings_Click(sender, e);
            return;
        }

        try
        {
            var region = _settings.MinimapRegion;
            var shot = ScreenCaptureService.CaptureRegion(region);
            _minimapImage = shot;
            ImgMinimapPreview.Source = shot;
            TxtMinimapPlaceholder.Visibility = shot == null ? Visibility.Visible : Visibility.Collapsed;
            TxtMinimapStatus.Text = $"✅ 已截取小地图 ({(int)region.Width}×{(int)region.Height}) — 可随时重新截取";
            TxtMinimapHint.Text = "✅ 区域已设，点击按钮即可重新截取（战局变化时可反复截取）";
            _minimapReady = true;
            UpdateAnalyzeButton();
        }
        catch (Exception ex)
        {
            TxtMinimapStatus.Text = "❌ 截取异常：" + ex.Message;
        }
    }

    private void UpdateAnalyzeButton()
    {
        var myShipOk = !string.IsNullOrWhiteSpace(TxtMyShip.Text.Trim());
        var lineupTouched = _lineupReady
            || !string.IsNullOrWhiteSpace(TxtMyShip.Text.Trim())
            || !string.IsNullOrWhiteSpace(TxtAllShips.Text.Trim());
        BtnAnalyze.IsEnabled = lineupTouched && _minimapReady && myShipOk;
    }

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
        TxtStatus.Text = "构建知识库并查询玩家战绩...";
        TxtResult.Document.Blocks.Clear();
        TxtResult.Document.Blocks.Add(new Paragraph(new Run("") { Foreground = System.Windows.Media.Brushes.White }));

        try
        {
            // 1. 构建知识库
            var allNames = ParseNames(TxtAllShips.Text);
            if (!string.IsNullOrWhiteSpace(myShip) &&
                !allNames.Any(n => string.Equals(n, myShip, StringComparison.OrdinalIgnoreCase)))
            {
                allNames.Insert(0, myShip);
            }
            var kbText = _database.BuildKnowledgeText(allNames);
            AppLog.Info($"开始分析 | 我的船: {myShip} | 舰船数: {allNames.Count} | API: {_settings.ApiBackend}");

            // 2. 查询玩家战绩（按配置选择 API 后端）
            string playerThreatText = "";
            if (_playerShipPairs.Count > 0)
            {
                TxtStatus.Text = $"查询玩家战绩中（{_settings.ApiBackend}）...";
                try
                {
                    var progress = new Progress<string>(s => TxtStatus.Text = s);
                    playerThreatText = await QueryPlayerStatsAsync(progress, _cts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    playerThreatText = "";
                    TxtStatus.Text = "玩家战绩查询失败（已降级）: " + ex.Message;
                }
            }

            // 3. 调 AI 分析
            var analyzer = AIAnalyzerFactory.Create(_settings);
            var minimapBase64 = ScreenCaptureService.EncodeToBase64(_minimapImage);
            string lineupBase64 = "";
            if (_lineupImage != null)
            {
                lineupBase64 = ScreenCaptureService.EncodeToBase64(_lineupImage);
            }

            var req = new BattleAnalysisRequest
            {
                MinimapImage = _minimapImage,
                ImageBase64 = minimapBase64,
                LineupImage = _lineupImage,
                LineupImageBase64 = lineupBase64,
                MyShip = myShip,
                AllShips = string.Join("、", allNames),
                KnowledgeBaseText = kbText,
                PlayerThreatText = playerThreatText,
                SystemPrompt = _settings.SystemPrompt,
                LineupFromAutoDetect = _lineupFromAutoDetect,
                // 流式回调：AI 生成一段就立刻异步追加到结果框
                OnStreamChunk = chunk =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        // 仅在用户处于底部时自动滚动
                        var atBottom = TxtResult.VerticalOffset >=
                            TxtResult.ExtentHeight - TxtResult.ViewportHeight - 20;
                        AppendText(chunk);
                        if (atBottom)
                            TxtResult.ScrollToEnd();
                    });
                },
            };

            TxtStatus.Text = "AI 分析中（流式输出）...";
            var result = await analyzer.AnalyzeAsync(req, _cts.Token);

            if (result.Success)
            {
                // 流式输出时已逐段写入，这里做最终格式化
                FormatResultAsMarkdown();
                var detectTag = _lineupFromAutoDetect ? "自动检测" : "AI识别";
                TxtStatus.Text = $"分析完成 · {result.ProviderName}";
                TxtFooter.Text = $"用时 {result.Elapsed.TotalSeconds:0.0}s · 知识库命中 {HitCount(kbText)} 艘 · 阵容{detectTag} · 模型 {result.ProviderName}";
            }
            else
            {
                SetResultError("❌ 分析失败：" + result.Error);
                TxtStatus.Text = "分析失败";
                TxtFooter.Text = "";
            }
        }
        catch (Exception ex)
        {
            SetResultError("❌ 发生异常：" + ex.Message);
            TxtStatus.Text = "异常";
        }
        finally
        {
            UpdateAnalyzeButton();
            _cts = null;
        }
    }

    /// <summary>根据配置选择 API 后端查询玩家战绩</summary>
    private async Task<string> QueryPlayerStatsAsync(IProgress<string> progress, CancellationToken ct)
    {
        List<PlayerThreatInfo> infos;

        switch (_settings.ApiBackend)
        {
            case ApiBackend.WgPublic:
            case ApiBackend.WgPublicYuyuko:
                var useProxy = _settings.ApiBackend == ApiBackend.WgPublicYuyuko;
                infos = await WgApiClient.AssessPlayersAsync(
                    _playerShipPairs, _settings.Server, _settings.WgApplicationId,
                    useProxy, progress, ct);
                break;
            case ApiBackend.Shinoaki:
            default:
                infos = await ShinoakiApiClient.AssessPlayersAsync(
                    _playerShipPairs, _settings.Server, progress, ct);
                break;
        }

        return BuildPlayerThreatText(infos);
    }

    private static List<string> ParseNames(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text.Split(new[] { ',', '，', '、', '\n', '\r', ' ' },
                StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
    }

    private static int HitCount(string kbText) => kbText.Count(c => c == '【');

    private static string BuildPlayerThreatText(List<PlayerThreatInfo> infos)
    {
        if (infos.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("=== 玩家威胁评估（来自战绩 API 联网查询，玩家名与舰船名由游戏数据精确解析）===");
        sb.AppendLine("阵营：[自己] [队友] [敌方] —— 由游戏内部数据确定，100%准确。");
        sb.AppendLine();
        foreach (var info in infos)
            sb.AppendLine(info.ToAiLine());
        return sb.ToString();
    }

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

            var canonical = !string.IsNullOrEmpty(vlevel) && raw.Trim().StartsWith(vlevel, StringComparison.OrdinalIgnoreCase)
                ? $"{vlevel} {name}"
                : name;

            var rawPure = Regex.Replace(raw.Trim(), @"^[IVXLCDM]+\s+", "");
            if (Math.Abs(name.Length - rawPure.Length) > 2) { dropped++; continue; }
            result.Add(canonical);
        }
        return result;
    }

    // ===== RichText 输出辅助 =====

    /// <summary>往结果 RichTextBox 末尾追加文本</summary>
    private void AppendText(string text)
    {
        var doc = TxtResult.Document;
        var last = doc.Blocks.LastBlock as Paragraph;
        if (last == null)
        {
            last = new Paragraph();
            doc.Blocks.Add(last);
        }
        last.Inlines.Add(new Run(text)
        {
            Foreground = System.Windows.Media.Brushes.LightGray
        });
    }

    /// <summary>设置错误文本</summary>
    private void SetResultError(string text)
    {
        TxtResult.Document.Blocks.Clear();
        TxtResult.Document.Blocks.Add(new Paragraph(new Run(text)
        {
            Foreground = System.Windows.Media.Brushes.OrangeRed,
            FontSize = 13
        }));
    }

    /// <summary>提取纯文本（用于剪贴板）</summary>
    private string GetResultPlainText()
    {
        var range = new TextRange(TxtResult.Document.ContentStart, TxtResult.Document.ContentEnd);
        return range.Text;
    }

    /// <summary>清除结果区并写入纯文本</summary>
    private void SetResultPlain(string text)
    {
        TxtResult.Document.Blocks.Clear();
        TxtResult.Document.Blocks.Add(new Paragraph(new Run(text)
        {
            Foreground = System.Windows.Media.Brushes.LightGray
        }));
    }

    /// <summary>把当前结果区内容按 Markdown 风格格式化</summary>
    private void FormatResultAsMarkdown()
    {
        var raw = GetResultPlainText().Trim();
        if (string.IsNullOrEmpty(raw)) return;

        TxtResult.Document.Blocks.Clear();
        var doc = TxtResult.Document;

        // 按双换行分段
        var paragraphs = raw.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var para in paragraphs)
        {
            var trimmed = para.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var p = new Paragraph { Margin = new Thickness(0, 2, 0, 6) };

            // 检测节标题: "1.【...】" "2.【...】" 等
            var headerMatch = Regex.Match(trimmed, @"^(\d+)\.【(.+?)】");
            if (headerMatch.Success)
            {
                p.Inlines.Add(new Run($"{headerMatch.Groups[1].Value}.【{headerMatch.Groups[2].Value}】")
                {
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7))
                });
                // 标题后面的内容
                var rest = trimmed[headerMatch.Length..].TrimStart('\r', '\n', ' ');
                if (!string.IsNullOrEmpty(rest))
                    p.Inlines.Add(new Run("\n" + rest) { Foreground = System.Windows.Media.Brushes.LightGray });
                doc.Blocks.Add(p);
                continue;
            }

            // 检测子标题: "  - ..."
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("  - "))
            {
                var content = Regex.Replace(trimmed, @"^\s*-\s*", "");
                p.Inlines.Add(new Run("• " + content) { Foreground = System.Windows.Media.Brushes.LightGray });
                p.Margin = new Thickness(20, 1, 0, 2);
                doc.Blocks.Add(p);
                continue;
            }

            // 检测编号列表项: "  1. ..."
            var numMatch = Regex.Match(trimmed, @"^\s*(\d+)[\.\、\)]\s*(.+)");
            if (numMatch.Success)
            {
                p.Inlines.Add(new Run($"{numMatch.Groups[1].Value}. {numMatch.Groups[2].Value}")
                { Foreground = System.Windows.Media.Brushes.LightGray });
                p.Margin = new Thickness(20, 1, 0, 2);
                doc.Blocks.Add(p);
                continue;
            }

            // 高亮关键词: **加粗** 转为 Bold
            var parts = Regex.Split(trimmed, @"(\*\*.*?\*\*)");
            foreach (var part in parts)
            {
                if (part.StartsWith("**") && part.EndsWith("**"))
                {
                    var inner = part[2..^2];
                    p.Inlines.Add(new Run(inner) { FontWeight = FontWeights.Bold,
                        Foreground = System.Windows.Media.Brushes.White });
                }
                else
                {
                    p.Inlines.Add(new Run(part) { Foreground = System.Windows.Media.Brushes.LightGray });
                }
            }
            doc.Blocks.Add(p);
        }
    }
}
