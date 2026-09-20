using RepoShelf.Core;
using Xunit;

namespace RepoShelf.Tests;

/// <summary>Search: Chinese notes, English README, exact names, filters, escaping.</summary>
public class SearchTests
{
    private static (Store store, SearchService search) Seed()
    {
        var store = Store.Open(":memory:");
        var repos = new RepoService(store, new GitHubClient(baseUrl: "http://unused"));
        repos.UpsertSource(Meta(1, "alice", "dotforge", "Manage your dotfiles with ease", new[] { "dotfiles", "cli" }, "TypeScript"),
            readme: "# dotforge\n\nA CLI tool for managing dotfiles across machines. Supports symlinks and templates.", readmeProvided: true);
        repos.UpsertSource(Meta(2, "bob", "zustand-cn", "State management for React", new[] { "react", "state" }, "TypeScript"),
            readme: "# zustand-cn\n\nA small state management library for React applications.", readmeProvided: true);
        repos.UpdateAnnotation(2, new System.Text.Json.Nodes.JsonObject
        {
            ["notes"] = "轻量的 React 状态管理库，适合中小型项目",
            ["tags"] = new System.Text.Json.Nodes.JsonArray("前端", "react"),
            ["status"] = "tried",
            ["projects"] = new System.Text.Json.Nodes.JsonArray("side-app"),
        });
        repos.UpsertSource(Meta(3, "carol", "note-sync", "Sync notes", Array.Empty<string>(), "Go", archived: true),
            readme: "# note-sync\n\nSync markdown notes between devices.", readmeProvided: true);
        repos.UpdateAnnotation(3, new System.Text.Json.Nodes.JsonObject
        {
            ["reason"] = "想研究它的增量同步算法",
            ["notes"] = "配置文件用 YAML，支持增量同步",
            ["tags"] = new System.Text.Json.Nodes.JsonArray("笔记"),
        });
        return (store, new SearchService(store));
    }

    private static RepoMeta Meta(long id, string owner, string name, string description, string[] topics, string? language, bool archived = false) =>
        new(id, owner, name, $"{owner}/{name}", $"https://github.com/{owner}/{name}", description, topics.ToList(), language, "MIT", archived, "2026-09-01T00:00:00Z", 42, "main");

    [Fact]
    public void ChineseQueryMatchesChineseNotes()
    {
        var (_, search) = Seed();
        var result = search.Search(new SearchService.SearchParams(Query: "状态管理"));
        Assert.Equal(1, result.Total);
        Assert.Equal("zustand-cn", result.Items[0].Repo["name"]!.GetValue<string>());
        Assert.Contains("notes", result.Items[0].MatchedFields);
        Assert.Contains("<mark>", result.Items[0].Snippet);
    }

    [Fact]
    public void ChineseQueryMatchesChineseOnlyNotes()
    {
        var (_, search) = Seed();
        var result = search.Search(new SearchService.SearchParams(Query: "增量同步"));
        Assert.Equal(1, result.Total);
        Assert.Equal("note-sync", result.Items[0].Repo["name"]!.GetValue<string>());
    }

    [Fact]
    public void EnglishQueryMatchesReadme()
    {
        var (_, search) = Seed();
        var result = search.Search(new SearchService.SearchParams(Query: "symlinks"));
        Assert.Equal(1, result.Total);
        Assert.Equal("dotforge", result.Items[0].Repo["name"]!.GetValue<string>());
        Assert.Contains("readme", result.Items[0].MatchedFields);
    }

    [Fact]
    public void ExactNameRanksFirst()
    {
        var store = Store.Open(":memory:");
        var repos = new RepoService(store, new GitHubClient(baseUrl: "http://unused"));
        repos.UpsertSource(Meta(10, "a", "react", "", Array.Empty<string>(), null));
        repos.UpsertSource(Meta(11, "b", "react-awesome", "react things", Array.Empty<string>(), null));
        var search = new SearchService(store);
        var result = search.Search(new SearchService.SearchParams(Query: "react"));
        Assert.Equal("react", result.Items[0].Repo["name"]!.GetValue<string>());
    }

    [Fact]
    public void AndSemanticsRequireAllTokens()
    {
        var (_, search) = Seed();
        Assert.Equal(1, search.Search(new SearchService.SearchParams(Query: "react 状态管理")).Total);
        Assert.Equal(0, search.Search(new SearchService.SearchParams(Query: "react 不存在的词")).Total);
    }

    [Fact]
    public void FiltersCombineWithKeywordSearch()
    {
        var (_, search) = Seed();
        Assert.Equal(1, search.Search(new SearchService.SearchParams(Query: "react", Status: "tried")).Total);
        Assert.Equal(0, search.Search(new SearchService.SearchParams(Query: "react", Status: "inbox")).Total);
        Assert.Equal(1, search.Search(new SearchService.SearchParams(Query: "react", Tag: "前端")).Total);
        Assert.Equal(1, search.Search(new SearchService.SearchParams(Query: "sync", Language: "go")).Total);
        Assert.Equal(0, search.Search(new SearchService.SearchParams(Query: "sync", Archived: false)).Total);
        Assert.Equal(1, search.Search(new SearchService.SearchParams(Query: "sync", Archived: true)).Total);
        Assert.Equal(1, search.Search(new SearchService.SearchParams(Query: "react", Project: "side-app")).Total);
    }

    [Fact]
    public void EmptyQueryListsNewestFirst()
    {
        var (_, search) = Seed();
        var result = search.Search(new SearchService.SearchParams());
        Assert.Equal(3, result.Total);
        Assert.Equal("note-sync", result.Items[0].Repo["name"]!.GetValue<string>());
    }

    [Fact]
    public void SnippetsEscapeHtml()
    {
        var store = Store.Open(":memory:");
        var repos = new RepoService(store, new GitHubClient(baseUrl: "http://unused"));
        var (repoId, _, _) = repos.UpsertSource(Meta(20, "x", "evil", "", Array.Empty<string>(), null));
        repos.UpdateAnnotation(repoId, new System.Text.Json.Nodes.JsonObject { ["notes"] = "记住 <script>alert(1)</script> 这个 payload" });
        var search = new SearchService(store);
        var result = search.Search(new SearchService.SearchParams(Query: "payload"));
        Assert.Single(result.Items);
        Assert.DoesNotContain("<script>", result.Items[0].Snippet);
        Assert.Contains("&lt;script&gt;", result.Items[0].Snippet);
    }
}

public class PrefixSearchTests
{
    [Fact]
    public void PrefixMatchesLatinTokens()
    {
        using var store = Store.Open(":memory:");
        var repos = new RepoService(store, new GitHubClient(baseUrl: "http://unused"));
        repos.UpsertSource(new RepoMeta(1, "me", "archify", "me/archify", "u", "diagrams", new(), "TypeScript", "MIT", false, null, 1, "main"));
        repos.UpsertSource(new RepoMeta(2, "me", "zoo", "me/zoo", "u", "animals", new(), "Go", "MIT", false, null, 1, "main"));
        var search = new SearchService(store);
        var result = search.Search(new SearchService.SearchParams(Query: "ar"));
        Assert.Equal(1, result.Total);
        Assert.Equal("archify", result.Items[0].Repo["name"]!.GetValue<string>());
        // Prefix must not turn into substring matching across word boundaries.
        Assert.Equal(0, search.Search(new SearchService.SearchParams(Query: "oo")).Total); // "oo" is not a prefix of "zoo"
    }
}

public class SnippetMarkupTests
{
    [Fact]
    public void ReadmeSnippetsStripHtmlTags()
    {
        using var store = Store.Open(":memory:");
        var repos = new RepoService(store, new GitHubClient(baseUrl: "http://unused"));
        var (id, _, _) = repos.UpsertSource(
            new RepoMeta(1, "a", "badgeheavy", "a/badgeheavy", "u", "", new(), null, null, false, null, 1, "main"),
            readme: "<p align=\"center\"><strong>English</strong> | <a href=\"./x.md\">简体中文</a></p>\n<p align=\"center\"><img src=\"badge.svg\"/></p>\n\n这个库用来做增量同步的演示。",
            readmeProvided: true);
        var search = new SearchService(store);
        var result = search.Search(new SearchService.SearchParams(Query: "增量同步"));
        Assert.Single(result.Items);
        Assert.DoesNotContain("<p align", result.Items[0].Snippet);
        Assert.DoesNotContain("<img", result.Items[0].Snippet);
        Assert.Contains("<mark>", result.Items[0].Snippet);
    }
}
