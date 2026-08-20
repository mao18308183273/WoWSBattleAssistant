using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WoWSBattleAssistant.Models;

namespace WoWSBattleAssistant.Views;

/// <summary>
/// 双方战力对比悬浮窗。小尺寸可拖拽，开局自动显示双方战力总和；
/// 单击展开每个玩家明细（玩家名 ↔ 舰船名 ↔ 战力），右键隐藏。
/// </summary>
public partial class PowerOverlayWindow : Window
{
    private bool _expanded;
    private bool _dragging;
    private Point _mouseDownPos;
    private const double DragThreshold = 4; // 超过此距离才视为拖拽

    /// <summary>最近一次加载的玩家战力数据（供"复制敌方战力"使用）</summary>
    private System.Collections.Generic.List<PlayerThreatInfo> _lastInfos = new();

    public PowerOverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>更新战力数据（开局检测到新对局时调用）</summary>
    public void UpdatePower(IReadOnlyList<PlayerThreatInfo> infos)
    {
        _lastInfos = infos.ToList();

        var team1 = infos.Where(i => i.Relation == 0 || i.Relation == 1).ToList(); // 自己+队友
        var team2 = infos.Where(i => i.Relation == 2).ToList();                    // 敌方

        TxtTeam1Name.Text = $"我方 {team1.Count} 人";
        TxtTeam2Name.Text = $"敌方 {team2.Count} 人";
        TxtTeam1Score.Text = FormatScore(team1);
        TxtTeam2Score.Text = FormatScore(team2);

        PlayerListPanel.Children.Clear();
        AddTeamSection("我方", team1, "#FF7EB8FF");
        AddTeamSection("敌方", team2, "#FFFF7E7E");
    }

    private static string FormatScore(List<PlayerThreatInfo> list)
    {
        var values = list.Where(i => i.SearchHit == true && !i.HasError && i.PrValue > 0)
                         .Select(i => (double)i.PrValue).ToList();
        if (values.Count == 0) return "—";
        var avg = (int)values.Average();
        // 显示平均 PR，已命中的数量标注
        return values.Count < list.Count ? $"~{avg}" : $"{avg}";
    }

    private void AddTeamSection(string title, List<PlayerThreatInfo> list, string colorHex)
    {
        var titleBlock = new TextBlock
        {
            Text = $"── {title}（{list.Count}）──",
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)),
            FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 2)
        };
        PlayerListPanel.Children.Add(titleBlock);

        foreach (var p in list)
            PlayerListPanel.Children.Add(BuildPlayerLine(p));
    }

    private static StackPanel BuildPlayerLine(PlayerThreatInfo p)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

        // 第一行：玩家名 + 战力
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var name = new TextBlock
        {
            Text = p.UserName,
            Foreground = Brushes.White,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 120
        };
        row.Children.Add(name);

        string stat;
        Brush statBrush;
        if (p.HasError)
        {
            stat = "查询失败"; statBrush = Brushes.OrangeRed;
        }
        else if (p.SearchHit == true)
        {
            stat = $"PR {p.PrValue} {p.PrName} · {p.WinRate:0.0}% · {p.Battles}场";
            statBrush = p.PrValue >= 1500 ? Brushes.LightGreen : Brushes.Gray;
        }
        else if (p.SearchHit == false)
        {
            stat = "未搜到(疑人机)"; statBrush = Brushes.Gray;
        }
        else
        {
            stat = "未知"; statBrush = Brushes.Gray;
        }

        var statBlock = new TextBlock { Text = stat, Foreground = statBrush, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(statBlock);
        wrap.Children.Add(row);

        // 第二行：该玩家驾驶的舰船（与玩家名一一对应）
        var shipBlock = new TextBlock
        {
            Text = string.IsNullOrEmpty(p.ShipName) ? "（未识别舰船）" : $"⚓ {p.ShipName}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0xB8, 0xFF)),
            FontSize = 10,
            Margin = new Thickness(14, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        wrap.Children.Add(shipBlock);

        return wrap;
    }

    // ===== 一键复制敌方战力（粘贴到游戏内聊天/微信，给队友提供支持）=====

    private void BtnCopyEnemy_Click(object sender, RoutedEventArgs e)
    {
        var my = _lastInfos.Where(i => i.Relation == 0 || i.Relation == 1).ToList(); // 我方
        var enemy = _lastInfos.Where(i => i.Relation == 2).ToList();                  // 敌方
        if (my.Count + enemy.Count == 0)
        {
            SetCopyFeedback("暂无数据：请先开局并查询战力（主界面 ⚔）");
            return;
        }

        var sb = new StringBuilder();

        // 1) 双方战力对比（平均 PR，多少 VS 多少）
        double? myAvg = AvgPr(my), enAvg = AvgPr(enemy);
        string myTxt = myAvg.HasValue ? (HasUnknown(my) ? $"~{(int)myAvg}" : $"{(int)myAvg}") : "—";
        string enTxt = enAvg.HasValue ? (HasUnknown(enemy) ? $"~{(int)enAvg}" : $"{(int)enAvg}") : "—";
        sb.AppendLine($"【双方战力】我方 {myTxt} VS 敌方 {enTxt}");

        // 2) 敌方清单：人名 ｜ 船 ｜ 战力（每人一行）
        sb.AppendLine("敌方：");
        for (int i = 0; i < enemy.Count; i++)
        {
            var p = enemy[i];
            string stat = p.HasError ? "查询失败"
                : p.SearchHit == true ? $"PR {p.PrValue}"
                : p.SearchHit == false ? "未搜到" : "未查询";
            var ship = string.IsNullOrEmpty(p.ShipName) ? "?" : p.ShipName;
            sb.AppendLine($"{i + 1}. {p.UserName}｜{ship}｜{stat}");
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            SetCopyFeedback($"✅ 已复制敌方 {enemy.Count} 人战力");
        }
        catch (Exception ex)
        {
            SetCopyFeedback($"❌ 复制失败: {ex.Message}");
        }
    }

    /// <summary>队伍平均 PR（仅统计查询命中且有值的玩家）</summary>
    private static double? AvgPr(List<PlayerThreatInfo> list)
    {
        var vals = list.Where(i => i.SearchHit == true && !i.HasError && i.PrValue > 0)
                       .Select(i => (double)i.PrValue).ToList();
        return vals.Count == 0 ? (double?)null : vals.Average();
    }

    /// <summary>队伍中是否还有未统计到的玩家（命中数 &lt; 总人数，平均 PR 加"~"）</summary>
    private static bool HasUnknown(List<PlayerThreatInfo> list)
    {
        int counted = list.Count(i => i.SearchHit == true && !i.HasError && i.PrValue > 0);
        return counted > 0 && counted < list.Count;
    }

    /// <summary>复制反馈：按钮文字短暂变化后恢复</summary>
    private void SetCopyFeedback(string text)
    {
        var original = "📋 复制敌方战力";
        BtnCopyEnemy.Content = text;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        timer.Tick += (_, _) =>
        {
            BtnCopyEnemy.Content = original;
            timer.Stop();
        };
        timer.Start();
    }

    // ===== 交互：按下记录起点，松开判断是单击还是拖拽 =====

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _mouseDownPos = e.GetPosition(this);
        _dragging = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var pos = e.GetPosition(this);
            if (!_dragging && (Math.Abs(pos.X - _mouseDownPos.X) > DragThreshold ||
                               Math.Abs(pos.Y - _mouseDownPos.Y) > DragThreshold))
            {
                _dragging = true; // 进入拖拽模式
            }
            if (_dragging)
            {
                Left += pos.X - _mouseDownPos.X;
                Top += pos.Y - _mouseDownPos.Y;
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
        {
            ToggleExpand(); // 单击（未拖拽）→ 展开/收起
        }
        _dragging = false;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Hide(); // 右键隐藏悬浮窗
    }

    private void ToggleExpand()
    {
        _expanded = !_expanded;
        if (_expanded)
        {
            DetailPanel.Visibility = Visibility.Visible;
            DetailPanel.Height = double.NaN; // 自适应内容（受 ScrollViewer MaxHeight 限制）
            Height = 320;
        }
        else
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            DetailPanel.Height = 0;
            Height = 64;
        }
    }
}
