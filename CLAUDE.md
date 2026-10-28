# Scheduled Tasks

Public repo: github.com/dddd-zdf/scheduled-tasks (MIT). A WPF app (.NET 10) for Windows Task Scheduler.

## Layout
- `src/TaskService.cs`: all Task Scheduler access, through the COM API `Schedule.Service` (via `dynamic`). Listing, run/stop, on/off, delete (backs up the task XML to `%LOCALAPPDATA%\Scheduled Tasks Manager\Deleted` first).
- `src/MainWindow.xaml(.cs)`: the window. Colors come only from Fluent theme resources (`{DynamicResource ...}`), never hard-coded, so light/dark follow the system (`ThemeMode="System"` in `App.xaml`).
- `src/TaskRow.cs`: one row of the list.

## Build and run
- `dotnet publish src -c Release -o publish` makes a single framework-dependent `publish\Scheduled Tasks.exe` (needs the .NET 10 Desktop Runtime).
- Close any running copy first or the publish can't overwrite the exe.

## Release
- Bump `<Version>` in `src/ScheduledTasks.csproj`, publish, commit, push, then
  `gh release create vX.Y.Z "publish/Scheduled Tasks.exe" --title "vX.Y.Z" --notes "..."`.
- Note each release's changes in CHANGELOG.md.

## Testing
- Never run, turn off or delete real tasks to test. Make a throwaway one and remove it (and its backup) after:
  `Register-ScheduledTask -TaskName 'zz Test task (safe to delete)' -Action (New-ScheduledTaskAction -Execute cmd.exe -Argument '/c exit 0') -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).AddDays(30))`
- Measure idle CPU only without touching the window (clicks and Refresh cause short spikes).
