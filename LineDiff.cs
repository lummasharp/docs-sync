using System;
using System.Collections.Generic;
using System.Linq;

namespace DocsSync;

public enum DiffKind { Same, Added, Removed, Gap }

// One line of a diff. NewLine is the 1-based line in the new text (for Removed: the line it sat before).
public record DiffOp(DiffKind Kind, string Text, int NewLine);

public static class LineDiff
{
    public static string[] Split(string s) => s.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    public static List<DiffOp> Ops(string oldText, string newText)
    {
        var x = Split(oldText);
        var y = Split(newText);
        // Edits are usually local: strip the shared head and tail so the LCS table stays small.
        int pre = 0;
        while (pre < x.Length && pre < y.Length && x[pre] == y[pre]) pre++;
        int suf = 0;
        while (suf < x.Length - pre && suf < y.Length - pre && x[x.Length - 1 - suf] == y[y.Length - 1 - suf]) suf++;
        var a = x[pre..(x.Length - suf)];
        var b = y[pre..(y.Length - suf)];

        var ops = new List<DiffOp>();
        var line = 1;
        for (var i = 0; i < pre; i++) ops.Add(new DiffOp(DiffKind.Same, y[i], line++));

        if ((long)a.Length * b.Length > 4_000_000)
        {
            // shortcut: a rewrite this big is reported as one replaced block; fine for spotting what changed.
            foreach (var t in a) ops.Add(new DiffOp(DiffKind.Removed, t, line));
            foreach (var t in b) ops.Add(new DiffOp(DiffKind.Added, t, line++));
        }
        else
        {
            var lcs = new int[a.Length + 1, b.Length + 1];
            for (var i = a.Length - 1; i >= 0; i--)
                for (var j = b.Length - 1; j >= 0; j--)
                    lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int p = 0, q = 0;
            while (p < a.Length || q < b.Length)
            {
                if (p < a.Length && q < b.Length && a[p] == b[q]) { ops.Add(new DiffOp(DiffKind.Same, b[q], line++)); p++; q++; }
                else if (q < b.Length && (p == a.Length || lcs[p, q + 1] >= lcs[p + 1, q])) ops.Add(new DiffOp(DiffKind.Added, b[q++], line++));
                else ops.Add(new DiffOp(DiffKind.Removed, a[p++], line));
            }
        }

        for (var i = y.Length - suf; i < y.Length; i++) ops.Add(new DiffOp(DiffKind.Same, y[i], line++));
        return ops;
    }

    // "12–40, 88": lines of the new file that were added or rewritten, plus where lines were deleted.
    public static string ChangedLines(string oldText, string newText)
    {
        var ops = Ops(oldText, newText);
        var marks = new SortedSet<int>();
        var newCount = Split(newText).Length;
        // Per run of changed lines: a rewrite marks its new lines; a pure deletion marks where it was.
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind == DiffKind.Same) continue;
            var j = i;
            while (j < ops.Count && ops[j].Kind != DiffKind.Same) j++;
            var hunk = ops.GetRange(i, j - i);
            if (hunk.Any(o => o.Kind == DiffKind.Added))
                foreach (var o in hunk.Where(o => o.Kind == DiffKind.Added)) marks.Add(o.NewLine);
            else
                marks.Add(Math.Clamp(hunk[0].NewLine, 1, Math.Max(1, newCount)));
            i = j - 1;
        }

        var ranges = new List<string>();
        int? start = null, prev = null;
        foreach (var n in marks.Append(int.MaxValue))
        {
            if (prev != null && n == prev + 1) { prev = n; continue; }
            if (start != null) ranges.Add(start == prev ? $"{start}" : $"{start}–{prev}");
            start = prev = n;
        }
        return string.Join(", ", ranges);
    }
}
