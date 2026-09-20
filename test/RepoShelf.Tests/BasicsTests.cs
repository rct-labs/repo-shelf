using RepoShelf.Core;
using Xunit;

namespace RepoShelf.Tests;

public class TokenizerTests
{
    [Fact]
    public void EnglishWords()
    {
        Assert.Equal(new[] { "hello", "world", "foo_bar42" }, Tokenizer.Tokenize("Hello, WORLD! foo_bar42"));
    }

    [Fact]
    public void ChineseBigrams()
    {
        Assert.Equal(new[] { "状态", "态管", "管理" }, Tokenizer.Tokenize("状态管理"));
    }

    [Fact]
    public void SingleCjkCharStaysUnigram()
    {
        Assert.Equal(new[] { "云" }, Tokenizer.Tokenize("云"));
    }

    [Fact]
    public void MixedSplitsAtScriptBoundaries()
    {
        Assert.Equal(new[] { "使用", "react", "管理", "理状", "状态" }, Tokenizer.Tokenize("使用React管理状态"));
    }

    [Fact]
    public void QueryTokensDeduplicated()
    {
        Assert.Equal(new[] { "测试", "试测" }, Tokenizer.TokenizeQuery("测试测试测试"));
    }

    [Fact]
    public void EmptyInput()
    {
        Assert.Empty(Tokenizer.Tokenize(""));
        Assert.Empty(Tokenizer.Tokenize(null));
        Assert.Empty(Tokenizer.Tokenize("   --- !!! "));
    }
}

public class NormalizeTests
{
    [Theory]
    [InlineData("https://github.com/octocat/Hello-World")]
    [InlineData("https://github.com/octocat/Hello-World/")]
    [InlineData("https://github.com/octocat/Hello-World/tree/main/docs")]
    [InlineData("https://github.com/octocat/Hello-World/issues/3#comment")]
    [InlineData("https://github.com/octocat/Hello-World?tab=readme")]
    [InlineData("https://github.com/octocat/Hello-World.git")]
    [InlineData("https://www.github.com/octocat/Hello-World")]
    [InlineData("http://github.com/octocat/Hello-World")]
    [InlineData("github.com/octocat/Hello-World")]
    [InlineData("  https://github.com/octocat/Hello-World  ")]
    public void NormalizesVariants(string input)
    {
        var r = RepoUrlParser.Parse(input);
        Assert.Equal("octocat", r.Owner);
        Assert.Equal("Hello-World", r.Repo);
        Assert.Equal("https://github.com/octocat/Hello-World", r.Url);
    }

    [Theory]
    [InlineData("https://gitlab.com/octocat/Hello-World")]
    [InlineData("https://gist.github.com/octocat/12345")]
    [InlineData("https://raw.githubusercontent.com/octocat/Hello-World/main/README.md")]
    [InlineData("https://api.github.com/repos/octocat/Hello-World")]
    [InlineData("https://github.com.evil.example/octocat/Hello-World")]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/octocat")]
    [InlineData("https://github.com/settings/profile")]
    [InlineData("https://github.com/topics/javascript")]
    [InlineData("https://github.com/trending/js")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://github.com/octocat/Hello-World")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://github.com/octo cat/repo")]
    [InlineData("https://github.com/octocat/..")]
    public void RejectsInvalid(string input)
    {
        Assert.Throws<RepoUrlParser.InvalidUrlException>(() => RepoUrlParser.Parse(input));
    }
}

public class StoreTests
{
    [Fact]
    public void Fts5IsAvailable()
    {
        using var store = Store.Open(":memory:");
        lock (store.Sync)
        {
            using var cmd = store.Conn.CreateCommand();
            cmd.CommandText = "INSERT INTO search_fts (name, owner, description, topics, reason, notes, readme) VALUES ('你好', 'o', '', '', '', '', '');";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT COUNT(*) FROM search_fts WHERE search_fts MATCH '你好'";
            Assert.Equal(1L, cmd.ExecuteScalar());
        }
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        using var store = Store.Open(":memory:");
        Assert.Null(store.GetSetting("k"));
        store.SetSetting("k", "v");
        Assert.Equal("v", store.GetSetting("k"));
        store.SetSetting("k", null);
        Assert.Null(store.GetSetting("k"));
    }
}
