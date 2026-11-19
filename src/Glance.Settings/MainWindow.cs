// The Settings window: a list of pages on the left, the selected page on the right. Every control saves at once
// (settings.json, a quarter of a second after the last change) and the running widget applies it within a second.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Glance.SetupApp;
using Wpf.Ui.Controls;

namespace Glance.SettingsApp;

sealed class MainWindow : FluentWindow
{
    readonly Settings settings;
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly System.Windows.Controls.ListBox nav = new();
    readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly Pages pages;
    readonly System.Windows.Controls.TextBlock savedNote = new() { Opacity = 0, Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };

    static readonly (string Id, string Title, SymbolRegular Icon)[] PageList =
    {
        ("welcome", "Welcome", SymbolRegular.HandWave24),
        ("general", "General", SymbolRegular.Settings24),
        ("views", "Views", SymbolRegular.Grid24),
        ("weather", "Weather", SymbolRegular.WeatherPartlyCloudyDay24),
        ("agenda", "Calendar & tasks", SymbolRegular.CalendarLtr24),
        ("music", "Music", SymbolRegular.MusicNote224),
        ("countdowns", "Countdowns", SymbolRegular.Timer24),
        ("appearance", "Appearance", SymbolRegular.PaintBrush24),
        ("accessibility", "Accessibility", SymbolRegular.Accessibility24),
        ("updates", "Updates", SymbolRegular.ArrowSync24),
        ("about", "About", SymbolRegular.Info24),
    };

    public MainWindow(Settings settings, string startPage)
    {
        this.settings = settings;
        pages = new Pages(settings, Changed, this);
        Title = App.WindowTitle;
        Width = 1000;
        Height = 760;
        MinWidth = 760;
        MinHeight = 520;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowCornerPreference = WindowCornerPreference.Round;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/glance.png"));
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        // Test copies open on another monitor and never take focus (GLANCE_DEV_INSTANCE=1).
        if (Environment.GetEnvironmentVariable("GLANCE_DEV_INSTANCE") == "1")
        {
            ShowActivated = false;
            if (DevWindowPlacement.InitialPosition() is Point p) { WindowStartupLocation = WindowStartupLocation.Manual; Left = p.X; Top = p.Y; }
            Loaded += (s, e) => DevWindowPlacement.FitToMonitor(this);
        }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titleBar = new TitleBar { Title = App.WindowTitle, ShowMaximize = true, Icon = new ImageIcon { Source = Icon } };
        root.Children.Add(titleBar);

        var body = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(body, 1);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(body);

        nav.Margin = new Thickness(10, 6, 6, 12);
        nav.ItemContainerStyle = NavItemStyle();
        nav.BorderThickness = new Thickness(0);
        nav.Background = Brushes.Transparent;
        foreach (var (id, title, icon) in PageList)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 3, 2, 3) };
            row.Children.Add(new SymbolIcon { Symbol = icon, FontSize = 20, Margin = new Thickness(0, 0, 12, 0) });
            row.Children.Add(new System.Windows.Controls.TextBlock { Text = title, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            nav.Items.Add(new ListBoxItem { Content = row, Tag = id, ToolTip = title });
        }
        nav.SelectionChanged += (s, e) =>
        {
            if (nav.SelectedItem is ListBoxItem item) Show((string)item.Tag);
        };
        body.Children.Add(nav);

        var right = new Grid();
        Grid.SetColumn(right, 1);
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.Children.Add(scroller);
        savedNote.HorizontalAlignment = HorizontalAlignment.Right;
        savedNote.VerticalAlignment = VerticalAlignment.Top;
        savedNote.Margin = new Thickness(0, 26, 34, 0);
        savedNote.Text = "Saved";
        savedNote.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        right.Children.Add(savedNote);
        body.Children.Add(right);
        Content = root;

        saveTimer.Tick += (s, e) =>
        {
            saveTimer.Stop();
            if (Settings.Save(settings)) FlashSaved();
        };
        Closing += (s, e) => { if (saveTimer.IsEnabled) { saveTimer.Stop(); Settings.Save(settings); } };

        int start = Array.FindIndex(PageList, p => p.Id == startPage);
        nav.SelectedIndex = start >= 0 ? start : 1;
    }

    // Draws the window's content (title bar, page list, the page) to a PNG without showing the window. The
    // backdrop is Windows' Mica on screen; here it's a plain dark fill.
    public static void RenderTo(Settings settings, string page, string file, int width, int height)
    {
        var w = new MainWindow(settings, page) { ShowInTaskbar = false };
        var root = (UIElement)w.Content;
        w.Content = null;
        var host = new Border { Background = new SolidColorBrush(Color.FromRgb(32, 30, 32)), Child = root, Width = width, Height = height };
        host.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(file);
        enc.Save(fs);
        w.saveTimer.Stop();
        w.Close();
    }

    // Page list items like Windows Settings: a soft highlight and a short accent bar on the selected page.
    static Style NavItemStyle() => (Style)System.Windows.Markup.XamlReader.Parse(@"
<Style TargetType='ListBoxItem' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Setter Property='Foreground' Value='{DynamicResource TextFillColorPrimaryBrush}' />
  <Setter Property='FocusVisualStyle' Value='{x:Null}' />
  <Setter Property='Cursor' Value='Hand' />
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='ListBoxItem'>
        <Grid Margin='0,1,0,1'>
          <Border x:Name='bg' CornerRadius='5' Background='Transparent' />
          <Border x:Name='bar' Width='3' Height='18' CornerRadius='1.5' HorizontalAlignment='Left' Margin='0,0,0,0'
                  Background='{DynamicResource GlanceAccentBrush}' Visibility='Collapsed' />
          <ContentPresenter Margin='12,8,10,8' VerticalAlignment='Center' />
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bg' Property='Background' Value='#12FFFFFF' /></Trigger>
          <Trigger Property='IsSelected' Value='True'>
            <Setter TargetName='bg' Property='Background' Value='#1CFFFFFF' />
            <Setter TargetName='bar' Property='Visibility' Value='Visible' />
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>");

    void Changed()
    {
        saveTimer.Stop();
        saveTimer.Start();
    }

    void FlashSaved()
    {
        savedNote.BeginAnimation(OpacityProperty, null);
        var a = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(120)));
        a.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(1300)));
        a.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(1700)));
        savedNote.BeginAnimation(OpacityProperty, a);
    }

    public void Show(string id)
    {
        int i = Array.FindIndex(PageList, p => p.Id == id);
        if (i >= 0 && nav.SelectedIndex != i) { nav.SelectedIndex = i; return; }
        scroller.Content = pages.Build(id);
        scroller.ScrollToTop();
    }
}
