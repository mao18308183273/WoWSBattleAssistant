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
        // 手动字段拷贝,避免 JsonSerializer 默认配置无法处理 Rect.Empty 的无穷大值
        return new AppSettings
        {
            AiProvider = s.AiProvider,
            GlmApiKey = s.GlmApiKey,
            GlmModel = s.GlmModel,
            QwenApiKey = s.QwenApiKey,
            QwenModel = s.QwenModel,
            ShipDataPath = s.ShipDataPath,
            MinimapRegion = s.MinimapRegion,
            WindowLeft = s.WindowLeft,
            WindowTop = s.WindowTop,
            WindowWidth = s.WindowWidth,
            WindowHeight = s.WindowHeight,
            AttachKnowledgeBase = s.AttachKnowledgeBase,
            SystemPrompt = s.SystemPrompt,
        };
    }

    private void LoadUi()
    {
        RbGlm.IsChecked = _draft.AiProvider == AiProvider.Glm;
        RbQwen.IsChecked = _draft.AiProvider == AiProvider.Qwen;

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

        TxtShipDataPath.Text = _draft.ShipDataPath;
        UpdateShipCount();

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
        // 先隐藏设置窗口，避免遮挡框选
        this.Hide();
        try
        {
            var sel = new RegionSelectorWindow();
            sel.Owner = null;
            if (sel.ShowDialog() == true)
            {
                _draft.MinimapRegion = sel.SelectedRegion;
                UpdateRegionText();
            }
        }
        finally
        {
            this.Show();
            this.Activate();
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
        _draft.AiProvider = RbGlm.IsChecked == true ? AiProvider.Glm : AiProvider.Qwen;
        _draft.GlmApiKey = PbGlmKey.Password;
        _draft.GlmModel = CbGlmModel.SelectedItem?.ToString() ?? "glm-4v";
        _draft.QwenApiKey = PbQwenKey.Password;
        _draft.QwenModel = CbQwenModel.SelectedItem?.ToString() ?? "qwen-vl-plus";
        _draft.ShipDataPath = TxtShipDataPath.Text.Trim();
        _draft.SystemPrompt = TxtSystemPrompt.Text;

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
        dst.ShipDataPath = src.ShipDataPath;
        dst.MinimapRegion = src.MinimapRegion;
        dst.SystemPrompt = src.SystemPrompt;
    }
}
