using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WoWSBattleAssistant.Views;

/// <summary>
/// 全屏框选窗口。用户拖拽选择小地图区域，返回物理像素坐标的 Rect。
/// 用 Win32 GetCursorPos 获取物理坐标，与 Graphics.CopyFromScreen 一致，避免 DPI 错位。
/// </summary>
public partial class RegionSelectorWindow : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private Point _startPhys; // 物理像素起点
    private Point _endPhys;
    private bool _isDragging;
    private double _dpiScaleX = 1.0, _dpiScaleY = 1.0;
    private double _virtLeft, _virtTop; // 虚拟桌面左上角（物理像素）

    /// <summary>框选结果（物理像素坐标）</summary>
    public Rect SelectedRegion { get; private set; } = Rect.Empty;

    public RegionSelectorWindow()
    {
        InitializeComponent();
        Loaded += RegionSelectorWindow_Loaded;
        KeyDown += RegionSelectorWindow_KeyDown;
    }

    private void RegionSelectorWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 覆盖整个虚拟桌面（多显示器也能框选）
        _virtLeft = SystemParameters.VirtualScreenLeft;
        _virtTop = SystemParameters.VirtualScreenTop;
        Left = _virtLeft;
        Top = _virtTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // 计算 DPI 缩放（WPF 坐标 -> 物理像素）
        var src = PresentationSource.FromVisual(this);
        if (src != null)
        {
            _dpiScaleX = src.CompositionTarget!.TransformToDevice.M11;
            _dpiScaleY = src.CompositionTarget.TransformToDevice.M22;
        }
    }

    private void RegionSelectorWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SelectedRegion = Rect.Empty;
            DialogResult = false;
            Close();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _isDragging = true;
        _startPhys = GetPhysicalCursorPos();
        _endPhys = _startPhys;
        UpdateSelectionVisual();
        CaptureMouse();
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_isDragging) return;
        _endPhys = GetPhysicalCursorPos();
        UpdateSelectionVisual();
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ReleaseMouseCapture();
        _endPhys = GetPhysicalCursorPos();

        var x = Math.Min(_startPhys.X, _endPhys.X);
        var y = Math.Min(_startPhys.Y, _endPhys.Y);
        var w = Math.Abs(_endPhys.X - _startPhys.X);
        var h = Math.Abs(_endPhys.Y - _startPhys.Y);

        if (w < 10 || h < 10)
        {
            // 选区过小，视为取消
            SelectedRegion = Rect.Empty;
            DialogResult = false;
        }
        else
        {
            SelectedRegion = new Rect(x, y, w, h);
            DialogResult = true;
        }
        Close();
        base.OnMouseLeftButtonUp(e);
    }

    /// <summary>获取物理像素坐标的鼠标位置</summary>
    private Point GetPhysicalCursorPos()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    /// <summary>把物理坐标转换为 Canvas 内的 WPF 坐标用于显示</summary>
    private Point PhysToCanvas(Point phys)
    {
        // 物理桌面坐标 -> 本窗口 WPF 坐标
        // 窗口左上角在虚拟桌面的位置（WPF 坐标）= _virtLeft/_virtTop（SystemParameters 返回 WPF 坐标）
        // 物理坐标 = (WPF坐标) * dpi
        // 所以 WPF坐标 = 物理坐标 / dpi
        var wx = (phys.X / _dpiScaleX) - _virtLeft;
        var wy = (phys.Y / _dpiScaleY) - _virtTop;
        return new Point(wx, wy);
    }

    private void UpdateSelectionVisual()
    {
        if (!_isDragging)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var p1 = PhysToCanvas(_startPhys);
        var p2 = PhysToCanvas(_endPhys);
        var x = Math.Min(p1.X, p2.X);
        var y = Math.Min(p1.Y, p2.Y);
        var w = Math.Abs(p2.X - p1.X);
        var h = Math.Abs(p2.Y - p1.Y);

        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;
        SelectionRect.Visibility = Visibility.Visible;

        var physW = Math.Abs(_endPhys.X - _startPhys.X);
        var physH = Math.Abs(_endPhys.Y - _startPhys.Y);
        SizeBadgeText.Text = $"{(int)physW} × {(int)physH} px";
        Canvas.SetLeft(SizeBadge, x + w + 6);
        Canvas.SetTop(SizeBadge, y + h + 6);
        SizeBadge.Visibility = Visibility.Visible;

        SizeTip.Text = $"物理像素: {(int)physW} × {(int)physH} px";
    }
}
