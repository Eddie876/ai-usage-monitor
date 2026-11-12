using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace AiUsageWidget;

public partial class MainWindow : Window
{
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly List<Usage> readings = new();
    private bool exiting, refreshing, loaded;
    private readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiUsageWidget", "window.json");
    private record WindowPosition(double Left, double Top);

    public MainWindow()
    {
        InitializeComponent();
        trayIcon = CreateTrayIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "AI 額度", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("顯示／隱藏", null, (_, _) => ToggleWindow());
        menu.Items.Add("立即更新", null, async (_, _) => await Refresh());
        menu.Items.Add("複製 Claude 連線狀態", null, (_, _) =>
        {
            try { Clipboard.SetText(ClaudeLive.DiagnosticStatus()); }
            catch (System.Runtime.InteropServices.ExternalException) { }
        });
        var start = new Forms.ToolStripMenuItem("開機啟動") { Checked = IsAutoStartEnabled(), CheckOnClick = false };
        start.Click += (_, _) => { SetAutoStart(!IsAutoStartEnabled()); start.Checked = IsAutoStartEnabled(); };
        menu.Items.Add(start);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("結束", null, (_, _) => { exiting = true; SavePosition(); tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); System.Windows.Application.Current.Shutdown(); });
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleWindow(); };
        timer.Tick += async (_, _) => await Refresh();
        displayTimer.Tick += (_, _) => { if (readings.Count >= 3) readings[2] = UsageSources.Copilot(); Render(); };
        readings.Add(new("Claude", null, Array.Empty<Limit>(), null, "等待查詢…"));
        readings.Add(new("Codex", null, Array.Empty<Limit>(), null, "等待查詢…"));
        readings.Add(UsageSources.Copilot());
        Render();
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestorePosition(); loaded = true;
        timer.Start(); displayTimer.Start();
        await Refresh();
    }
    private async Task Refresh()
    {
        if (refreshing) return;
        refreshing = true; RefreshButton.IsEnabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var claudeTask = ClaudeLive.Fetch(timeout.Token);
            var codexTask = UsageSources.Codex(timeout.Token);
            var liveClaude = await claudeTask;
            var claude = liveClaude ?? await Task.Run(() => UsageSources.Claude());
            if (liveClaude is null) claude = claude with { Note = $"{ClaudeLive.LastFailure}\n{claude.Note}" };
            var codex = await codexTask;
            readings.Clear(); readings.Add(claude); readings.Add(codex); readings.Add(UsageSources.Copilot());
            Render();
        }
        finally { refreshing = false; RefreshButton.IsEnabled = true; }
    }
    private void Render()
    {
        Cards.Children.Clear();
        foreach (var usage in readings) Cards.Children.Add(Card(usage));
    }
    private Border Card(Usage u)
    {
        var card = new Border { Background = Brush("#181B21"), BorderBrush = Brush("#262A33"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10,8,10,6), Margin = new Thickness(0,0,0,6) };
        var content = new StackPanel(); card.Child = content;
        var heading = new DockPanel { LastChildFill = false, Margin = new Thickness(0,0,0,5) };
        heading.Children.Add(new TextBlock { Text = u.Provider, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var plan = u.Provider == "Claude" ? "Team" : u.Plan;
        if (!string.IsNullOrWhiteSpace(plan))
        {
            var badge = new Border { BorderBrush = Brush("#262A33"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(7,1,7,1), Margin = new Thickness(8,0,0,0) };
            badge.Child = new TextBlock { Text = plan, Foreground = Brush("#9AA3AD"), FontSize = 11, FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(badge, Dock.Right);
            heading.Children.Add(badge);
        }
        content.Children.Add(heading);
        foreach (var limit in u.Limits)
        {
            var row = new StackPanel { Margin = new Thickness(0,0,0,5), Opacity = u.Stale ? .5 : 1 };
            var rowHead = new Grid { Margin = new Thickness(0,0,0,5) };
            rowHead.ColumnDefinitions.Add(new ColumnDefinition()); rowHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowHead.Children.Add(new TextBlock { Text = limit.Label });
            var expired = limit.Reset is { } reset && reset <= DateTimeOffset.UtcNow;
            var remaining = new TextBlock { Text = expired ? "待更新" : $"剩餘 {Math.Max(0,100-limit.Used):0}%", FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(remaining, 1); rowHead.Children.Add(remaining); row.Children.Add(rowHead);
            var track = new ProgressBar { Height = 8, Minimum = 0, Maximum = 100, Value = expired ? 0 : Math.Clamp(limit.Used, 0, 100), Foreground = Brush(u.Stale || expired ? "#6B7480" : limit.Used >= 90 ? "#F85149" : limit.Used >= 70 ? "#E3A008" : "#3FB950"), Background = Brush("#2A2F39"), BorderThickness = new Thickness(0) };
            track.Template = ProgressTemplate(); row.Children.Add(track);
            row.Children.Add(new TextBlock { Text = ResetText(limit.Reset), FontSize = 12, Foreground = Brush("#9AA3AD"), Margin = new Thickness(0,4,0,0) });
            content.Children.Add(row);
        }
        if (u.Limits.Count == 0) content.Children.Add(new TextBlock { Text = u.Observed is not null ? "沒有額度上限或資料" : "尚未連接", FontSize = 12, Foreground = Brush("#9AA3AD") });
        else if (u.Stale) content.Children.Add(new TextBlock { Text = "資料可能已過期", FontSize = 12, Foreground = Brush("#E3A008") });
        return card;
    }
    private static ControlTemplate ProgressTemplate()
    {
        var outer = new FrameworkElementFactory(typeof(Border)); outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(4)); outer.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty)); outer.SetValue(Border.ClipToBoundsProperty, true);
        var grid = new FrameworkElementFactory(typeof(Grid)); grid.Name = "PART_Track"; outer.AppendChild(grid);
        var indicator = new FrameworkElementFactory(typeof(Border)); indicator.Name = "PART_Indicator"; indicator.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.ForegroundProperty)); indicator.SetValue(Border.CornerRadiusProperty, new CornerRadius(4)); indicator.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left); grid.AppendChild(indicator);
        var template = new ControlTemplate(typeof(ProgressBar)) { VisualTree = outer }; return template;
    }
    private static Brush Brush(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("AiUsageWidget.icon.ico") ?? throw new InvalidOperationException("Application icon is missing.");
        using var icon = new System.Drawing.Icon(stream);
        using var source = icon.ToBitmap();
        int left = source.Width, top = source.Height, right = -1, bottom = -1;
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                if (source.GetPixel(x, y).A >= 32)
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
        if (right < left || bottom < top) return (System.Drawing.Icon)icon.Clone();
        var width = right - left + 1; var height = bottom - top + 1;
        const int size = 64, padding = 0;
        var scale = (double)(size - 2 * padding) / Math.Max(width, height);
        var targetWidth = (float)(width * scale); var targetHeight = (float)(height * scale);
        using var bitmap = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new System.Drawing.RectangleF((size - targetWidth) / 2, (size - targetHeight) / 2, targetWidth, targetHeight), new System.Drawing.RectangleF(left, top, width, height), System.Drawing.GraphicsUnit.Pixel);
        }
        var handle = bitmap.GetHicon();
        try { using var resized = System.Drawing.Icon.FromHandle(handle); return (System.Drawing.Icon)resized.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
    private static string ResetText(DateTimeOffset? date)
    {
        if (date is null) return "重置時間未知";
        var time = date.Value - DateTimeOffset.UtcNow;
        if (time <= TimeSpan.Zero) return "已到重置時間，等待更新";
        if (time.TotalMinutes < 1) return "不到 1 分鐘後重置";
        if (time.TotalDays >= 1) return $"{(int)time.TotalDays} 天 {time.Hours} 小時後重置";
        if (time.TotalHours >= 1) return $"{(int)time.TotalHours} 小時 {time.Minutes} 分後重置";
        return $"{time.Minutes} 分鐘後重置";
    }
    private void ToggleWindow() { if (IsVisible) Hide(); else { Show(); WindowState = WindowState.Normal; Activate(); } }
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
        for (var element = e.OriginalSource as DependencyObject; element is not null; element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is System.Windows.Controls.Primitives.ButtonBase || element is System.Windows.Controls.Primitives.ScrollBar) return;
        DragMove();
        if (loaded) SavePosition();
    }
    private void OnActivated(object? sender, EventArgs e) => Opacity = 1;
    private void OnDeactivated(object? sender, EventArgs e) => Opacity = .72;
    private async void OnRefresh(object sender, RoutedEventArgs e) => await Refresh();
    private void OnHide(object sender, RoutedEventArgs e) => Hide();
    private void OnClosing(object? sender, CancelEventArgs e) { if (!exiting) { e.Cancel = true; Hide(); } }
    private void RestorePosition()
    {
        try
        {
            WindowPosition? position = File.Exists(settingsFile) ? JsonSerializer.Deserialize<WindowPosition>(File.ReadAllText(settingsFile)) : null;
            // WPF positions are device-independent; use the primary working area for the default and fallback.
            var area = SystemParameters.WorkArea;
            if (position is not null && double.IsFinite(position.Left) && double.IsFinite(position.Top) && Forms.Screen.AllScreens.Any(s => { var scale = VisualTreeHelper.GetDpi(this); var x = position.Left * scale.DpiScaleX; var y = position.Top * scale.DpiScaleY; return x >= s.WorkingArea.Left - 100 && x < s.WorkingArea.Right - 30 && y >= s.WorkingArea.Top && y < s.WorkingArea.Bottom - 30; })) { Left = position.Left; Top = position.Top; }
            else { Left = Math.Max(area.Left, area.Right - Width - 16); Top = area.Top + 16; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { var area = SystemParameters.WorkArea; Left = area.Right - Width - 16; Top = area.Top + 16; }
    }
    private void SavePosition()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!); File.WriteAllText(settingsFile, JsonSerializer.Serialize(new WindowPosition(Left, Top))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    private static bool IsAutoStartEnabled()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("AiUsageWidget") is string; }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
    private static void SetAutoStart(bool enabled)
    {
        try { using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); if (enabled && Environment.ProcessPath is { } path) key.SetValue("AiUsageWidget", $"\"{path}\""); else key.DeleteValue("AiUsageWidget", false); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }
}
