using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScheduledTasks;

public partial class MainWindow : Window
{
    string scope = "watch";
    List<TaskRow> rows = new();
    HashSet<string> watched;
    DateTime loadedAt = DateTime.MinValue;
    string note = "";
    bool loading;
    bool reloadRequested;
    bool openingWatchList;

    static readonly bool IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public MainWindow()
    {
        InitializeComponent();
        if (IsAdmin) { Title += " (admin)"; BAdmin.Visibility = Visibility.Collapsed; }
        Loaded += async (_, _) => await LoadRows();
        // Refresh when you come back to the window, at most every 15 seconds.
        Activated += async (_, _) =>
        {
            if (IsLoaded && !openingWatchList && scope != "windows" && (DateTime.Now - loadedAt).TotalSeconds > 15) await LoadRows();
        };
    }

    TaskRow Selected => TaskGrid.SelectedItem as TaskRow;

    async Task LoadRows()
    {
        if (loading) { reloadRequested = true; return; }
        loading = true;
        UpdateDetails();
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            do
            {
                reloadRequested = false;
                string requestedScope = scope;
                var (paths, loadedRows) = await Task.Run(() =>
                {
                    var watchList = WatchList.Load() ?? WatchList.Initialize(
                        TaskService.Load("apps").Where(r => r.RunsScript).Select(r => r.Key));
                    var tasks = requestedScope == "watch" ? TaskService.LoadWatched(watchList) : TaskService.Load(requestedScope);
                    return (watchList, tasks);
                });

                if (reloadRequested) continue;
                watched = paths;
                rows = loadedRows;
                loadedAt = DateTime.Now;
            } while (reloadRequested);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { Mouse.OverrideCursor = null; loading = false; }
        ShowRows();
    }

    void ShowRows()
    {
        string keep = Selected?.Key;
        string q = Search.Text.Trim();
        var shown = rows.Where(r =>
            (OnlyProblems.IsChecked != true || r.Problem) &&
            (q == "" || $"{r.Name} {r.Folder} {r.Schedule} {r.Command} {r.Description}".Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        TaskGrid.ItemsSource = shown;
        if (keep != null) TaskGrid.SelectedItem = shown.FirstOrDefault(r => StringComparer.OrdinalIgnoreCase.Equals(r.Key, keep));

        int problems = rows.Count(r => r.Problem);
        string text = $"{shown.Count} shown";
        if (problems > 0) text += $"  ·  {problems} with problems";
        if (loadedAt != DateTime.MinValue) text += $"  ·  updated {loadedAt:h:mm tt}";
        if (note != "") text += $"  ·  {note}";
        Status.Text = text;
        UpdateDetails();
    }

    void UpdateDetails()
    {
        TaskRow t = Selected;
        foreach (UIElement e in new UIElement[] { BRun, BStop, BToggle, BSchedule, BShow, BDelete, MRun, MStop, MToggle, MSchedule, MShow, MDelete }) e.IsEnabled = t != null;
        BManageWatchList.IsEnabled = watched != null && !openingWatchList && !loading;
        BWatch.IsEnabled = MWatch.IsEnabled = t != null && watched != null && !loading;
        BWatch.Content = MWatch.Header = t != null && watched?.Contains(t.Key) == true
            ? "Remove from watch list" : "Add to watch list";
        if (t == null) { DName.Text = "Pick a task"; DDesc.Text = ""; DCmd.Text = ""; return; }
        DName.Text = t.Folder != "\\" ? $"{t.Name}   ({t.Folder})" : t.Name;
        DDesc.Text = t.Description;
        DDesc.Visibility = string.IsNullOrEmpty(t.Description) ? Visibility.Collapsed : Visibility.Visible;
        DCmd.Text = t.Command;
        bool off = t.StateKind == "off";
        BRun.IsEnabled = MRun.IsEnabled = !off && t.StateKind != "run";
        BStop.IsEnabled = MStop.IsEnabled = t.StateKind == "run";
        BToggle.Content = MToggle.Header = off ? "Turn on" : "Turn off";
        BShow.IsEnabled = MShow.IsEnabled = !string.IsNullOrEmpty(t.File);
    }

    bool Confirm(string text, string title) =>
        MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    static bool IsAccessDenied(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is COMException c && c.HResult == unchecked((int)0x80070005));

    const string AdminMessage = "Access denied. This task may need admin rights. Click 'Restart as admin' and try again.";

    void ShowError(Exception ex) => MessageBox.Show(this, IsAccessDenied(ex) ? AdminMessage : ex.Message,
        "Scheduled Tasks", MessageBoxButton.OK, MessageBoxImage.Error);

    async void OnManageWatchList(object sender, RoutedEventArgs e)
    {
        openingWatchList = true;
        UpdateDetails();
        try
        {
            var candidates = await Task.Run(() => TaskService.Load("all"));
            var current = WatchList.Load() ?? throw new InvalidOperationException("The watch list file is missing. Refresh the Watch list to set it up again.");
            var picker = new WatchListWindow(candidates, current, (add, remove) =>
                watched = WatchList.Update(add, remove)) { Owner = this };
            if (picker.ShowDialog() == true)
            {
                note = "Watch list saved";
                if (scope == "watch") await LoadRows();
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { openingWatchList = false; ShowRows(); }
    }

    async void OnToggleWatched(object sender, RoutedEventArgs e)
    {
        TaskRow task = Selected;
        if (task == null || watched == null || loading) return;
        bool remove = watched.Contains(task.Key);
        try { watched = WatchList.Update(remove ? [] : [task.Key], remove ? [task.Key] : []); }
        catch (Exception ex) { ShowError(ex); return; }
        note = remove ? $"Removed {task.Name} from watch list" : $"Added {task.Name} to watch list";
        if (scope == "watch") await LoadRows();
        else ShowRows();
    }

    async Task Act(Action<TaskRow> action, string done, int settleMs = 0)
    {
        TaskRow t = Selected;
        if (t == null) return;
        try { action(t); }
        catch (Exception ex) { ShowError(ex); return; }
        note = $"{done} {t.Name}";
        if (settleMs > 0) await Task.Delay(settleMs);
        await LoadRows();
    }

    async void OnRun(object sender, RoutedEventArgs e) => await Act(TaskService.Run, "Started", 700);
    async void OnStop(object sender, RoutedEventArgs e) => await Act(TaskService.Stop, "Stopped");

    async void OnToggle(object sender, RoutedEventArgs e)
    {
        TaskRow t = Selected;
        if (t == null) return;
        if (t.StateKind == "off") await Act(r => TaskService.SetEnabled(r, true), "Turned on");
        else if (Confirm($"Turn off \"{t.Name}\"?\n\nIt won't run until you turn it back on.", "Turn off task"))
            await Act(r => TaskService.SetEnabled(r, false), "Turned off");
    }

    async void OnEditSchedule(object sender, RoutedEventArgs e)
    {
        TaskRow task = Selected;
        if (task == null) return;

        ScheduleSnapshot original;
        try { original = TaskService.LoadSchedule(task); }
        catch (Exception ex) { ShowError(ex); return; }

        bool changed = false;
        var editor = new ScheduleWindow(task.Name, original.Triggers, triggers =>
        {
            try { changed = TaskService.SaveSchedule(task, original, triggers); }
            catch (Exception ex) when (IsAccessDenied(ex)) { throw new InvalidOperationException(AdminMessage, ex); }
        }) { Owner = this };

        if (editor.ShowDialog() == true && changed)
        {
            note = $"Updated schedule for {task.Name}";
            await LoadRows();
        }
    }

    void OnShow(object sender, RoutedEventArgs e)
    {
        TaskRow t = Selected;
        if (t == null) return;
        if (File.Exists(t.File)) Process.Start("explorer.exe", $"/select,\"{t.File}\"");
        else MessageBox.Show(this, $"The file isn't there any more:\n{t.File}", "Scheduled Tasks", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    async void OnDelete(object sender, RoutedEventArgs e)
    {
        TaskRow t = Selected;
        if (t == null) return;
        if (!Confirm($"Delete \"{t.Name}\"?\n\nIt's removed from Task Scheduler. A backup copy is saved first, so it can be restored:\n{TaskService.BackupDir}", "Delete task")) return;
        await Act(r => TaskService.Delete(r), "Deleted", 0);
        if (!rows.Any(r => r.Key == t.Key)) note = $"Deleted {t.Name} (backup saved)";
        ShowRows();
    }

    async void OnScope(object sender, RoutedEventArgs e)
    {
        scope = (string)((RadioButton)sender).Tag;
        if (!IsLoaded) return;
        note = "";
        rows = new();
        ShowRows();
        await LoadRows();
    }

    void OnFilter(object sender, RoutedEventArgs e) { if (IsLoaded) ShowRows(); }
    void OnSelection(object sender, SelectionChangedEventArgs e) => UpdateDetails();
    void OnTaskRightClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(TaskGrid, e.OriginalSource as DependencyObject) is DataGridRow row)
            TaskGrid.SelectedItem = row.Item;
    }
    async void OnRefresh(object sender, RoutedEventArgs e) { note = ""; await LoadRows(); }

    void OnBackups(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(TaskService.BackupDir);
        Process.Start("explorer.exe", $"\"{TaskService.BackupDir}\"");
    }

    void OnAdmin(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (System.ComponentModel.Win32Exception) { }  // UAC prompt cancelled
    }
}
