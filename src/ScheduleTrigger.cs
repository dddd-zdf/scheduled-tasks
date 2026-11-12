namespace ScheduledTasks;

public enum ScheduleKind { Unsupported, Once, Daily, Weekly }

public sealed record ScheduleTrigger(
    int? OriginalIndex,
    ScheduleKind Kind,
    DateTime? Start,
    int Interval,
    int Weekdays,
    bool Enabled,
    string Offset,
    TimeSpan? RepeatInterval,
    TimeSpan? RepeatDuration,
    string Summary);

internal sealed record ScheduleSnapshot(string Xml, int LogonType, IReadOnlyList<ScheduleTrigger> Triggers);
