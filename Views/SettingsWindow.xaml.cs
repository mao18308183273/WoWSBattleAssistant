using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WoWSBattleAssistant.Models;
using WoWSBattleAssistant.Services;
using WoWSBattleAssistant.Services.AI;

namespace WoWSBattleAssistant.Views;

/// <summary>设置面板。编辑并保存 AppSettings。</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ShipDatabase _database;
    private AppSettings _draft; // 编辑副本

    public SettingsWindow(AppSettings settings, ShipDatabase database)
    {
        InitializeComponent();
        _settings = settings;
        _database = database;
        // 深拷贝一份做编辑，取消时不影响原对象
        _draft = CloneSettings(settings);
        LoadUi();
    }

    private static AppSettings CloneSettings(AppSettings s)
    {
        return new AppSettings
        {
            AiProvider = s.AiProvider,
            GlmApiKey = s.GlmApiKey,
            GlmModel = s.GlmModel,
            QwenApiKey = s.QwenApiKey,
            QwenModel = s.QwenModel,
            DeepSeekToken = s.DeepSeekToken,
            DeepSeekCookie = s.DeepSeekCookie,
            EnableDeepSeekThinking = s.EnableDeepSeekThinking,
            ShipDataPath = s.ShipDataPath,
            MinimapRegion = s.MinimapRegion,
            WindowLeft = s.WindowLeft,
            WindowTop = s.WindowTop,
            WindowWidth = s.WindowWidth,
            WindowHeight = s.WindowHeight,
            AttachKnowledgeBase = s.AttachKnowledgeBase,
            SystemPrompt = s.SystemPrompt,
            Server = s.Server,
            GamePath = s.GamePath,
            ApiBackend = s.ApiBackend,
            WgApplicationId = s.WgApplicationId,
        };
    }

    private void LoadUi()
    {
        RbGlm.IsChecked = _draft.AiProvider == AiProvider.Glm;
        RbQwen.IsChecked = _draft.AiProvider == AiProvider.Qwen;
        RbDeepSeek.IsChecked = _draft.AiProvider == AiProvider.DeepSeek;

        PbGlmKey.Password = _draft.GlmApiKey;
        CbGlmModel.Items.Clear();
        foreach (var m in AIAnalyzerFactory.GlmModels) CbGlmModel.Items.Add(m);
        if (string.IsNullOrEmpty(_draft.GlmModel) || !AIAnalyzerFactory.GlmModels.Contains(_draft.GlmModel))
            CbGlmModel.Items.Add(_draft.GlmModel);
        CbGlmModel.SelectedItem = string.IsNullOrEmpty(_draft.GlmModel) ? "glm-4v" : _draft.GlmModel;

        PbQwenKey.Password = _draft.QwenApiKey;
        CbQwenModel.Items.Clear();
        foreach (var m in AIAnalyzerFactory.QwenModels) CbQwenModel.Items.Add(m);
        if (string.IsNullOrEmpty(_draft.QwenModel) || !AIAnalyzerFactory.QwenModels.Contains(_draft.QwenModel))
            CbQwenModel.Items.Add(_draft.QwenModel);
        CbQwenModel.SelectedItem = string.IsNullOrEmpty(_draft.QwenModel) ? "qwen-vl-plus" : _draft.QwenModel;

        PbDeepSeekToken.Password = _draft.DeepSeekToken;
        TxtDeepSeekCookie.Text = _draft.DeepSeekCookie;
        ChkDsThinking.IsChecked = _draft.EnableDeepSeekThinking;

        TxtShipDataPath.Text = _draft.ShipDataPath;
        UpdateShipCount();

        // 游戏路径
        TxtGamePath.Text = _draft.GamePath;
        VerifyGamePath();

        // 战绩 API 后端
        CbApiBackend.Items.Clear();
        CbApiBackend.Items.Add("Shinoaki (默认)");
        CbApiBackend.Items.Add("WG Public");
        CbApiBackend.Items.Add("Vortex");
        CbApiBackend.Items.Add("WG+Yuyuko代理");
        CbApiBackend.SelectedIndex = (int)_draft.ApiBackend;

        // 服务器选择
        CbServer.Items.Clear();
        CbServer.Items.Add("cn");  // 国服
        CbServer.Items.Add("asia"); // 亚服
        CbServer.Items.Add("eu");   // 欧服
        CbServer.Items.Add("na");   // 美服
        CbServer.Items.Add("ru");   // 俄服
        CbServer.SelectedItem = string.IsNullOrWhiteSpace(_draft.Server) ? "cn" : _draft.Server;

        UpdateRegionText();
        TxtSystemPrompt.Text = _draft.SystemPrompt;
    }

    private void UpdateShipCount()
    {
        if (_database.IsLoaded)
            TxtShipCount.Text = $"已加载 {_database.TotalCount} 艘战舰";
        else
            TxtShipCount.Text = "知识库未加载";
    }

    private void UpdateRegionText()
    {
        var r = _draft.MinimapRegion;
        TxtRegion.Text = r.IsEmpty
            ? "未设置（请点击下方按钮框选）"
            : $"区域: X={r.X:0}, Y={r.Y:0}, 宽={r.Width:0}, 高={r.Height:0}";
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "战舰数据 JSON|*.json|所有文件|*.*",
            Title = "选择战舰数据 JSON 文件"
        };
        if (dlg.ShowDialog() == true)
        {
            TxtShipDataPath.Text = dlg.FileName;
            _draft.ShipDataPath = dlg.FileName;
        }
    }

    private void BtnBrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var autoPath = DetectGamePath();
        if (!string.IsNullOrWhiteSpace(autoPath) && Directory.Exists(autoPath))
        {
            TxtGamePath.Text = autoPath;
            _draft.GamePath = autoPath;
            VerifyGamePath();
            MessageBox.Show($"已自动检测到游戏目录:\n{autoPath}\n\n如不正确，请手动修改路径。", "自动检测");
        }
        else
        {
            MessageBox.Show("未能自动检测到游戏目录。\n请手动将游戏安装路径粘贴到输入框中（含 bin、replays 子目录）。", "未找到");
        }
    }

    private void BtnVerifyGame_Click(object sender, RoutedEventArgs e)
    {
        _draft.GamePath = TxtGamePath.Text.Trim();
        VerifyGamePath();
    }

    private void VerifyGamePath()
    {
        var path = TxtGamePath.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            TxtGamePathStatus.Text = "⬜ 未设置目录";
            TxtGamePathStatus.Foreground = System.Windows.Media.Brushes.Gray;
            return;
        }

        if (!Directory.Exists(path))
        {
            TxtGamePathStatus.Text = "❌ 目录不存在";
            TxtGamePathStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            return;
        }

        var replayDir = Path.Combine(path, "replays");
        if (!Directory.Exists(replayDir))
        {
            TxtGamePathStatus.Text = "⚠ 目录存在但没有 replays 子文件夹，可能不是游戏安装目录";
            TxtGamePathStatus.Foreground = System.Windows.Media.Brushes.Orange;
            return;
        }

        // 检查是否有最近的 tempArenaInfo.json
        try
        {
            var tempFiles = Directory.GetFiles(replayDir, "tempArenaInfo.json", SearchOption.AllDirectories);
            if (tempFiles.Length > 0)
            {
                var latest = new FileInfo(tempFiles.OrderByDescending(f => new FileInfo(f).LastWriteTime).First());
                var age = DateTime.Now - latest.LastWriteTime;
                var ageStr = age.TotalMinutes < 1 ? "刚刚" : age.TotalHours < 1 ? $"{age.TotalMinutes:0} 分钟前" : $"{age.TotalHours:0.0} 小时前";
                TxtGamePathStatus.Text = $"✅ 验证成功！replays 目录正常，最近对局数据: {ageStr}";
                TxtGamePathStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
            }
            else
            {
                TxtGamePathStatus.Text = "✅ 目录有效（含 replays 文件夹）。尚未检测到对局数据文件，进入游戏后会自动生成。";
                TxtGamePathStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
            }
        }
        catch
        {
            TxtGamePathStatus.Text = "✅ 目录有效（含 replays 文件夹）";
            TxtGamePathStatus.Foreground = System.Windows.Media.Brushes.LimeGreen;
        }
    }

    /// <summary>从注册表和常见路径自动检测游戏安装目录</summary>
    private static string DetectGamePath()
    {
        // 尝试从注册表检测
        try
        {
            var keys = new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WOWS.CN",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\WOWS.CN",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WorldOfWarships",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\WorldOfWarships",
            };
            foreach (var key in keys)
            {
                using var rk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
                if (rk == null) continue;
                var loc = rk.GetValue("InstallLocation")?.ToString();
                if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                    return loc;
            }
        }
        catch { }

        // 常见安装路径
        var common = new[]
        {
            @"C:\Games\World_of_Warships_CN",
            @"C:\Games\World_of_Warships",
            @"D:\Games\World_of_Warships_CN",
            @"D:\Games\World_of_Warships",
        };
        foreach (var p in common)
        {
            if (Directory.Exists(p) && Directory.Exists(Path.Combine(p, "replays")))
                return p;
        }
        return "";
    }

    private async void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtShipDataPath.Text.Trim();
        if (!File.Exists(path))
        {
            MessageBox.Show("文件不存在: " + path, "提示");
            return;
        }
        _draft.ShipDataPath = path;
        BtnReload.IsEnabled = false;
        BtnReload.Content = "加载中...";
        try
        {
            await _database.LoadAsync(path);
            _settings.ShipDataPath = path; // 立即生效
            UpdateShipCount();
            MessageBox.Show($"加载成功，共 {_database.TotalCount} 艘战舰。", "完成");
        }
        catch (Exception ex)
        {
            MessageBox.Show("加载失败: " + ex.Message, "错误");
        }
        finally
        {
            BtnReload.IsEnabled = true;
            BtnReload.Content = "重新加载知识库";
        }
    }

    private void BtnSelectRegion_Click(object sender, RoutedEventArgs e)
    {
        var sel = new RegionSelectorWindow();
        sel.Owner = null;
        if (sel.ShowDialog() == true)
        {
            _draft.MinimapRegion = sel.SelectedRegion;
            UpdateRegionText();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        // 把 UI 值写回 _draft
        _draft.AiProvider = RbGlm.IsChecked == true ? AiProvider.Glm
            : RbDeepSeek.IsChecked == true ? AiProvider.DeepSeek
            : AiProvider.Qwen;
        _draft.GlmApiKey = PbGlmKey.Password;
        _draft.GlmModel = CbGlmModel.SelectedItem?.ToString() ?? "glm-4v";
        _draft.QwenApiKey = PbQwenKey.Password;
        _draft.QwenModel = CbQwenModel.SelectedItem?.ToString() ?? "qwen-vl-plus";
        _draft.DeepSeekToken = PbDeepSeekToken.Password;
        _draft.DeepSeekCookie = TxtDeepSeekCookie.Text;
        _draft.EnableDeepSeekThinking = ChkDsThinking.IsChecked == true;
        _draft.ShipDataPath = TxtShipDataPath.Text.Trim();
        _draft.SystemPrompt = TxtSystemPrompt.Text;
        _draft.Server = CbServer.SelectedItem?.ToString() ?? "cn";
        _draft.GamePath = TxtGamePath.Text.Trim();
        _draft.ApiBackend = (ApiBackend)CbApiBackend.SelectedIndex;

        // 校验
        if (_draft.AiProvider == AiProvider.Glm && string.IsNullOrWhiteSpace(_draft.GlmApiKey))
        {
            MessageBox.Show("请填写 GLM API Key", "提示");
            return;
        }
        if (_draft.AiProvider == AiProvider.Qwen && string.IsNullOrWhiteSpace(_draft.QwenApiKey))
        {
            MessageBox.Show("请填写通义 API Key", "提示");
            return;
        }
        if (_draft.AiProvider == AiProvider.DeepSeek && string.IsNullOrWhiteSpace(_draft.DeepSeekToken))
        {
            MessageBox.Show("请填写 DeepSeek Token", "提示");
            return;
        }

        // 复制回原对象并保存
        CopySettings(_draft, _settings);
        SettingsStore.Save(_settings);
        DialogResult = true;
        Close();
    }

    private static void CopySettings(AppSettings src, AppSettings dst)
    {
        dst.AiProvider = src.AiProvider;
        dst.GlmApiKey = src.GlmApiKey;
        dst.GlmModel = src.GlmModel;
        dst.QwenApiKey = src.QwenApiKey;
        dst.QwenModel = src.QwenModel;
        dst.DeepSeekToken = src.DeepSeekToken;
        dst.DeepSeekCookie = src.DeepSeekCookie;
        dst.EnableDeepSeekThinking = src.EnableDeepSeekThinking;
        dst.ShipDataPath = src.ShipDataPath;
        dst.MinimapRegion = src.MinimapRegion;
        dst.SystemPrompt = src.SystemPrompt;
        dst.Server = src.Server;
        dst.GamePath = src.GamePath;
        dst.ApiBackend = src.ApiBackend;
        dst.WgApplicationId = src.WgApplicationId;
    }

    private void BtnRefreshLog_Click(object sender, RoutedEventArgs e)
    {
        TxtLogViewer.Text = AppLog.ReadTail(500);
        TxtLogViewer.ScrollToEnd();
    }

    private void BtnExportLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "日志文件|*.log|文本文件|*.txt|所有文件|*.*",
            Title = "导出日志",
            FileName = $"WoWSBA_log_{DateTime.Now:yyyyMMdd_HHmmss}.log"
        };
        if (dlg.ShowDialog() == true)
        {
            if (AppLog.ExportTo(dlg.FileName))
                MessageBox.Show($"日志已导出到:\n{dlg.FileName}", "导出成功");
            else
                MessageBox.Show("导出失败，可能没有日志文件。", "导出失败");
        }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定要清空所有日志吗？", "确认", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
        {
            AppLog.Clear();
            TxtLogViewer.Text = "(日志已清空)";
        }
    }
}
