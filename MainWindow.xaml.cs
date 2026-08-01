using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WoWSBattleAssistant.Models;
using WoWSBattleAssistant.Services;
using WoWSBattleAssistant.Services.AI;
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

    // 识别到的我方舰船列表（用户从中选一艘作为 MyShip，剩下的进 Allies）
    private readonly List<string> _recognizedAllies = new();

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
        var provider = _settings.AiProvider == AiProvider.Glm
            ? $"智谱 {_settings.GlmModel}"
            : $"通义 {_settings.QwenModel}";
        var keyOk = _settings.AiProvider == AiProvider.Glm
            ? !string.IsNullOrWhiteSpace(_settings.GlmApiKey)
            : !string.IsNullOrWhiteSpace(_settings.QwenApiKey);
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
        _recognizedAllies.Clear();

        TxtMyShip.Text = "";
        TxtAllies.Text = "";
        TxtEnemies.Text = "";
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
        var hasKey = _settings.AiProvider == AiProvider.Glm
            ? !string.IsNullOrWhiteSpace(_settings.GlmApiKey)
            : !string.IsNullOrWhiteSpace(_settings.QwenApiKey);
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
                // 识别失败也允许用户手动填写，所以不阻止 _lineupReady 通过手动输入推进
                UpdateAnalyzeButton();
                return;
            }

            // 4. 填表：我方全部进 Allies 框 + 填下拉框，敌方进 Enemies 框
            _recognizedAllies.Clear();
            _recognizedAllies.AddRange(rec.Allies);

            CboMyShip.Items.Clear();
            foreach (var name in rec.Allies) CboMyShip.Items.Add(name);

            TxtAllies.Text = string.Join("、", rec.Allies);
            TxtEnemies.Text = string.Join("、", rec.Enemies);
            TxtMyShip.Text = "";

            TxtLineupStatus.Text = $"✅ 识别到 我方{rec.Allies.Count}艘 / 敌方{rec.Enemies.Count}艘。请从下拉框指定你的战舰。";
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

        // 从 Allies 框中移除这艘船
        var allies = ParseNames(TxtAllies.Text);
        allies.RemoveAll(s => string.Equals(s, shipName, StringComparison.OrdinalIgnoreCase));
        TxtAllies.Text = string.Join("、", allies);

        TxtLineupStatus.Text = $"✅ 已指定你的战舰: {shipName}（剩余我方 {allies.Count} 艘）";
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
            || !string.IsNullOrWhiteSpace(TxtAllies.Text.Trim())
            || !string.IsNullOrWhiteSpace(TxtEnemies.Text.Trim());
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
            // 1. 构建知识库
            var enemyNames = ParseNames(TxtEnemies.Text);
            var allyNames = ParseNames(TxtAllies.Text);
            var allNames = new List<string> { myShip };
            allNames.AddRange(enemyNames);
            allNames.AddRange(allyNames);
            var kbText = _database.BuildKnowledgeText(allNames);

            // 2. 调 AI 分析
            var analyzer = AIAnalyzerFactory.Create(_settings);
            var req = new BattleAnalysisRequest
            {
                MinimapImage = _minimapImage,
                ImageBase64 = ScreenCaptureService.EncodeToBase64(_minimapImage),
                MyShip = myShip,
                AlliedShips = string.Join("、", allyNames),
                EnemyShips = string.Join("、", enemyNames),
                KnowledgeBaseText = kbText,
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
}
