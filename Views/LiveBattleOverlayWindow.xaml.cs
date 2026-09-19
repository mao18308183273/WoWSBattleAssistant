using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WoWSBattleAssistant.Services;

namespace WoWSBattleAssistant.Views;

/// <summary>
/// 实时战况悬浮窗。显示官方 ModsAPI 模组连接状态 + 最近战况事件，
/// 并提供快捷操作：📸 截小地图（自动放大到最大再截，清晰度更高）、🤖 AI 分析。
/// 伤害/击杀等数值游戏内已有显示，这里不做重复展示。
/// 拖拽移动，右键隐藏，✕ 关闭。
/// </summary>
public partial class LiveBattleOverlayWindow : Window
{
    private readonly ModDataMonitor _monitor;
    private readonly DispatcherTimer _timer;
    private readonly Action? _onCaptureMinimap;
    private readonly Action? _onAnalyze;
    private bool _dragging;
    private Point _mouseDownPos;
    private const double DragThreshold = 4;
    private bool _wasVisible;

    public LiveBattleOverlayWindow(ModDataMonitor monitor, double left, double top,
        Action? onCaptureMinimap = null, Action? onAnalyze = null)
    {
        InitializeComponent();
        _monitor = monitor;
        _onCaptureMinimap = onCaptureMinimap;
        _onAnalyze = onAnalyze;
        Left = left;
        Top = top;

        // 按钮无回调时禁用（如 mod 未启用但仍打开了浮窗）
        BtnCapture.IsEnabled = _onCaptureMinimap != null;
        BtnAnalyze.IsEnabled = _onAnalyze != null;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
        {
            _monitor.CheckNow();
            Render();
        }, Dispatcher);
        _timer.Start();
        _monitor.CheckNow();
        Render();
    }

    /// <summary>按最新数据渲染整个浮窗（连接状态 + 最近事件）。</summary>
    private void Render()
    {
        var s = _monitor.GetOverlayState();

        // 状态行：连接 + 对局
        if (!s.ModReady)
        {
            if (s.Connecting)
            {
                TxtStatus.Text = "🟡 连接中…";
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4D));
                TxtStatus.ToolTip = "模组已加载，正在等待事件系统就绪（mod 会自动重试，稍候即可）";
            }
            else
            {
                TxtStatus.Text = "🟡 模组未连接";
                TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4D));
                TxtStatus.ToolTip = "mod_ready.json 不存在或心跳超时。请确认：① 已在设置中安装模组 ② 安装后重启过游戏 ③ 游戏正在运行";
            }
        }
        else if (s.InBattle)
        {
            TxtStatus.Text = "🟢 已连接 · 对局中";
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x37, 0xD6, 0x7A));
        }
        else if (s.BattleEnded)
        {
            TxtStatus.Text = "🟢 已连接 · 对局结束";
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xB4));
        }
        else
        {
            TxtStatus.Text = "🟢 已连接 · 等待对局";
            TxtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0xB8, 0xFF));
        }

        var playerPart = string.IsNullOrEmpty(s.Player) ? "" : s.Player;
        var modePart = string.IsNullOrEmpty(s.BattleType) ? "" : $" · {s.BattleType}";
        TxtPlayer.Text = (playerPart + modePart).Trim(' ', '·');

        // 最近事件
        TxtEvents.Text = s.RecentEventLines.Count > 0
            ? string.Join("\n", s.RecentEventLines.TakeLast(5))
            : "（暂无事件）";
    }

    // ===== 快捷操作 =====
    private void BtnCapture_Click(object sender, RoutedEventArgs e)
    {
        try { _onCaptureMinimap?.Invoke(); }
        catch { /* 截图流程自带容错 */ }
    }

    private void BtnAnalyze_Click(object sender, RoutedEventArgs e)
    {
        try { _onAnalyze?.Invoke(); }
        catch { /* 分析流程自带容错 */ }
    }

    // ===== 拖拽移动 =====
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        _dragging = true;
        _mouseDownPos = e.GetPosition(this);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (!_dragging) return;
        var pos = e.GetPosition(this);
        if ((pos - _mouseDownPos).Length > DragThreshold)
        {
            _dragging = false;
            DragMove();
        }
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        _dragging = false;
    }

    // ===== 右键隐藏 / ✕ 关闭 =====
    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseRightButtonUp(e);
        Hide();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>记录可见性供外部同步（主窗口设置关闭时判断是否曾显示）。</summary>
    public bool WasEverVisible
    {
        get
        {
            if (IsVisible) _wasVisible = true;
            return _wasVisible;
        }
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
