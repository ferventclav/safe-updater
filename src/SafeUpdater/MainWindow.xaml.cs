using System.Diagnostics;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SafeUpdater.Core;

namespace SafeUpdater;

public sealed partial class MainWindow : Window
{
    private static readonly string SmokeScript = Path.Combine(AppContext.BaseDirectory, "Assets", "smoke-test.py");

    private readonly Settings _settings = Settings.Load();
    private readonly Dictionary<UpdateSource, GroupView> _groups = [];
    private readonly List<UpdateItem> _items = [];
    private readonly Dictionary<UpdateSource, SourceResult> _results = [];
    private readonly Dictionary<string, bool> _manual = [];
    private readonly Expander _hiddenExpander;
    private bool _rustupPresent;
    private readonly StackPanel _hiddenList = new() { Spacing = 12 };
    private bool _busy;
    private int _pendingChecks;
    private RowView? _shownRow;
    private string? _lastRunLog;

    public MainWindow()
    {
        InitializeComponent();

        // Windows 11 look, as in Safe Disk Cleaner: Mica BaseAlt, content in the title bar, system caption buttons.
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        string ico = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(ico))
        {
            AppWindow.SetIcon(ico);
        }
        string png = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png");
        if (File.Exists(png))
        {
            TitleBarIcon.Source = new BitmapImage(new Uri(png));
        }
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WindowSizing.SetMinimumSize(hwnd, 900, 600);
        WindowSizing.ResizeAndCenter(AppWindow, hwnd, 1320, 860);

        Activated += (_, e) => TitleBarText.Opacity = e.WindowActivationState == WindowActivationState.Deactivated ? 0.5 : 1.0;
        RootGrid.SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        MajorsToggle.IsChecked = _settings.IncludeMajors;
        TestToggle.IsChecked = _settings.TestAfterUpdate;
        RestoreToggle.IsChecked = _settings.RestorePoint;

        foreach (SourceCheck s in Checker.Sources)
        {
            var g = new GroupView(this, s.Source);
            _groups[s.Source] = g;
            GroupsPanel.Children.Add(g.Card);
        }
        // Unsafe updates: never listed with a checkbox, only named here with the reason.
        _hiddenExpander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = _hiddenList,
            Visibility = Visibility.Collapsed,
        };
        GroupsPanel.Children.Add(_hiddenExpander);
        ShowDetailsIntro();
        RootGrid.Loaded += async (_, _) => await CheckAsync();
    }

    public void ShowError(string title, string message)
    {
        ErrorInfoBar.Title = title;
        ErrorInfoBar.Message = message;
        ErrorInfoBar.IsOpen = true;
    }

    // ---------------------------------------------------------------- checking

    private async Task CheckAsync()
    {
        if (_busy)
        {
            return;
        }
        SetBusy(true, "Checking apps, Windows, drivers and dev tools…");
        WorkBar.IsIndeterminate = true;
        ResultInfoBar.IsOpen = false;
        NoticeInfoBar.IsOpen = false;
        _items.Clear();
        _results.Clear();
        _manual.Clear();
        _rustupPresent = Tools.Rustup() is not null;
        _pendingChecks = Checker.Sources.Count;
        foreach (GroupView g in _groups.Values)
        {
            g.SetChecking();
        }
        RebuildHidden();
        UpdateSummary();

        await Task.WhenAll(Checker.Sources.Select(async s =>
        {
            SourceResult res;
            try
            {
                res = await Task.Run(() => s.Check(CancellationToken.None));
            }
            catch (Exception ex)
            {
                RunLog.WriteError(ex.ToString());
                res = new SourceResult { Source = s.Source, Failed = true, Message = "The check failed: " + ex.Message };
            }
            // Back on the UI thread here (the window's synchronisation context).
            _pendingChecks--;
            ShowResult(res);
        }));

        _settings.LastChecked = DateTime.Now;
        _settings.Save();
        WorkBar.IsIndeterminate = false;
        SetBusy(false, _items.Any(i => !i.Hidden && i.Selectable) ? "Untick anything you'd rather leave, then press Update selected." : "Nothing safe to update.");

        List<string> warnings = Preflight.Warnings();
        if (warnings.Count > 0)
        {
            NoticeInfoBar.Severity = InfoBarSeverity.Warning;
            NoticeInfoBar.Title = "Before you update";
            NoticeInfoBar.Message = string.Join(" ", warnings);
            NoticeInfoBar.IsOpen = true;
        }
    }

    private void ShowResult(SourceResult r)
    {
        _results[r.Source] = r;
        Reapply();
    }

    /// <summary>
    /// Runs the rules over every group together (some look across groups, like Chocolatey owning a winget app),
    /// keeps the ticks you changed by hand, and redraws.
    /// </summary>
    private void Reapply()
    {
        _items.Clear();
        foreach (SourceResult res in _results.Values)
        {
            _items.AddRange(res.Items);
        }
        IReadOnlyCollection<string> choco = _results.TryGetValue(UpdateSource.Chocolatey, out SourceResult? c) ? c.Managed : [];
        Policy.ApplyAll(_items, PolicyContext.From(_settings, choco, _rustupPresent));
        foreach (UpdateItem i in _items.Where(i => !i.Hidden && i.Selectable))
        {
            if (_manual.TryGetValue(i.Key, out bool on))
            {
                i.Selected = on;
            }
        }
        foreach ((UpdateSource source, SourceResult res) in _results)
        {
            _groups[source].Show(res);
        }
        RebuildHidden();
        UpdateSummary();
    }

    private void RememberChoice(UpdateItem item) => _manual[item.Key] = item.Selected;

    private void SetIgnored(UpdateItem item, bool ignored)
    {
        if (ignored)
        {
            _settings.Ignored.Add(item.IgnoreKey);
        }
        else
        {
            _settings.Ignored.Remove(item.IgnoreKey);
        }
        _settings.Save();
        _shownRow = null;
        ShowDetailsIntro();
        Reapply();
    }

    private void RebuildHidden()
    {
        List<UpdateItem> hidden = _items.Where(i => i.Hidden).ToList();
        _hiddenList.Children.Clear();
        foreach (UpdateItem i in hidden.OrderBy(i => i.Source))
        {
            var entry = new StackPanel { Spacing = 2 };
            var head = new TextBlock { Style = Style("BodyStrongTextBlockStyle"), TextWrapping = TextWrapping.Wrap };
            head.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = i.Name });
            string versions = i.Kind is ItemKind.Package && i.Current.Length > 0 ? $"   {i.Current}  →  {i.Available}" : "";
            head.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = versions, FontWeight = Microsoft.UI.Text.FontWeights.Normal });
            entry.Children.Add(head);
            entry.Children.Add(new TextBlock { Text = $"{Checker.Title(i.Source)} · {i.Badge}", Style = Style("MutedCaptionStyle") });
            entry.Children.Add(new TextBlock { Text = i.Note, Style = Style("MutedBodyStyle"), TextWrapping = TextWrapping.Wrap });
            if (i.IgnoredByUser)
            {
                var again = new HyperlinkButton { Content = "Show again", Padding = new Thickness(0, 2, 0, 2) };
                UpdateItem target = i;
                again.Click += (_, _) => SetIgnored(target, ignored: false);
                entry.Children.Add(again);
            }
            _hiddenList.Children.Add(entry);
        }
        _hiddenExpander.Header = $"Left out for safety ({hidden.Count}). These aren't listed above, so they can't be ticked by accident.";
        _hiddenExpander.Visibility = hidden.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSummary()
    {
        int n = _items.Count(i => i.Selectable && !i.Hidden);
        int sel = _items.Count(i => i.Selectable && !i.Hidden && i.Selected);
        int hidden = _items.Count(i => i.Hidden);
        SummaryText.Text = _pendingChecks > 0
            ? $"Checking… {Checker.Sources.Count - _pendingChecks} of {Checker.Sources.Count} done"
            : n == 0 ? "Everything safe is up to date" : $"{n} safe update{(n == 1 ? "" : "s")}";
        SelectedText.Text = (n == 0 ? "" : $"{sel} selected") + (hidden > 0 ? $"{(n == 0 ? "" : ", ")}{hidden} left out for safety" : "");
        UpdateButton.IsEnabled = !_busy && sel > 0;
        UpdateButton.Content = sel > 0 ? $"Update selected ({sel})" : "Update selected";
        AdminInfoBar.IsOpen = !_busy && _items.Any(i => i.Selectable && !i.Hidden && i.Selected && i.NeedsAdmin);

        var parts = new List<string>();
        if (_settings.LastChecked is DateTime c)
        {
            parts.Add($"Checked {When(c)}");
        }
        if (_settings.LastUpdated is DateTime u)
        {
            parts.Add($"last updated {When(u)}");
        }
        LastCheckedText.Text = string.Join(", ", parts);
    }

    private static string When(DateTime t) =>
        t.Date == DateTime.Today ? $"today at {t:HH:mm}" : t.Date == DateTime.Today.AddDays(-1) ? $"yesterday at {t:HH:mm}" : $"{t:d MMM} at {t:HH:mm}";

    // ---------------------------------------------------------------- updating

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        List<UpdateItem> selected = _items.Where(i => i.Selectable && !i.Hidden && i.Selected).ToList();
        if (selected.Count == 0)
        {
            return;
        }
        List<string> open = RunningApps.Find(selected);
        if (open.Count > 0)
        {
            var dlg = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = open.Count == 1 ? "One of these apps is open" : "Some of these apps are open",
                Content = $"{string.Join(", ", open)} {(open.Count == 1 ? "is" : "are")} running. The installer may close {(open.Count == 1 ? "it" : "them")}, so save your work first.",
                PrimaryButtonText = "Update anyway",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        ResultInfoBar.IsOpen = false;
        NoticeInfoBar.IsOpen = false;
        ErrorInfoBar.IsOpen = false;
        SetBusy(true, "Starting…");
        WorkBar.IsIndeterminate = false;
        WorkBar.Maximum = selected.Count;
        WorkBar.Value = 0;

        var run = new UpdateRun(_settings, SmokeScript);
        run.Changed += item => DispatcherQueue.TryEnqueue(() => OnItemChanged(item, selected));
        run.Status += text => DispatcherQueue.TryEnqueue(() => StatusText.Text = text);
        RunSummary summary;
        try
        {
            summary = await Task.Run(() => run.RunAsync(selected));
        }
        catch (Exception ex)
        {
            RunLog.WriteError(ex.ToString());
            ShowError("The update run stopped", ex.Message);
            SetBusy(false, "");
            return;
        }

        _lastRunLog = summary.LogFile;
        ResultInfoBar.Severity = summary.Failed > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        ResultInfoBar.Title = summary.Failed > 0 ? $"{summary.Updated} updated, {summary.Failed} didn't" : $"{summary.Updated} updated";
        var notes = new List<string>(summary.Notes);
        if (summary.Skipped > 0)
        {
            notes.Insert(0, $"{summary.Skipped} kept back or skipped (see each row).");
        }
        ResultInfoBar.Message = string.Join(" ", notes);
        ResultInfoBar.IsOpen = true;
        if (summary.RebootRecommended)
        {
            NoticeInfoBar.Title = "Restart to finish";
            NoticeInfoBar.Message = "Some updates finish installing when Windows restarts.";
            NoticeInfoBar.IsOpen = true;
        }
        SetBusy(false, $"Finished at {DateTime.Now:HH:mm}. Press Check again to see what's left.");
    }

    private void OnItemChanged(UpdateItem item, List<UpdateItem> run)
    {
        if (_groups.TryGetValue(item.Source, out GroupView? g))
        {
            g.Refresh(item);
        }
        WorkBar.Value = run.Count(i => i.Status is ItemStatus.Done or ItemStatus.Failed or ItemStatus.Skipped);
        if (_shownRow?.Item == item)
        {
            ShowDetails(_shownRow);
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        MajorsToggle.IsEnabled = !busy;
        WorkBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = status;
        foreach (GroupView g in _groups.Values)
        {
            g.SetEnabled(!busy);
        }
        UpdateSummary();
    }

    // ---------------------------------------------------------------- commands

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckAsync();

    private void MajorsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.IncludeMajors = MajorsToggle.IsChecked == true;
        _settings.Save();
        // Majors move between the list and the left-out section, so everything is re-decided and redrawn.
        _manual.Clear();
        _shownRow = null;
        ShowDetailsIntro();
        Reapply();
    }

    private void TestToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.TestAfterUpdate = TestToggle.IsChecked == true;
        _settings.Save();
    }

    private void RestoreToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.RestorePoint = RestoreToggle.IsChecked == true;
        _settings.Save();
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        string? snap = _settings.LastPipSnapshot;
        if (snap is null || !File.Exists(snap))
        {
            Notice("Nothing to roll back", "No Python update has run from here yet, so there is no snapshot.");
            return;
        }
        var dlg = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Roll back the last Python update?",
            Content = $"Every package in your user site goes back to the version it had on {File.GetLastWriteTime(snap):d MMM 'at' HH:mm}.",
            PrimaryButtonText = "Roll back",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        SetBusy(true, "Rolling back Python packages…");
        WorkBar.IsIndeterminate = true;
        ProcResult r = await Task.Run(() => UpdateRun.RestoreSnapshotAsync(snap));
        WorkBar.IsIndeterminate = false;
        SetBusy(false, "");
        ResultInfoBar.Severity = r.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ResultInfoBar.Title = r.Ok ? "Python packages rolled back" : "The rollback didn't finish";
        ResultInfoBar.Message = r.Tail(1).FirstOrDefault() ?? "";
        ResultInfoBar.IsOpen = true;
    }

    private void OpenNvidia_Click(object sender, RoutedEventArgs e)
    {
        if (!NvidiaSource.OpenApp())
        {
            Notice("NVIDIA app not found", "It isn't in the usual folder. Open it from the Start menu.");
        }
    }

    private void WindowsUpdateSettings_Click(object sender, RoutedEventArgs e) => Open("ms-settings:windowsupdate");

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Root);
        Open(AppPaths.Root);
    }

    private void OpenRunLog_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRunLog is not null && File.Exists(_lastRunLog))
        {
            Open(_lastRunLog);
        }
        else
        {
            Open(AppPaths.Logs);
        }
    }

    private static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private void Notice(string title, string message)
    {
        NoticeInfoBar.Severity = InfoBarSeverity.Informational;
        NoticeInfoBar.Title = title;
        NoticeInfoBar.Message = message;
        NoticeInfoBar.IsOpen = true;
    }

    // ---------------------------------------------------------------- details pane

    private void ShowDetailsIntro()
    {
        DetailsTitle.Text = "Details";
        DetailsWhy.Text = "Pick an update to see what it is, why it is or isn't selected, and the exact command it runs.";
        DetailsList.Children.Clear();
        foreach (SourceCheck s in Checker.Sources)
        {
            DetailsList.Children.Add(Field(Checker.Title(s.Source), Checker.Describe(s.Source), mono: false));
        }
    }

    private void ShowDetails(RowView row)
    {
        if (_shownRow is not null && _shownRow != row)
        {
            _shownRow.SetShown(false);
        }
        _shownRow = row;
        row.SetShown(true);
        UpdateItem i = row.Item;
        DetailsTitle.Text = i.Name;
        DetailsWhy.Text = i.Note ?? DefaultWhy(i);
        DetailsList.Children.Clear();
        if (i.Kind is ItemKind.Package or ItemKind.Info)
        {
            DetailsList.Children.Add(Field("Installed", string.IsNullOrEmpty(i.Current) ? "Unknown" : i.Current, mono: false));
            DetailsList.Children.Add(Field(i.TargetVersion is null ? "Newest" : "Will install", i.TargetVersion ?? i.Available, mono: false));
        }
        else if (i.Kind is ItemKind.WindowsUpdate or ItemKind.Driver)
        {
            if (!string.IsNullOrEmpty(i.Detail))
            {
                DetailsList.Children.Add(Field("Article", i.Detail, mono: false));
            }
            if (i.DriverDate is DateTime d)
            {
                DetailsList.Children.Add(Field("Driver date", d.ToString("d MMM yyyy"), mono: false));
            }
            if (!string.IsNullOrEmpty(i.Available))
            {
                DetailsList.Children.Add(Field("Download", i.Available, mono: false));
            }
        }
        DetailsList.Children.Add(Field("Runs", Commands.Describe(i), mono: true));
        if (i.NeedsAdmin)
        {
            DetailsList.Children.Add(Field("Admin", "Yes, in the one admin step of the run", mono: false));
        }
        if (i.Result is not null)
        {
            DetailsList.Children.Add(Field("Result", i.Result, mono: false));
        }
        if (i.Output.Count > 0)
        {
            DetailsList.Children.Add(Field("Last lines of output", string.Join("\n", i.Output), mono: true));
        }
        if (i.Selectable && i.Kind != ItemKind.StoreScan && !_busy)
        {
            var hide = new Button { Content = "Don't show this update", Margin = new Thickness(0, 6, 0, 0) };
            ToolTipService.SetToolTip(hide, "Moves it to the left-out list until you choose Show again there.");
            hide.Click += (_, _) => SetIgnored(i, ignored: true);
            DetailsList.Children.Add(hide);
        }
    }

    private static string DefaultWhy(UpdateItem i) => i.Kind switch
    {
        ItemKind.StoreScan => "Asks the Microsoft Store to update all your Store apps in the background.",
        ItemKind.WindowsUpdate => "An update offered by Windows Update.",
        ItemKind.Driver => "An optional driver offered by Windows Update.",
        _ when i.Recommended => "Picked: a routine update within the same version family.",
        _ => "Not picked by default.",
    };

    private static StackPanel Field(string label, string value, bool mono)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = label, Style = Style("MutedCaptionStyle") });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            Style = mono ? Style("MonoCaptionStyle") : Style("BodyTextBlockStyle"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });
        return panel;
    }

    private void ApplyLayout(double width)
    {
        bool narrow = width < 1060;
        DetailsColumn.Width = new GridLength(narrow ? 0 : 380);
        DetailsRow.Height = new GridLength(narrow ? 300 : 0);
        Grid.SetRow(DetailsPane, narrow ? 1 : 0);
        Grid.SetColumn(DetailsPane, narrow ? 0 : 1);
    }

    private static Style Style(string key) => (Style)Application.Current.Resources[key];

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    // ---------------------------------------------------------------- group card

    private sealed class GroupView
    {
        private readonly MainWindow _owner;
        private readonly UpdateSource _source;
        private readonly CheckBox _all;
        private readonly TextBlock _count;
        private readonly TextBlock _status;
        private readonly ProgressRing _ring;
        private readonly StackPanel _rows;
        private readonly Dictionary<string, RowView> _views = [];
        private SourceResult? _last;

        public Border Card { get; }

        public GroupView(MainWindow owner, UpdateSource source)
        {
            _owner = owner;
            _source = source;

            _all = new CheckBox { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            AutomationProps(_all, "Select every update in " + Checker.Title(source));
            _all.Click += (_, _) =>
            {
                bool on = _all.IsChecked == true;
                foreach (RowView v in _views.Values.Where(v => v.Item.Selectable))
                {
                    v.Item.Selected = on;
                    _owner.RememberChoice(v.Item);
                    v.Refresh();
                }
                Sync();
                _owner.UpdateSummary();
            };

            var icon = new FontIcon { Glyph = Glyph(source), FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = Checker.Title(source), Style = Style("BodyStrongTextBlockStyle") };
            var desc = new TextBlock { Text = Checker.Describe(source), Style = Style("MutedCaptionStyle"), TextWrapping = TextWrapping.Wrap };
            var titles = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(title);
            titles.Children.Add(desc);

            _count = new TextBlock { Style = Style("BodyStrongTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center };
            _ring = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };

            var header = new Grid { ColumnSpacing = 12 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Place(header, _all, 0);
            Place(header, icon, 1);
            Place(header, titles, 2);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(_ring);
            right.Children.Add(_count);
            Place(header, right, 3);

            _status = new TextBlock { Style = Style("MutedBodyStyle"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(40, 0, 0, 0), Visibility = Visibility.Collapsed };
            _rows = new StackPanel { Spacing = 2 };

            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(header);
            body.Children.Add(_status);
            body.Children.Add(_rows);
            Card = new Border { Style = Style("CardStyle"), Padding = new Thickness(16, 12, 16, 12), Child = body };
        }

        public void SetChecking()
        {
            _last = null;
            Card.Visibility = Visibility.Visible;
            _rows.Children.Clear();
            _views.Clear();
            _all.Visibility = Visibility.Collapsed;
            _count.Text = "";
            _ring.IsActive = true;
            _ring.Visibility = Visibility.Visible;
            SetStatus("Checking…");
        }

        public void Show(SourceResult r)
        {
            _last = r;
            // A tool this PC doesn't have: no card at all.
            Card.Visibility = r.Absent ? Visibility.Collapsed : Visibility.Visible;
            _ring.IsActive = false;
            _ring.Visibility = Visibility.Collapsed;
            _rows.Children.Clear();
            _views.Clear();
            List<UpdateItem> visible = r.Items.Where(i => !i.Hidden).ToList();
            foreach (UpdateItem i in visible)
            {
                var v = new RowView(_owner, this, i);
                _views[i.Key] = v;
                _rows.Children.Add(v.Root);
            }
            int n = visible.Count(i => i.Selectable);
            int hidden = r.Items.Count - visible.Count;
            _count.Text = n > 0 ? n.ToString() : "";
            _all.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
            string? status = r.Message;
            if (status is null && visible.Count == 0)
            {
                status = hidden > 0 ? $"Nothing safe to update. {hidden} left out for safety, listed at the end." : "Up to date";
            }
            else if (hidden > 0 && !r.Failed)
            {
                status = (status is null ? "" : status + ". ") + $"{hidden} more left out for safety, listed at the end.";
            }
            SetStatus(status);
            _status.Foreground = r.Failed ? Brush("SystemFillColorCautionBrush") : Brush("TextFillColorSecondaryBrush");
            Sync();
        }

        public void Refresh(UpdateItem item)
        {
            if (_views.TryGetValue(item.Key, out RowView? v))
            {
                v.Refresh();
            }
        }

        public void Rebuild()
        {
            if (_last is not null)
            {
                Show(_last);
            }
        }

        public void SetEnabled(bool on)
        {
            _all.IsEnabled = on;
            foreach (RowView v in _views.Values)
            {
                v.Check.IsEnabled = on && v.Item.Selectable;
            }
        }

        public void Sync()
        {
            List<UpdateItem> sel = _views.Values.Select(v => v.Item).Where(i => i.Selectable).ToList();
            int on = sel.Count(i => i.Selected);
            _all.IsChecked = on == 0 ? false : on == sel.Count ? true : null;
        }

        public void RowToggled() => Sync();

        private void SetStatus(string? text)
        {
            _status.Text = text ?? "";
            _status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string Glyph(UpdateSource s) => s switch
        {
            UpdateSource.Apps => "",
            UpdateSource.Chocolatey => "",
            UpdateSource.Scoop => "",
            UpdateSource.Windows => "",
            UpdateSource.Nvidia => "",
            UpdateSource.Python => "",
            UpdateSource.Node => "",
            UpdateSource.Rust => "",
            UpdateSource.ClaudeCode => "",
            _ => "",
        };
    }

    // ---------------------------------------------------------------- one row

    private sealed class RowView
    {
        private readonly MainWindow _owner;
        private readonly GroupView _group;
        private readonly TextBlock _versions;
        private readonly Border _badge;
        private readonly TextBlock _badgeText;
        private readonly FontIcon _statusIcon;
        private readonly ProgressRing _ring;
        private readonly TextBlock _statusText;
        private bool _shown;

        public UpdateItem Item { get; }
        public Border Root { get; }
        public CheckBox Check { get; }

        public RowView(MainWindow owner, GroupView group, UpdateItem item)
        {
            _owner = owner;
            _group = group;
            Item = item;

            Check = new CheckBox { MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, IsEnabled = item.Selectable };
            AutomationProps(Check, "Update " + item.Name);
            Check.Click += (_, _) =>
            {
                Item.Selected = Check.IsChecked == true;
                _owner.RememberChoice(Item);
                _group.RowToggled();
                _owner.UpdateSummary();
            };

            var name = new TextBlock { Text = item.Name, Style = Style("BodyTextBlockStyle"), TextTrimming = TextTrimming.CharacterEllipsis };
            string sub = item.Kind switch
            {
                ItemKind.WindowsUpdate or ItemKind.Driver => item.Detail ?? "",
                ItemKind.StoreScan => "Store apps update in the background",
                _ => item.Id,
            };
            var subText = new TextBlock { Text = sub, Style = Style("MutedCaptionStyle"), TextTrimming = TextTrimming.CharacterEllipsis };
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(name);
            if (sub.Length > 0)
            {
                names.Children.Add(subText);
            }

            _versions = new TextBlock { Style = Style("BodyTextBlockStyle"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            _badgeText = new TextBlock { Style = Style("CaptionTextBlockStyle"), FontWeight = FontWeights.SemiBold };
            _badge = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center, Child = _badgeText };
            _statusIcon = new FontIcon { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            _ring = new ProgressRing { Width = 14, Height = 14, IsActive = false, Visibility = Visibility.Collapsed };
            _statusText = new TextBlock { Style = Style("MutedCaptionStyle"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            status.Children.Add(_ring);
            status.Children.Add(_statusIcon);
            status.Children.Add(_statusText);

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 150 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            Place(grid, Check, 0);
            Place(grid, names, 1);
            Place(grid, _versions, 2);
            Place(grid, _badge, 3);
            Place(grid, status, 4);

            Root = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(0, 6, 8, 6), Background = Brush("SubtleFillColorTransparentBrush"), Child = grid };
            Root.Tapped += (_, _) => _owner.ShowDetails(this);
            Root.PointerEntered += (_, _) => { if (!_shown) { Root.Background = Brush("SubtleFillColorSecondaryBrush"); } };
            Root.PointerExited += (_, _) => { if (!_shown) { Root.Background = Brush("SubtleFillColorTransparentBrush"); } };
            Refresh();
        }

        public void SetShown(bool shown)
        {
            _shown = shown;
            Root.Background = Brush(shown ? "SubtleFillColorTertiaryBrush" : "SubtleFillColorTransparentBrush");
        }

        public void Refresh()
        {
            UpdateItem i = Item;
            Check.IsChecked = i.Selectable && i.Selected;
            string target = i.TargetVersion ?? i.Available;
            _versions.Text = i.Kind switch
            {
                ItemKind.WindowsUpdate or ItemKind.Driver => i.Available,
                ItemKind.StoreScan => "",
                _ => $"{(i.UnknownVersion || i.Current.Length == 0 ? "?" : i.Current)}  →  {target}",
            };

            _badge.Visibility = i.Badge is null ? Visibility.Collapsed : Visibility.Visible;
            _badgeText.Text = i.Badge ?? "";
            (string bg, string fg) = i.Badge switch
            {
                "Major" => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush"),
                "Held" or "Old driver" => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush"),
                "Same family" => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush"),
                _ => ("SubtleFillColorSecondaryBrush", "TextFillColorSecondaryBrush"),
            };
            _badge.Background = Brush(bg);
            _badgeText.Foreground = Brush(fg);

            bool running = i.Status == ItemStatus.Running;
            _ring.IsActive = running;
            _ring.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            (string glyph, string brush) = i.Status switch
            {
                ItemStatus.Done => ("", "SystemFillColorSuccessBrush"),
                ItemStatus.Failed => ("", "SystemFillColorCriticalBrush"),
                ItemStatus.Skipped => ("", "SystemFillColorCautionBrush"),
                ItemStatus.Queued => ("", "TextFillColorSecondaryBrush"),
                _ => ("", "TextFillColorSecondaryBrush"),
            };
            _statusIcon.Glyph = glyph;
            _statusIcon.Foreground = Brush(brush);
            _statusIcon.Visibility = glyph.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            _statusText.Text = i.Status switch
            {
                ItemStatus.Idle => "",
                ItemStatus.Queued => "Waiting",
                ItemStatus.Running => "Installing",
                ItemStatus.Done => i.Result?.StartsWith("Already", StringComparison.Ordinal) == true ? "Up to date" : "Updated",
                ItemStatus.Failed => i.Result?.StartsWith("Rolled back", StringComparison.Ordinal) == true ? "Rolled back" : "Failed",
                ItemStatus.Skipped => "Kept back",
                _ => "",
            };
            ToolTipService.SetToolTip(Root, i.Result);
        }
    }

    private static void Place(Grid grid, FrameworkElement child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static void AutomationProps(DependencyObject element, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
