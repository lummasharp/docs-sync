using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DocsSync;

// Run with: DocsSync.exe --check <report.txt>   (exit code = number of failures)
public static class SelfCheck
{
    public static int Run(string outPath)
    {
        var log = new StringBuilder();
        var fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; log.AppendLine((ok ? "ok   " : "FAIL ") + what); }

        Check(Scanner.GlobMatch("src/server/**", "src/server/a/b.luau"), "glob ** spans folders");
        Check(!Scanner.GlobMatch("src/server/*.luau", "src/server/a/b.luau"), "glob * stays in one folder");
        Check(Scanner.GlobMatch("src/**/Shop*.luau", "src/client/ShopUI.luau"), "glob mixed");
        Check(Scanner.GlobMatch("src/**/Shop*.luau", "src/ShopUI.luau"), "glob ** matches zero folders");
        Check(Scanner.Keywords("The Sprinter", "Elevator").SequenceEqual(new[] { "Sprinter" }), "keywords drop stop words");
        Check(Scanner.Keywords("Elevator Shop", "Elevator").SequenceEqual(new[] { "Shop" }), "keywords drop project name");
        Check(Scanner.Keywords("Elevator", "Elevator").Count == 0, "hub note has no keywords");
        Check(Scanner.KeywordMatch(new() { "UI" }, "src/client/ShopUI.luau") && !Scanner.KeywordMatch(new() { "UI" }, "src/build.luau"), "acronyms are case-sensitive");
        Check(Scanner.KeywordMatch(new() { "Floors" }, "src/server/FloorGenerator.luau") && Scanner.KeywordMatch(new() { "Entities" }, "src/VoidEntity.luau"), "plural titles match singular files");
        Check(Scanner.Covers("---\ncovers: [src/a/**, \"src/b.luau\"]\n---\nx")?.SequenceEqual(new[] { "src/a/**", "src/b.luau" }) == true, "covers inline list");
        Check(Scanner.Covers("---\r\ntags: [x]\r\ncovers:\r\n  - src/a/**\r\n  - src/b.luau\r\n---\r\n")?.Count == 2, "covers block list (CRLF)");
        Check(Scanner.Covers("# no frontmatter") == null, "no frontmatter");

        var diff = DocUpdater.Diff("a\nb\nc\nd\ne\nf\ng\nh", "a\nb\nC\nd\ne\nf\ng\nh\ni");
        Check(diff.Count(d => d.Kind == DiffKind.Added) == 2 && diff.Count(d => d.Kind == DiffKind.Removed) == 1, "diff counts edits");
        Check(diff.Any(d => d.Kind == DiffKind.Gap) && diff.First().Text == "a", "diff collapses unchanged lines");
        Check(DocUpdater.Diff("x\r\ny", "x\ny\n").Count == 0, "diff ignores line endings");

        Check(LineDiff.ChangedLines("a\nb\nc\nd", "a\nB\nc\nd\ne\nf") == "2, 5–6", "line ranges: edit and append");
        Check(LineDiff.ChangedLines("a\nb\nc\nd", "a\nd") == "2", "line ranges: deletion marks where it was");
        Check(LineDiff.ChangedLines("a\nb", "a\r\nb\r\n") == "", "line ranges: same text, other line endings");

        static string Fns(string file, string src) => string.Join(",", Languages.Functions(file, src));
        Check(Fns("a.py", "def run():\n    pass\nasync def fetch(x):\n") == "run,fetch", "functions: Python");
        Check(Fns("a.cs", "public void Run()\n{\n    if (x)\n    Foo();\n}\nprivate static async Task<int> LoadAsync(int id)\n") == "Run,LoadAsync", "functions: C#, skips calls and if");
        Check(Fns("a.ts", "export function go() {}\nconst add = (a, b) => a + b;\n") == "go,add", "functions: TypeScript");
        Check(Fns("a.go", "func main() {}\nfunc (s *Srv) Serve() {}\n") == "main,Serve", "functions: Go");
        Check(Fns("a.luau", "local function run() end\nfunction M.jump() end\n") == "run,M.jump", "functions: Luau");
        Check(Languages.IsCode("x.shader", new[] { ".shader" }) && !Languages.IsCode("x.shader", new string[0]), "extra file types");

        // Stale lifecycle on a throwaway project.
        var tmp = Path.Combine(Path.GetTempPath(), "docsync-check-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var src = Directory.CreateDirectory(Path.Combine(tmp, "proj", "src")).FullName;
            var vault = Directory.CreateDirectory(Path.Combine(tmp, "vault", "Proj")).FullName;
            var code = Path.Combine(src, "SprinterAI.luau");
            var note = Path.Combine(vault, "The Sprinter.md");
            var t0 = DateTime.UtcNow;
            File.WriteAllText(code, "local function run() end\n");
            File.WriteAllText(note, "# The Sprinter\n");
            File.SetLastWriteTimeUtc(code, t0.AddHours(-1));
            File.SetLastWriteTimeUtc(note, t0.AddHours(-2));
            var cfg = new Config { Vault = Path.Combine(tmp, "vault"), Projects = { new ProjectCfg { Name = "Proj", Root = Path.Combine(tmp, "proj"), VaultFolder = "Proj" } } };
            var state = new Dictionary<string, NoteState>();
            var snaps = Path.Combine(tmp, "snaps");

            var r = Scanner.Scan(cfg, state, t0, snaps).Single();
            Check(r.IsOk && r.CurrentFiles.Count == 1, "first run: matched by name and up to date");

            File.WriteAllText(code, "local function run() end\nfunction jump() end\n");
            File.SetLastWriteTimeUtc(code, t0.AddMinutes(1));
            r = Scanner.Scan(cfg, state, t0, snaps).Single();
            Check(r.IsStale && r.Files.Single().Kind == ChangeKind.Changed && r.FuncsAdded.SequenceEqual(new[] { "jump" }), "code edit makes the note stale, lists new function");
            Check(r.Files.Single().Lines == "2", "changed line range comes from the snapshot");
            var all = DocUpdater.FillTemplateAll("{count}: {notes}", new[] { r }, cfg);
            Check(all.StartsWith("1: ## The Sprinter (Proj)") && all.Contains("lines 2") && all.Contains("Functions added: jump"), "all-notes template lists each note");
            var filled = DocUpdater.FillTemplate("{project}|{note}|{changed_files}|{functions_added}|{functions_removed}|{nope}", r, cfg);
            Check(filled == $"Proj|The Sprinter|- {code} (edited, lines 2)|jump|none|{{nope}}", "prompt template fills tokens, keeps unknown ones");

            Scanner.MarkUpToDate(state, r, t0.AddMinutes(2), snaps);
            Check(Scanner.Scan(cfg, state, t0, snaps).Single().IsOk, "mark up to date clears it");

            File.SetLastWriteTimeUtc(code, t0.AddMinutes(3));
            File.SetLastWriteTimeUtc(note, t0.AddMinutes(4));
            Check(Scanner.Scan(cfg, state, t0, snaps).Single().IsOk, "editing the note after the code clears it");

            File.Delete(code);
            r = Scanner.Scan(cfg, state, t0, snaps).Single();
            Check(r.IsStale && r.Files.Single().Kind == ChangeKind.Removed && r.FuncsRemoved.Count == 2, "deleting covered code makes the note stale");
            Scanner.MarkUpToDate(state, r, t0.AddMinutes(5), snaps);
            Check(Scanner.Scan(cfg, state, t0, snaps).Single().IsUnmatched, "after acknowledging, a note with no code is unmatched");
        }
        finally { Directory.Delete(tmp, true); }

        // Dry run against the real config (state is not saved).
        var real = Scanner.LoadConfig();
        log.AppendLine().AppendLine($"vault: {real.Vault}");
        foreach (var p in real.Projects) log.AppendLine($"project: {p.Name} -> {p.Root} [{string.Join(", ", p.SourceDirs)}]");
        foreach (var rep in Scanner.Scan(real, new(), DateTime.UtcNow))
            log.AppendLine($"  {rep.Status,-9} {rep.Project}/{rep.Title}: {rep.CurrentFiles.Count} files ({rep.MatchedBy})");

        log.AppendLine().AppendLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
        File.WriteAllText(outPath, log.ToString());
        return fails;
    }
}
