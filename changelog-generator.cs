using Octokit;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

var groupLabelsInOrder = new (string label, string title)[]
{
    ("api", "New features/APIs"),
    //("enhancement", "Enhancements"),
    //("perf", "Performance"),
    ("area-dev-tools", "Dev-Tools"),
    ("bug", "Bugfixes"),
    ("bugfix", "Bugfixes"),
    ("fix", "Bugfixes"),
    ("fixes", "Bugfixes"),

    ("dev-tools", "Dev-Tools"),
    ("devtools", "Dev-Tools"),
};

var prefixLabels = new (string label, string title)[]
{
    ("breaking-change", "Breaking-Change"),
    ("os-linux", "Linux"),
    ("os-windows", "Windows"),
    ("os-macos", "macOS"),
    ("os-browser", "Browser"),
    ("os-ios", "iOS"),
    ("os-android", "Android"),
    ("area-x11", "Linux"),

    ("win32", "Windows"),
    ("win", "Windows"),
    ("osx", "macOS"),
    ("macos", "macOS"),
    ("browser", "Browser"),
    ("wasm", "Browser"),
    ("ios", "iOS"),
    ("android", "Android")
};

var tagsToIgnore = new string[]
{
    "wont-backport",
    "backported-0.9"
};

var rootCommand = new RootCommand
{
    new Option<string>(
        "--org",
        getDefaultValue: () => "AvaloniaUI"),
    new Option<string>(
        "--repo",
        getDefaultValue: () => "Avalonia"),
    new Option<string>(
        "--from",
        description: "The commitish (SHA, tag etc) of the previous release"),
    new Option<string>(
        "--to",
        description: "The commitish (SHA, tag etc) of the new release"),
    new Option<string>(
        "--auth-token",
        description: "The auth token")
};

rootCommand.Handler = CommandHandler.Create<string, string, string, string, string?>(async (org, repo, from, to, authToken) =>
{
    var mergeMessageRegex = new Regex(@"^(Revert \"")?Merge pull request #(\d*)");
    var github = string.IsNullOrEmpty(authToken)
        ? new GitHubClient(new ProductHeaderValue("changelog-gen"))
        : new GitHubClient(new ProductHeaderValue("changelog-gen"), new Store(authToken));

    var repository = await github.Repository.Get(org, repo);
    var compareResult = await github.Repository.Commit.Compare(repository.Id, from, to);
    var merged = new List<int>();
    
    foreach (var commit in compareResult.Commits)
    {
        var match = mergeMessageRegex.Match(commit.Commit.Message);
        
        if (match.Success && int.TryParse(match.Groups[2].Value, out var prNumber))
        {
            var revert = match.Groups[1].Success;

            if (!revert)
                merged.Add(prNumber);
            else
                merged.Remove(prNumber);
        }
    }

    if (merged.Count > 0)
    {
        Console.WriteLine($"Found {merged.Count} merged PRs between {from} and {to}.");
        Console.WriteLine();
    }
    else
    {
        Console.WriteLine("No merged PRs found");
        return;
    }

    merged.Sort();

    var prs = new List<GhIssue>();

    foreach (var prNumber in merged)
    {
        Console.WriteLine($"Reading #{prNumber}");
        var pr = await github.PullRequest.Get(repository.Id, prNumber);
        prs.Add(new(pr));
    }

    Console.WriteLine();

    foreach (var group in ConvertIssues(prs).GroupBy(i => i.Group))
    {
        Console.WriteLine($"### {group.Key ?? "Misc"}");
        Console.WriteLine();
        foreach (var (_, number, prefixes, title, questionable) in group.OrderBy(x => x.Number))
        {
            if (questionable)
            {
                Console.Write("??? ");
            }
            Console.WriteLine(prefixes.Length > 0
                ? $"#{number} {prefixes} {title}"
                : $"#{number} {title}");
        }
        Console.WriteLine();
        Console.WriteLine();
    }

    Console.ReadLine();
});

return await rootCommand.InvokeAsync(args);

IEnumerable<(string Group, int Number, string Prefixes, string Title, bool Questionable)> ConvertIssues(IEnumerable<GhIssue> issues)
{
    foreach (var group in issues
    .Where(item => !item.Labels.Any(l => tagsToIgnore.Contains(l.Name)))
    .Select(item => (
        item,
        prefix: TransformLabels(item.Labels, prefixLabels).Concat(ParseLabelsFromTitle(item.Title, prefixLabels))
            .Select(t => t.title).Distinct().ToArray(),
        group: TransformLabels(item.Labels, groupLabelsInOrder).Concat(ParseLabelsFromTitle(item.Title, groupLabelsInOrder)).FirstOrDefault()
    ))
    .GroupBy(t => t.group.title)
    .OrderBy(t => groupLabelsInOrder.Select(l => l.title).ToList().IndexOf(t.Key) is var index && index >= 0 ? index : int.MaxValue))
    {
        foreach (var (item, prefix, _) in group.OrderByDescending(t => t.prefix.Length > 0).ThenBy(t => t.item.Number))
        {
            var prefixes = string.Concat(prefix.OrderBy(t => prefixLabels
                .Select(l => l.title).ToList().IndexOf(t) is var index && index >= 0 ? index : int.MaxValue)
                .Select(p => $"[{p}]"));
            var hasBackportedLabel = item.Labels.Any(l => l.Name == "backported-0.10.x");

            yield return (group.Key ?? "Misc", item.Number, prefixes, item.Title, !hasBackportedLabel);
        }
    }
}


static IEnumerable<(string label, string title)> TransformLabels(IReadOnlyList<Label> labels, (string label, string title)[] transformation)
    => transformation.Where(t => labels.Any(l => StringComparer.OrdinalIgnoreCase.Equals(t.label, l.Name)));

static IEnumerable<(string label, string title)> ParseLabelsFromTitle(string title, (string label, string title)[] transformation)
    => transformation.Where(t => new Regex($@"\b({t.label})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline).IsMatch(title));

record Store(string Token) : ICredentialStore
{
    public Task<Credentials> GetCredentials() => Task.FromResult(new Credentials(Token));
}

record GhIssue(int Number, string Title, IReadOnlyList<Label> Labels)
{
    public GhIssue(Issue issue) : this(issue.Number, issue.Title, issue.Labels) { }

    public GhIssue(PullRequest pr) : this(pr.Number, pr.Title, pr.Labels) { }
}