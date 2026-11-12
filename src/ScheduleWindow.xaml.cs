using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace ScheduledTasks;

public partial class ScheduleWindow : Window
{
    static readonly ScheduleKind[] EditableKinds = { ScheduleKind.Once, ScheduleKind.Daily, ScheduleKind.Weekly };
    static readonly string[] ClockFormats = { @"hh\:mm", @"hh\:mm\:ss" };
    static readonly string[] DayNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

    readonly Action<IReadOnlyList<ScheduleTrigger>> save;
    readonly ObservableCollection<TriggerRow> rows = new();
    readonly CheckBox[] weekdayBoxes;
    TriggerRow selected;
    bool changingSelection;

    public ScheduleWindow(string taskName, IReadOnlyList<ScheduleTrigger> triggers, Action<IReadOnlyList<ScheduleTrigger>> save)
    {
        InitializeComponent();
        this.save = save;
        weekdayBoxes = new[] { SundayBox, MondayBox, TuesdayBox, WednesdayBox, ThursdayBox, FridayBox, SaturdayBox };
        TaskTitle.Text = taskName;
        foreach (var trigger in triggers) rows.Add(new TriggerRow(trigger));
        TriggerGrid.ItemsSource = rows;
        SelectRow(rows.FirstOrDefault(row => row.IsEditable) ?? rows.FirstOrDefault());
    }

    void OnTriggerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingSelection) return;
        var next = TriggerGrid.SelectedItem as TriggerRow;
        if (next == selected) return;
        if (!TryApplyEditor())
        {
            changingSelection = true;
            TriggerGrid.SelectedItem = selected;
            changingSelection = false;
            return;
        }
        selected = next;
        LoadEditor();
    }

    void SelectRow(TriggerRow row)
    {
        changingSelection = true;
        TriggerGrid.SelectedItem = row;
        selected = row;
        changingSelection = false;
        LoadEditor();
    }

    void OnAdd(object sender, RoutedEventArgs e)
    {
        if (!TryApplyEditor()) return;
        DateTime now = DateTime.Now;
        DateTime start = now.Date.AddHours(now.Hour + 1);
        var row = new TriggerRow(new ScheduleTrigger(null, ScheduleKind.Daily, start, 1, 0, true, "", null, null, ""));
        rows.Add(row);
        SelectRow(row);
        TriggerGrid.ScrollIntoView(row);
        KindBox.Focus();
    }

    void OnRemove(object sender, RoutedEventArgs e)
    {
        if (selected?.IsEditable != true) return;
        int index = rows.IndexOf(selected);
        changingSelection = true;
        rows.RemoveAt(index);
        SelectRow(rows.Count > 0 ? rows[Math.Min(index, rows.Count - 1)] : null);
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryApplyEditor()) return;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            save(rows.Select(row => row.Value).ToArray());
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowError($"Could not save the schedule. {ex.Message}");
        }
    }

    bool TryApplyEditor()
    {
        if (selected?.IsEditable != true) return true;

        ErrorText.Visibility = Visibility.Collapsed;
        if (DateBox.SelectedDate is not DateTime date)
            return ShowError("Choose a start date.");
        if (!TimeSpan.TryParseExact(TimeBox.Text.Trim(), ClockFormats, CultureInfo.InvariantCulture, out TimeSpan clock))
            return ShowError("Enter the time in 24-hour format, such as 08:30 or 08:30:15.");

        var original = selected.Value;
        TimeSpan originalTime = original.Start.Value.TimeOfDay;
        if (originalTime.Ticks - originalTime.Ticks % TimeSpan.TicksPerSecond == clock.Ticks)
            clock = originalTime;

        var kind = EditableKinds[KindBox.SelectedIndex];
        int interval = 1;
        int weekdays = 0;
        if (kind == ScheduleKind.Daily && !TryParseInterval(DailyIntervalBox.Text, 365, "Daily interval", out interval))
            return false;
        if (kind == ScheduleKind.Weekly)
        {
            if (!TryParseInterval(WeeklyIntervalBox.Text, 52, "Weekly interval", out interval)) return false;
            for (int i = 0; i < weekdayBoxes.Length; i++)
                if (weekdayBoxes[i].IsChecked == true) weekdays |= 1 << i;
            if (weekdays == 0) return ShowError("Select at least one weekday.");
        }

        if (!TryParseDuration(RepeatIntervalBox.Text, "Repetition interval", out TimeSpan? repeatInterval) ||
            !TryParseDuration(RepeatDurationBox.Text, "Repetition duration", out TimeSpan? repeatDuration))
            return false;
        if (repeatInterval > TimeSpan.FromDays(31))
            return ShowError("The repetition interval can be at most 31 days.");
        if (repeatDuration != null && repeatInterval == null)
            return ShowError("Enter a repetition interval before setting a duration.");

        selected.Update(original with
        {
            Kind = kind,
            Start = date.Date.Add(clock),
            Interval = interval,
            Weekdays = weekdays,
            Enabled = EnabledBox.IsChecked == true,
            RepeatInterval = repeatInterval,
            RepeatDuration = repeatDuration,
        });
        return true;
    }

    bool TryParseInterval(string text, int max, string label, out int value) =>
        (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= max)
        || ShowError($"{label} must be a whole number from 1 to {max}.");

    bool TryParseDuration(string text, string label, out TimeSpan? value)
    {
        value = null;
        text = text.Trim();
        if (text == "") return true;
        if (!text.Contains(':') || !TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out TimeSpan duration)
            || duration < TimeSpan.FromMinutes(1))
            return ShowError($"{label} must be at least one minute. Use hh:mm:ss or d.hh:mm:ss.");
        value = duration;
        return true;
    }

    void LoadEditor()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        bool editable = selected?.IsEditable == true;
        FormPanel.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        UnsupportedText.Visibility = selected != null && !editable ? Visibility.Visible : Visibility.Collapsed;
        EditorTitle.Text = selected == null ? "No triggers selected" : editable ? "Trigger settings" : selected.Value.Summary;
        RemoveButton.IsEnabled = editable;
        AddButton.IsEnabled = rows.Count < 48;
        if (!editable) return;

        var trigger = selected.Value;
        DateTime start = trigger.Start.Value;
        DateBox.SelectedDate = start.Date;
        TimeBox.Text = start.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        OffsetText.Text = trigger.Offset switch
        {
            "" => "Local time",
            "Z" => "UTC",
            _ => "UTC" + trigger.Offset,
        };
        EnabledBox.IsChecked = trigger.Enabled;
        DailyIntervalBox.Text = trigger.Kind == ScheduleKind.Daily ? trigger.Interval.ToString(CultureInfo.InvariantCulture) : "1";
        WeeklyIntervalBox.Text = trigger.Kind == ScheduleKind.Weekly ? trigger.Interval.ToString(CultureInfo.InvariantCulture) : "1";
        int weekdays = trigger.Kind == ScheduleKind.Weekly ? trigger.Weekdays : 1 << (int)start.DayOfWeek;
        for (int i = 0; i < weekdayBoxes.Length; i++) weekdayBoxes[i].IsChecked = (weekdays & (1 << i)) != 0;
        RepeatIntervalBox.Text = trigger.RepeatInterval?.ToString("c", CultureInfo.InvariantCulture) ?? "";
        RepeatDurationBox.Text = trigger.RepeatDuration?.ToString("c", CultureInfo.InvariantCulture) ?? "";
        KindBox.SelectedIndex = Array.IndexOf(EditableKinds, trigger.Kind);
        UpdateKindFields();
    }

    void OnKindChanged(object sender, SelectionChangedEventArgs e) => UpdateKindFields();

    void UpdateKindFields()
    {
        DailyPanel.Visibility = KindBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        WeeklyPanel.Visibility = KindBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    bool ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
        return false;
    }

    sealed class TriggerRow(ScheduleTrigger value) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        public ScheduleTrigger Value { get; private set; } = value;
        public bool IsEditable => Value.Kind != ScheduleKind.Unsupported;
        public string EnabledLabel => Value.Enabled ? "On" : "Off";

        public string Summary
        {
            get
            {
                if (!IsEditable) return Value.Summary;
                string time = Value.Start.Value.ToString("HH:mm", CultureInfo.InvariantCulture);
                string days = string.Join(", ", DayNames.Where((_, i) => (Value.Weekdays & (1 << i)) != 0));
                return Value.Kind switch
                {
                    ScheduleKind.Once => $"Once on {Value.Start.Value:d} at {time}",
                    ScheduleKind.Daily when Value.Interval == 1 => $"Daily at {time}",
                    ScheduleKind.Daily => $"Every {Value.Interval} days at {time}",
                    ScheduleKind.Weekly when Value.Interval == 1 => $"Weekly on {days} at {time}",
                    _ => $"Every {Value.Interval} weeks on {days} at {time}",
                };
            }
        }

        public void Update(ScheduleTrigger updated)
        {
            if (Value == updated) return;
            Value = updated;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EnabledLabel)));
        }
    }
}
