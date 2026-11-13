#nullable enable

using System.IO;
using System.Security.Principal;
using System.Text.Json;

namespace ScheduledTasks;

// Persists the task paths that the user has chosen to watch.
internal static class WatchList
{
    static readonly string StorageDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scheduled Tasks Manager");
    static readonly string FilePath = Path.Combine(StorageDirectory, "Watch List.json");

    static readonly string MutexName = $"Local\\ScheduledTasksManager.WatchList.{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";

    internal static HashSet<string>? Load() => WithMutex(() =>
    {
        try { return Read(); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    });

    internal static HashSet<string> Initialize(IEnumerable<string> initialKeys)
    {
        return WithMutex(() =>
        {
            try { return Read(); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }

            var initialSet = new HashSet<string>(initialKeys, StringComparer.OrdinalIgnoreCase);
            if (TryPublishNew(initialSet)) return initialSet;

            // Another writer won the create race. Its data is authoritative,
            // including an explicitly empty set.
            return Read();
        });
    }

    internal static HashSet<string> Update(IEnumerable<string> add, IEnumerable<string> remove)
    {
        return WithMutex(() =>
        {
            HashSet<string> current;
            try { current = Read(); }
            catch (FileNotFoundException) { throw MissingWatchList(); }
            catch (DirectoryNotFoundException) { throw MissingWatchList(); }

            if (add.Any() || remove.Any())
            {
                current.UnionWith(add);
                current.ExceptWith(remove);
                Publish(current);
            }
            return current;
        });
    }

    static FileNotFoundException MissingWatchList() =>
        new("The watch list file is missing. Refresh the Watch list to set it up again.", FilePath);

    static HashSet<string> Read()
    {
        string json = File.ReadAllText(FilePath);
        string[]? paths = JsonSerializer.Deserialize<string[]>(json);
        if (paths is null)
            throw new InvalidDataException("The watch list must contain a JSON array of task paths.");

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || path[0] != '\\' || path[^1] == '\\')
                throw new InvalidDataException("The watch list contains an invalid task path.");
            result.Add(path);
        }
        return result;
    }

    static bool TryPublishNew(HashSet<string> paths)
    {
        try
        {
            Publish(paths, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(FilePath))
        {
            return false;
        }
    }

    static void Publish(HashSet<string> paths, bool overwrite = true)
    {
        Directory.CreateDirectory(StorageDirectory);
        string temporaryPath = Path.Combine(StorageDirectory, $".Watch List.{Guid.NewGuid():N}.tmp");
        try
        {
            string[] orderedPaths = paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, orderedPaths, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, FilePath, overwrite);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    static T WithMutex<T>(Func<T> operation)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool acquired = false;
        try
        {
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // WaitOne acquired an abandoned mutex; the protected data is
                // still safe to inspect and repair under this ownership.
            }
            acquired = true;
            return operation();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }
}
