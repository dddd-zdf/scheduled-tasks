# Scheduled Tasks

A small, fast window for managing Windows Task Scheduler tasks, made to be quicker to use than the built-in Task Scheduler console.

- See every task with its schedule in plain words ("Weekly Fri at 7:00 AM"), when it last ran, whether that worked, and when it runs next.
- **Run now**, **Stop**, **Turn on/off**, **Show file** (opens the script or program in Explorer) and **Delete**, from buttons or a right-click.
- Three views: **My scripts** (tasks that run a .ps1, .py, .vbs, .cmd... file), **All apps** (everything outside Windows' own folder) and **Windows** (the built-in tasks).
- Filter box and an **Only problems** switch that shows tasks whose last run failed.
- Delete saves a backup of the task first, in `%LOCALAPPDATA%\Scheduled Tasks Manager\Deleted`. To restore one:
  `schtasks /create /xml "<backup file>.xml" /tn "<task name>"`
- Windows 11 look that follows your light/dark setting.
- Uses no CPU while it's open and idle, and nothing runs once you close it.

## Download

Get `Scheduled Tasks.exe` from the [latest release](../../releases/latest) and run it. No installer.

It needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows will offer the download if it's missing). Works on Windows 10 and 11; the theme looks best on 11.

Some tasks, such as ones set up to run with admin rights, can only be changed as administrator. Use the **Restart as admin** button for those.

## Build

Needs the .NET 10 SDK.

```
dotnet publish src -c Release -o publish
```

This makes a single `publish\Scheduled Tasks.exe`.

## License

MIT
