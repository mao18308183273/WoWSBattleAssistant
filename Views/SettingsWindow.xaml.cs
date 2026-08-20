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

    private static readonly System.Net.Http.HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

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
            EnableVoiceControl = s.EnableVoiceControl,
            VoiceConfidenceThreshold = s.VoiceConfidenceThreshold,
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

        // 只显示当前选中提供方的配置面板
        UpdateAiConfigPanelVisibility();

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

        // 语音控制
        ChkVoiceControl.IsChecked = _draft.EnableVoiceControl;
        SldVoiceThreshold.Value = _draft.VoiceConfidenceThreshold;
        TxtThresholdLabel.Text = _draft.VoiceConfidenceThreshold.ToString("0.0");
        SldVoiceThreshold.ValueChanged += (_, _) =>
        {
            _draft.VoiceConfidenceThreshold = Math.Round(SldVoiceThreshold.Value, 1);
            TxtThresholdLabel.Text = _draft.VoiceConfidenceThreshold.ToString("0.0");
        };
        UpdateVoiceStatus();

        // 战力悬浮窗
        ChkPowerOverlay.IsChecked = _draft.EnablePowerOverlay;

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

    private void BtnBrowseGameFolder_Click(object sender, RoutedEventArgs e)
    {
        // 用 FolderBrowserDialog 让你直接选目录，避免手敲或复制粘贴可能带来的不可见字符问题
        var initDir = string.IsNullOrWhiteSpace(TxtGamePath.Text) ? null : SafeParent(TxtGamePath.Text);
        var dlg = new OpenFolderDialog
        {
            Title = "选择游戏根目录（含 bin、replays 子文件夹的那个）",
            InitialDirectory = initDir,
        };
        try
        {
            if (dlg.ShowDialog() == true)
            {
                TxtGamePath.Text = dlg.FolderName;
                _draft.GamePath = dlg.FolderName;
                VerifyGamePath();
            }
        }
        catch (Win32Exception)
        {
            // 上一次保存的路径所在驱动器不存在（如 U 盘已拔出），回退到默认位置
            dlg.InitialDirectory = null;
            try
            {
                if (dlg.ShowDialog() == true)
                {
                    TxtGamePath.Text = dlg.FolderName;
                    _draft.GamePath = dlg.FolderName;
                    VerifyGamePath();
                }
            }
            catch { /* 用户取消或再次失败，忽略 */ }
        }
    }

    private static string SafeParent(string path)
    {
        try
        {
            var p = Path.GetDirectoryName(path);
            return string.IsNullOrEmpty(p) ? path : p;
        }
        catch { return path; }
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
            // Directory.Exists 返回 false 未必是真的"没这个目录"——
            // 最常见原因是映射网络驱动器（NAS/共享文件夹）在特定运行上下文
            //（管理员权限、服务会话等）里不可见。
            var diag = DiagnoseInaccessiblePath(path);
            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            // 父目录存在 → 在同级找相似名称，提示用户
            var hint = "";
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                var siblings = Directory.GetDirectories(parent)
                    .Select(Path.GetFileName)
                    .Where(n => n != null && (
                        n.Contains("Warship", StringComparison.OrdinalIgnoreCase) ||
                        n.Contains("WoWS", StringComparison.OrdinalIgnoreCase)))
                    .Cast<string>()
                    .ToList();
                if (siblings.Count > 0)
                    hint = $" {parent} 下的战舰相关文件夹: {string.Join(" / ", siblings.Select(s => "\"" + s + "\""))}。";
            }
            TxtGamePathStatus.Text = $"❌ 无法访问此目录{(string.IsNullOrEmpty(diag) ? "" : $"（{diag}）")}。{hint}若目录确实存在，可能当前程序权限看不到网络驱动器，请尝试改用 UNC 路径（如 \\\\NAS名\\共享\\Games\\...）。";
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
        catch (Exception ex)
        {
            AppLog.Warn($"游戏路径验证异常: {ex.Message}");
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
        catch (Exception ex) { AppLog.Warn($"注册表检测游戏路径失败: {ex.Message}"); }

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

    private void OnAiProviderChanged(object sender, RoutedEventArgs e)
    {
        UpdateAiConfigPanelVisibility();
    }

    private void UpdateAiConfigPanelVisibility()
    {
        GlmConfigPanel.Visibility = RbGlm.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        QwenConfigPanel.Visibility = RbQwen.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DsConfigPanel.Visibility = RbDeepSeek.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
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
        _draft.EnableVoiceControl = ChkVoiceControl.IsChecked == true;
        _draft.VoiceConfidenceThreshold = Math.Round(SldVoiceThreshold.Value, 1);
        _draft.EnablePowerOverlay = ChkPowerOverlay.IsChecked == true;
        _draft.ShipDataPath = TxtShipDataPath.Text.Trim();
        _draft.SystemPrompt = TxtSystemPrompt.Text;
        _draft.Server = CbServer.SelectedItem?.ToString() ?? "cn";
        _draft.GamePath = TxtGamePath.Text.Trim();
        _draft.ApiBackend = (ApiBackend)CbApiBackend.SelectedIndex;

        // 校验：API Key 缺失时仅提醒，不阻止保存（悬浮窗等非 AI 功能不需要 Key）
        if (_draft.AiProvider == AiProvider.Glm && string.IsNullOrWhiteSpace(_draft.GlmApiKey))
        {
            MessageBox.Show("GLM API Key 未填写，AI 分析功能将不可用。\n如需使用 AI 分析，请到智谱开放平台获取 Key。", "提示");
        }
        else if (_draft.AiProvider == AiProvider.Qwen && string.IsNullOrWhiteSpace(_draft.QwenApiKey))
        {
            MessageBox.Show("通义 API Key 未填写，AI 分析功能将不可用。\n如需使用 AI 分析，请到阿里云百炼平台获取 Key。", "提示");
        }
        else if (_draft.AiProvider == AiProvider.DeepSeek && string.IsNullOrWhiteSpace(_draft.DeepSeekToken))
        {
            MessageBox.Show("DeepSeek Token 未填写，AI 分析功能将不可用。\n如需使用 AI 分析，请登录 chat.deepseek.com → F12 → 应用 → 本地存储 → 复制 userToken。", "提示");
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
        dst.EnableVoiceControl = src.EnableVoiceControl;
        dst.VoiceConfidenceThreshold = src.VoiceConfidenceThreshold;
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

    private void ChkVoiceControl_Changed(object sender, RoutedEventArgs e)
    {
        UpdateVoiceStatus();
    }

    private void UpdateVoiceStatus()
    {
        if (ChkVoiceControl.IsChecked == true)
        {
            var info = VoiceController.GetInstalledRecognizerInfo();
            if (info != null)
                TxtVoiceStatus.Text = $"✅ 已检测到语音引擎: {info}。使用 Windows 默认麦克风。";
            else
            {
                TxtVoiceStatus.Text = "⚠ 未找到中文语音识别引擎。请点击下方「打开 Windows 语音设置」→ 添加中文（简体）语言 → 勾选「语音识别」安装语音包后重启软件。";
                TxtVoiceStatus.Foreground = System.Windows.Media.Brushes.OrangeRed;
            }
        }
        else
        {
            TxtVoiceStatus.Text = "语音控制已关闭";
        }
    }

    private void BtnOpenSpeechSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Windows 10/11 语音设置深链
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:speech",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // 旧版 Windows 回退到控制面板
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "control",
                    Arguments = "/name Microsoft.SpeechRecognition",
                    UseShellExecute = true
                });
            }
            catch
            {
                MessageBox.Show($"无法自动打开语音设置，请手动打开：\n设置 → 时间和语言 → 语言 → 中文(简体) → 语音\n\n错误: {ex.Message}",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private async void BtnTestMic_Click(object sender, RoutedEventArgs e)
    {
        BtnTestMic.IsEnabled = false;
        BtnTestMic.Content = "测试中…";
        TxtVoiceTestResult.Text = "正在初始化语音识别（3 秒后开始说话）…";
        TxtVoiceTestResult.Foreground = System.Windows.Media.Brushes.Gray;
        try
        {
            using var tester = new VoiceTestRunner();
            var text = await tester.RunAsync(TimeSpan.FromSeconds(5));
            if (string.IsNullOrWhiteSpace(text))
            {
                TxtVoiceTestResult.Text = "❌ 5 秒内未识别到任何语音。可能原因：麦克风权限未开启、未选默认麦克风、或者环境噪音太大。";
                TxtVoiceTestResult.Foreground = System.Windows.Media.Brushes.OrangeRed;
            }
            else
            {
                TxtVoiceTestResult.Text = $"✅ 识别成功: \"{text}\"";
                TxtVoiceTestResult.Foreground = System.Windows.Media.Brushes.LimeGreen;
            }
        }
        catch (Exception ex)
        {
            TxtVoiceTestResult.Text = $"❌ 测试失败: {ex.Message}";
            TxtVoiceTestResult.Foreground = System.Windows.Media.Brushes.OrangeRed;
        }
        finally
        {
            BtnTestMic.IsEnabled = true;
            BtnTestMic.Content = "🎤 测试麦克风";
        }
    }

    private async void BtnTestApi_Click(object sender, RoutedEventArgs e)
    {
        TxtApiTestResult.Text = "测试中...";
        TxtApiTestResult.Foreground = System.Windows.Media.Brushes.Gray;

        try
        {
            var backend = (ApiBackend)CbApiBackend.SelectedIndex;
            var server = CbServer.SelectedItem?.ToString() ?? "cn";
            var testPlayer = "test";

            if (backend == ApiBackend.Shinoaki || backend == ApiBackend.Vortex)
            {
                var resp = await SharedHttp.GetStringAsync($"https://wows.mgaia.top/api/shinoaki/user/search/v2/{server}/{testPlayer}?type=exact");
                TxtApiTestResult.Text = $"✅ Shinoaki API 连接正常 ({server}服)";
                TxtApiTestResult.Foreground = System.Windows.Media.Brushes.LimeGreen;
            }
            else
            {
                var appId = _draft.WgApplicationId;
                var domain = backend == ApiBackend.WgPublicYuyuko
                    ? "dev-proxy.wows.shinoaki.com:7700/dev"
                    : "api.worldofwarships.asia";
                var url = backend == ApiBackend.WgPublicYuyuko
                    ? $"https://dev-proxy.wows.shinoaki.com:7700/dev/wows/account/list/?application_id={appId}&search={testPlayer}"
                    : $"https://api.worldofwarships.asia/wows/account/list/?application_id={appId}&search={testPlayer}";

                var resp = await SharedHttp.GetStringAsync(url);
                TxtApiTestResult.Text = $"✅ WG API 连接正常";
                TxtApiTestResult.Foreground = System.Windows.Media.Brushes.LimeGreen;
            }
        }
        catch (Exception ex)
        {
            TxtApiTestResult.Text = $"❌ 连接失败: {ex.Message}";
            TxtApiTestResult.Foreground = System.Windows.Media.Brushes.OrangeRed;
        }
    }

    /// <summary>诊断为什么 Directory.Exists 失败。返回可读的原因字符串。</summary>
    private static string DiagnoseInaccessiblePath(string path)
    {
        try
        {
            // 先查路径里有没有可疑字符（全角字母/标点、零宽字符、不可见 Unicode 等极易导致 Directory.Exists 失败）
            var suspicious = new List<string>();
            for (int i = 0; i < path.Length; i++)
            {
                var c = path[i];
                bool isAsciiPrintable = c >= 0x20 && c < 0x7F;
                bool isCJK = c >= 0x4E00 && c <= 0x9FFF; // 中文表意文字（OK）
                bool isCJKPunct = c == '、' || c == '。' || c == '，';
                if (!isAsciiPrintable && !isCJK && !isCJKPunct)
                    suspicious.Add($"位置 {i}: U+{((int)c):X4} ('{c}')");
                // 全角字母/数字也算可疑（容易混入复制粘贴）
                if (c >= 0xFF01 && c <= 0xFF5E)
                    suspicious.Add($"位置 {i}: 全角字符 U+{((int)c):X4} ('{c}')");
            }
            if (suspicious.Count > 0)
                return "路径包含可疑字符（复制粘贴可能带进来全角/不可见字符）: " + string.Join("; ", suspicious.Take(5)) +
                       (suspicious.Count > 5 ? $" …共 {suspicious.Count} 处" : "") + "。建议改用「📂 浏览」按钮直接选择目录。";

            path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetPathRoot(path);
            if (root == null) return "无法解析盘符";

            var drives = DriveInfo.GetDrives()
                .FirstOrDefault(d => string.Equals(d.Name.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (drives == null) return "该盘符不存在";
            if (!drives.IsReady) return "该盘符未就绪（如未插盘、网络未连）";
            if (drives.DriveType == DriveType.Network)
                return "映射网络驱动器，当前权限看不到。请在文件资源管理器地址栏找到 UNC 路径（如 \\\\server\\share），用 UNC 替代盘符。";

            // 盘符是本地盘 → 逐级拆路径，看卡在哪一级
            var parts = new List<string>();
            var current = path;
            while (current != null && current.Length > root.Length)
            {
                parts.Add(current);
                current = Path.GetDirectoryName(current);
            }
            parts.Reverse();
            string? lastExists = root;
            foreach (var p in parts)
            {
                if (Directory.Exists(p)) { lastExists = p; continue; }
                return $"\"{Path.GetFileName(p)}\" 这一级不存在（{lastExists} 可见，之下就没有了）。建议打开文件资源管理器确认 {lastExists} 下的实际文件夹名。";
            }
        }
        catch (Exception ex)
        {
            return $"访问异常: {ex.Message}";
        }
        return "";
    }
}
