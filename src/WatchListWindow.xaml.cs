using System.Windows;
using System.Windows.Data;

namespace ScheduledTasks;

public partial class WatchListWindow : Window
{
    readonly List<TaskChoice> choices;
    readonly HashSet<string> original;
    readonly Action<IEnumerable<string>, IEnumerable<string>> save;
    readonly ListCollectionView choiceView;

    internal WatchListWindow(IEnumerable<TaskRow> tasks, HashSet<string> watched,
        Action<IEnumerable<string>, IEnumerable<string>> save)
    {
        original = watched;
        this.save = save;
        // Include saved paths that are currently missing or inaccessible so they can still be removed.
        choices = tasks.Select(t => t.Key).Union(watched, StringComparer.OrdinalIgnoreCase)
            .Select(path => new TaskChoice(path, watched.Contains(path)))
            .OrderByDescending(c => c.Watched)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
        choiceView = new ListCollectionView(choices);
        InitializeComponent();
        TaskChoices.ItemsSource = choiceView;
        Loaded += (_, _) => Filter.Focus();
    }

    void OnFilter(object sender, RoutedEventArgs e)
    {
        string query = Filter.Text.Trim();
        choiceView.Filter = item => ((TaskChoice)item).Key.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var add = choices.Where(c => c.Watched && !original.Contains(c.Key)).Select(c => c.Key);
            var remove = choices.Where(c => !c.Watched && original.Contains(c.Key)).Select(c => c.Key);
            save(add, remove);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    sealed class TaskChoice(string key, bool watched)
    {
        public string Key { get; } = key;
        public string Name { get; } = key[(key.LastIndexOf('\\') + 1)..];
        public string Folder { get; } = key[..Math.Max(1, key.LastIndexOf('\\'))];
        public bool Watched { get; set; } = watched;
    }
}
