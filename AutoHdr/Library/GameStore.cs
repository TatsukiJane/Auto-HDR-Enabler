using System.Collections.ObjectModel;
using AutoHdr.Core;

namespace AutoHdr.Library;

public sealed record MergeResult(int Found, int Added, int Removed);

/// <summary>
/// The game list. The collection changes only on the UI thread,
/// matching a game by process path uses an immutable snapshot and is safe from any thread.
/// </summary>
public sealed class GameStore
{
    readonly System.Threading.Timer saveTimer;
    readonly object saveGate = new();
    volatile GameRecord[] snapshot = [];

    GameStore(IEnumerable<GameRecord> games)
    {
        foreach (var g in games) Games.Add(g);
        Games.CollectionChanged += (_, _) => RebuildSnapshot();
        RebuildSnapshot();
        saveTimer = new System.Threading.Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public ObservableCollection<GameRecord> Games { get; } = [];

    public static GameStore Load()
    {
        var list = JsonStore.Load<List<GameRecord>>(AppPaths.GamesFile);
        foreach (var g in list.Where(g => g.Source == GameSources.Manual))
            g.Missing = g.ExePath == null || !File.Exists(g.ExePath);
        return new GameStore(list);
    }

    void RebuildSnapshot() => snapshot = Games.ToArray();

    /// <summary>Called both from the UI thread and from the SaveSoon timer: without a lock both writes share games.json.tmp.</summary>
    public void Save()
    {
        lock (saveGate)
        {
            try { JsonStore.Save(AppPaths.GamesFile, snapshot.ToList()); }
            catch (Exception ex) { Log.Error(Loc.T("Не удалось сохранить список игр", "Could not save the game list"), ex); }
        }
    }

    /// <summary>Save with a delay so that a series of checkbox clicks writes the file once.</summary>
    public void SaveSoon() => saveTimer.Change(700, Timeout.Infinite);

    /// <summary>The game an exe belongs to: exact path match or the deepest install folder.</summary>
    public GameRecord? Match(string exePath)
    {
        GameRecord? best = null;
        int bestLength = -1;
        foreach (var g in snapshot)
        {
            if (g.Missing) continue;
            if (g.ExePath != null && string.Equals(g.ExePath, exePath, StringComparison.OrdinalIgnoreCase)) return g;
            if (g.InstallDir != null && g.InstallDir.Length > bestLength && PathUtil.IsUnder(exePath, g.InstallDir))
            {
                best = g;
                bestLength = g.InstallDir.Length;
            }
        }
        return best;
    }

    /// <summary>
    /// Merges a scan result into the current list. Manual settings are kept.
    /// Games from scanned sources that are no longer on disk are marked as not installed.
    /// </summary>
    public MergeResult Merge(IReadOnlyList<GameRecord> scanned, IReadOnlySet<string> scannedSources, IEnumerable<string> ignoredIds)
    {
        var ignored = new HashSet<string>(ignoredIds, StringComparer.OrdinalIgnoreCase);
        var byId = Games.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);
        var foundIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var takenDirs = new List<string>();
        int added = 0;

        // Higher-priority sources first: a folder duplicate goes to them.
        // A duplicate is the same folder or a nested one (Rockstar keeps GTA IV in a subfolder of the Steam game).
        var ordered = scanned.OrderBy(g => Array.IndexOf(GameSources.Priority, g.Source)).ToList();
        foreach (var found in ordered)
        {
            if (ignored.Contains(found.Id)) continue;
            if (found.InstallDir is { } dir)
            {
                if (takenDirs.Any(t => string.Equals(t, dir, StringComparison.OrdinalIgnoreCase)
                                       || PathUtil.IsUnder(dir, t) || PathUtil.IsUnder(t, dir)))
                    continue;
                takenDirs.Add(dir);
            }
            foundIds.Add(found.Id);

            if (byId.TryGetValue(found.Id, out var existing))
            {
                existing.Name = found.Name;
                existing.InstallDir = found.InstallDir;
                existing.ExePath = found.ExePath ?? existing.ExePath;
                existing.StoreId = found.StoreId ?? existing.StoreId;
                existing.Missing = false;
                continue;
            }

            Games.Add(found);
            byId[found.Id] = found;
            added++;
        }

        int removed = 0;
        foreach (var g in Games)
        {
            if (!scannedSources.Contains(g.Source) || foundIds.Contains(g.Id) || g.Missing) continue;
            g.Missing = true;
            removed++;
        }

        return new MergeResult(foundIds.Count, added, removed);
    }

    public GameRecord AddOrGet(GameRecord game)
    {
        var existing = Games.FirstOrDefault(g => string.Equals(g.Id, game.Id, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        Games.Add(game);
        return game;
    }

    public void Remove(GameRecord game) => Games.Remove(game);
}
