using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace DocsSync;

// Which files count as code, and how to spot function names in each language.
// shortcut: regexes, not parsers; they catch ordinary declarations and can miss unusual formatting.
public static class Languages
{
    const RegexOptions M = RegexOptions.Multiline | RegexOptions.CultureInvariant;

    static readonly Regex Lua = new(@"^\s*(?:local\s+)?function\s+([\w.:]+)", M);
    static readonly Regex Python = new(@"^\s*(?:async\s+)?def\s+(\w+)", M);
    static readonly Regex Js = new(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*(\w+)|^\s*(?:export\s+)?(?:const|let|var)\s+(\w+)\s*=\s*(?:async\s*)?(?:\([^)]*\)|\w+)\s*=>", M);
    static readonly Regex Go = new(@"^func\s+(?:\([^)]*\)\s*)?(\w+)", M);
    static readonly Regex Rust = new(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:const\s+)?(?:async\s+)?(?:unsafe\s+)?fn\s+(\w+)", M);
    static readonly Regex GdScript = new(@"^\s*(?:static\s+)?func\s+(\w+)", M);
    static readonly Regex Swift = new(@"^\s*(?:@\w+\s+)*(?:(?:public|private|internal|fileprivate|open|static|class|override|mutating|final)\s+)*func\s+(\w+)", M);
    static readonly Regex Kotlin = new(@"^\s*(?:(?:public|private|internal|protected|override|open|suspend|inline|private|abstract|operator)\s+)*fun\s+(?:<[^>]+>\s*)?(?:[\w.]+\.)?(\w+)\s*\(", M);
    static readonly Regex Ruby = new(@"^\s*def\s+(?:self\.)?(\w+[?!=]?)", M);
    static readonly Regex Php = new(@"^\s*(?:(?:public|private|protected|static|abstract|final)\s+)*function\s+(\w+)", M);
    // C#, Java, C, C++, Dart: "<modifiers> <type> Name(...)" at the start of a line, not followed by ';'.
    static readonly Regex CLike = new(@"^\s*(?:(?:public|private|protected|internal|static|virtual|override|abstract|async|final|sealed|extern|inline|constexpr|synchronized|unsafe|partial|new)\s+)*[\w<>\[\],.:*&?]+\s+[*&]?(\w+)\s*\([^;]*$", M);
    static readonly HashSet<string> NotFunctions = new() { "if", "for", "foreach", "while", "switch", "catch", "return", "using", "lock", "sizeof", "typeof", "nameof", "else", "new", "delete", "throw", "await" };

    static readonly Dictionary<string, Regex> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".lua"] = Lua, [".luau"] = Lua,
        [".py"] = Python,
        [".js"] = Js, [".jsx"] = Js, [".mjs"] = Js, [".cjs"] = Js, [".ts"] = Js, [".tsx"] = Js, [".vue"] = Js, [".svelte"] = Js,
        [".go"] = Go,
        [".rs"] = Rust,
        [".gd"] = GdScript,
        [".swift"] = Swift,
        [".kt"] = Kotlin, [".kts"] = Kotlin,
        [".rb"] = Ruby,
        [".php"] = Php,
        [".cs"] = CLike, [".java"] = CLike, [".c"] = CLike, [".h"] = CLike, [".cpp"] = CLike, [".cc"] = CLike, [".hpp"] = CLike, [".dart"] = CLike,
    };

    public static bool IsCode(string path, IReadOnlyCollection<string> extra) =>
        ByExtension.ContainsKey(Path.GetExtension(path)) || extra.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> Functions(string path, string source)
    {
        if (!ByExtension.TryGetValue(Path.GetExtension(path), out var rx)) return Enumerable.Empty<string>();
        return rx.Matches(source)
            .Select(m => m.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value)
            .Where(n => !NotFunctions.Contains(n));
    }

    // ".gd, .shader" -> [".gd", ".shader"]
    public static List<string> ParseExtensions(string text) =>
        text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
