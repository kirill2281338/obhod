using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Obhod.Core;

namespace Obhod;

/// <summary>Главный экран: три большие кнопки и выезжающая снизу панель (сервисы, тесты, VPN, обновления, настройки). Темы.</summary>
public partial class MainWindow
{
    private bool _drawerOpen;

    /// <summary>Весь текст интерфейса — строчными буквами.</summary>
    private static string Lc(string? s) => (s ?? "").ToLowerInvariant();
    private bool _layoutHooked;

    private void HookLayout()
    {
        if (_layoutHooked) return;
        _layoutHooked = true;
        ContentGrid.SizeChanged += (_, _) =>
        {
            if (!_drawerOpen) return;
            DrawerHost.BeginAnimation(HeightProperty, null);
            DrawerHost.Height = DrawerTarget();
        };
    }

    /// <summary>Панель занимает всё, кроме полосы с тремя кнопками (они при этом плавно уменьшаются).</summary>
    private double DrawerTarget() => Math.Max(300, ContentGrid.ActualHeight - 122);

    private void OpenDrawer(RadioButton tab)
    {
        HookLayout();
        bool sameTab = tab.IsChecked == true;
        tab.IsChecked = true;
        if (_drawerOpen && sameTab) { AnimateDrawer(false); return; } // повторное нажатие сворачивает
        if (!_drawerOpen) AnimateDrawer(true);
    }

    /// <summary>
    /// Компактные карточки: пока открыта панель, три кнопки сжимаются в узкую полосу
    /// (название, статус и маленькая кнопка с цветным кольцом), чтобы списку досталось почти всё окно.
    /// </summary>
    private void SetCompact(bool compact)
    {
        var cards = new (Viewbox box, Border pill, FrameworkElement sub, FrameworkElement action, FrameworkElement ctrl, Border card)[]
        {
            (TBox, TPill, TSub, TAction, TCtrl, TCard), (ZBox, ZPill, ZSub, ZAction, ZCtrl, ZCard), (VBox, VPill, VSub, VAction, VCtrl, VCard),
        };
        foreach (var (box, pill, sub, action, ctrl, card) in cards)
        {
            Grid.SetRow(box, compact ? 0 : 1);
            box.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            box.VerticalAlignment = compact ? VerticalAlignment.Center : VerticalAlignment.Stretch;
            box.Width = compact ? 56 : double.NaN;
            box.Height = compact ? 56 : double.NaN;
            box.Margin = compact ? new Thickness(0) : new Thickness(0, 12, 0, 12);
            pill.Margin = compact ? new Thickness(0, 6, 70, 0) : new Thickness(0, 6, 0, 0);
            sub.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            action.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ctrl.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(380)));
        }
    }

    private void AnimateDrawer(bool open)
    {
        _drawerOpen = open;
        SetCompact(open);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var dur = TimeSpan.FromMilliseconds(open ? 520 : 380);
        DrawerHost.BeginAnimation(HeightProperty, new DoubleAnimation(open ? DrawerTarget() : 0, dur) { EasingFunction = ease });
        Drawer.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 420 : 240)));
        DrawerT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(open ? 0 : 40, dur)
        { EasingFunction = new BackEase { Amplitude = open ? 0.25 : 0, EasingMode = EasingMode.EaseOut } });
        ((RotateTransform)ZMoreArrow.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(open && (TabServices.IsChecked == true || TabTests.IsChecked == true) ? 180 : 0, TimeSpan.FromMilliseconds(300)));
    }

    private void ZMore_Click(object sender, RoutedEventArgs e) =>
        OpenDrawer(TabTests.IsChecked == true ? TabTests : TabServices);

    private void VMore_Click(object sender, RoutedEventArgs e)
    {
        OpenDrawer(TabVpn);
        if (_drawerOpen && SelectedVpn == null) VpnInput.Focus();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenDrawer(TabSettings);

    private void OpenDns_Click(object sender, RoutedEventArgs e)
    {
        try { new DnsWindow { Owner = this }.ShowDialog(); } catch (Exception ex) { Log.Write("dns: " + ex.Message); }
    }

    private void CloseDrawer_Click(object sender, RoutedEventArgs e) => AnimateDrawer(false);

    private async void VPower_Click(object sender, RoutedEventArgs e)
    {
        bool want = VPower.IsChecked == true;
        VPower.IsChecked = !want;
        if (want && (V.Servers.Count == 0 || !VpnEngine.XrayInstalled || (V.Mode == "tun" && !VpnEngine.SingInstalled)))
        {
            OpenDrawer(TabVpn);
            Toast(V.Servers.Count == 0 ? "добавьте ссылку или подписку vpn." : "сначала скачайте ядро vpn кнопкой «скачать».");
            return;
        }
        await VpnConnectAsync(want);
    }

    // ---------- Темы ----------

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _s.Theme = _s.Theme == "black" ? "graphite" : "black";
        _s.Save();
        ApplyTheme(_s.Theme);
        Toast(_s.Theme == "black" ? "тема: чёрная" : "тема: графит");
    }

    private static void ApplyTheme(string theme)
    {
        var res = Application.Current.Resources;
        bool black = theme == "black";
        var bg = new RadialGradientBrush { Center = new Point(0.3, 0.2), GradientOrigin = new Point(0.3, 0.2), RadiusX = 1.1, RadiusY = 1.2 };
        if (black)
        {
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0x14, 0x14, 0x16), 0));
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0x08, 0x08, 0x09), 0.55));
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0x00, 0x00, 0x00), 1));
        }
        else
        {
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0x30, 0x30, 0x33), 0));
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0x23, 0x23, 0x25), 0.55));
            bg.GradientStops.Add(new GradientStop(Color.FromRgb(0x18, 0x18, 0x1A), 1));
        }
        bg.Freeze();
        res["BgBrush"] = bg;
        res["ShapesOpacity"] = black ? 0.5 : 1.0;

        var glass = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        glass.GradientStops.Add(new GradientStop(Color.FromArgb(black ? (byte)0x12 : (byte)0x17, 255, 255, 255), 0));
        glass.GradientStops.Add(new GradientStop(Color.FromArgb(black ? (byte)0x05 : (byte)0x08, 255, 255, 255), 0.45));
        glass.GradientStops.Add(new GradientStop(Color.FromArgb(black ? (byte)0x0A : (byte)0x0F, 255, 255, 255), 1));
        glass.Freeze();
        res["GlassFill"] = glass;
    }
}
