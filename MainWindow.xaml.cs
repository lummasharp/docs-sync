using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace DocsSync;

public partial class MainWindow : Window
{
    Config _cfg = new();
    readonly Dictionary<string, NoteState> _state = Scanner.LoadState();
    List<NoteReport> _reports = new();
    readonly List<FileSystemWatcher> _watchers = new();
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };
    readonly WF.NotifyIcon _tray = new();
    HashSet<string> _staleKeys = new();
    bool _quitting, _toldAboutTray;

    public MainWindow()
    {
        InitializeComponent();
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rescan(); };
        _clock.Tick += (_, _) => Rescan(); // also refreshes the "5m ago" labels
        SetupTray();
        SourceInitialized += (_, _) => RoundCorners();
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        LoadConfigAndWatch();
        _clock.Start();
    }

    void LoadConfigAndWatch()
    {
        _cfg = Scanner.LoadConfig();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        var dirs = _cfg.Projects.Select(p => p.Root).Append(_cfg.Vault).Where(Directory.Exists).Distinct();
        foreach (var dir in dirs)
        {
            var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName };
            FileSystemEventHandler onChange = (_, e) =>
            {
                if (e.FullPath.Contains(Path.DirectorySeparatorChar + ".obsidian") || e.FullPath.Contains(Path.DirectorySeparatorChar + ".git")) return;
                Dispatcher.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
            };
            w.Changed += onChange; w.Created += onChange; w.Deleted += onChange;
            w.Renamed += (s, e) => onChange(s, e);
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
        Rescan();
    }

    void Rescan()
    {
        var selected = (NoteList.SelectedItem as NoteReport)?.Key;
        try
        {
            _reports = Scanner.Scan(_cfg, _state, DateTime.UtcNow, Scanner.SnapshotDir);
            Scanner.SaveState(_state);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SummaryText.Text = "Couldn't read a folder: " + e.Message;
            return;
        }

        var stale = _reports.Where(r => r.IsStale).ToList();
        var fresh = stale.Where(r => !_staleKeys.Contains(r.Key)).ToList();
        if (fresh.Count > 0 && !IsVisible && _cfg.Notifications)
            _tray.ShowBalloonTip(4000, "Docs need an update",
                fresh.Count == 1 ? $"{fresh[0].Title} ({fresh[0].Project}) no longer matches its code." : $"{fresh.Count} notes no longer match their code.",
                WF.ToolTipIcon.None);
        _staleKeys = stale.Select(r => r.Key).ToHashSet();

        SummaryText.Text = _cfg.Projects.Count == 0 ? "No projects set up"
            : stale.Count == 0 ? $"{_cfg.Projects.Count} project{(_cfg.Projects.Count == 1 ? "" : "s")} · all docs current"
            : $"{stale.Count} note{(stale.Count == 1 ? "" : "s")} need{(stale.Count == 1 ? "s" : "")} an update";
        UpdateTray(stale.Count);
        // Results for notes that no longer need an update (edited by hand, marked current) are out of date.
        foreach (var path in _proposals.Keys.Concat(_claudeErrors.Keys).Distinct().ToList())
            if (!stale.Any(r => r.NotePath == path) && path != _busyPath) { _proposals.Remove(path); _claudeErrors.Remove(path); }
        ApplyFilter(selected);
        RefreshClaudeUi();
    }

    void ApplyFilter(string? selectKey = null)
    {
        if (NoteList == null) return;
        var items = TabStale.IsChecked == true ? _reports.Where(r => r.IsStale)
            : TabNone.IsChecked == true ? _reports.Where(r => r.IsUnmatched)
            : _reports;
        var list = items.ToList();
        NoteList.ItemsSource = list;
        NoteList.SelectedItem = list.FirstOrDefault(r => r.Key == selectKey) ?? (TabStale.IsChecked == true ? list.FirstOrDefault() : null);

        ListEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListEmptyText.Text = _cfg.Projects.Count == 0 ? "No projects found. Open Settings to add one."
            : TabStale.IsChecked == true ? "All docs match the code"
            : TabNone.IsChecked == true ? "Every note is linked to code" : "No notes found";
        DetailEmptyText.Text = _cfg.Projects.Count == 0
            ? "Docs Sync pairs each Obsidian vault folder with a project folder of the same name. Use Settings to add pairs."
            : "Select a note to see what changed";
        ShowDetail();
    }

    void ShowDetail()
    {
        var r = NoteList.SelectedItem as NoteReport;
        Detail.DataContext = r;
        Detail.Visibility = r == null ? Visibility.Collapsed : Visibility.Visible;
        DetailEmpty.Visibility = r == null ? Visibility.Visible : Visibility.Collapsed;
        if (r?.NotePath != _shownPath) Detail.ScrollToTop(); // a background rescan shouldn't jump the view
        _shownPath = r?.NotePath;
        ShowClaudeState();
    }

    string? _shownPath;

    void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail();
    void Tab_Changed(object sender, RoutedEventArgs e) => ApplyFilter((NoteList?.SelectedItem as NoteReport)?.Key);
    void Refresh_Click(object sender, RoutedEventArgs e) => LoadConfigAndWatch();

    // --- settings ---

    readonly ObservableCollection<ProjectRow> _rows = new();

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        VaultBox.Text = _cfg.Vault;
        _rows.Clear();
        foreach (var p in _cfg.Projects) _rows.Add(ProjectRow.From(p));
        ProjectList.ItemsSource = _rows;
        UpdateNoProjects();
        KeyBox.Password = "";
        UpdateKeyStatus();
        (_cfg.Model switch { "claude-sonnet-5-5" => ModelSonnet, "claude-haiku-5-5" => ModelHaiku, _ => ModelOpus }).IsChecked = true;
        (_cfg.Effort switch { "low" => EffortLow, "high" => EffortHigh, _ => EffortMedium }).IsChecked = true;
        StartupSwitch.IsChecked = Startup.Enabled;
        NotifySwitch.IsChecked = _cfg.Notifications;
        _tplOne = _cfg.PromptTemplate;
        _tplAll = _cfg.PromptTemplateAll;
        _tplShowingAll = false;
        TplOne.IsChecked = true;
        ShowTemplate();
        FileTypesBox.Text = string.Join(", ", _cfg.ExtraFileTypes);
        SettingsError.Text = "";
        MainView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        SettingsScroll.ScrollToTop();
    }

    void SettingsCancel_Click(object sender, RoutedEventArgs e) => CloseSettings();

    void CloseSettings()
    {
        SettingsView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
    }

    void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        var vault = VaultBox.Text.Trim().Trim('"');
        if (!Directory.Exists(vault)) { SettingsError.Text = "The vault folder doesn't exist."; return; }
        var projects = new List<ProjectCfg>();
        foreach (var r in _rows)
        {
            var name = string.IsNullOrWhiteSpace(r.Name) ? Path.GetFileName(r.Root.TrimEnd('\\', '/')) : r.Name.Trim();
            if (!Directory.Exists(r.Root)) { SettingsError.Text = $"{name}: the code folder doesn't exist."; return; }
            if (!Directory.Exists(Path.Combine(vault, r.VaultFolder))) { SettingsError.Text = $"{name}: there's no \"{r.VaultFolder}\" folder in the vault."; return; }
            var sources = r.Sources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            projects.Add(new ProjectCfg { Name = name, Root = r.Root.Trim().Trim('"'), VaultFolder = r.VaultFolder.Trim().Trim('\\', '/'), SourceDirs = sources.Count > 0 ? sources : new() { "." } });
        }

        try
        {
            if (KeyBox.Password.Length > 0) Secrets.SaveApiKey(KeyBox.Password);
            Startup.Enabled = StartupSwitch.IsChecked == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SettingsError.Text = "Couldn't save: " + ex.Message;
            return;
        }

        _cfg.Vault = vault;
        _cfg.Projects = projects;
        _cfg.Model = ModelSonnet.IsChecked == true ? "claude-sonnet-5-5" : ModelHaiku.IsChecked == true ? "claude-haiku-5-5" : "claude-opus-5-5";
        _cfg.Effort = EffortLow.IsChecked == true ? "low" : EffortHigh.IsChecked == true ? "high" : "medium";
        _cfg.Notifications = NotifySwitch.IsChecked == true;
        _cfg.ExtraFileTypes = Languages.ParseExtensions(FileTypesBox.Text);
        KeepTemplateEdits();
        _cfg.PromptTemplate = string.IsNullOrWhiteSpace(_tplOne) ? DocUpdater.DefaultTemplate : _tplOne;
        _cfg.PromptTemplateAll = string.IsNullOrWhiteSpace(_tplAll) ? DocUpdater.DefaultTemplateAll : _tplAll;
        Scanner.SaveConfig(_cfg);
        CloseSettings();
        LoadConfigAndWatch();
        SummaryText.Text = "Settings saved";
    }

    // The template editor switches between the one-note and all-notes templates.
    string _tplOne = "", _tplAll = "";
    bool _tplShowingAll;

    static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    void KeepTemplateEdits()
    {
        if (_tplShowingAll) _tplAll = TemplateBox.Text; else _tplOne = TemplateBox.Text;
    }

    void ShowTemplate()
    {
        TemplateBox.Text = Crlf(_tplShowingAll ? _tplAll : _tplOne);
        TemplateBox.CaretIndex = 0;
        TemplateBox.ScrollToHome();
        TokensText.Text = _tplShowingAll ? DocUpdater.TemplateTokensAll : DocUpdater.TemplateTokens;
    }

    void TemplateTab_Changed(object sender, RoutedEventArgs e)
    {
        if (TemplateBox == null || TplAll.IsChecked == _tplShowingAll) return;
        KeepTemplateEdits();
        _tplShowingAll = TplAll.IsChecked == true;
        ShowTemplate();
    }

    void ResetTemplate_Click(object sender, RoutedEventArgs e) =>
        TemplateBox.Text = Crlf(_tplShowingAll ? DocUpdater.DefaultTemplateAll : DocUpdater.DefaultTemplate);

    // --- no-key prompt popup ---

    bool _promptIsAll;

    void ShowPrompt(NoteReport r)
    {
        try { PromptText.Text = DocUpdater.FillTemplate(_cfg.PromptTemplate, r, _cfg); }
        catch (IOException ex) { ShowClaudeError(r.NotePath, "Couldn't read a file for the prompt: " + ex.Message); return; }
        _promptIsAll = false;
        PromptHint.Text = "There's no API key set, so Docs Sync can't ask Claude itself. Paste this into Claude Code, which can open and edit the files, or into claude.ai.";
        OpenPromptPopup();
    }

    void ShowPromptAll(IReadOnlyList<NoteReport> notes)
    {
        try { PromptText.Text = DocUpdater.FillTemplateAll(_cfg.PromptTemplateAll, notes, _cfg); }
        catch (IOException ex) { SummaryText.Text = "Couldn't read a file for the prompt: " + ex.Message; return; }
        _promptIsAll = true;
        PromptHint.Text = $"There's no API key set, so Docs Sync can't ask Claude itself. This prompt covers all {notes.Count} notes that need an update. Paste it into Claude Code, which can open and edit the files.";
        OpenPromptPopup();
    }

    void OpenPromptPopup()
    {
        CopyPromptButton.Content = "Copy prompt";
        PromptPopup.Visibility = Visibility.Visible;
        CopyPromptButton.Focus();
    }

    void ClosePrompt_Click(object sender, RoutedEventArgs e) => PromptPopup.Visibility = Visibility.Collapsed;

    void PromptPopup_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape) PromptPopup.Visibility = Visibility.Collapsed;
    }

    void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PromptText.Text);
            CopyPromptButton.Content = "Copied";
        }
        catch (COMException)
        {
            PromptText.Focus();
            PromptText.SelectAll(); // clipboard busy: leave it selected for Ctrl+C
            CopyPromptButton.Content = "Press Ctrl+C";
        }
    }

    void PromptSettings_Click(object sender, RoutedEventArgs e)
    {
        PromptPopup.Visibility = Visibility.Collapsed;
        Settings_Click(sender, e);
        (_promptIsAll ? TplAll : TplOne).IsChecked = true;
        if (sender is Button { Content: "Edit template" }) Dispatcher.BeginInvoke(() => TemplateBox.BringIntoView(), DispatcherPriority.Loaded);
    }

    void UpdateNoProjects() => NoProjects.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void UpdateKeyStatus()
    {
        var saved = Secrets.LoadApiKey();
        var env = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        KeyStatus.Text = saved != null ? $"Key saved (ends in …{saved[^Math.Min(4, saved.Length)..]}). Paste a new one to replace it."
            : !string.IsNullOrEmpty(env) ? "Using the ANTHROPIC_API_KEY environment variable. Paste a key to use a different one."
            : "No key yet. Paste one to turn on Update with Claude.";
        ForgetKeyButton.Visibility = saved != null ? Visibility.Visible : Visibility.Collapsed;
    }

    void ForgetKey_Click(object sender, RoutedEventArgs e)
    {
        Secrets.SaveApiKey(null);
        UpdateKeyStatus();
    }

    void BrowseVault_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(VaultBox.Text) is { } dir) VaultBox.Text = dir;
    }

    void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProjectRow row } || PickFolder(row.Root) is not { } dir) return;
        row.Root = dir;
        if (string.IsNullOrWhiteSpace(row.Name)) row.Name = Path.GetFileName(dir);
        if (Directory.Exists(Path.Combine(dir, "src"))) row.Sources = "src";
    }

    void BrowseNotes_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProjectRow row }) return;
        var vault = VaultBox.Text.Trim();
        if (PickFolder(Path.Combine(vault, row.VaultFolder)) is not { } dir) return;
        var rel = Path.GetRelativePath(vault, dir);
        if (rel.StartsWith("..") || Path.IsPathRooted(rel)) { SettingsError.Text = "Pick a folder inside the vault."; return; }
        row.VaultFolder = rel == "." ? "" : rel;
        SettingsError.Text = "";
    }

    void AddProject_Click(object sender, RoutedEventArgs e)
    {
        _rows.Add(new ProjectRow { Sources = "src" });
        UpdateNoProjects();
    }

    void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ProjectRow row }) _rows.Remove(row);
        UpdateNoProjects();
    }

    // Asks for the folder that holds your projects, then pairs its subfolders with same-named vault folders.
    void AutoDetect_Click(object sender, RoutedEventArgs e)
    {
        if (PickFolder(Directory.Exists(Scanner.DefaultProjectsDir) ? Scanner.DefaultProjectsDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is not { } dir) return;
        var vault = VaultBox.Text.Trim().Trim('"');
        var found = Scanner.DefaultConfig(dir, Directory.Exists(vault) ? vault : null);
        var added = 0;
        foreach (var p in found.Projects.Where(p => !_rows.Any(r => string.Equals(r.Root.TrimEnd('\\'), p.Root, StringComparison.OrdinalIgnoreCase))))
        {
            _rows.Add(ProjectRow.From(p));
            added++;
        }
        if (string.IsNullOrWhiteSpace(VaultBox.Text)) VaultBox.Text = found.Vault;
        SettingsError.Text = added == 0 ? $"No new projects found. Folders in {Path.GetFileName(dir)} need the same name as a folder in the vault." : "";
        UpdateNoProjects();
    }

    void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Scanner.AppDir);
        Open(Scanner.AppDir);
    }

    static string? PickFolder(string start)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog();
        if (Directory.Exists(start)) dlg.InitialDirectory = start;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    // --- Claude ---

    CancellationTokenSource? _claudeCts;   // set while Claude is working (one note or Update all)
    string? _busyPath;                     // the note Claude is on right now
    (int Done, int Total)? _batch;         // progress while Update all runs
    readonly Dictionary<string, DocProposal> _proposals = new();
    readonly Dictionary<string, string> _claudeErrors = new();

    async void Claude_Click(object sender, RoutedEventArgs e)
    {
        if (Detail.DataContext is not NoteReport r || _claudeCts != null) return;
        if (!Secrets.HasKey) { ShowPrompt(r); return; }
        await RunClaude(new[] { r });
    }

    async void UpdateAll_Click(object sender, RoutedEventArgs e)
    {
        if (_claudeCts != null) return;
        var stale = _reports.Where(r => r.IsStale).ToList();
        if (!Secrets.HasKey) { ShowPromptAll(stale); return; }
        await RunClaude(stale.Where(r => !_proposals.ContainsKey(r.NotePath)).ToList());
    }

    // One note at a time: stays inside rate limits, and each result shows up as soon as it's ready.
    async Task RunClaude(IReadOnlyList<NoteReport> notes)
    {
        if (notes.Count == 0) return;
        var cts = _claudeCts = new CancellationTokenSource();
        _batch = notes.Count > 1 ? (0, notes.Count) : null;
        try
        {
            foreach (var r in notes)
            {
                if (cts.IsCancellationRequested) break;
                _busyPath = r.NotePath;
                _proposals.Remove(r.NotePath);
                _claudeErrors.Remove(r.NotePath);
                var files = r.Files.Count(f => f.Kind != ChangeKind.Removed);
                ClaudeBusyText.Text = $"Claude is reading the note and {files} changed file{(files == 1 ? "" : "s")}…";
                RefreshClaudeUi();
                try { _proposals[r.NotePath] = await DocUpdater.Propose(r, _cfg, cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (DocUpdateException ex)
                {
                    _claudeErrors[r.NotePath] = ex.Message;
                    if (ex.StopsBatch) break; // a bad key or rate limit would fail every remaining note too
                }
                catch (IOException ex) { _claudeErrors[r.NotePath] = "Couldn't read a file: " + ex.Message; }
                if (_batch is { } b) _batch = (b.Done + 1, b.Total);
            }
        }
        finally
        {
            _busyPath = null;
            _batch = null;
            _claudeCts = null;
            cts.Dispose();
            RefreshClaudeUi();
        }
    }

    void ClaudeCancel_Click(object sender, RoutedEventArgs e) => _claudeCts?.Cancel();

    void ApplyProposal_Click(object sender, RoutedEventArgs e)
    {
        if ((Detail.DataContext as NoteReport)?.NotePath is not { } path || !_proposals.TryGetValue(path, out var p)) return;
        if (SaveProposal(p)) SummaryText.Text = "Note updated. The old version is in backups.";
        Rescan();
    }

    void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        var ready = _proposals.Values.Where(p => p.Added + p.Removed > 0).ToList();
        var saved = ready.Count(SaveProposal);
        SummaryText.Text = saved == ready.Count ? $"Updated {saved} note{(saved == 1 ? "" : "s")}. Old versions are in backups."
            : $"Updated {saved} of {ready.Count} notes. Open the others to see why.";
        Rescan();
    }

    bool SaveProposal(DocProposal p)
    {
        try
        {
            DocUpdater.Apply(p);
            _proposals.Remove(p.NotePath);
            return true;
        }
        catch (Exception ex) when (ex is DocUpdateException or IOException or UnauthorizedAccessException)
        {
            _claudeErrors[p.NotePath] = ex.Message;
            return false;
        }
    }

    void DiscardProposal_Click(object sender, RoutedEventArgs e)
    {
        if ((Detail.DataContext as NoteReport)?.NotePath is { } path) _proposals.Remove(path);
        RefreshClaudeUi();
    }

    void DiscardAll_Click(object sender, RoutedEventArgs e)
    {
        _proposals.Clear();
        RefreshClaudeUi();
    }

    void ShowClaudeError(string path, string message)
    {
        _claudeErrors[path] = message;
        ShowClaudeState();
    }

    void RefreshClaudeUi()
    {
        foreach (var r in _reports)
            r.ClaudeBadge = r.NotePath == _busyPath ? "Working" : _proposals.ContainsKey(r.NotePath) ? "Ready" : "";
        NoteList.Items.Refresh();
        ShowClaudeState();
        UpdateBatchBar();
    }

    // Bottom of the note list: Update all, its progress, or Save all once results are in.
    void UpdateBatchBar()
    {
        var running = _claudeCts != null;
        var stale = _reports.Count(r => r.IsStale);
        var ready = _proposals.Values.Count(p => p.Added + p.Removed > 0);
        var waiting = _reports.Count(r => r.IsStale && !_proposals.ContainsKey(r.NotePath));

        BatchBusy.Visibility = running && _batch != null ? Visibility.Visible : Visibility.Collapsed;
        if (_batch is { } b) BatchBusyText.Text = $"Updating {b.Done + 1} of {b.Total}…";
        UpdateAllButton.Visibility = !running && waiting > 0 && stale >= 2 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAllButton.Content = ready > 0 ? $"Update the other {waiting} with Claude" : $"Update all {stale} with Claude";
        SaveAllRow.Visibility = !running && ready >= 2 ? Visibility.Visible : Visibility.Collapsed;
        SaveAllButton.Content = $"Save {ready} updates";
        BatchBar.Visibility = BatchBusy.Visibility == Visibility.Visible || UpdateAllButton.Visibility == Visibility.Visible || SaveAllRow.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // Busy / error / proposal panels follow the selected note, so they survive rescans and list changes.
    void ShowClaudeState()
    {
        var path = (Detail.DataContext as NoteReport)?.NotePath;
        var busy = path != null && path == _busyPath;
        var proposal = path != null ? _proposals.GetValueOrDefault(path) : null;
        var error = path != null ? _claudeErrors.GetValueOrDefault(path) : null;

        ClaudeBusy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ClaudeButton.IsEnabled = _claudeCts == null;
        ClaudeError.Visibility = error != null && !busy ? Visibility.Visible : Visibility.Collapsed;
        ClaudeErrorText.Text = error ?? "";
        Proposal.Visibility = proposal != null ? Visibility.Visible : Visibility.Collapsed;
        if (proposal != null)
        {
            ProposalStats.Text = proposal.Stats;
            DiffList.ItemsSource = proposal.Diff;
            ApplyButton.IsEnabled = proposal.Added + proposal.Removed > 0;
        }

        var spinning = _claudeCts != null;
        if (spinning && _spin == null)
        {
            _spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, _spin);
            BatchSpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, _spin);
        }
        else if (!spinning && _spin != null)
        {
            SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            BatchSpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            _spin = null;
        }
    }

    DoubleAnimation? _spin;

    void OpenNote_Click(object sender, RoutedEventArgs e)
    {
        if (Detail.DataContext is not NoteReport r) return;
        var file = Path.ChangeExtension(r.Key, null).Replace('\\', '/');
        if (!Open($"obsidian://open?vault={Uri.EscapeDataString(r.VaultName)}&file={Uri.EscapeDataString(file)}"))
            Open(r.NotePath);
    }

    void Ack_Click(object sender, RoutedEventArgs e)
    {
        if (Detail.DataContext is not NoteReport r) return;
        Scanner.MarkUpToDate(_state, r, DateTime.UtcNow, Scanner.SnapshotDir);
        Rescan();
    }

    void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && File.Exists(path)) Open(path);
    }

    void CopySnippet_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Snippet.Text); SummaryText.Text = "Copied"; }
        catch (COMException) { SummaryText.Text = "Clipboard is busy, try again"; }
    }

    static bool Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); return true; }
        catch (Win32Exception) { return false; }
    }

    // --- window chrome ---

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_quitting) { base.OnClosing(e); return; }
        e.Cancel = true;
        Hide();
        if (!_toldAboutTray)
        {
            _toldAboutTray = true;
            _tray.ShowBalloonTip(3000, "Docs Sync is still running", "It keeps watching in the tray. Right-click the icon to quit.", WF.ToolTipIcon.None);
        }
    }

    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void Quit()
    {
        _quitting = true;
        _tray.Visible = false;
        _tray.Dispose();
        foreach (var w in _watchers) w.Dispose();
        Application.Current.Shutdown();
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    void RoundCorners()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2, dark = 1;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE (Win11; ignored elsewhere)
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));  // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    // --- tray ---

    void SetupTray()
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Open Docs Sync", null, (_, _) => ShowFromTray());
        menu.Items.Add("Rescan", null, (_, _) => LoadConfigAndWatch());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) ShowFromTray(); };
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
        _tray.Visible = true;
        UpdateTray(0);
    }

    int _trayCount = -1;
    SD.Icon? _trayIcon;

    void UpdateTray(int stale)
    {
        _tray.Text = stale == 0 ? "Docs Sync: all docs current" : $"Docs Sync: {stale} note{(stale == 1 ? "" : "s")} to update";
        if (stale == _trayCount) return;
        _trayCount = stale;

        // Accent tile with a document mark; an amber dot appears when something is stale.
        using var bmp = new SD.Bitmap(32, 32);
        using (var g = SD.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
            using var tile = new SD.Drawing2D.GraphicsPath();
            tile.AddArc(2, 2, 10, 10, 180, 90); tile.AddArc(20, 2, 10, 10, 270, 90);
            tile.AddArc(20, 20, 10, 10, 0, 90); tile.AddArc(2, 20, 10, 10, 90, 90);
            tile.CloseFigure();
            using var accent = new SD.SolidBrush(SD.Color.FromArgb(0x8E, 0xA2, 0xFF));
            g.FillPath(accent, tile);
            using var ink = new SD.Pen(SD.Color.FromArgb(0x0E, 0x10, 0x13), 2.4f) { StartCap = SD.Drawing2D.LineCap.Round, EndCap = SD.Drawing2D.LineCap.Round };
            g.DrawLine(ink, 10, 11, 22, 11);
            g.DrawLine(ink, 10, 16, 22, 16);
            g.DrawLine(ink, 10, 21, 17, 21);
            if (stale > 0)
            {
                using var ring = new SD.SolidBrush(SD.Color.FromArgb(0x0E, 0x10, 0x13));
                using var dot = new SD.SolidBrush(SD.Color.FromArgb(0xE8, 0xA9, 0x5C));
                g.FillEllipse(ring, 19, 0, 13, 13);
                g.FillEllipse(dot, 21, 2, 9, 9);
            }
        }
        var handle = bmp.GetHicon();
        var old = _trayIcon;
        _trayIcon = (SD.Icon)SD.Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        _tray.Icon = _trayIcon;
        old?.Dispose();
        Icon = Imaging.CreateBitmapSourceFromHIcon(_trayIcon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
    }
}

// One editable row in Settings > Projects.
public class ProjectRow : INotifyPropertyChanged
{
    string _name = "", _root = "", _vaultFolder = "", _sources = "";
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Root { get => _root; set => Set(ref _root, value); }
    public string VaultFolder { get => _vaultFolder; set => Set(ref _vaultFolder, value); }
    public string Sources { get => _sources; set => Set(ref _sources, value); }

    public static ProjectRow From(ProjectCfg p) =>
        new() { Name = p.Name, Root = p.Root, VaultFolder = p.VaultFolder, Sources = string.Join(", ", p.SourceDirs) };

    public event PropertyChangedEventHandler? PropertyChanged;
    void Set(ref string field, string value, [CallerMemberName] string? prop = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
