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
    string scope = "mine";
    List<TaskRow> rows = new();
    DateTime loadedAt = DateTime.MinValue;
    string note = "";
    bool loading;

    static readonly bool IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public MainWindow()
    {
        InitializeComponent();
        if (IsAdmin) { Title += " (admin)"; BAdmin.Visibility = Visibility.Collapsed; }
        Loaded += async (_, _) => await LoadRows();
        // Refresh when you come back to the window, at most every 15 seconds.
        Activated += async (_, _) =>
        {
            if (IsLoaded && scope != "windows" && (DateTime.Now - loadedAt).TotalSeconds > 15) await LoadRows();
        };
    }

    TaskRow Selected => TaskGrid.SelectedItem as TaskRow;

    async Task LoadRows()
    {
        if (loading) return;
        loading = true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            string s = scope;
            rows = await Task.Run(() => TaskService.Load(s));
            loadedAt = DateTime.Now;
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
        if (keep != null) TaskGrid.SelectedItem = shown.FirstOrDefault(r => r.Key == keep);

        int problems = rows.Count(r => r.Problem);
        string text = $"{shown.Count} shown";
        if (problems > 0) text += $"  ·  {problems} with problems";
        text += $"  ·  updated {loadedAt:h:mm tt}";
        if (note != "") text += $"  ·  {note}";
        Status.Text = text;
        UpdateDetails();
    }

    void UpdateDetails()
    {
        TaskRow t = Selected;
        foreach (UIElement e in new UIElement[] { BRun, BStop, BToggle, BShow, BDelete, MRun, MStop, MToggle, MShow, MDelete }) e.IsEnabled = t != null;
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

    void ShowError(Exception ex)
    {
        string msg = ex.Message;
        if (ex is UnauthorizedAccessException || (ex is COMException c && c.HResult == unchecked((int)0x80070005)))
            msg = "Access denied. This task needs admin rights: click 'Restart as admin' and try again.";
        MessageBox.Show(this, msg, "Scheduled Tasks", MessageBoxButton.OK, MessageBoxImage.Error);
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
        await LoadRows();
    }

    void OnFilter(object sender, RoutedEventArgs e) { if (IsLoaded) ShowRows(); }
    void OnSelection(object sender, SelectionChangedEventArgs e) => UpdateDetails();
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
