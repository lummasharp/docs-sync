using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

namespace DocsSync;

public class DiffLine
{
    public string Text { get; init; } = "";
    public DiffKind Kind { get; init; }
    public string Sign => Kind switch { DiffKind.Added => "+", DiffKind.Removed => "−", _ => " " };
}

public class DocProposal
{
    public string NotePath { get; init; } = "";
    public DateTime NoteMtimeUtc { get; init; }
    public string NewText { get; init; } = "";
    public List<DiffLine> Diff { get; init; } = new();
    public int Added => Diff.Count(d => d.Kind == DiffKind.Added);
    public int Removed => Diff.Count(d => d.Kind == DiffKind.Removed);
    public string Stats => Added + Removed == 0 ? "Claude found nothing to change" : $"+{Added}  −{Removed} lines";
}

public static class DocUpdater
{
    const int MaxInputChars = 600_000; // ~150k tokens; bigger notes or file sets need covers: narrowed

    const string SystemPrompt = """
        You keep a developer's Obsidian notes in sync with their project's source code.
        You get one note and the code files it documents that changed after the note was last edited.
        Update the note so every statement in it is true for the current code.

        Rules:
        - Change only what the code changes require. Keep everything else word for word: wording, headings, order, frontmatter, tags, wikilinks, the "Back to [[...]]" line.
        - Match the note's existing voice, formatting and level of detail. Add a short section only when the code adds something the note's topic clearly covers.
        - Describe behaviour you can see in the code. Never invent numbers, names or features.
        - If a deleted file or function is mentioned, remove or correct that mention.
        - Return the complete updated note as plain Markdown, with no code fence around it and nothing before or after it.
        """;

    public const string DefaultTemplate = """
        Update my Obsidian note so it matches the current code.

        Project: {project} ({project_root})
        Note: {note} ({note_path})

        Code that changed since the note was last edited:
        {changed_files}

        Deleted files: {deleted_files}
        Functions added: {functions_added}
        Functions removed: {functions_removed}

        Read the note and the changed lines in those files. Change only what the code changes require and keep everything else as it is: wording, headings, frontmatter, tags, wikilinks and the "Back to [[...]]" line. Describe only behaviour you can see in the code, and don't invent numbers or features. Edit the note file directly, then tell me in a few lines what you changed.
        """;

    public const string DefaultTemplateAll = """
        Update my Obsidian notes so they match the current code. Each note below lists the code that changed since it was last edited.

        {notes}

        For each note, read it and the changed lines in its files. Change only what the code changes require and keep everything else as it is: wording, headings, frontmatter, tags, wikilinks and the "Back to [[...]]" line. Describe only behaviour you can see in the code, and don't invent numbers or features. Edit the note files directly, then give me a short list of what you changed in each note.
        """;

    public const string TemplateTokens = "{project}  {project_root}  {note}  {note_path}  {vault}  {changed_files}  {deleted_files}  {functions_added}  {functions_removed}  {note_text}  {code}";
    public const string TemplateTokensAll = "{notes}  {vault}  {count}";

    static string ListOrNone(IEnumerable<string> items) { var s = string.Join(", ", items); return s.Length > 0 ? s : "none"; }

    // "- C:\...\Sprinter.server.luau (edited, lines 12–40, 88)"
    static string FileLines(NoteReport r)
    {
        var live = r.Files.Where(f => f.Kind != ChangeKind.Removed).ToList();
        return live.Count == 0 ? "- none" : string.Join("\n", live.Select(f => $"- {f.Full} ({f.KindLabel}{(f.Lines.Length > 0 ? ", lines " + f.Lines : "")})"));
    }

    static string Fill(string template, Dictionary<string, Func<string>> values) =>
        System.Text.RegularExpressions.Regex.Replace(template.Replace("\r\n", "\n"), @"\{(\w+)\}",
            m => values.TryGetValue(m.Groups[1].Value, out var get) ? get() : m.Value).Replace("\n", "\r\n");

    // Fills {tokens} for one note. Unknown tokens are left as written.
    public static string FillTemplate(string template, NoteReport r, Config cfg)
    {
        var live = r.Files.Where(f => f.Kind != ChangeKind.Removed).ToList();
        return Fill(template, new()
        {
            ["project"] = () => r.Project,
            ["project_root"] = () => r.ProjectRoot,
            ["note"] = () => r.Title,
            ["note_path"] = () => r.NotePath,
            ["vault"] = () => cfg.Vault,
            ["changed_files"] = () => FileLines(r),
            ["deleted_files"] = () => ListOrNone(r.Files.Where(f => f.Kind == ChangeKind.Removed).Select(f => f.Rel)),
            ["functions_added"] = () => ListOrNone(r.FuncsAdded),
            ["functions_removed"] = () => ListOrNone(r.FuncsRemoved),
            ["note_text"] = () => File.ReadAllText(r.NotePath),
            ["code"] = () => string.Join("\n\n", live.Select(f => $"```{Path.GetExtension(f.Full).TrimStart('.')}\n// {f.Rel}\n{File.ReadAllText(f.Full).TrimEnd()}\n```")),
        });
    }

    // Fills the all-notes template; {notes} is one section per stale note.
    public static string FillTemplateAll(string template, IReadOnlyList<NoteReport> notes, Config cfg) =>
        Fill(template, new()
        {
            ["count"] = () => notes.Count.ToString(),
            ["vault"] = () => cfg.Vault,
            ["notes"] = () => string.Join("\n\n", notes.Select(r =>
                $"## {r.Title} ({r.Project})\nNote: {r.NotePath}\nChanged code:\n{FileLines(r)}" +
                (r.Files.Any(f => f.Kind == ChangeKind.Removed) ? "\nDeleted files: " + ListOrNone(r.Files.Where(f => f.Kind == ChangeKind.Removed).Select(f => f.Rel)) : "") +
                (r.FuncsAdded.Count > 0 ? "\nFunctions added: " + string.Join(", ", r.FuncsAdded) : "") +
                (r.FuncsRemoved.Count > 0 ? "\nFunctions removed: " + string.Join(", ", r.FuncsRemoved) : ""))),
        });

    public static async Task<DocProposal> Propose(NoteReport r, Config cfg, CancellationToken ct)
    {
        var note = await File.ReadAllTextAsync(r.NotePath, ct);
        var noteMtime = File.GetLastWriteTimeUtc(r.NotePath);

        var sb = new StringBuilder();
        sb.Append("Project: ").AppendLine(r.Project).AppendLine();
        sb.Append("<note path=\"").Append(r.Key).AppendLine("\">").AppendLine(note).AppendLine("</note>").AppendLine();
        foreach (var f in r.Files.Where(f => f.Kind != ChangeKind.Removed))
        {
            var code = await File.ReadAllTextAsync(f.Full, ct);
            sb.Append("<file path=\"").Append(f.Rel).Append("\" status=\"").Append(f.KindLabel)
                .Append(f.Lines.Length > 0 ? "\" changed_lines=\"" + f.Lines : "").AppendLine("\">").AppendLine(code).AppendLine("</file>").AppendLine();
        }
        var removed = r.Files.Where(f => f.Kind == ChangeKind.Removed).Select(f => f.Rel).ToList();
        if (removed.Count > 0) sb.Append("Deleted files: ").AppendLine(string.Join(", ", removed));
        if (r.FuncsAdded.Count > 0) sb.Append("Functions added: ").AppendLine(string.Join(", ", r.FuncsAdded));
        if (r.FuncsRemoved.Count > 0) sb.Append("Functions removed: ").AppendLine(string.Join(", ", r.FuncsRemoved));
        sb.AppendLine().AppendLine("Return the full updated note.");
        if (sb.Length > MaxInputChars)
            throw new DocUpdateException("These files are too large to send in one request. Add a covers: line to the note to narrow it to the files that matter.");

        var key = Secrets.LoadApiKey();
        var client = key != null ? new AnthropicClient { ApiKey = key } : new AnthropicClient(); // falls back to ANTHROPIC_API_KEY
        var useFallback = !cfg.Model.StartsWith("claude-haiku", StringComparison.Ordinal); // Haiku has no server-side fallback

        BetaMessage resp;
        try
        {
            resp = await client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = cfg.Model,
                MaxTokens = 16000,
                Betas = useFallback ? ["server-side-fallback-2026-07-01"] : null,
                Fallbacks = useFallback ? (BetaFallbacksParam)new Default() : null,
                OutputConfig = new BetaOutputConfig { Effort = cfg.Effort switch { "low" => Effort.Low, "high" => Effort.High, _ => Effort.Medium } },
                System = SystemPrompt,
                Messages = [new BetaUserMessageParam { Content = sb.ToString() }],
            }, ct);
        }
        catch (AnthropicUnauthorizedException) { throw new DocUpdateException("Claude rejected the API key. Check it in Settings.", true); }
        catch (AnthropicRateLimitException) { throw new DocUpdateException("Rate limited by the Claude API. Wait a minute and try again.", true); }
        catch (Anthropic5xxException) { throw new DocUpdateException("The Claude API is having trouble right now. Try again in a minute.", true); }
        catch (AnthropicIOException e) { throw new DocUpdateException("Couldn't reach the Claude API. Check your connection. (" + e.Message + ")", true); }
        catch (AnthropicApiException e) { throw new DocUpdateException("The Claude API returned an error: " + e.Message); }

        if (resp.StopReason == "refusal") throw new DocUpdateException("Claude declined this request.");
        if (resp.StopReason == "max_tokens") throw new DocUpdateException("The note is too long for one reply. Split it into smaller notes.");

        var text = string.Concat(resp.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        text = StripFence(text).TrimEnd() + "\n";
        if (string.IsNullOrWhiteSpace(text)) throw new DocUpdateException("Claude returned an empty note. Try again.");
        if (note.Contains("\r\n")) text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"); // keep the note's line endings

        return new DocProposal { NotePath = r.NotePath, NoteMtimeUtc = noteMtime, NewText = text, Diff = Diff(note, text) };
    }

    // Writes the note, keeping a backup. Refuses if the note changed while Claude was working.
    public static string Apply(DocProposal p)
    {
        if (File.GetLastWriteTimeUtc(p.NotePath) != p.NoteMtimeUtc)
            throw new DocUpdateException("The note was edited after Claude read it. Generate the update again.");
        var backups = Directory.CreateDirectory(Path.Combine(Scanner.AppDir, "backups")).FullName;
        var backup = Path.Combine(backups, $"{DateTime.Now:yyyyMMdd-HHmmss} {Path.GetFileName(p.NotePath)}");
        File.Copy(p.NotePath, backup);
        File.WriteAllText(p.NotePath, p.NewText);
        return backup;
    }

    static string StripFence(string s)
    {
        var t = s.Trim();
        if (!t.StartsWith("```")) return s;
        var first = t.IndexOf('\n');
        var last = t.LastIndexOf("```", StringComparison.Ordinal);
        return first > 0 && last > first ? t[(first + 1)..last] : s;
    }

    // Note diff for the preview: unchanged runs collapsed to 2 lines of context.
    public static List<DiffLine> Diff(string a, string b)
    {
        var all = LineDiff.Ops(a, b);
        if (all.All(d => d.Kind == DiffKind.Same)) return new();
        const int ctx = 2;
        var keep = new bool[all.Count];
        for (var i = 0; i < all.Count; i++)
            if (all[i].Kind != DiffKind.Same)
                for (var k = Math.Max(0, i - ctx); k <= Math.Min(all.Count - 1, i + ctx); k++) keep[k] = true;
        var shown = new List<DiffLine>();
        for (var i = 0; i < all.Count; i++)
        {
            if (keep[i]) shown.Add(new DiffLine { Text = all[i].Text, Kind = all[i].Kind });
            else if (shown.Count == 0 || shown[^1].Kind != DiffKind.Gap) shown.Add(new DiffLine { Text = "⋯", Kind = DiffKind.Gap });
        }
        return shown;
    }
}

public class DocUpdateException : Exception
{
    public DocUpdateException(string message, bool stopsBatch = false) : base(message) { StopsBatch = stopsBatch; }
    public bool StopsBatch { get; } // the same error would hit every other note too
}
