using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace ScheduledTasks;

public static partial class TaskService
{
    static readonly XNamespace TaskXml = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    static readonly string[] Weekdays = Enum.GetNames<DayOfWeek>();
    static readonly Regex BoundaryOffset = new(@"(?:Z|[+-][0-9]{2}:[0-9]{2})$");
    static readonly string[] TriggerFields = { "Enabled", "StartBoundary", "EndBoundary", "Repetition", "ExecutionTimeLimit", "RandomDelay" };

    internal static ScheduleSnapshot LoadSchedule(TaskRow row)
    {
        dynamic task = Get(row);
        string xml = task.Xml;
        int logonType = task.Definition.Principal.LogonType;
        if (logonType is 1 or 6)
            throw new InvalidOperationException("Windows requires the task account’s password to update this task. Edit it in Windows Task Scheduler.");

        var triggers = XDocument.Parse(xml).Root.Element(TaskXml + "Triggers");
        return new ScheduleSnapshot(xml, logonType, triggers?.Elements().Select(ReadTrigger).ToArray() ?? Array.Empty<ScheduleTrigger>());
    }

    static ScheduleTrigger ReadTrigger(XElement element, int index)
    {
        var daily = element.Element(TaskXml + "ScheduleByDay");
        var weekly = element.Element(TaskXml + "ScheduleByWeek");
        var kind = element.Name.LocalName switch
        {
            "TimeTrigger" => ScheduleKind.Once,
            "CalendarTrigger" when daily != null => ScheduleKind.Daily,
            "CalendarTrigger" when weekly != null => ScheduleKind.Weekly,
            _ => ScheduleKind.Unsupported,
        };

        bool enabled = (bool?)element.Element(TaskXml + "Enabled") ?? true;
        string boundary = (string)element.Element(TaskXml + "StartBoundary") ?? "";
        string offset = BoundaryOffset.Match(boundary).Value;
        if (kind != ScheduleKind.Unsupported &&
            DateTime.TryParseExact(boundary[..(boundary.Length - offset.Length)], "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start))
        {
            int interval = (int?)daily?.Element(TaskXml + "DaysInterval") ?? (int?)weekly?.Element(TaskXml + "WeeksInterval") ?? 1;
            var days = weekly?.Element(TaskXml + "DaysOfWeek");
            int mask = 0;
            for (int i = 0; i < Weekdays.Length; i++)
                if (days?.Element(TaskXml + Weekdays[i]) != null) mask |= 1 << i;

            var repeat = element.Element(TaskXml + "Repetition");
            return new ScheduleTrigger(index, kind, start, interval, mask, enabled, offset,
                (TimeSpan?)repeat?.Element(TaskXml + "Interval"), (TimeSpan?)repeat?.Element(TaskXml + "Duration"), "");
        }

        string label = element.Name.LocalName switch
        {
            "CalendarTrigger" when kind == ScheduleKind.Unsupported => "Monthly",
            "BootTrigger" => "At startup",
            "LogonTrigger" => "At log on",
            "IdleTrigger" => "When idle",
            "EventTrigger" => "On an event",
            "RegistrationTrigger" => "On registration",
            "SessionStateChangeTrigger" => "On session state change",
            _ => element.Name.LocalName,
        };
        return new ScheduleTrigger(index, ScheduleKind.Unsupported, null, 1, 0, enabled, offset, null, null, label);
    }

    internal static bool SaveSchedule(TaskRow row, ScheduleSnapshot original, IReadOnlyList<ScheduleTrigger> updated)
    {
        if (updated.SequenceEqual(original.Triggers)) return false;

        var xml = XDocument.Parse(original.Xml, LoadOptions.PreserveWhitespace);
        var container = xml.Root.Element(TaskXml + "Triggers");
        if (container == null)
        {
            container = new XElement(TaskXml + "Triggers");
            xml.Root.Add(container);
        }

        var elements = container.Elements().ToArray();
        var retained = updated.Select(trigger => trigger.OriginalIndex).ToHashSet();
        foreach (var trigger in original.Triggers)
            if (trigger.Kind != ScheduleKind.Unsupported && !retained.Contains(trigger.OriginalIndex))
                elements[trigger.OriginalIndex.Value].Remove();

        foreach (var trigger in updated)
        {
            int? index = trigger.OriginalIndex;
            var previous = index.HasValue ? original.Triggers[index.Value] : null;
            if (trigger == previous || previous?.Kind == ScheduleKind.Unsupported) continue;

            var element = index.HasValue ? elements[index.Value] : new XElement(TaskXml + "TimeTrigger");
            WriteTrigger(element, previous, trigger);
            if (!index.HasValue) container.Add(element);
        }

        dynamic folder = Connect().GetFolder(row.Folder);
        dynamic task = folder.GetTask(row.Name);
        if ((string)task.Xml != original.Xml)
            throw new InvalidOperationException("This task changed while you were editing it. Close the editor and reopen it to load the current schedule.");

        const int update = 0x04, keepPrincipalAce = 0x10, ignoreRegistrationTriggers = 0x20;
        // Use the principal in the existing XML, which carries the scheduler's account identifier.
        folder.RegisterTask(row.Name, xml.ToString(SaveOptions.DisableFormatting),
            update | keepPrincipalAce | ignoreRegistrationTriggers, null, null, original.LogonType, null);
        return true;
    }

    static void WriteTrigger(XElement element, ScheduleTrigger previous, ScheduleTrigger trigger)
    {
        if (trigger.Start is not DateTime start)
            throw new InvalidOperationException("Choose a start date and time for the trigger.");

        if (previous?.Enabled != trigger.Enabled)
            SetTriggerField(element, "Enabled", XmlConvert.ToString(trigger.Enabled));
        if (previous?.Start != start)
            SetTriggerField(element, "StartBoundary", start.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture) + trigger.Offset);

        if (previous?.RepeatInterval != trigger.RepeatInterval || previous?.RepeatDuration != trigger.RepeatDuration)
        {
            if (trigger.RepeatInterval is not TimeSpan interval)
                element.Element(TaskXml + "Repetition")?.Remove();
            else
            {
                var repeat = element.Element(TaskXml + "Repetition") ?? AddTriggerField(element, "Repetition");
                if (previous?.RepeatInterval != interval)
                    repeat.SetElementValue(TaskXml + "Interval", XmlConvert.ToString(interval));
                if (previous?.RepeatDuration != trigger.RepeatDuration)
                {
                    repeat.SetElementValue(TaskXml + "Duration", trigger.RepeatDuration is TimeSpan duration ? XmlConvert.ToString(duration) : null);
                    if (trigger.RepeatDuration == null) repeat.Element(TaskXml + "StopAtDurationEnd")?.Remove();
                }
            }
        }

        if (previous?.Kind != trigger.Kind)
        {
            element.Element(TaskXml + "ScheduleByDay")?.Remove();
            element.Element(TaskXml + "ScheduleByWeek")?.Remove();
            element.Name = TaskXml + (trigger.Kind == ScheduleKind.Once ? "TimeTrigger" : "CalendarTrigger");
        }

        if (trigger.Kind == ScheduleKind.Daily && (previous?.Kind != trigger.Kind || previous.Interval != trigger.Interval))
        {
            var daily = element.Element(TaskXml + "ScheduleByDay") ?? AddTriggerField(element, "ScheduleByDay");
            daily.SetElementValue(TaskXml + "DaysInterval", trigger.Interval);
        }
        if (trigger.Kind == ScheduleKind.Weekly)
        {
            var weekly = element.Element(TaskXml + "ScheduleByWeek") ?? AddTriggerField(element, "ScheduleByWeek");
            if (previous?.Kind != trigger.Kind || previous.Interval != trigger.Interval)
                weekly.SetElementValue(TaskXml + "WeeksInterval", trigger.Interval);
            if (previous?.Kind != trigger.Kind || previous.Weekdays != trigger.Weekdays)
            {
                var days = new XElement(TaskXml + "DaysOfWeek", Weekdays.Where((_, i) => (trigger.Weekdays & (1 << i)) != 0)
                    .Select(day => new XElement(TaskXml + day)));
                weekly.Element(TaskXml + "DaysOfWeek")?.Remove();
                weekly.Add(days);
            }
        }
    }

    static void SetTriggerField(XElement trigger, string name, string value) =>
        (trigger.Element(TaskXml + name) ?? AddTriggerField(trigger, name)).Value = value;

    static XElement AddTriggerField(XElement trigger, string name)
    {
        var field = new XElement(TaskXml + name);
        int position = Array.IndexOf(TriggerFields, name);
        var following = position < 0 ? null : trigger.Elements().FirstOrDefault(element =>
        {
            int index = Array.IndexOf(TriggerFields, element.Name.LocalName);
            return index < 0 || index > position;
        });
        if (following == null) trigger.Add(field);
        else following.AddBeforeSelf(field);
        return field;
    }
}
