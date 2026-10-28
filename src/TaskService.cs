using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ScheduledTasks;

// Talks to Windows Task Scheduler through its COM API (Schedule.Service).
public static class TaskService
{
    static readonly Dictionary<uint, string> KnownResults = new()
    {
        [0x00000000] = "OK",
        [0x00000001] = "Failed (exit 1)",
        [0x00000002] = "Failed: file not found",
        [0x00041301] = "Running now",
        [0x00041303] = "Never run",
        [0x00041306] = "Stopped by user",
        [0x8004131F] = "Skipped: already running",
        [0x800710E0] = "Refused: conditions not met",
        [0xC000013A] = "Closed (shutdown / Ctrl+C)",
        [0xFFFD0000] = "Failed: PowerShell script not found",
    };
    static readonly string[] Days = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
    static readonly Regex ScriptArg = new(@"""([^""]+\.(?:ps1|pyw?|vbs|cmd|bat|js))""|(\S+\.(?:ps1|pyw?|vbs|cmd|bat|js))\b", RegexOptions.IgnoreCase);
    static readonly Regex ScriptExe = new(@"\.(?:ps1|cmd|bat|vbs)$", RegexOptions.IgnoreCase);
    static readonly Regex Interval = new(@"^PT(?:(\d+)H)?(?:(\d+)M)?$");

    public static string BackupDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scheduled Tasks Manager", "Deleted");

    static dynamic Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        service.Connect();
        return service;
    }

    static bool IsWindowsFolder(string path) =>
        path.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase);

    // scope: "mine" (tasks that run a script: .ps1, .py, .vbs, .cmd, ...), "apps" (everything outside \Microsoft), "windows" (\Microsoft only).
    public static List<TaskRow> Load(string scope)
    {
        var rows = new List<TaskRow>();
        Walk(Connect().GetFolder("\\"), scope, rows);
        return rows.OrderBy(r => !r.Mine).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void Walk(dynamic folder, string scope, List<TaskRow> rows)
    {
        string path = folder.Path;
        bool windows = IsWindowsFolder(path);
        if (scope == "windows" ? windows : !windows)
        {
            dynamic tasks = folder.GetTasks(1);  // 1 = include hidden tasks
            for (int i = 1; i <= tasks.Count; i++)
            {
                TaskRow row;
                try { row = Build(tasks.Item(i), path); }
                catch (COMException) { continue; }  // deleted while we were reading the list
                if (scope != "mine" || row.Mine) rows.Add(row);
            }
        }
        dynamic subs = folder.GetFolders(0);
        for (int i = 1; i <= subs.Count; i++)
        {
            dynamic sub = subs.Item(i);
            if (scope == "windows" || !IsWindowsFolder((string)sub.Path)) Walk(sub, scope, rows);
        }
    }

    static TaskRow Build(dynamic t, string folder)
    {
        var r = new TaskRow { Folder = folder, Name = t.Name };
        var schedule = new List<string>();
        var commands = new List<string>();
        string firstExe = null, firstArgs = null;
        try
        {
            dynamic def = t.Definition;
            r.Description = def.RegistrationInfo.Description;
            dynamic triggers = def.Triggers;
            for (int i = 1; i <= triggers.Count; i++) schedule.Add(FormatTrigger(triggers.Item(i)));
            dynamic actions = def.Actions;
            for (int i = 1; i <= actions.Count; i++)
            {
                dynamic a = actions.Item(i);
                if ((int)a.Type != 0) continue;  // 0 = run a program
                string exe = a.Path, args = a.Arguments;
                commands.Add((exe + " " + args).Trim());
                if (firstExe == null) { firstExe = exe; firstArgs = args ?? ""; }
            }
        }
        catch { }  // some system tasks can't be read without admin rights

        r.Schedule = schedule.Count > 0 ? string.Join("; ", schedule) : "No schedule";
        r.Command = string.Join("\n", commands);
        bool script = false;
        if (firstExe != null)
        {
            string exe = Environment.ExpandEnvironmentVariables(firstExe).Trim('"');
            Match m = ScriptArg.Match(Environment.ExpandEnvironmentVariables(firstArgs));
            if (m.Success) { r.File = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value; script = true; }
            else { r.File = exe; script = ScriptExe.IsMatch(exe); }
        }
        r.Mine = script;

        switch ((int)t.State)
        {
            case 1: r.State = "Off"; r.StateKind = "off"; break;
            case 2: r.State = "Queued"; r.StateKind = "run"; break;
            case 4: r.State = "Running"; r.StateKind = "run"; break;
            default: r.State = "On"; r.StateKind = "on"; break;
        }

        try
        {
            DateTime last = t.LastRunTime, next = t.NextRunTime;
            if (last.Year >= 2000) r.LastRun = last;
            if (next.Year >= 2000 && r.StateKind != "off") r.NextRun = next;
            uint code = unchecked((uint)(int)t.LastTaskResult);
            r.Result = KnownResults.TryGetValue(code, out string known) ? known : $"Failed: code 0x{code:X8}";
            r.ResultKind = code switch { 0 => "ok", 0x41301 => "run", 0x41303 => "never", _ => "bad" };
            if (r.LastRun == null && r.ResultKind == "bad") { r.Result = "Never run"; r.ResultKind = "never"; }
        }
        catch { }

        r.LastText = FormatWhen(r.LastRun) ?? "Never";
        r.NextText = FormatWhen(r.NextRun) ?? "";
        r.Problem = r.ResultKind == "bad" && r.StateKind != "off";
        return r;
    }

    static string FormatWhen(DateTime? when)
    {
        if (when is not DateTime t) return null;
        string time = t.ToString("h:mm tt");
        return (t.Date - DateTime.Today).Days switch
        {
            0 => "Today " + time,
            -1 => "Yesterday " + time,
            1 => "Tomorrow " + time,
            _ => t.ToString("ddd MMM d, h:mm tt"),
        };
    }

    static string FormatTrigger(dynamic tr)
    {
        string at = "";
        try { string start = tr.StartBoundary; if (!string.IsNullOrEmpty(start)) at = DateTime.Parse(start).ToString("h:mm tt"); } catch { }
        string text;
        switch ((int)tr.Type)
        {
            case 1: text = $"Once at {at}"; break;
            case 2:
                int every = tr.DaysInterval;
                text = every > 1 ? $"Every {every} days at {at}" : $"Daily at {at}";
                break;
            case 3:
                int mask = tr.DaysOfWeek;
                text = $"Weekly {string.Join(", ", Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => Days[i]))} at {at}";
                break;
            case 4: case 5: text = $"Monthly at {at}"; break;
            case 6: text = "When idle"; break;
            case 7: text = "When created"; break;
            case 8: text = "At startup"; break;
            case 9: text = "At log on"; break;
            case 11: text = "On lock/unlock"; break;
            case 0: text = "On an event"; break;
            default: text = "Custom schedule"; break;
        }
        try
        {
            string rep = tr.Repetition.Interval;
            Match m = rep == null ? Match.Empty : Interval.Match(rep);
            if (m.Success && (m.Groups[1].Success || m.Groups[2].Success))
            {
                string h = m.Groups[1].Value, min = m.Groups[2].Value;
                string every = h == "" ? $"{min} min" : min == "" ? (h == "1" ? "hour" : $"{h} hours") : $"{h} h {min} min";
                text += $", then every {every}";
            }
        }
        catch { }
        if (!(bool)tr.Enabled) text += " (off)";
        return text;
    }

    static dynamic Get(TaskRow row) => Connect().GetFolder(row.Folder).GetTask(row.Name);

    public static void Run(TaskRow row) => Get(row).Run(null);
    public static void Stop(TaskRow row) => Get(row).Stop(0);
    public static void SetEnabled(TaskRow row, bool on) => Get(row).Enabled = on;

    // Saves the task's XML to BackupDir (restorable with schtasks /create /xml), then deletes it.
    public static string Delete(TaskRow row)
    {
        dynamic folder = Connect().GetFolder(row.Folder);
        string xml = folder.GetTask(row.Name).Xml;
        Directory.CreateDirectory(BackupDir);
        string safe = Regex.Replace($"{row.Folder.Trim('\\')} {row.Name}".Trim(), @"[\\/:*?""<>|]", "_");
        string file = Path.Combine(BackupDir, $"{safe} {DateTime.Now:yyyy-MM-dd_HHmmss}.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        folder.DeleteTask(row.Name, 0);
        return file;
    }
}
