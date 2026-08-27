using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using TModManager.App.Hosting;

namespace TModManager.Tests;

/// <summary>内嵌 Kestrel 集成测试：临时 AppPaths（不触真实 data.json / Mods），逐条 /api/* 冒烟。
/// 只走安全路径：404/400/409 分支、空数据分支；绝不真实启动游戏、打开文件框、联网。</summary>
public class ApiIntegrationTests
{
    [Fact]
    public async Task AllRoutes_SmokeTest_OnTempPaths()
    {
        using var tmp = new TestPaths();
        var handle = await WebHostFactory.StartAsync(tmp.AppPaths, null);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{handle.Port}/") };

            // ---- 生命周期 ----
            await AssertOk(client.PostAsync("/api/ready", null));
            await AssertOk(client.PostAsync("/api/shutdown", null)); // 测试进程无 Application → 直接 ok

            // ---- 版本 CRUD ----
            var bat = tmp.CreateBat();
            var createdResp = await client.PostAsync("/api/versions", JsonContent.Create(new { name = "测试版", bat_path = bat }));
            Assert.Equal(HttpStatusCode.Created, createdResp.StatusCode);
            using var createdDoc = JsonDocument.Parse(await createdResp.Content.ReadAsStringAsync());
            var verId = createdDoc.RootElement.GetProperty("id").GetString();
            Assert.NotNull(verId);

            using var versions1 = JsonDocument.Parse(await client.GetStringAsync("/api/versions"));
            Assert.Equal(1, versions1.RootElement.GetArrayLength());

            await AssertOk(client.PostAsync($"/api/versions/{verId}/rename", JsonContent.Create(new { name = "新名字" })));
            using var versions2 = JsonDocument.Parse(await client.GetStringAsync("/api/versions"));
            Assert.Equal("新名字", versions2.RootElement[0].GetProperty("name").GetString());

            // 不存在 id → 404
            Assert.Equal(404, (int)(await client.PostAsync("/api/versions/nope/restore", null)).StatusCode);
            Assert.Equal(404, (int)(await client.PostAsync("/api/versions/nope/rename", JsonContent.Create(new { name = "x" }))).StatusCode);
            Assert.Equal(404, (int)(await client.DeleteAsync("/api/versions/nope")).StatusCode);

            // ---- 整合包 / 模组列表 ----
            await AssertOk(client.GetAsync("/api/global_packs"));                     // 空
            Assert.Equal(404, (int)(await client.GetAsync("/api/packs/nope/mods")).StatusCode);
            await AssertOk(client.GetAsync($"/api/versions/{verId}/mods"));           // Mods 目录存在 → 空列表 200

            // ---- 启动失败分支（版本存在但未加包 → 400；此时还没建 pack）----
            Assert.Equal(400, (int)(await client.PostAsync($"/api/versions/{verId}/launch/PackX", null)).StatusCode);
            Assert.Equal(404, (int)(await client.PostAsync("/api/versions/nope/launch/PackX", null)).StatusCode);
            Assert.Equal(400, (int)(await client.PostAsync("/api/launch_current", null)).StatusCode);  // 无 last_launch
            Assert.Equal(404, (int)(await client.PostAsync("/api/versions/nope/launch_direct", null)).StatusCode);
            // launch_special：路由必须存在且返回 JSON body（空 body 会在此抛 JsonException）
            var sp = await client.PostAsync("/api/versions/nope/launch_special", null);
            Assert.Equal(404, (int)sp.StatusCode);
            using (var spDoc = JsonDocument.Parse(await sp.Content.ReadAsStringAsync()))
                Assert.Equal("版本不存在", spDoc.RootElement.GetProperty("error").GetString());
            Assert.Equal(404, (int)(await client.PostAsync("/api/kill_tmodloader", null)).StatusCode);

            // ---- 加整合包（先建目录满足 PackExists）----
            Directory.CreateDirectory(Path.Combine(tmp.ModsDir, "ModPacks", "PackX"));
            await AssertOk(client.PostAsync($"/api/versions/{verId}/packs", JsonContent.Create(new { pack_id = "PackX" })));
            await AssertOk(client.PostAsync("/api/packs/PackX/desc", JsonContent.Create(new { desc = "测试描述" })));
            Assert.Equal(400, (int)(await client.PostAsync($"/api/versions/{verId}/packs", JsonContent.Create(new { pack_id = "PackX" }))).StatusCode); // 重复添加

            // ---- 设置 / 收藏 ----
            await AssertOk(client.GetAsync("/api/meta_settings"));
            await AssertOk(client.PostAsync("/api/meta_settings", JsonContent.Create(new { show_stats = true })));
            await AssertOk(client.GetAsync("/api/favorites"));
            await AssertOk(client.PostAsync("/api/favorites", JsonContent.Create(new { versions = new[] { "a" }, packs = Array.Empty<string>(), seq = 1 })));

            // ---- 特殊加载 ----
            await AssertOk(client.GetAsync("/api/special_status"));
            await AssertOk(client.GetAsync("/api/global_special"));
            await AssertOk(client.GetAsync("/api/special/PackX"));
            await AssertOk(client.PostAsync("/api/special/PackX", JsonContent.Create(new { independent = true, mods = Array.Empty<string>() })));
            await AssertOk(client.PostAsync("/api/global_special", JsonContent.Create(new { active = false, mods = Array.Empty<string>() })));

            // ---- 状态 ----
            using var status = JsonDocument.Parse(await client.GetStringAsync("/api/status"));
            Assert.True(status.RootElement.TryGetProperty("tmodloader_running", out _));
            Assert.True(status.RootElement.TryGetProperty("missing_versions", out _));

            // ---- 模组元数据（临时路径：本地逆向空 + 无 workshop → 不联网）----
            await AssertOk(client.GetAsync("/api/mod_meta"));
            await AssertOk(client.PostAsync("/api/mod_meta/refresh", JsonContent.Create(new { force = false })));

            // ---- 当前 enabled ----
            using var enabled = JsonDocument.Parse(await client.GetStringAsync("/api/current_enabled"));
            Assert.True(enabled.RootElement.TryGetProperty("mods", out _));

            // ---- 联网 / 收尾（不触网：checking 分支；非法链接 400；无 Application 时 pick_bat 返回空）----
            using var tu = JsonDocument.Parse(await client.GetStringAsync("/api/tmod_update"));
            Assert.Equal("checking", tu.RootElement.GetProperty("reason").GetString());
            Assert.Equal(400, (int)(await client.PostAsync("/api/open_url", JsonContent.Create(new { url = "javascript:x" }))).StatusCode);
            await AssertOk(client.PostAsync("/api/pick_bat", null));

            // ---- 打开版本文件夹（只测 400/404 安全分支，不真打开资源管理器）----
            Assert.Equal(400, (int)(await client.PostAsync("/api/open_version_folder", JsonContent.Create(new { }))).StatusCode);
            Assert.Equal(404, (int)(await client.PostAsync("/api/open_version_folder", JsonContent.Create(new { bat_path = Path.Combine(tmp.BaseDir, "NoSuchDir", "x.bat") }))).StatusCode);

            // ---- 打开模组文件夹（只测 400/404 安全分支）----
            Assert.Equal(400, (int)(await client.PostAsync("/api/open_mod_folder", JsonContent.Create(new { }))).StatusCode);
            Assert.Equal(404, (int)(await client.PostAsync("/api/open_mod_folder", JsonContent.Create(new { name = "NoSuchMod" }))).StatusCode);

            // ---- 删除后清空 ----
            await AssertOk(client.DeleteAsync($"/api/versions/{verId}"));
            using var versionsFinal = JsonDocument.Parse(await client.GetStringAsync("/api/versions"));
            Assert.Equal(0, versionsFinal.RootElement.GetArrayLength());
        }
        finally
        {
            handle.Stop();
        }
    }

    private static async Task AssertOk(Task<HttpResponseMessage> task)
    {
        using var resp = await task;
        Assert.True(resp.IsSuccessStatusCode, $"期望 2xx，实际 {(int)resp.StatusCode}");
    }
}
