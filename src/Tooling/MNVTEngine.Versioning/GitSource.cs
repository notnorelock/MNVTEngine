using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace MNVTEngine.Versioning;

/// <summary>Where the source of a build came from.</summary>
/// <param name="Kind">"git" (read from the repository) or "unavailable" (no Git metadata: source archive, no git).</param>
/// <param name="Commit">Full commit hash; null when unavailable.</param>
/// <param name="Branch">Branch name; in a detached HEAD the CI's branch if it names one, else null.</param>
/// <param name="CommitCount">Commits in the history (PATCH); null when it cannot be counted (shallow clone, no Git).</param>
/// <param name="Problem">Why something is missing, for the build log and the metadata.</param>
public sealed record SourceRevision(
    string Kind, string? Commit, string? Branch, string? Subject, bool Dirty, bool Detached, bool Shallow, int? CommitCount, string? Problem)
{
    public static SourceRevision Unavailable(string problem) => new("unavailable", null, null, null, Dirty: false, Detached: false, Shallow: false, null, problem);

    /// <summary>The first 12 characters of the commit, or "unknown".</summary>
    public string ShortCommit => Commit is { Length: >= 12 } ? Commit[..12] : Commit ?? "unknown";
}

/// <summary>Reads a repository's state with the <c>git</c> command line. Never changes the repository.</summary>
public static class GitSource
{
    private static readonly string[] CiBranchVariables = ["GITHUB_HEAD_REF", "GITHUB_REF_NAME", "BUILD_SOURCEBRANCHNAME", "CI_COMMIT_REF_NAME", "BRANCH_NAME"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <param name="root">A folder of the work tree.</param>
    /// <param name="firstParent">Count only first-parent commits (merges count once).</param>
    /// <param name="environment">Environment lookup, for a CI's branch in a detached HEAD.</param>
    public static SourceRevision Read(string root, bool firstParent, Func<string, string?> environment, string git = "git")
    {
        ArgumentNullException.ThrowIfNull(environment);
        var inside = Run(git, root, "rev-parse", "--is-inside-work-tree");
        if (inside.Error is { } error)
        {
            return SourceRevision.Unavailable(error);
        }

        if (inside.ExitCode != 0 || inside.Output != "true")
        {
            return SourceRevision.Unavailable($"'{root}' is not in a Git work tree.");
        }

        var head = Run(git, root, "rev-parse", "--verify", "HEAD");
        if (head.ExitCode != 0)
        {
            return SourceRevision.Unavailable("The repository has no commits.");
        }

        var shallow = Run(git, root, "rev-parse", "--is-shallow-repository").Output == "true";
        int? count = null;
        string? problem = null;
        if (shallow)
        {
            problem = "Shallow clone: the commit count is incomplete (fetch the full history, or set MNVT_PATCH).";
        }
        else
        {
            var counted = firstParent ? Run(git, root, "rev-list", "--count", "--first-parent", "HEAD") : Run(git, root, "rev-list", "--count", "HEAD");
            if (counted.ExitCode == 0 && int.TryParse(counted.Output, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                count = n;
            }
            else
            {
                problem = $"git rev-list --count failed: {counted.Output}";
            }
        }

        var branch = Run(git, root, "symbolic-ref", "--short", "-q", "HEAD");
        var detached = branch.ExitCode != 0;
        var subject = Run(git, root, "log", "-1", "--format=%s", "HEAD");
        var status = Run(git, root, "status", "--porcelain");
        return new SourceRevision(
            "git",
            head.Output,
            detached ? CiBranch(environment) : branch.Output,
            subject.ExitCode == 0 && subject.Output.Length > 0 ? subject.Output : null,
            Dirty: status.ExitCode != 0 || status.Output.Length > 0, // a status that fails cannot prove the tree clean
            detached,
            shallow,
            count,
            problem);
    }

    /// <summary>The branch a CI checked out in a detached HEAD (only the names CIs document for it).</summary>
    private static string? CiBranch(Func<string, string?> environment) =>
        CiBranchVariables.Select(environment).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static (int ExitCode, string Output, string? Error) Run(string git, string root, params string[] arguments)
    {
        var start = new ProcessStartInfo(git)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(root);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_OPTIONAL_LOCKS"] = "0"; // git status must not take the index lock of a repository in use
        start.Environment["LC_ALL"] = "C";
        try
        {
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return (-1, "", $"git {arguments[0]} did not finish within {Timeout.TotalSeconds:0} s.");
            }

            return (process.ExitCode, process.ExitCode == 0 ? output.Trim() : error.Result.Trim(), null);
        }
        catch (Win32Exception ex)
        {
            return (-1, "", $"Git is not installed or not on PATH ({ex.Message}).");
        }
    }
}
