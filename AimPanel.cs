using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WoWSBattleAssistant.Services;

namespace WoWSBattleAssistant;

/// <summary>预瞄目标列表的显示模型（把后端字段拼成人类可读的短文本）。</summary>
public sealed class AimTargetView
{
    public long Eid { get; init; }
    public string Name { get; init; } = "?";
    public bool Visible { get; init; }
    public double Dist { get; init; }
    public double SpeedKn { get; init; }
    public double Flight { get; init; }
    public double Lead { get; init; }
    public double BrgNow { get; init; }
    public double BrgAim { get; init; }
    public double HitProb { get; init; }
    public bool Moving => SpeedKn >= 1.0;

    public string DistText => $"{Dist:0} m";
    public string SpeedText => Moving ? $"{SpeedKn:0.0} kn" : "静止";
    public string FlightText => $"{Flight:0.00} s";
    public string LeadText => Moving ? $"{Lead:0} m" : "—";
    public string BrgText => Moving ? $"{BrgNow:0}° → {BrgAim:0}°" : $"{BrgNow:0}°";
    public string HitText => $"{(int)(HitProb * 100)}%";
    public string LeadHint => Moving
        ? $"提前 {Lead:0} 米（飞行 {Flight:0.00} 秒）"
        : "目标当前静止，不需要提前量";
}

public partial class MainWindow
{
    private readonly ObservableCollection<AimTargetView> _aimTargets = new();
    /// <summary>当前锁定（右键选中）的目标；null 表示未锁定。</summary>
    private AimTargetView? _aimLocked;
    private DispatcherTimer? _aimUiTimer;

    private void InitAimUi()
    {
        LsvAimTargets.ItemsSource = _aimTargets;
        CmbShellType.SelectedIndex = 0;
        CmbShellType.SelectionChanged += CmbShellType_SelectionChanged;

        // 每 500ms 刷一次列表。读取走 LiveAimMonitor.Current，
        // 它内部会先尝试重新读一次 live_aim.json，保证不是陈旧数据。
        _aimUiTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _aimUiTimer.Tick += (_, _) => RefreshAimUi();
        _aimUiTimer.Start();
    }

    private void CmbShellType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbShellType.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), out var sp) && sp > 0)
        {
            _liveAim.ShellSpeed = sp;
            AppLog.Info($"预瞄弹速切换为 {sp} m/s（{item.Content}）");
        }
        RefreshAimUi();
    }

    private void RefreshAimUi()
    {
        var snap = _liveAim.Current;
        TxtAimStatus.Text = _liveAim.LastStatus;

        var list = snap?.Targets ?? new List<LiveAimMonitor.Target>();
        // 只显示"已点亮"的：没点亮的敌舰位置我们不掌握，不做推测。
        // 锁定的目标即使此刻暂时不可见也保留在列表里，避免"锁着就没了"。
        var vis = list.Where(t => t.Visible || (t.Eid == _aimLocked?.Eid)).ToList();
        vis.Sort((a, b) =>
        {
            if (a.Eid == _aimLocked?.Eid) return -1;
            if (b.Eid == _aimLocked?.Eid) return 1;
            return a.Dist.CompareTo(b.Dist);
        });

        TxtAimPlaceholder.Visibility = vis.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        TxtAimPlaceholder.Text = snap == null
            ? "等待对局开始…"
            : (list.Count == 0 ? "暂无目标（对局尚未开始或回放未生成）"
                              : "当前没有已点亮的敌方目标");

        // 增量刷新：只有集合内容真的变了才重建，避免每 500ms 闪一次
        bool same = _aimTargets.Count == vis.Count;
        if (same)
        {
            for (int i = 0; i < vis.Count; i++)
                if (_aimTargets[i].Eid != vis[i].Eid) { same = false; break; }
        }
        if (!same)
        {
            var keepLocked = _aimLocked;
            _aimTargets.Clear();
            foreach (var t in vis)
                _aimTargets.Add(new AimTargetView
                {
                    Eid = t.Eid,
                    Name = t.Name ?? "?",
                    Visible = t.Visible,
                    Dist = t.Dist,
                    SpeedKn = t.SpeedKn,
                    Flight = t.Flight,
                    Lead = t.Lead,
                    BrgNow = t.BrgNow,
                    BrgAim = t.BrgAim,
                    HitProb = t.HitProb,
                });
            // 用 eid 恢复锁定（换了新实例，引用会失效）
            _aimLocked = keepLocked == null
                ? null
                : _aimTargets.FirstOrDefault(x => x.Eid == keepLocked.Eid);
        }
        else
        {
            // 同一批目标也要刷新数值（距离/提前量每帧都在变）
            for (int i = 0; i < vis.Count; i++)
            {
                var t = vis[i];
                var old = _aimTargets[i];
                if (old.Eid != t.Eid) continue;
                // AimTargetView 是 init-only，用替换的方式更新
                _aimTargets[i] = new AimTargetView
                {
                    Eid = t.Eid,
                    Name = t.Name ?? "?",
                    Visible = t.Visible,
                    Dist = t.Dist,
                    SpeedKn = t.SpeedKn,
                    Flight = t.Flight,
                    Lead = t.Lead,
                    BrgNow = t.BrgNow,
                    BrgAim = t.BrgAim,
                    HitProb = t.HitProb,
                };
            }
        }
        HighlightLockedRow();
        UpdateAimLockPanel();
    }

    private void HighlightLockedRow()
    {
        foreach (var item in LsvAimTargets.Items)
        {
            if (item is not AimTargetView v) continue;
            bool locked = _aimLocked != null && v.Eid == _aimLocked.Eid;
            if (item is ListViewItem li)
            {
                li.Background = locked
                    ? new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x1D))
                    : Brushes.Transparent;
                li.FontWeight = locked ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }

    private void UpdateAimLockPanel()
    {
        if (_aimLocked == null)
        {
            AimLockPanel.Visibility = Visibility.Collapsed;
            return;
        }
        AimLockPanel.Visibility = Visibility.Visible;
        AimLockInfo.Children.Clear();

        void Add(string label, string value, string? color = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            sp.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9B, 0xA4, 0xB8)),
                FontSize = 11,
                MinWidth = 68,
            });
            sp.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = color == null
                    ? new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF4))
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
            });
            AimLockInfo.Children.Add(sp);
        }

        Add("锁定目标", _aimLocked.Name);
        Add("距离", $"{_aimLocked.Dist:0} 米");
        Add("目标速度", _aimLocked.Moving ? $"{_aimLocked.SpeedKn:0.0} 节" : "静止",
            _aimLocked.Moving ? "#FF34D399" : "#FF9BA4B8");
        Add("弹道飞行", $"{_aimLocked.Flight:0.00} 秒");
        Add("提前量", _aimLocked.LeadText,
            _aimLocked.Moving ? "#FF34D399" : "#FF9BA4B8");
        Add("方位修正", _aimLocked.BrgText, "#FF60A5FA");
        Add("命中估计", _aimLocked.HitText,
            _aimLocked.HitProb >= 0.5 ? "#FF34D399" :
            _aimLocked.HitProb >= 0.3 ? "#FFF59E0B" : "#FFF87171");
        if (!_aimLocked.Visible)
            Add("提示", "该目标当前不可见，位置数据可能已过期", "#FFF87171");
    }

    // ---------------- 右键锁定 ----------------

    private void LsvAimTargets_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 命中测试：点在某一行上才锁定
        var row = FindDataGridRowItem(e.OriginalSource as DependencyObject);
        if (row?.DataContext is AimTargetView v)
        {
            LockTarget(v);
            e.Handled = true;
        }
    }

    private void LsvAimTargets_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = FindDataGridRowItem(e.OriginalSource as DependencyObject);
        // 没点在行上就屏蔽右键菜单，统一走"右键即锁定"的交互
        e.Handled = row?.DataContext is AimTargetView;
    }

    private void LockTarget(AimTargetView v)
    {
        // 再次右键同一目标 = 解除锁定
        if (_aimLocked != null && _aimLocked.Eid == v.Eid) _aimLocked = null;
        else _aimLocked = v;
        RefreshAimUi();
        if (_aimLocked != null)
            AppLog.Info($"预瞄锁定：{_aimLocked.Name} 距 {_aimLocked.Dist:0} m 提前 {_aimLocked.Lead:0} m");
    }

    private static ListViewItem? FindDataGridRowItem(DependencyObject? src)
    {
        while (src != null && src is not Visual)
            src = VisualTreeHelper.GetParent(src);
        while (src != null)
        {
            if (src is ListViewItem item) return item;
            src = VisualTreeHelper.GetParent(src);
        }
        return null;
    }
}
