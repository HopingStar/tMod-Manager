using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using TModManager.Core.Network;

namespace TModManager.Tests;

public class NetworkServiceTests
{
    [Fact]
    public async Task GitHubService_ParsesLatestStableAndPreview()
    {
        var json = """
            [{"tag_name":"v2026.07.2.5","prerelease":true,"published_at":"2026-07-07T00:00:00Z","html_url":"https://github.com/x/preview"},
             {"tag_name":"v2026.06.3.6","prerelease":false,"published_at":"2026-06-14T00:00:00Z","html_url":"https://github.com/x/stable"}]
            """;
        using var client = MockClient(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8) });
        var svc = new GitHubService(client);

        var result = await svc.FetchReleasesAsync();

        Assert.True(result.Ok);
        Assert.Equal("2026.06.3.6", result.LatestStable!["version"]?.ToString());
        Assert.Equal("https://github.com/x/stable", result.LatestStable["url"]?.ToString());
        Assert.Equal("2026.07.2.5", result.LatestPreview!["version"]?.ToString());
        Assert.NotNull(result.LatestStable["days"]);
    }

    [Fact]
    public async Task GitHubService_NoNumericTag_ReturnsNoRelease()
    {
        using var client = MockClient(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""[{"tag_name":"nonsense","prerelease":false}]""") });
        var svc = new GitHubService(client);
        var result = await svc.FetchReleasesAsync();
        Assert.False(result.Ok);
        Assert.Equal("no_release", result.Reason);
    }

    [Fact]
    public async Task GitHubService_Non200_Throws()
    {
        using var client = MockClient(() => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var svc = new GitHubService(client);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.FetchReleasesAsync());
    }

    [Fact]
    public async Task SteamService_ReturnsMetaAndPreservesRawTypes()
    {
        var json = """
            {"response":{"publishedfiledetails":[
                {"publishedfileid":"111","title":"Mod One","preview_url":"https://x/1.png","time_updated":100,"file_size":"1024","tags":[{"tag":"Gameplay"},{"tag":"1.4.4"}]},
                {"publishedfileid":"222","title":"Mod Two","file_size":2000}
            ]}}
            """;
        string? capturedForm = null;
        using var client = MockClient(req =>
        {
            // 捕获请求表单（不能在 mock 内断言：异常会被 SteamService 吞掉导致结果为空）
            capturedForm = req.Content is FormUrlEncodedContent f ? f.ReadAsStringAsync().Result : null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) };
        });
        var svc = new SteamService(client);
        var result = await svc.QueryPublishDetailsAsync(new List<string> { "111", "222" });

        Assert.Equal(2, result.Results.Count);
        // FormUrlEncodedContent 会把 [ ] 编码为 %5B %5D
        Assert.Contains("publishedfileids%5B1%5D", capturedForm ?? "");
        Assert.Equal("Mod One", result.Results["111"]["name"]?.ToString());
        Assert.Equal("Gameplay", result.Results["111"]["tags"]?[0]?.ToString());
        // file_size 保留原始类型（字符串 "1024"）
        Assert.Equal("1024", result.Results["111"]["file_size"]?.ToString());
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task SteamService_MissingId_RecordsError()
    {
        var json = """{"response":{"publishedfiledetails":[{"publishedfileid":"111","title":"Mod One"}]}}""";
        using var client = MockClient(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8) });
        var svc = new SteamService(client);
        var result = await svc.QueryPublishDetailsAsync(new List<string> { "111", "999" });

        Assert.True(result.Results.ContainsKey("111"));
        Assert.True(result.Errors.ContainsKey("999"));
        Assert.Contains("未找到", result.Errors["999"]);
    }

    [Fact]
    public async Task SteamService_NetworkError_RecordsAllAsFailed()
    {
        using var client = MockClient(() => throw new InvalidOperationException("boom"));
        var svc = new SteamService(client);
        var result = await svc.QueryPublishDetailsAsync(new List<string> { "111", "222" });
        Assert.Empty(result.Results);
        Assert.Equal(2, result.Errors.Count);
    }

    [Fact]
    public async Task SteamService_EmptyIds_ReturnsEmpty()
    {
        using var client = MockClient(() => throw new InvalidOperationException("不应被调用"));
        var svc = new SteamService(client);
        var result = await svc.QueryPublishDetailsAsync(new List<string>());
        Assert.Empty(result.Results);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task TmodUpdateService_CheckingThenOk()
    {
        var json = """[{"tag_name":"v2026.06.3.6","prerelease":false,"published_at":"2026-06-14T00:00:00Z","html_url":"https://github.com/x"}]""";
        using var client = MockClient(() => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8) });
        var svc = new TmodUpdateService(new GitHubService(client));

        var first = await svc.GetAsync();
        Assert.Equal("checking", first["reason"]?.ToString());

        // 轮询等后台拉取完成（mock 即时返回，通常 <100ms）
        JsonObject? okResult = null;
        for (int i = 0; i < 150 && okResult == null; i++)
        {
            await Task.Delay(20);
            var cur = await svc.GetAsync();
            if (cur["ok"]?.GetValue<bool>() == true) okResult = cur;
        }
        Assert.NotNull(okResult);
        Assert.Equal("2026.06.3.6", okResult!["latest_stable"]!["version"]?.ToString());
    }

    private static HttpClient MockClient(Func<HttpResponseMessage> respond)
        => new(new MockHandler(respond));
    private static HttpClient MockClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new MockHandler(respond));
}

/// <summary>测试用 HttpMessageHandler：返回预设响应，不触网。</summary>
internal sealed class MockHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public MockHandler(Func<HttpResponseMessage> respond) : this(_ => respond()) { }
    public MockHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_respond(request));
}
