using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using TModManager.App.Hosting;
using TModManager.Core.Processes;

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

            // ⚠️ 底下这几条断言会被**真实的 tModLoader 进程**影响：游戏开着时所有「启动类」接口
            // 一律先返回 409，`kill_tmodloader` 更会**真的把游戏杀掉**。
            // 本套集成测试的前提是「没有游戏在跑」（它只应该碰临时目录）。
            // 所以游戏在跑时跳过这一段 —— 绝不能因为跑个测试把用户玩的游戏关了。
            var tmlBusy = new TmlProcessService().IsRunning(force: true);

            // ---- 启动失败分支（版本存在但未加包 → 400；此时还没建 pack）----
            if (!tmlBusy)
            {
                Assert.Equal(400, (int)(await client.PostAsync($"/api/versions/{verId}/launch/PackX", null)).StatusCode);
                Assert.Equal(400, (int)(await client.PostAsync("/api/launch_current", null)).StatusCode);  // 无 last_launch
                Assert.Equal(404, (int)(await client.PostAsync("/api/kill_tmodloader", null)).StatusCode);
            }
            Assert.Equal(404, (int)(await client.PostAsync("/api/versions/nope/launch/PackX", null)).StatusCode);
            Assert.Equal(404, (int)(await client.PostAsync("/api/versions/nope/launch_direct", null)).StatusCode);
            // launch_special：路由必须存在且返回 JSON body（空 body 会在此抛 JsonException）
            var sp = await client.PostAsync("/api/versions/nope/launch_special", null);
            Assert.Equal(404, (int)sp.StatusCode);
            using (var spDoc = JsonDocument.Parse(await sp.Content.ReadAsStringAsync()))
                Assert.Equal("版本不存在", spDoc.RootElement.GetProperty("error").GetString());

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

            // ---- 崩溃日志：无日志目录 → 空列表；白名单之外一律拒绝 ----
            using (var logs = JsonDocument.Parse(await client.GetStringAsync("/api/logs")))
                Assert.Equal(0, logs.RootElement.GetArrayLength());

            var outsideFile = Path.Combine(tmp.BaseDir, "secret.log");
            File.WriteAllText(outsideFile, "nope");
            Assert.Equal(404, (int)(await client.GetAsync("/api/logs/content?path=" + Uri.EscapeDataString(outsideFile))).StatusCode);
            Assert.Equal(404, (int)(await client.PostAsync("/api/logs/open_folder", JsonContent.Create(new { path = outsideFile }))).StatusCode);

            // 放一个假日志目录：能列出、能读、错误行被标出
            var logDir = Path.Combine(tmp.BaseDir, "tModLoader-Logs");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "client.log");
            File.WriteAllText(logPath, "hello\n[Error] boom\nall good\n");

            using (var logs2 = JsonDocument.Parse(await client.GetStringAsync("/api/logs")))
                Assert.Equal(1, logs2.RootElement.GetArrayLength());
            using (var logResp = await client.GetAsync("/api/logs/content?path=" + Uri.EscapeDataString(logPath)))
            using (var logDoc = JsonDocument.Parse(await logResp.Content.ReadAsStringAsync()))
            {
                Assert.Contains("boom", logDoc.RootElement.GetProperty("text").GetString());
                Assert.Equal(2, logDoc.RootElement.GetProperty("error_lines")[0].GetInt32());
            }

            // ---- 存档管理：假世界 + 假人物 → 列举 / 备份 / 还原 ----
            var worldsDir = Path.Combine(tmp.BaseDir, "Worlds");
            var playersDir = Path.Combine(tmp.BaseDir, "Players");
            Directory.CreateDirectory(worldsDir);
            Directory.CreateDirectory(playersDir);
            var worldPath = Path.Combine(worldsDir, "TestWorld.wld");
            File.WriteAllText(worldPath, "world-bytes");
            File.WriteAllText(Path.Combine(playersDir, "Hero.plr"), "player-bytes");

            using (var saves = JsonDocument.Parse(await client.GetStringAsync("/api/saves")))
            {
                Assert.Equal(1, saves.RootElement.GetProperty("worlds").GetArrayLength());
                Assert.Equal("TestWorld", saves.RootElement.GetProperty("worlds")[0].GetProperty("name").GetString());
                Assert.Equal(1, saves.RootElement.GetProperty("players").GetArrayLength());
                // 前端依赖的字段名（snake_case）必须在
                var w0 = saves.RootElement.GetProperty("worlds")[0];
                Assert.True(w0.TryGetProperty("file_name", out _));
                Assert.True(w0.TryGetProperty("backup_count", out _));
                Assert.True(w0.TryGetProperty("modified", out _));
            }

            await AssertOk(client.PostAsync("/api/saves/backup", JsonContent.Create(new { kind = "world", path = worldPath })));

            string backupPath;
            using (var saves2 = JsonDocument.Parse(await client.GetStringAsync("/api/saves")))
            {
                var backups = saves2.RootElement.GetProperty("backups");
                Assert.Equal(1, backups.GetArrayLength());
                Assert.Equal("TestWorld.wld", backups[0].GetProperty("original_name").GetString());
                backupPath = backups[0].GetProperty("path").GetString()!;
            }

            // 改动存档后还原，内容应回到备份时的样子
            File.WriteAllText(worldPath, "changed");
            await AssertOk(client.PostAsync("/api/saves/restore", JsonContent.Create(new { path = backupPath })));
            Assert.Equal("world-bytes", File.ReadAllText(worldPath));

            // 白名单之外一律拒绝
            Assert.Equal(400, (int)(await client.PostAsync("/api/saves/backup", JsonContent.Create(new { kind = "world", path = outsideFile }))).StatusCode);
            Assert.Equal(400, (int)(await client.PostAsync("/api/saves/restore", JsonContent.Create(new { path = outsideFile }))).StatusCode);

            // ---- 游玩统计：前述启动分支全部失败 ⇒ 不应计数 ----
            using (var st = JsonDocument.Parse(await client.GetStringAsync("/api/stats")))
            {
                Assert.Equal(0, st.RootElement.GetProperty("total_seconds").GetDouble());
                Assert.Equal(JsonValueKind.Null, st.RootElement.GetProperty("active").ValueKind);
                // 前端依赖的字段名（snake_case）必须在
                Assert.Equal(JsonValueKind.Object, st.RootElement.GetProperty("version_launches").ValueKind);
                Assert.Equal(JsonValueKind.Object, st.RootElement.GetProperty("pack_launches").ValueKind);
                Assert.Equal(JsonValueKind.Object, st.RootElement.GetProperty("version_seconds").ValueKind);
                Assert.Equal(JsonValueKind.Object, st.RootElement.GetProperty("pack_seconds").ValueKind);
                Assert.Equal(JsonValueKind.Array, st.RootElement.GetProperty("recent").ValueKind);
            }
            await AssertOk(client.PostAsync("/api/stats/reset", null));

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

    /// <summary>启动整合包前的准入校验：空 / 坏 enabled.json 必须被拒绝，且**绝不**动用户的 enabled.json。</summary>
    [Fact]
    public async Task PackLaunch_RejectsEmptyOrBrokenPack_AndLeavesUserConfigAlone()
    {
        // 游戏开着时「启动整合包」会先返回 409，这组 400 断言不成立（见 AllRoutes_SmokeTest 里的说明）
        if (new TmlProcessService().IsRunning(force: true)) return;

        using var tmp = new TestPaths();
        var handle = await WebHostFactory.StartAsync(tmp.AppPaths, null);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{handle.Port}/") };

            var createdResp = await client.PostAsync("/api/versions", JsonContent.Create(new { name = "V", bat_path = tmp.CreateBat() }));
            Assert.Equal(HttpStatusCode.Created, createdResp.StatusCode);
            using var createdDoc = JsonDocument.Parse(await createdResp.Content.ReadAsStringAsync());
            var verId = createdDoc.RootElement.GetProperty("id").GetString();

            var enabledPath = Path.Combine(tmp.ModsDir, "enabled.json");
            File.WriteAllText(enabledPath, "[\"KeepMe\"]");            // 用户当前配置

            var packMods = Path.Combine(tmp.ModsDir, "ModPacks", "PackX", "Mods");
            Directory.CreateDirectory(packMods);
            await AssertOk(client.PostAsync($"/api/versions/{verId}/packs", JsonContent.Create(new { pack_id = "PackX" })));

            // 空数组的整合包 → 拒绝（否则会把全部模组禁用）
            File.WriteAllText(Path.Combine(packMods, "enabled.json"), "[]");
            var resp = await client.PostAsync($"/api/versions/{verId}/launch/PackX", null);
            Assert.Equal(400, (int)resp.StatusCode);
            Assert.Contains("空", await resp.Content.ReadAsStringAsync());
            Assert.Equal("[\"KeepMe\"]", File.ReadAllText(enabledPath));   // 用户配置分毫未动
            Assert.False(File.Exists(enabledPath + ".bk"));                 // 也不再产生 .bk

            // 坏 JSON → 同样拒绝
            File.WriteAllText(Path.Combine(packMods, "enabled.json"), "{ broken");
            Assert.Equal(400, (int)(await client.PostAsync($"/api/versions/{verId}/launch/PackX", null)).StatusCode);
            Assert.Equal("[\"KeepMe\"]", File.ReadAllText(enabledPath));

            // 被拒绝的启动不该留下快照
            using (var snaps = JsonDocument.Parse(await client.GetStringAsync("/api/snapshots")))
                Assert.Equal(0, snaps.RootElement.GetProperty("snapshots").GetArrayLength());

            // 还原一个不存在的快照 → 404
            Assert.Equal(404, (int)(await client.PostAsync("/api/snapshots/restore", JsonContent.Create(new { id = "nope" }))).StatusCode);
        }
        finally
        {
            handle.Stop();
        }
    }

    /// <summary>启动前体检：enabled.json 里启用、但该版本里找不到的模组要能被报出来。</summary>
    [Fact]
    public async Task MissingMods_ListsEnabledButAbsentMods()
    {
        using var tmp = new TestPaths();
        var handle = await WebHostFactory.StartAsync(tmp.AppPaths, null);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{handle.Port}/") };

            var resp = await client.PostAsync("/api/versions", JsonContent.Create(new { name = "V", bat_path = tmp.CreateBat(), mods_path = tmp.ModsDir }));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var verId = doc.RootElement.GetProperty("id").GetString();

            // enabled.json 启用了两个，但本地只有一个 .tmod
            File.WriteAllText(Path.Combine(tmp.ModsDir, "enabled.json"), "[\"PresentMod\",\"AbsentMod\"]");
            File.WriteAllBytes(Path.Combine(tmp.ModsDir, "PresentMod.tmod"), Array.Empty<byte>());

            using var body = JsonDocument.Parse(await client.GetStringAsync($"/api/versions/{verId}/missing_mods"));
            var missing = body.RootElement.GetProperty("missing").EnumerateArray().Select(x => x.GetString()).ToList();

            Assert.Equal(new[] { "AbsentMod" }, missing);
            Assert.Equal(1, body.RootElement.GetProperty("missing_count").GetInt32());
            Assert.Equal(2, body.RootElement.GetProperty("enabled_count").GetInt32());

            // 版本不存在 → 404
            Assert.Equal(404, (int)(await client.GetAsync("/api/versions/nope/missing_mods")).StatusCode);
        }
        finally
        {
            handle.Stop();
        }
    }

    /// <summary>截图：只列顶层图片、能取图，且目录外 / 非图片一律拒绝。</summary>
    [Fact]
    public async Task Captures_ListsTopLevelImages_AndRejectsOutsidePaths()
    {
        using var tmp = new TestPaths();
        var caps = Path.Combine(tmp.BaseDir, "Captures");     // = Mods 目录的父目录下
        Directory.CreateDirectory(caps);
        File.WriteAllBytes(Path.Combine(caps, "a.png"), new byte[] { 1, 2, 3 });
        File.WriteAllText(Path.Combine(caps, "note.txt"), "not an image");
        Directory.CreateDirectory(Path.Combine(caps, "CachedImages"));   // 游戏的贴图缓存，不该被列出
        File.WriteAllBytes(Path.Combine(caps, "CachedImages", "b.png"), new byte[] { 1 });

        var handle = await WebHostFactory.StartAsync(tmp.AppPaths, null);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{handle.Port}/") };

            using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/captures"));
            var list = doc.RootElement.GetProperty("captures");
            Assert.Equal(1, list.GetArrayLength());
            Assert.Equal("a.png", list[0].GetProperty("file_name").GetString());

            var a = Path.Combine(caps, "a.png");
            Assert.True((await client.GetAsync("/api/captures/image?path=" + Uri.EscapeDataString(a))).IsSuccessStatusCode);

            // 非图片 / 目录外 → 404
            Assert.Equal(404, (int)(await client.GetAsync("/api/captures/image?path=" + Uri.EscapeDataString(Path.Combine(caps, "note.txt")))).StatusCode);
            Assert.Equal(404, (int)(await client.GetAsync("/api/captures/image?path=" + Uri.EscapeDataString(tmp.CreateBat()))).StatusCode);
        }
        finally { handle.Stop(); }
    }

    /// <summary>本地模组版本回滚：列表 / 详情 / 上传添加 / 回滚，以及「回滚不改列表条数」这条核心不变量。</summary>
    [Fact]
    public async Task ModVersions_UploadRollback_KeepsListSize()
    {
        using var tmp = new TestPaths();
        FakeTmod.Write(tmp.ModsDir, "Foo.tmod", "Foo", "0.1", filler: 10);

        var handle = await WebHostFactory.StartAsync(tmp.AppPaths, null);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{handle.Port}/") };
            client.Timeout = TimeSpan.FromSeconds(60);   // 上传是整份文件走 HTTP

            // 列表：只有一个模组、只有一个版本 ⇒ 不给回滚
            using (var d = JsonDocument.Parse(await client.GetStringAsync("/api/mod_versions")))
            {
                var rows = d.RootElement.GetProperty("mods");
                Assert.Equal(1, rows.GetArrayLength());
                Assert.Equal("Foo", rows[0].GetProperty("internal_name").GetString());
                Assert.Equal("0.1", rows[0].GetProperty("current_version").GetString());
                Assert.False(rows[0].GetProperty("has_rollback").GetBoolean());
                Assert.EndsWith("mod_snapshots", d.RootElement.GetProperty("snapshot_dir").GetString()!);
            }

            // 拖入走上传：文件本体 POST 上来 + 文件名走 query
            var v2 = FakeTmod.Build("Foo", "0.2", filler: 30);
            using (var body = new ByteArrayContent(v2))
            {
                body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                var up = await client.PostAsync("/api/mod_versions/upload?name=Foo&file=Foo2.tmod", body);
                Assert.True(up.IsSuccessStatusCode, await up.Content.ReadAsStringAsync());
            }

            // 内部名对不上 → 400
            using (var body = new ByteArrayContent(FakeTmod.Build("Bar", "9.9")))
            {
                body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                Assert.Equal(400, (int)(await client.PostAsync("/api/mod_versions/upload?name=Foo&file=Bar.tmod", body)).StatusCode);
            }
            // 不是 .tmod → 400
            using (var body = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }))
            {
                body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                Assert.Equal(400, (int)(await client.PostAsync("/api/mod_versions/upload?name=Foo&file=x.tmod", body)).StatusCode);
            }

            string targetSha;
            using (var d = JsonDocument.Parse(await client.GetStringAsync("/api/mod_versions/detail?name=Foo")))
            {
                var vers = d.RootElement.GetProperty("versions");
                Assert.Equal(2, vers.GetArrayLength());
                Assert.True(d.RootElement.GetProperty("has_rollback").GetBoolean());
                Assert.Equal("Foo2.tmod", vers[1].GetProperty("original_file").GetString());
                targetSha = vers[1].GetProperty("sha256").GetString()!;
            }

            // 回滚到 0.2
            var rb = await client.PostAsync("/api/mod_versions/rollback",
                JsonContent.Create(new { name = "Foo", sha256 = targetSha, force = false }));
            Assert.True(rb.IsSuccessStatusCode, await rb.Content.ReadAsStringAsync());
            using (var d = JsonDocument.Parse(await rb.Content.ReadAsStringAsync()))
                Assert.Equal(2, d.RootElement.GetProperty("versions").GetArrayLength());   // ← 条数不变

            var only = Assert.Single(Directory.GetFiles(tmp.ModsDir, "*.tmod"));
            Assert.Equal(v2, File.ReadAllBytes(only));
            Assert.True(File.Exists(Path.Combine(tmp.BaseDir, "mod_snapshots", "Foo", "Foo.tmod")));

            // 不存在的模组 / 非法名字
            Assert.Equal(404, (int)(await client.GetAsync("/api/mod_versions/detail?name=Nope")).StatusCode);
            Assert.Equal(404, (int)(await client.GetAsync("/api/mod_versions/detail?name=..")).StatusCode);
        }
        finally { handle.Stop(); }
    }

    private static async Task AssertOk(Task<HttpResponseMessage> task)
    {
        using var resp = await task;
        Assert.True(resp.IsSuccessStatusCode, $"期望 2xx，实际 {(int)resp.StatusCode}");
    }
}
