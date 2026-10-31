using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DocsSync;

public class ProjectCfg
{
    public string Name { get; set; } = "";
    public string Root { get; set; } = "";
    public string VaultFolder { get; set; } = "";
    public List<string> SourceDirs { get; set; } = new() { "src" };
}

public class Config
{
    public string Vault { get; set; } = "";
    public List<ProjectCfg> Projects { get; set; } = new();
    public bool Notifications { get; set; } = true;
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "medium";
    public string PromptTemplate { get; set; } = DocUpdater.DefaultTemplate;
    public string PromptTemplateAll { get; set; } = DocUpdater.DefaultTemplateAll;
    public List<string> ExtraFileTypes { get; set; } = new(); // e.g. ".shader"; common languages are built in
}

// What the code looked like the last time a note was known to be up to date.
public class NoteState
{
    public DateTime AckUtc { get; set; }
    public List<string> Files { get; set; } = new();
    public List<string> Funcs { get; set; } = new();
}

public enum ChangeKind { Changed, Added, Removed }

public class FileChange
{
    public string Rel { get; init; } = "";
    public string Full { get; init; } = "";
    public DateTime MtimeUtc { get; init; }
    public ChangeKind Kind { get; init; }
    public string Lines { get; set; } = ""; // "12–40, 88" when an older copy is known
    public string LinesLabel => Lines.Length > 0 ? "lines " + Lines : "";
    public string Meta => string.Join(" · ", new[] { KindLabel, LinesLabel, When }.Where(s => s.Length > 0));
    public string Glyph => Kind switch { ChangeKind.Added => "", ChangeKind.Removed => "", _ => "" };
    public string KindLabel => Kind switch { ChangeKind.Added => "new", ChangeKind.Removed => "deleted", _ => "edited" };
    public string When => Kind == ChangeKind.Removed ? "" : Scanner.Ago(MtimeUtc);
}

public enum NoteStatus { Stale, Ok, Unmatched }

public class NoteReport
{
    public string Project { get; init; } = "";
    public string ProjectRoot { get; init; } = "";
    public string Title { get; init; } = "";
    public string NotePath { get; init; } = "";
    public string Key { get; init; } = "";
    public string VaultName { get; init; } = "";
    public DateTime NoteMtimeUtc { get; init; }
    public string MatchedBy { get; init; } = "";
    public List<FileChange> Files { get; } = new();
    public List<string> FuncsAdded { get; } = new();
    public List<string> FuncsRemoved { get; } = new();
    public List<string> CurrentFiles { get; init; } = new();
    public List<string> CurrentFuncs { get; init; } = new();
    public string ClaudeBadge { get; set; } = ""; // "Working" / "Ready" while Claude has this note

    public NoteStatus Status => Files.Count > 0 || FuncsAdded.Count > 0 || FuncsRemoved.Count > 0 ? NoteStatus.Stale
        : CurrentFiles.Count == 0 ? NoteStatus.Unmatched : NoteStatus.Ok;
    public bool IsStale => Status == NoteStatus.Stale;
    public bool IsOk => Status == NoteStatus.Ok;
    public bool IsUnmatched => Status == NoteStatus.Unmatched;
    public bool HasFiles => Files.Count > 0;
    public bool HasFuncs => FuncsAdded.Count + FuncsRemoved.Count > 0;
    public DateTime LastChangeUtc => Files.Where(f => f.Kind != ChangeKind.Removed).Select(f => f.MtimeUtc).DefaultIfEmpty(NoteMtimeUtc).Max();

    public string Summary => Status switch
    {
        NoteStatus.Unmatched => "No code matched",
        NoteStatus.Ok => $"Up to date · {CurrentFiles.Count} file{(CurrentFiles.Count == 1 ? "" : "s")}",
        _ => Files.Count > 0 ? $"{Files.Count} file{(Files.Count == 1 ? "" : "s")} changed" : "Functions changed",
    };
    public string ListTime => Status == NoteStatus.Stale ? Scanner.Ago(LastChangeUtc) : "";
    public string NoteEdited => "Note edited " + Scanner.Ago(NoteMtimeUtc);
    public IEnumerable<string> FuncLines => FuncsAdded.Select(f => "+  " + f).Concat(FuncsRemoved.Select(f => "−  " + f));
    public string CoversSnippet => "---\ncovers: [src/path/to/" + Title.Replace(" ", "") + "/**]\n---";
}

public static class Scanner
{
    static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "of", "and", "with", "for", "to", "in", "on", "my" };

    public static string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DocsSync");
    public static string ConfigPath => Path.Combine(AppDir, "config.json");
    static string StatePath => Path.Combine(AppDir, "state.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Config LoadConfig()
    {
        Directory.CreateDirectory(AppDir);
        if (File.Exists(ConfigPath))
        {
            try { return JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? DefaultConfig(); }
            catch (JsonException) { return DefaultConfig(); } // keep the user's broken file; they can fix it from Settings
        }
        var cfg = DefaultConfig();
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, Json));
        return cfg;
    }

    // Pairs each vault folder with a same-named folder in Desktop\projects (Elevator <-> elevator).
    public static string DefaultProjectsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "projects");

    // Pairs each vault folder with a same-named folder in projectsDir (Elevator <-> elevator).
    public static Config DefaultConfig(string? projectsDir = null, string? vault = null)
    {
        var cfg = new Config { Vault = vault ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Obsidian Vault") };
        var projects = projectsDir ?? DefaultProjectsDir;
        if (!Directory.Exists(cfg.Vault) || !Directory.Exists(projects)) return cfg;
        foreach (var vdir in Directory.GetDirectories(cfg.Vault))
        {
            var name = Path.GetFileName(vdir);
            if (name.StartsWith('.')) continue;
            var root = Directory.GetDirectories(projects).FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
            if (root == null) continue;
            cfg.Projects.Add(new ProjectCfg
            {
                Name = name, Root = root, VaultFolder = name,
                SourceDirs = Directory.Exists(Path.Combine(root, "src")) ? new() { "src" } : new() { "." },
            });
        }
        return cfg;
    }

    public static void SaveConfig(Config cfg)
    {
        Directory.CreateDirectory(AppDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, Json));
    }

    public static Dictionary<string, NoteState> LoadState()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, NoteState>>(File.ReadAllText(StatePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public static void SaveState(Dictionary<string, NoteState> state)
    {
        Directory.CreateDirectory(AppDir);
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        File.Move(tmp, StatePath, true);
    }

    public static List<string> Keywords(string title, string project) =>
        Regex.Split(title, @"[^A-Za-z0-9]+")
            .Where(t => !StopWords.Contains(t) && !t.Equals(project, StringComparison.OrdinalIgnoreCase))
            .Where(t => t.Length >= 3 || (t.Length == 2 && t.All(char.IsUpper)))
            .ToList();

    // Acronyms (UI, AI) match case-sensitively so "UI" doesn't hit "build".
    public static bool KeywordMatch(List<string> keywords, string rel) =>
        keywords.Count > 0 && keywords.All(k => k.All(char.IsUpper)
            ? rel.Contains(k, StringComparison.Ordinal)
            : rel.Contains(Singular(k), StringComparison.OrdinalIgnoreCase));

    // "Floors" note -> FloorGenerator.luau, "Entities" -> VoidEntity.luau
    static string Singular(string w) =>
        w.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && w.Length > 4 ? w[..^3] + "y"
        : w.EndsWith('s') && !w.EndsWith("ss") && w.Length > 3 ? w[..^1] : w;

    public static bool GlobMatch(string glob, string rel)
    {
        var rx = "^" + Regex.Escape(glob.Replace('\\', '/').TrimStart('.', '/'))
            .Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$";
        return Regex.IsMatch(rel, rx, RegexOptions.IgnoreCase);
    }

    // Reads `covers:` from frontmatter, inline ([a, b]), list (- a) or single value.
    public static List<string>? Covers(string text)
    {
        if (!text.StartsWith("---")) return null;
        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return null;
        var fm = text[3..end].Replace("\r", "") + "\n";
        static string Clean(string s) => s.Trim().Trim('"', '\'');
        var inline = Regex.Match(fm, @"^covers:[ \t]*\[(.*)\][ \t]*$", RegexOptions.Multiline);
        if (inline.Success) return inline.Groups[1].Value.Split(',').Select(Clean).Where(s => s.Length > 0).ToList();
        var list = Regex.Match(fm, @"^covers:[ \t]*\n((?:[ \t]*-[^\n]*\n)+)", RegexOptions.Multiline);
        if (list.Success) return list.Groups[1].Value.Split('\n').Select(l => Clean(l.Trim().TrimStart('-'))).Where(s => s.Length > 0).ToList();
        var single = Regex.Match(fm, @"^covers:[ \t]*(\S.*)$", RegexOptions.Multiline);
        return single.Success ? new() { Clean(single.Groups[1].Value) } : null;
    }

    public static string SnapshotDir => Path.Combine(AppDir, "snapshots");

    // Copies of each covered file as the note last knew it, so changed line ranges can be shown.
    static string SnapshotFolder(string root, string noteKey) =>
        Path.Combine(root, Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(noteKey)))[..16]);

    static void SaveSnapshot(string? root, string noteKey, string projectRoot, IEnumerable<string> rels)
    {
        if (root == null) return;
        var dir = SnapshotFolder(root, noteKey);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            foreach (var rel in rels)
            {
                var dest = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(Path.Combine(projectRoot, rel), dest, true);
            }
        }
        catch (IOException) { } // line numbers are a nice-to-have; tracking still works without them
        catch (UnauthorizedAccessException) { }
    }

    static readonly Dictionary<string, (DateTime Mtime, string Lines)> LinesCache = new();

    static string ChangedLines(string? root, string noteKey, string full, string rel, DateTime mtime)
    {
        if (root == null) return "";
        var snap = Path.Combine(SnapshotFolder(root, noteKey), rel);
        if (LinesCache.TryGetValue(snap, out var hit) && hit.Mtime == mtime) return hit.Lines;
        if (SafeRead(snap) is not { } before || SafeRead(full) is not { } now) return "";
        var lines = LineDiff.ChangedLines(before, now);
        LinesCache[snap] = (mtime, lines);
        return lines;
    }

    // snapshotRoot: where to keep file copies for line numbers; null skips them (dry runs).
    public static List<NoteReport> Scan(Config cfg, Dictionary<string, NoteState> state, DateTime nowUtc, string? snapshotRoot = null)
    {
        var reports = new List<NoteReport>();
        var vaultName = Path.GetFileName(cfg.Vault.TrimEnd('\\', '/'));
        foreach (var p in cfg.Projects)
        {
            var vaultDir = Path.Combine(cfg.Vault, p.VaultFolder);
            if (!Directory.Exists(vaultDir) || !Directory.Exists(p.Root)) continue;
            var code = p.SourceDirs.Select(s => Path.Combine(p.Root, s)).Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                .Where(f => Languages.IsCode(f, cfg.ExtraFileTypes))
                .Select(f => (Full: f, Rel: Path.GetRelativePath(p.Root, f).Replace('\\', '/')))
                .Distinct().ToList();

            foreach (var note in Directory.EnumerateFiles(vaultDir, "*.md", SearchOption.AllDirectories))
            {
                string text;
                try { text = File.ReadAllText(note); } catch (IOException) { continue; } // Obsidian mid-save; next scan gets it
                var title = Path.GetFileNameWithoutExtension(note);
                var globs = Covers(text);
                var keywords = Keywords(title, p.Name);
                var covered = globs != null
                    ? code.Where(c => globs.Any(g => GlobMatch(g, c.Rel))).ToList()
                    : code.Where(c => KeywordMatch(keywords, c.Rel)).ToList();
                var rels = covered.Select(c => c.Rel).ToList();
                var funcs = covered.SelectMany(c => SafeRead(c.Full) is { } src ? Languages.Functions(c.Full, src) : Enumerable.Empty<string>())
                    .Distinct().OrderBy(f => f).ToList();
                var key = Path.GetRelativePath(cfg.Vault, note);
                var noteMtime = File.GetLastWriteTimeUtc(note);

                var r = new NoteReport
                {
                    Project = p.Name, ProjectRoot = p.Root, Title = title, NotePath = note, Key = key, VaultName = vaultName,
                    NoteMtimeUtc = noteMtime, CurrentFiles = rels, CurrentFuncs = funcs,
                    MatchedBy = globs != null ? "covers: " + string.Join(", ", globs) : "name: " + string.Join(" + ", keywords),
                };
                reports.Add(r);
                if (rels.Count == 0 && !state.ContainsKey(key)) continue;

                // First sight, or the note was edited since: the note now describes the code as it is.
                // shortcut: a note edit re-baselines function names too, so renames made before that edit aren't listed.
                if (!state.TryGetValue(key, out var st) || noteMtime > st.AckUtc)
                {
                    state[key] = st = new NoteState { AckUtc = st == null ? nowUtc : noteMtime, Files = rels, Funcs = funcs };
                    SaveSnapshot(snapshotRoot, key, p.Root, rels);
                }

                var old = st.Files.ToHashSet();
                foreach (var c in covered)
                {
                    var mt = File.GetLastWriteTimeUtc(c.Full);
                    if (!old.Contains(c.Rel)) r.Files.Add(new FileChange { Rel = c.Rel, Full = c.Full, MtimeUtc = mt, Kind = ChangeKind.Added });
                    else if (mt > st.AckUtc) r.Files.Add(new FileChange { Rel = c.Rel, Full = c.Full, MtimeUtc = mt, Kind = ChangeKind.Changed, Lines = ChangedLines(snapshotRoot, key, c.Full, c.Rel, mt) });
                }
                foreach (var gone in st.Files.Except(rels))
                    r.Files.Add(new FileChange { Rel = gone, Full = Path.Combine(p.Root, gone), Kind = ChangeKind.Removed });
                r.FuncsAdded.AddRange(funcs.Except(st.Funcs));
                r.FuncsRemoved.AddRange(st.Funcs.Except(funcs));
                r.Files.Sort((a, b) => b.MtimeUtc.CompareTo(a.MtimeUtc));
            }
        }
        return reports
            .OrderBy(r => r.Status)
            .ThenByDescending(r => r.IsStale ? r.LastChangeUtc : DateTime.MinValue)
            .ThenBy(r => r.Project).ThenBy(r => r.Title)
            .ToList();
    }

    public static void MarkUpToDate(Dictionary<string, NoteState> state, NoteReport r, DateTime nowUtc, string? snapshotRoot = null)
    {
        state[r.Key] = new NoteState { AckUtc = nowUtc, Files = r.CurrentFiles, Funcs = r.CurrentFuncs };
        SaveSnapshot(snapshotRoot, r.Key, r.ProjectRoot, r.CurrentFiles);
    }

    static string? SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch (IOException) { return null; }
    }

    public static string Ago(DateTime utc)
    {
        var s = DateTime.UtcNow - utc;
        if (s.TotalMinutes < 1) return "just now";
        if (s.TotalHours < 1) return $"{(int)s.TotalMinutes}m ago";
        if (s.TotalDays < 1) return $"{(int)s.TotalHours}h ago";
        if (s.TotalDays < 30) return $"{(int)s.TotalDays}d ago";
        return utc.ToLocalTime().ToString("d MMM");
    }
}
