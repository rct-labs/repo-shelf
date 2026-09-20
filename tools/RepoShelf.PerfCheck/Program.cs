// Performance check: seed 5,000 representative repositories, then measure
// warm local search latency. Mirrors tests/perf/run-perf.js (Node reference).
// Run: dotnet run --project tools/RepoShelf.PerfCheck -c Release

using System.Diagnostics;
using RepoShelf.Core;

const int DatasetSize = 5000;
const int Iterations = 30;
const double P95TargetMs = 500;

// Deterministic PRNG so the dataset is reproducible.
var rand = new Random(20260920);
T Pick<T>(IReadOnlyList<T> arr) => arr[rand.Next(arr.Count)];

string[] enWords = ["fast", "modular", "async", "reactive", "typed", "minimal", "distributed", "embedded", "secure", "portable", "declarative", "streaming", "compiler", "runtime", "schema", "router", "cache", "queue", "worker", "parser"];
string[] cnPhrases = ["状态管理", "增量同步", "本地优先", "全文检索", "配置生成", "部署脚本", "数据可视化", "权限控制", "离线缓存", "命令行工具", "静态分析", "任务调度", "Markdown 渲染", "端到端加密", "双语界面"];
string?[] languages = ["TypeScript", "JavaScript", "Python", "Go", "Rust", null];
string[] topicsPool = ["cli", "web", "database", "devops", "testing", "ui", "api", "search", "sync", "security"];
string[] statuses = ["inbox", "inbox", "inbox", "to_investigate", "tried", "adopted", "dismissed"];
string[] projects = ["repo-shelf", "home-lab", "work-infra", "side-app"];

string Sentence(string[] words, int n) => string.Join(' ', Enumerable.Range(0, n).Select(_ => Pick(words)));

var tmp = Path.Combine(Path.GetTempPath(), "repo-shelf-perf-" + Guid.NewGuid().ToString("N"));
using var store = Store.Open(Path.Combine(tmp, "perf.db"));
var repos = new RepoService(store, new GitHubClient(baseUrl: "http://unused"));
var search = new SearchService(store);

Console.WriteLine($"Seeding {DatasetSize} repositories…");
var names = new List<string>();
var seedStart = Stopwatch.GetTimestamp();
for (var i = 1; i <= DatasetSize; i++)
{
    var name = $"{Pick(enWords)}-{Pick(enWords)}-{i}";
    names.Add(name);
    var cn = rand.NextDouble() < 0.45;
    var meta = new RepoMeta(
        GithubId: i,
        Owner: $"user{i % 137}",
        Name: name,
        FullName: $"user{i % 137}/{name}",
        HtmlUrl: $"https://github.com/user{i % 137}/{name}",
        Description: $"{Sentence(enWords, 6)}{(cn ? $"，{Pick(cnPhrases)}的工具" : "")}",
        Topics: new List<string> { Pick(topicsPool), Pick(topicsPool) },
        Language: Pick(languages),
        LicenseId: Pick(new[] { "MIT", "Apache-2.0", "GPL-3.0", null }),
        Archived: rand.NextDouble() < 0.08,
        PushedAt: "2026-09-01T00:00:00Z",
        Stars: rand.Next(20000),
        DefaultBranch: "main");
    var readme = $"# {name}\n\n{Sentence(enWords, 400)}.\n\n## Usage\n\n{Sentence(enWords, 300)}.\n";
    var (repoId, _, _) = repos.UpsertSource(meta, readme, readmeProvided: true);
    repos.UpdateAnnotation(repoId, new System.Text.Json.Nodes.JsonObject
    {
        ["reason"] = cn ? $"因为{Pick(cnPhrases)}很方便" : $"useful for {Sentence(enWords, 4)}",
        ["notes"] = cn ? $"这个库关于{Pick(cnPhrases)}，{Pick(cnPhrases)}做得不错，star 数还行。" : $"tried it for {Sentence(enWords, 8)}",
        ["tags"] = new System.Text.Json.Nodes.JsonArray(Pick(topicsPool), cn ? "中文笔记" : "english-note"),
        ["projects"] = rand.NextDouble() < 0.4 ? new System.Text.Json.Nodes.JsonArray(Pick(projects)) : new System.Text.Json.Nodes.JsonArray(),
        ["status"] = Pick(statuses),
    });
}
Console.WriteLine($"Seeded in {Stopwatch.GetElapsedTime(seedStart).TotalSeconds:F1}s");

var queries = new (string Label, Func<string> Q, Func<SearchService.SearchParams, SearchService.SearchParams>? With)[]
{
    ("exact-name", () => names[1234], null),
    ("english-readme-term", () => "compiler", null),
    ("chinese-notes-term", () => "状态管理", null),
    ("chinese-phrase", () => "增量同步", null),
    ("mixed-query", () => "cache 离线缓存", null),
    ("owner", () => "user42", null),
    ("rare-term", () => "declarative streaming", null),
    ("no-results", () => "definitely-not-present-xyz", null),
    ("filter-status", () => "parser", p => p with { Status = "adopted" }),
    ("filter-language", () => "queue", p => p with { Language = "Rust" }),
    ("filter-tag-archived", () => "web", p => p with { Tag = "sync", Archived = false }),
    ("empty-query-listing", () => "", null),
};

// Warm-up.
foreach (var (_, gen, with) in queries)
{
    for (var i = 0; i < 3; i++)
    {
        var p = new SearchService.SearchParams(Query: gen());
        search.Search(with?.Invoke(p) ?? p);
    }
}

Console.WriteLine("\nMeasuring (each query x30, warm)…");
var allSamples = new List<double>();
Console.WriteLine("\nquery                    mean(ms)   p95(ms)   max(ms)");
foreach (var (label, gen, with) in queries)
{
    var samples = new List<double>();
    for (var i = 0; i < Iterations; i++)
    {
        var p = new SearchService.SearchParams(Query: gen());
        var start = Stopwatch.GetTimestamp();
        search.Search(with?.Invoke(p) ?? p);
        samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
    samples.Sort();
    var mean = samples.Average();
    var p95 = samples[(int)Math.Ceiling(0.95 * samples.Count) - 1];
    var max = samples[^1];
    allSamples.AddRange(samples);
    Console.WriteLine($"{label,-22} {mean,8:F1} {p95,9:F1} {max,9:F1}");
}
allSamples.Sort();
var overallP95 = allSamples[(int)Math.Ceiling(0.95 * allSamples.Count) - 1];

Console.WriteLine("\n--- measurement context ---");
Console.WriteLine($"dataset: {DatasetSize} repos, synthetic mixed zh/en text, README ~8KB each");
Console.WriteLine($"machine: {Environment.ProcessorCount} logical cores; os={Environment.OSVersion}");
Console.WriteLine($"runtime: .NET {Environment.Version}; Microsoft.Data.Sqlite (native sqlite)");
Console.WriteLine($"method: Stopwatch around SearchService.Search(), warm index, {Iterations} iterations per query");
Console.WriteLine($"\noverall p95: {overallP95:F1} ms (target < {P95TargetMs} ms)");

try { Directory.Delete(tmp, recursive: true); } catch { /* ignore */ }
Console.WriteLine(overallP95 < P95TargetMs ? "PERF TARGET MET" : "PERF TARGET MISSED");
return overallP95 < P95TargetMs ? 0 : 1;
