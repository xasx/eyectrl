namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Thread-census ratchet for <c>src/**/*.cs</c> (18.11 exit-hang ticket).
/// <para>
/// The 87-minute linger was only possible because nothing bounded the exit; the investigation first
/// had to rule out every product thread ("no foreground thread exists" — the runtime waits only for
/// foreground threads once <c>Main</c> returns). That census is now a test: every thread the product
/// creates must be a background thread, so a diagnostic or worker can never keep the process alive.
/// </para>
/// <para>
/// It is a <b>ratchet, not the guard</b>: it passes on 1.9.0 and would NOT have caught 18.11. The
/// guard is the exit watchdog plus the per-step exit log.
/// </para>
/// <para>
/// Second rule: every <c>Dispatcher.Run()</c> pump needs a shutdown path in its file
/// (<c>InvokeShutdown</c>, including <c>BeginInvokeShutdown</c>), or the thread parks forever with
/// its windows alive.
/// </para>
/// </summary>
public class ThreadCensusRatchetTests
{
    [Fact]
    public void EveryThreadCreatedInProductSource_IsBackground()
    {
        var offenders = new List<string>();

        foreach ((string relative, string text) in ProductSources())
        {
            foreach (int index in FindAll(text, "new Thread("))
            {
                string statement = StatementAt(text, index);
                if (!statement.Contains("IsBackground = true", StringComparison.Ordinal))
                    offenders.Add($"{relative}:{LineAt(text, index)}");
            }
        }

        Check.That(string.Join(Environment.NewLine, offenders)).IsEmpty();
    }

    [Fact]
    public void EveryDispatcherRunLoop_HasAShutdownInItsFile()
    {
        var offenders = ProductSources()
            .Where(file => file.Text.Contains("Dispatcher.Run()", StringComparison.Ordinal))
            .Where(file => !file.Text.Contains("InvokeShutdown", StringComparison.Ordinal)) // covers BeginInvokeShutdown too
            .Select(file => file.Relative)
            .ToList();

        Check.That(string.Join(Environment.NewLine, offenders)).IsEmpty();
    }

    /// <summary>
    /// Every product .cs file under <c>src</c>, comment-stripped (a commented-out thread must not
    /// count, and a comment naming <c>Dispatcher.Run()</c> must not satisfy the shutdown rule).
    /// </summary>
    private static IEnumerable<(string Relative, string Text)> ProductSources()
    {
        DirectoryInfo root = SourceScanner.RepoRoot();
        string src = Path.Combine(root.FullName, "src");

        foreach (string path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root.FullName, path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal))
                continue;

            yield return (relative, SourceScanner.StripComments(File.ReadAllText(path)));
        }
    }

    private static IEnumerable<int> FindAll(string text, string needle)
    {
        int index = text.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            yield return index;
            index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The whole statement that starts at <paramref name="start"/>: from <c>new Thread(</c> through the
    /// first <c>;</c> outside every (), {} and [] — which is where the object initializer
    /// (<c>{ IsBackground = true, ... }</c>) lives, including multi-line initializers and lambda bodies.
    /// </summary>
    private static string StatementAt(string text, int start)
    {
        int parens = 0, braces = 0, brackets = 0;

        for (int i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(': parens++; break;
                case ')': parens--; break;
                case '{': braces++; break;
                case '}': braces--; break;
                case '[': brackets++; break;
                case ']': brackets--; break;
                case ';' when parens == 0 && braces == 0 && brackets == 0:
                    return text[start..(i + 1)];
            }
        }

        return text[start..];
    }

    private static int LineAt(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;
}
