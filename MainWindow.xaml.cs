using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
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
    private VoiceController? _voiceController;
    private DispatcherTimer? _pollingTimer;
    private CancellationTokenSource? _cts;

    // 流式输出缓冲（减少 RichTextBox layout 次数，参考 DeepSeek rAF 批处理）
    private readonly StringBuilder _appendBuffer = new();
    private readonly DispatcherTimer _appendTimer;

    // 步骤①②的状态
    private BitmapSource? _lineupImage;
    private BitmapSource? _minimapImage;
    private string? _latestMinimapBase64;
    private bool _lineupReady;
    private bool _minimapReady;

    /// <summary>阵容数据（来自自动检测或 AI 识别）</summary>
    private List<PlayerShipPair> _playerShipPairs = new();

    /// <summary>双方战力对比悬浮窗</summary>
    private Views.PowerOverlayWindow? _powerOverlay;

    /// <summary>是否由自动检测填充了阵容（true=无需 AI 验证，数据 100%准确）</summary>
    private bool _lineupFromAutoDetect;

    /// <summary>当前是否为自动模式</summary>
    private bool _isAutoMode = true;

    /// <summary>精简模式（仅显示结果区和必要按钮）</summary>
    private bool _compactMode;

    /// <summary>当前是否显示详细版分析</summary>
    private bool _showDetail;
    private string _briefText = "";
    private string _detailText = "";

    /// <summary>流式累积的全文</summary>
    private string _streamBuffer = "";

    /// <summary>流式累积的详情部分（--- 之后）</summary>
    private string _detailStream = "";

    /// <summary>是否已在流中检测到 --- 分隔符</summary>
    private bool _separatorFound;

    /// <summary>分析/追问进行中，防止按钮提前恢复</summary>
    private bool _analysisInProgress;

    /// <summary>多轮对话上下文</summary>
    private ConversationContext? _conversation;

    /// <summary>当前对局的标识（tempArenaInfo 的 battleStartTime），用于判断是否同局，同局复用对话上下文</summary>
    private string? _currentBattleKey;

    /// <summary>自动检测到的新对局暂存（等待知识库加载后再处理）</summary>
    private BattleDetectionResult? _pendingDetection;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        _isAutoMode = _settings.AutoDetectLineup;

        // 自动修复安装版数据库路径：如果默认路径不存在，从 exe 同目录找
        if (!File.Exists(_settings.ShipDataPath))
        {
            var exeDir = AppContext.BaseDirectory;
            var candidates = Directory.GetFiles(exeDir, "wows_ships_data_*.json").OrderByDescending(f => f).ToList();
            if (candidates.Count > 0)
            {
                _settings.ShipDataPath = candidates[0];
                SettingsStore.Save(_settings);
            }
        }

        // 全局未处理异常：防止闪退无提示
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            AppLog.Error($"致命崩溃: {ex?.Message}\n{ex?.StackTrace}");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[{DateTime.Now}] {ex?.Message}\n{ex?.StackTrace}");
        };
        Dispatcher.UnhandledException += (_, args) =>
        {
            args.Handled = true;
            var ex = args.Exception;
            AppLog.Error($"UI 线程异常: {ex.Message}\n{ex.StackTrace}");
            MessageBox.Show($"软件遇到错误:\n{ex.Message}\n\n详细信息已写入 crash.log，请发送给开发者。", "错误");
        };
        Loaded += MainWindow_Loaded;
        LocationChanged += MainWindow_LocationChanged;
        SizeChanged += MainWindow_LocationChanged;

        // 流式缓冲定时器 — 每 50ms 冲洗一次 RichTextBox，模拟 rAF 批处理
        _appendTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal,
            (_, _) => FlushAppendBuffer(), Dispatcher);
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
        InitVoiceControl();
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

            // 知识库未加载时仍可填充阵容（悬浮窗等非 AI 功能不需要知识库）
            if (!_database.IsLoaded)
            {
                TxtAutoStatus.Text = "✓ 检测到对局（知识库未加载，舰船名显示为原始 ID）";
            }
            else
            {
                TxtAutoStatus.Text = "✓ 检测到对局";
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

        // 判断是否新对局：tempArenaInfo 的 dateTime 变了就是新一局
        var newBattleKey = detection.BattleStartTime;
        if (string.IsNullOrEmpty(newBattleKey)) newBattleKey = Guid.NewGuid().ToString();
        if (_currentBattleKey != newBattleKey)
        {
            // 新对局 → 清空上一局的对话上下文，开始全新分析会话
            _conversation = null;
            FollowUpPanel.Visibility = Visibility.Collapsed;
            _currentBattleKey = newBattleKey;
            AppLog.Info($"新对局检测: {detection.BattleType}, 对话上下文已重置");
        }

        // 构建 PlayerShipPair 列表
        _playerShipPairs = new List<PlayerShipPair>();
        var allShipNames = new List<string>();
        string? myShip = null;

        foreach (var p in detection.Players)
        {
            string displayName;

            // 1) 优先使用 tempArenaInfo.json 本身提供的舰船名（如果有）
            if (!string.IsNullOrWhiteSpace(p.ShipRawName))
            {
                displayName = p.ShipRawName.Trim();
            }
            // 2) 知识库已加载 → 按 shipId 精确映射
            else if (_database.IsLoaded)
            {
                displayName = _database.GetShipDisplayName(p.ShipId);
            }
            // 3) 知识库未加载 → 用 "舰船(shipId)" 作为占位（悬浮窗和战绩查询不需要船名也能工作）
            else
            {
                displayName = $"舰船({p.ShipId})";
            }

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

        // 战力悬浮窗：设置开启时，本局对局数据到达就自动创建并查询战绩。
        // 不能只调 RefreshPowerOverlayAsync（它在 _powerOverlay 为 null 时直接跳过），
        // 否则"开设置时还没进对局"的场景下悬浮窗永远不会出现。
        if (_settings.EnablePowerOverlay)
        {
            EnsurePowerOverlay();
            _ = RefreshPowerOverlayAsync();
        }
    }

    /// <summary>若战力悬浮窗可见，自动查询双方战绩并更新显示。
    /// 无论悬浮窗是设置开启还是 ⚔ 按钮手动开启，只要可见就自动刷新。</summary>
    private async Task RefreshPowerOverlayAsync()
    {
        try
        {
            // 悬浮窗不可见（未开启或已隐藏）则跳过
            if (_powerOverlay == null || !_powerOverlay.IsVisible) return;
            if (_playerShipPairs.Count == 0) return;

            var infos = await QueryPlayerStatsListAsync(null, CancellationToken.None);
            if (infos.Count > 0)
                _powerOverlay?.UpdatePower(infos);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"战力悬浮窗更新失败: {ex.Message}");
        }
    }

    /// <summary>按需创建并显示战力悬浮窗</summary>
    private void EnsurePowerOverlay()
    {
        if (_powerOverlay != null)
        {
            _powerOverlay.Show();
            return;
        }
        _powerOverlay = new Views.PowerOverlayWindow
        {
            Left = _settings.OverlayLeft,
            Top = _settings.OverlayTop
        };
        // 悬浮窗被隐藏（右键/设置关闭）时，主界面按钮同步复位
        _powerOverlay.IsVisibleChanged += (_, _) =>
        {
            if (_powerOverlay == null || !_powerOverlay.IsVisible)
                BtnOverlay.Background = Brushes.Transparent;
        };
        _powerOverlay.Closed += (_, _) =>
        {
            // 记住悬浮窗位置，下次开局恢复
            if (_powerOverlay != null)
            {
                _settings.OverlayLeft = _powerOverlay.Left;
                _settings.OverlayTop = _powerOverlay.Top;
                SettingsStore.Save(_settings);
            }
            _powerOverlay = null;
        };
        _powerOverlay.Show();
    }

    /// <summary>根据设置启停战力悬浮窗（设置面板关闭时调用）</summary>
    private void SyncPowerOverlay()
    {
        if (_settings.EnablePowerOverlay && _playerShipPairs.Count > 0)
        {
            EnsurePowerOverlay();
            _ = RefreshPowerOverlayAsync();
        }
        else
        {
            _powerOverlay?.Hide();
        }
    }

    /// <summary>主界面按钮：一键开关战力悬浮窗（不依赖设置项，手动随时用）</summary>
    private void BtnOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (_powerOverlay != null && _powerOverlay.IsVisible)
        {
            _powerOverlay.Hide();
            BtnOverlay.Background = Brushes.Transparent;
            return;
        }

        EnsurePowerOverlay();
        BtnOverlay.Background = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));

        if (_playerShipPairs.Count > 0)
        {
            TxtStatus.Text = "查询双方战力中...";
            _ = RefreshPowerOverlayAsync();
        }
        else
        {
            // 还没有对局数据：先让悬浮窗显示空态
            _powerOverlay?.UpdatePower(new List<PlayerThreatInfo>());
            TxtStatus.Text = "悬浮窗已显示，等待对局数据（自动检测到对局后自动填充）";
        }
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
        _voiceController?.Dispose();
        _powerOverlay?.Close();
        _powerOverlay = null;
        Close();
    }

    // ===== 语音控制 =====

    private void InitVoiceControl()
    {
        if (!_settings.EnableVoiceControl)
        {
            VoiceIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            _voiceController = new VoiceController(Dispatcher, _settings.VoiceConfidenceThreshold);
            _voiceController.StatusChanged += OnVoiceStatusChanged;
            _voiceController.CommandRecognized += HandleVoiceCommand;
            _voiceController.Start();
        }
        catch (Exception ex)
        {
            AppLog.Error("语音初始化失败", ex);
            TxtVoiceIndicator.Text = "🎤 错误";
            VoiceIndicator.Visibility = Visibility.Visible;
        }
    }

    private void OnVoiceStatusChanged(string status)
    {
        Dispatcher.BeginInvoke(() =>
        {
            VoiceIndicator.Visibility = Visibility.Visible;
            TxtVoiceIndicator.Text = "🎤 " + status;
            if (status.Contains("已启动"))
            {
                VoiceIndicator.Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x20));
                TxtVoiceIndicator.Foreground = Brushes.White;
            }
            else if (status.Contains("停止"))
            {
                VoiceIndicator.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x55));
                TxtVoiceIndicator.Foreground = Brushes.LightGray;
            }
            else if (status.Contains("失败") || status.Contains("未找到"))
            {
                VoiceIndicator.Background = new SolidColorBrush(Color.FromRgb(0x5E, 0x1B, 0x1B));
                TxtVoiceIndicator.Foreground = Brushes.White;
            }
        });
    }

    private void HandleVoiceCommand(string command, double confidence)
    {
        AppLog.Info($"执行语音指令: {command} (cf={confidence:0.00})");
        switch (command)
        {
            case "截小地图":
                AppLog.Info("  → 截取小地图");
                FlashButton(BtnCaptureMinimap);
                BtnCaptureMinimap_Click(this, new RoutedEventArgs());
                break;
            case "分析":
                if (!BtnAnalyze.IsEnabled) { AppLog.Info("  → 分析按钮已禁用，跳过"); return; }
                AppLog.Info("  → 开始分析");
                FlashButton(BtnAnalyze);
                BtnAnalyze_Click(this, new RoutedEventArgs());
                break;
            case "清空":
                AppLog.Info("  → 清空内容");
                FlashButton(BtnClear);
                BtnClear_Click(this, new RoutedEventArgs());
                break;
            case "切自动":
                if (!_isAutoMode) { AppLog.Info("  → 切换到自动模式"); TglAuto_Click(this, new RoutedEventArgs()); }
                else AppLog.Info("  → 已是自动模式，跳过");
                break;
            case "切手动":
                if (_isAutoMode) { AppLog.Info("  → 切换到手动模式"); TglManual_Click(this, new RoutedEventArgs()); }
                else AppLog.Info("  → 已是手动模式，跳过");
                break;
            case "精简": case "精简模式": case "迷你": case "迷你模式": case "简洁": case "简洁模式":
                if (!_compactMode) { AppLog.Info($"  → 进入紧凑模式 ({command})"); BtnCompact_Click(this, new RoutedEventArgs()); }
                else AppLog.Info("  → 已是紧凑模式，跳过");
                break;
            case "完整": case "完整模式":
                if (_compactMode) { AppLog.Info($"  → 退出紧凑模式 ({command})"); BtnCompact_Click(this, new RoutedEventArgs()); }
                else AppLog.Info("  → 已是完整模式，跳过");
                break;
            case "最小化":
                AppLog.Info($"  → 最小化 (当前状态: {WindowState})");
                WindowState = WindowState.Minimized;
                break;
            case "恢复":
                AppLog.Info($"  → 恢复 (当前状态: {WindowState}, 可见={IsVisible})");
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                break;
            case "开战力":
                AppLog.Info("  → 打开战力浮窗");
                BtnOverlay_Click(this, new RoutedEventArgs());
                break;
            case "关战力":
                if (_powerOverlay != null && _powerOverlay.IsVisible)
                { AppLog.Info("  → 关闭战力浮窗"); _powerOverlay.Hide(); BtnOverlay.Background = Brushes.Transparent; }
                else AppLog.Info("  → 战力浮窗未打开，跳过");
                break;
            case "设置":
                AppLog.Info("  → 打开设置");
                BtnSettings_Click(this, new RoutedEventArgs());
                break;
            case "关闭设置":
                AppLog.Info("  → 关闭设置");
                foreach (Window w in Application.Current.Windows)
                    if (w is SettingsWindow) { w.DialogResult = false; w.Close(); break; }
                break;
            case "看详细":
                if (!_showDetail && BtnToggleDetail.Visibility == Visibility.Visible)
                { AppLog.Info("  → 展开详情"); ShowDetail(); }
                else AppLog.Info($"  → 跳过 (showDetail={_showDetail}, btnVisible={BtnToggleDetail.Visibility})");
                break;
            case "看简略":
                if (_showDetail) { AppLog.Info("  → 收起详情"); ShowBrief(); }
                else AppLog.Info("  → 已是简略，跳过");
                break;
            case "复制":
                AppLog.Info("  → 复制结果");
                CopyResultToClipboard();
                break;
            case "发送":
                if (FollowUpPanel.Visibility == Visibility.Visible && BtnFollowUp.IsEnabled)
                { AppLog.Info("  → 发送消息"); BtnFollowUp_Click(this, new RoutedEventArgs()); }
                else AppLog.Info($"  → 跳过发送 (panelVisible={FollowUpPanel.Visibility}, btnEnabled={BtnFollowUp.IsEnabled})");
                break;
            default:
                AppLog.Warn($"未处理的语音指令: {command}");
                break;
        }
    }

    /// <summary>按钮闪烁反馈——使用 SetValue/ClearValue 避免锁死 local 值</summary>
    private async void FlashButton(Button btn)
    {
        btn.SetValue(Control.BackgroundProperty, Brushes.White);
        await Task.Delay(100);
        btn.ClearValue(Control.BackgroundProperty);
    }

    // ===== 多轮对话追问 =====

    private void StoreConversationContext(BattleAnalysisRequest req, BattleAnalysisResult result)
    {
        // 构建 OpenAI 兼容的消息历史
        var messages = new List<object>();
        var systemPrompt = string.IsNullOrWhiteSpace(req.SystemPrompt)
            ? "你是《战舰世界》资深战术助手。" : req.SystemPrompt;

        messages.Add(new { role = "system", content = systemPrompt });

        // 用户原始消息（含图片+文本）
        var contentList = new List<object>();
        if (!string.IsNullOrWhiteSpace(req.LineupImageBase64))
            contentList.Add(new { type = "image_url", image_url = new { url = $"data:image/png;base64,{req.LineupImageBase64}" } });
        contentList.Add(new { type = "image_url", image_url = new { url = $"data:image/png;base64,{req.ImageBase64}" } });
        contentList.Add(new { type = "text", text = BuildUserTextForHistory(req) });
        messages.Add(new { role = "user", content = contentList });

        // AI 回复
        messages.Add(new { role = "assistant", content = result.Content });

        _conversation = new ConversationContext
        {
            Messages = messages,
            SystemPrompt = systemPrompt,
            KnowledgeBaseText = req.KnowledgeBaseText,
            PlayerThreatText = req.PlayerThreatText,
        };
    }

    private string BuildUserTextForHistory(BattleAnalysisRequest req)
    {
        var sb = new StringBuilder();
        if (req.LineupFromAutoDetect)
            sb.AppendLine("分析本局。阵营数据由游戏内部文件精确解析。图片：阵容+小地图截图。");
        else
            sb.AppendLine("分析本局。图片：阵容面板截图+小地图截图。");
        sb.AppendLine($"我的战舰：{req.MyShip}");
        sb.AppendLine($"本局舰船：{req.AllShips}");
        if (!string.IsNullOrWhiteSpace(req.KnowledgeBaseText))
            sb.Append(req.KnowledgeBaseText);
        if (!string.IsNullOrWhiteSpace(req.PlayerThreatText))
            sb.Append(req.PlayerThreatText);
        return sb.ToString();
    }

    private void ShowFollowUpInput()
    {
        FollowUpPanel.Visibility = Visibility.Visible;
        TxtFollowUp.Text = "";
    }

    private void AddFollowUpSeparator(string question)
    {
        // 追问模式下隐藏简略/详细切换
        BtnToggleDetail.Visibility = Visibility.Collapsed;

        var doc = TxtResult.Document;
        // 一条淡色分隔线
        var sep = new Paragraph(new Run("── " + DateTime.Now.ToString("HH:mm") + " ──")
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)),
            FontSize = 10
        }) { Margin = new Thickness(0, 10, 0, 4) };
        doc.Blocks.Add(sep);
        // 用户提问
        var q = new Paragraph(new Run("💬 " + question)
        {
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.Bold,
            FontSize = 12
        }) { Margin = new Thickness(0, 2, 0, 4) };
        doc.Blocks.Add(q);
        // AI 回答开始
        doc.Blocks.Add(new Paragraph(new Run("🤖 ")
        {
            Foreground = System.Windows.Media.Brushes.LightGray,
            FontSize = 11
        }) { Margin = new Thickness(0, 0, 0, 2) });
    }

    private void TxtFollowUp_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            BtnFollowUp_Click(sender, e);
        }
    }

    private async void BtnFollowUp_Click(object sender, RoutedEventArgs e)
    {
        if (_conversation == null) return;
        var question = TxtFollowUp.Text.Trim();
        if (string.IsNullOrEmpty(question)) return;

        TxtFollowUp.IsEnabled = false;
        BtnFollowUp.IsEnabled = false;
        _analysisInProgress = true;
        _appendTimer.Start(); // 启动流式缓冲
        TxtStatus.Text = "追问中…";

        try
        {
            // 在结果区追加分隔线和提问
            AddFollowUpSeparator(question);

            var req = new BattleAnalysisRequest
            {
                FollowUpQuestion = question,
                Conversation = _conversation,
                // 如果有新的小地图截图，附带之
                ImageBase64 = _latestMinimapBase64 ?? "",
                OnStreamChunk = chunk => Dispatcher.BeginInvoke(() =>
                {
                    // 追加到结果区末尾
                    var doc = TxtResult.Document;
                    var last = doc.Blocks.LastBlock as Paragraph;
                    if (last == null || last.Inlines.Count == 0)
                    {
                        last = new Paragraph();
                        doc.Blocks.Add(last);
                    }
                    last.Inlines.Add(new Run(chunk) { Foreground = System.Windows.Media.Brushes.LightGray });
                    // 简单滚动
                    if (TxtResult.VerticalOffset >= TxtResult.ExtentHeight - TxtResult.ViewportHeight - 20)
                        TxtResult.ScrollToEnd();
                }),
            };

            var analyzer = AIAnalyzerFactory.Create(_settings);
            var result = await analyzer.AnalyzeAsync(req);

            if (result.Success)
            {
                // 更新对话历史
                _conversation.Messages.Add(new { role = "user", content = question });
                _conversation.Messages.Add(new { role = "assistant", content = result.Content });
                TxtStatus.Text = $"追问完成 · {result.ProviderName}";
            }
            else
            {
                var last = TxtResult.Document.Blocks.LastBlock as Paragraph;
                if (last != null)
                    last.Inlines.Add(new Run($"\n（追问失败: {result.Error}）") { Foreground = Brushes.OrangeRed });
                TxtStatus.Text = "追问失败";
            }
        }
        catch (Exception ex)
        {
            var last = TxtResult.Document.Blocks.LastBlock as Paragraph;
            if (last != null)
                last.Inlines.Add(new Run($"\n（追问异常: {ex.Message}）") { Foreground = Brushes.OrangeRed });
        }
        finally
        {
            _analysisInProgress = false;
            // 冲掉缓冲区中最后残留的文字（不再有 chunk 进来推动定时器）
            FlushAppendBuffer();
            _appendTimer.Stop();
            TxtFollowUp.IsEnabled = true;
            BtnFollowUp.IsEnabled = true;
            TxtFollowUp.Clear();
            UpdateAnalyzeButton();
            _cts = null;
        }
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        _pollingTimer?.Stop();

        // 语音保持运行，不中断——全局语音控制需要持续生效。
        // 记录设置前的语音状态，设置关闭后按需同步。
        var voiceWasEnabled = _settings.EnableVoiceControl;
        var prevThreshold = _settings.VoiceConfidenceThreshold;

        var dlg = new SettingsWindow(_settings, _database) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            UpdateStatusBar();
            UpdateModeUI();
            if (!_database.IsLoaded)
                _ = LoadDatabaseAsync();
        }
        // 语音控制：只在设置变化时才重启，否则保持运行不中断
        if (_settings.EnableVoiceControl)
        {
            if (!voiceWasEnabled || Math.Abs(_settings.VoiceConfidenceThreshold - prevThreshold) > 0.001)
            {
                _voiceController?.Dispose();
                _voiceController = null;
                InitVoiceControl();
            }
        }
        else
        {
            if (voiceWasEnabled)
            {
                _voiceController?.Dispose();
                _voiceController = null;
            }
        }
        if (_isAutoMode) StartAutoDetection();
        // 战力悬浮窗：设置关闭后按当前配置启停
        SyncPowerOverlay();
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        // 取消并清理可能遗留的操作（分析/截图），否则 OnPollingTimer 会一直被阻塞跳过自动检测
        try { _cts?.Cancel(); } catch { }
        _cts = null;

        // 复位自动检测监控时间戳，确保同一场对局清空后能立即重新检测
        _fileMonitor.ResetWatch();

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
        _conversation = null;
        FollowUpPanel.Visibility = Visibility.Collapsed;
        _currentBattleKey = null;
        UpdateAnalyzeButton();

        if (_isAutoMode && _pollingTimer != null)
            TxtAutoStatus.Text = "🔄 自动检测中…等待进入对局";
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        CopyResultToClipboard();
    }

    private void CopyResultToClipboard()
    {
        try
        {
            var text = GetResultPlainText().Trim();
            if (string.IsNullOrEmpty(text) || text == "等待分析...")
            {
                TxtFooter.Text = "暂无分析结果可复制";
                return;
            }
            Clipboard.SetText(text);
            TxtFooter.Text = "已复制结果到剪贴板 (Ctrl+C)";
        }
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
        // 文本框/密码框内输入不拦截
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox ||
            Keyboard.FocusedElement is System.Windows.Controls.PasswordBox)
            return;

        // Ctrl+C：如果有选中内容则走原生复制（只复制选中部分），否则复制全部结果
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (TxtResult.Selection.IsEmpty)
            {
                CopyResultToClipboard();
                e.Handled = true;
            }
            // 有选区时放行，让 RichTextBox 的原生 Ctrl+C 只复制选中内容
            return;
        }

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
            var msg = "小地图区域尚未设置。\n\n是否现在去设置中框选？（只需设置一次）";
            if (MessageBox.Show(msg, "提示", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                BtnSettings_Click(sender, e);
            return;
        }

        try
        {
            var region = _settings.MinimapRegion;
            var shot = ScreenCaptureService.CaptureRegion(region);
            if (shot == null)
            {
                TxtStatus.Text = "❌ 小地图截取失败（区域为空或屏幕不可用）";
                AppLog.Warn("小地图截取失败: CaptureRegion 返回 null");
                return;
            }
            _minimapImage = shot;
            // 缓存 Base64，追问时可用
            _latestMinimapBase64 = ScreenCaptureService.EncodeToBase64(shot);
            ImgMinimapPreview.Source = shot;
            TxtMinimapPlaceholder.Visibility = shot == null ? Visibility.Visible : Visibility.Collapsed;
            TxtMinimapStatus.Text = $"✅ 已截取小地图 ({(int)region.Width}×{(int)region.Height}) — 可随时重新截取";
            TxtMinimapHint.Text = "✅ 区域已设，点击按钮即可重新截取（战局变化时可反复截取）";
            _minimapReady = true;
            // 状态栏始终可见（精简模式也看得到），给出明确反馈
            TxtStatus.Text = $"✅ 已截取小地图 ({(int)region.Width}×{(int)region.Height}) {DateTime.Now:HH:mm:ss} — 可再喊「分析」";
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0xFF, 0x9E));
            AppLog.Info($"小地图已截取 {region.Width:0}x{region.Height:0} ({DateTime.Now:HH:mm:ss})");
            UpdateAnalyzeButton();
        }
        catch (Exception ex)
        {
            TxtMinimapStatus.Text = "❌ 截取异常：" + ex.Message;
            TxtStatus.Text = "❌ 截取异常：" + ex.Message;
            AppLog.Error("小地图截取异常", ex);
        }
    }

    private void UpdateAnalyzeButton()
    {
        var myShipOk = !string.IsNullOrWhiteSpace(TxtMyShip.Text.Trim());
        var lineupTouched = _lineupReady
            || !string.IsNullOrWhiteSpace(TxtMyShip.Text.Trim())
            || !string.IsNullOrWhiteSpace(TxtAllShips.Text.Trim());
        BtnAnalyze.IsEnabled = !_analysisInProgress && lineupTouched && _minimapReady && myShipOk;
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
        _analysisInProgress = true;
        BtnAnalyze.IsEnabled = false;
        _appendTimer.Start(); // 启动流式缓冲
        // 恢复状态栏默认颜色（截图成功时是绿色）
        TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xBD, 0xBD, 0xBD));
        TxtStatus.Text = "构建知识库并查询玩家战绩...";
        TxtResult.Document.Blocks.Clear();
        TxtResult.Document.Blocks.Add(new Paragraph(new Run("") { Foreground = System.Windows.Media.Brushes.White }));

        _streamBuffer = "";
        _separatorFound = false;

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

            // 重置流式状态
            _streamBuffer = "";
            _detailStream = "";
            _separatorFound = false;
            _briefText = "";
            _detailText = "";

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
                // 同局重复分析 → 复用对话上下文，AI 知道上次的分析结果
                Conversation = _conversation,
                // 流式回调：遇到 --- 分隔符后只缓存不显示，点击「详细」时才渲染
                OnStreamChunk = chunk =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        _streamBuffer += chunk;
                        var atBottom = TxtResult.VerticalOffset >=
                            TxtResult.ExtentHeight - TxtResult.ViewportHeight - 20;

                        if (!_separatorFound)
                        {
                            var sepIdx = _streamBuffer.IndexOf("\n---\n", StringComparison.Ordinal);
                            if (sepIdx < 0) sepIdx = _streamBuffer.IndexOf("\n---\r\n", StringComparison.Ordinal);
                            if (sepIdx < 0 && _streamBuffer.StartsWith("---\n"))
                                sepIdx = 0;

                            if (sepIdx >= 0)
                            {
                                _separatorFound = true;
                                var prefix = _streamBuffer[..sepIdx].TrimEnd();
                                _detailStream = _streamBuffer[(sepIdx + 4)..]; // 跳过 "\n---\n"
                                // 切到简略视图
                                TxtResult.Document.Blocks.Clear();
                                var p = new Paragraph { Margin = new Thickness(0) };
                                RenderBriefParagraph(p, prefix);
                                TxtResult.Document.Blocks.Add(p);
                                BtnToggleDetail.Content = "详细";
                                BtnToggleDetail.Visibility = Visibility.Visible;
                            }
                            else
                            {
                                AppendText(chunk);
                            }
                        }
                        else
                        {
                            // 分隔符后：追加到详情缓冲
                            _detailStream += chunk;
                        }

                        if (atBottom)
                            TxtResult.ScrollToEnd();
                    });
                },
            };

            TxtStatus.Text = "AI 分析中（流式输出）...";
            var result = await analyzer.AnalyzeAsync(req, _cts.Token);

            if (result.Success)
            {
                // 保存对话上下文用于后续追问
                StoreConversationContext(req, result);
                // 显示追问输入框
                ShowFollowUpInput();

                // 拆分简略/详细（优先用流式缓冲）
                if (_separatorFound && !string.IsNullOrEmpty(_detailStream))
                {
                    _briefText = _streamBuffer.Split("\n---\n", 2)[0].TrimEnd();
                    _detailText = _detailStream.TrimStart(' ', '\r', '\n', '-');
                }
                else
                {
                    SplitBriefDetail();
                }
                if (!_separatorFound) ShowBrief();
                BtnToggleDetail.Visibility = string.IsNullOrEmpty(_detailText) ? Visibility.Collapsed : Visibility.Visible;
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
            _analysisInProgress = false;
            FlushAppendBuffer(); // 冲掉缓冲区残留
            _appendTimer.Stop();
            UpdateAnalyzeButton();
            _cts = null;
        }
    }

    /// <summary>根据配置选择 API 后端查询玩家战绩，返回结构化列表（战力悬浮窗复用）</summary>
    private async Task<List<PlayerThreatInfo>> QueryPlayerStatsListAsync(IProgress<string>? progress, CancellationToken ct)
    {
        switch (_settings.ApiBackend)
        {
            case ApiBackend.WgPublic:
            case ApiBackend.WgPublicYuyuko:
                var useProxy = _settings.ApiBackend == ApiBackend.WgPublicYuyuko;
                return await WgApiClient.AssessPlayersAsync(
                    _playerShipPairs, _settings.Server, _settings.WgApplicationId,
                    useProxy, progress, ct);
            case ApiBackend.Shinoaki:
            default:
                return await ShinoakiApiClient.AssessPlayersAsync(
                    _playerShipPairs, _settings.Server, progress, ct);
        }
    }

    /// <summary>根据配置选择 API 后端查询玩家战绩</summary>
    private async Task<string> QueryPlayerStatsAsync(IProgress<string> progress, CancellationToken ct)
    {
        var infos = await QueryPlayerStatsListAsync(progress, ct);
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

    /// <summary>往结果 RichTextBox 末尾追加文本（缓冲版：累积 50ms 再写入，大幅减少 layout 次数）</summary>
    private void AppendText(string text)
    {
        _appendBuffer.Append(text);
    }

    /// <summary>把缓冲区里的文本一次性写入 RichTextBox，然后触发丝滑滚动。</summary>
    private void FlushAppendBuffer()
    {
        if (_appendBuffer.Length == 0) return;
        var text = _appendBuffer.ToString();
        _appendBuffer.Clear();

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

        // 丝滑滚动到底部（模拟原生 smooth scroll）
        ScrollToEndSmooth();
    }

    /// <summary>强制冲洗缓冲（分析结束时调用，确保不丢最后一段文本）</summary>
    private void ForceFlushAppendBuffer()
    {
        FlushAppendBuffer();
        // 停止缓冲定时器 — 恢复正常追加模式
    }

    /// <summary>丝滑滚动到底部（easing 动画，参考 DeepSeek scrollTo behavior:smooth）</summary>
    private void ScrollToEndSmooth()
    {
        var sv = FindVisualChild<ScrollViewer>(TxtResult);
        if (sv == null) return;

        double from = sv.VerticalOffset;
        double to = sv.ScrollableHeight;
        if (to <= 0 || Math.Abs(to - from) < 1) return;

        // QuadraticEaseOut：到终点时慢下来，眼睛舒服
        var anim = new System.Windows.Media.Animation.DoubleAnimation(from, to,
            TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        sv.BeginAnimation(ScrollViewerOffset.VerticalOffsetProperty, anim);
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>把 ScrollViewer.VerticalOffset 暴露为可动画的附加 DP。</summary>
    private static class ScrollViewerOffset
    {
        public static readonly System.Windows.DependencyProperty VerticalOffsetProperty =
            System.Windows.DependencyProperty.RegisterAttached(
                "VerticalOffset", typeof(double), typeof(ScrollViewerOffset),
                new System.Windows.FrameworkPropertyMetadata(0.0,
                    System.Windows.FrameworkPropertyMetadataOptions.None,
                    (d, _) =>
                    {
                        if (d is ScrollViewer sv)
                            sv.ScrollToVerticalOffset((double)d.GetValue(VerticalOffsetProperty));
                    }));

        public static double GetVerticalOffset(System.Windows.DependencyObject obj) =>
            (double)obj.GetValue(VerticalOffsetProperty);
        public static void SetVerticalOffset(System.Windows.DependencyObject obj, double value) =>
            obj.SetValue(VerticalOffsetProperty, value);
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

    /// <summary>拆分 AI 输出为简略和详细两部分</summary>
    private void SplitBriefDetail()
    {
        var raw = _streamBuffer.Trim();
        _briefText = "";
        _detailText = "";
        _showDetail = false;

        // 找 "---" 分隔符（流式输出时已检测过，这里再确认一次）
        var sepIdx = raw.IndexOf("\n---\n", StringComparison.Ordinal);
        if (sepIdx < 0) sepIdx = raw.IndexOf("\n---\r\n", StringComparison.Ordinal);
        if (sepIdx < 0) sepIdx = raw.IndexOf("---\n", StringComparison.Ordinal);

        if (sepIdx >= 0)
        {
            _briefText = raw[..sepIdx].Trim();
            _detailText = raw[(sepIdx + 3)..].TrimStart('\r', '\n', '-', ' ');
        }
        else
        {
            _briefText = raw;
            _detailText = "";
        }
    }

    private void ShowBrief()
    {
        _showDetail = false;
        BtnToggleDetail.Content = "详细";
        TxtResult.Document.Blocks.Clear();
        var p = new Paragraph();
        RenderBriefParagraph(p, _briefText);
        TxtResult.Document.Blocks.Add(p);
    }

    private void ShowDetail()
    {
        _showDetail = true;
        BtnToggleDetail.Content = "简略";
        TxtResult.Document.Blocks.Clear();
        var doc = TxtResult.Document;

        // 简要部分
        var brief = !string.IsNullOrEmpty(_briefText) ? _briefText : _streamBuffer.Split("\n---\n", 2)[0].TrimEnd();
        if (!string.IsNullOrEmpty(brief))
        {
            var briefP = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            RenderBriefParagraph(briefP, brief);
            doc.Blocks.Add(briefP);

            doc.Blocks.Add(new Paragraph(new Run("─────────────")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3B, 0x55)),
                FontSize = 10
            }) { Margin = new Thickness(0, 4, 0, 4) });
        }

        // 详细部分（优先用流式缓冲，否则用 SplitBriefDetail 解析结果）
        var detail = !string.IsNullOrEmpty(_detailStream) ? _detailStream : _detailText;
        if (!string.IsNullOrEmpty(detail.TrimStart(' ', '\r', '\n', '-')))
            FormatTextAsMarkdown(detail.TrimStart(' ', '\r', '\n', '-'), doc);
        else if (!string.IsNullOrEmpty(_detailText))
            FormatTextAsMarkdown(_detailText, doc);
    }

    private static void RenderBriefParagraph(Paragraph p, string text)
    {
        // 简略版也做加粗解析
        var parts = System.Text.RegularExpressions.Regex.Split(text, @"(\*\*.*?\*\*)");
        foreach (var part in parts)
        {
            if (part.StartsWith("**") && part.EndsWith("**"))
            {
                p.Inlines.Add(new Run(part[2..^2])
                {
                    FontWeight = FontWeights.Bold,
                    Foreground = System.Windows.Media.Brushes.White,
                    FontSize = 13
                });
            }
            else
            {
                p.Inlines.Add(new Run(part)
                {
                    Foreground = System.Windows.Media.Brushes.LightGray,
                    FontSize = 12
                });
            }
        }
    }

    private void BtnToggleDetail_Click(object sender, RoutedEventArgs e)
    {
        if (_showDetail) ShowBrief(); else ShowDetail();
    }

    /// <summary>将文本按 Markdown 风格格式化为 FlowDocument 段落</summary>
    private static void FormatTextAsMarkdown(string text, FlowDocument doc)
    {
        if (string.IsNullOrEmpty(text)) return;
        var paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in paragraphs)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            var p = new Paragraph { Margin = new Thickness(0, 2, 0, 6) };

            var headerMatch = Regex.Match(trimmed, @"^(\d+)\.【(.+?)】");
            if (headerMatch.Success)
            {
                p.Inlines.Add(new Run($"{headerMatch.Groups[1].Value}.【{headerMatch.Groups[2].Value}】")
                {
                    FontWeight = FontWeights.Bold, FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7))
                });
                var rest = trimmed[headerMatch.Length..].TrimStart('\r', '\n', ' ');
                if (rest.Length > 0)
                    p.Inlines.Add(new Run("\n" + rest) { Foreground = System.Windows.Media.Brushes.LightGray });
                doc.Blocks.Add(p);
                continue;
            }

            if (trimmed.StartsWith("- ") || trimmed.StartsWith("  - "))
            {
                p.Inlines.Add(new Run("• " + Regex.Replace(trimmed, @"^\s*-\s*", ""))
                { Foreground = System.Windows.Media.Brushes.LightGray });
                p.Margin = new Thickness(20, 1, 0, 2);
                doc.Blocks.Add(p);
                continue;
            }

            var parts = Regex.Split(trimmed, @"(\*\*.*?\*\*)");
            foreach (var part in parts)
            {
                if (part.StartsWith("**") && part.EndsWith("**"))
                    p.Inlines.Add(new Run(part[2..^2]) { FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White });
                else
                    p.Inlines.Add(new Run(part) { Foreground = System.Windows.Media.Brushes.LightGray });
            }
            doc.Blocks.Add(p);
        }
    }
}
