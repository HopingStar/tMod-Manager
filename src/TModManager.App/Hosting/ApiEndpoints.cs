// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TModManager.Core.Captures;
using TModManager.Core.Configuration;
using TModManager.Core.Icons;
using TModManager.Core.Logging;
using TModManager.Core.Logs;
using TModManager.Core.Models;
using TModManager.Core.Mods;
using TModManager.Core.ModVersions;
using TModManager.Core.Network;
using TModManager.Core.Processes;
using TModManager.Core.Saves;
using TModManager.Core.Snapshots;
using TModManager.Core.Stats;
using TModManager.Core.Storage;
using TModManager.Core.Versions;

namespace TModManager.App.Hosting;

/// <summary>
/// 全部 /api/* 路由。JSON 字段名经 ConfigureHttpJsonOptions 统一为 snake_case。
/// 阶段 2：版本/整合包完整 CRUD + 设置/收藏 + 状态 + 打开 ModPacks。
/// </summary>
public static class ApiEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---- 生命周期 ----
        api.MapPost("/ready", (WindowControlService wc) => { wc.RequestShow(); return Results.Ok(new { ok = true }); });
        api.MapPost("/shutdown", () =>
        {
            // 不能 Invoke 阻塞请求线程：Shutdown → OnExit → Kestrel 优雅停止会等待在途请求完成，
            // 若本请求仍阻塞则死锁。用 BeginInvoke 立即返回，请求结束后 Kestrel 排水 → 进程正常退出。
            AppLog.Info("收到 /api/shutdown 请求");
            if (Application.Current is { } cur)
                cur.Dispatcher.BeginInvoke((Action)cur.Shutdown);
            return Results.Ok(new { ok = true });
        });

        // ---- 版本管理 ----
        api.MapGet("/versions", async (JsonStore store) => (await store.ReadAsync()).Versions);

        api.MapPost("/versions", async (AddVersionRequest? body, JsonStore store, AppPaths paths) =>
        {
            var name = (body?.Name ?? "").Trim();
            var bat = (body?.BatPath ?? "").Trim();
            if (name.Length == 0 || bat.Length == 0)
                return Results.Json(new { error = "版本名称和启动文件路径必填" }, statusCode: 400);
            if (!File.Exists(bat))
                return Results.Json(new { error = "启动文件 (.bat) 不存在" }, statusCode: 400);

            Directory.CreateDirectory(paths.DefaultModsPath);
            var created = await store.UpdateAsync(data =>
            {
                var v = new VersionEntry
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = name,
                    VersionInfo = "",
                    BatPath = bat,
                    ModsPath = paths.DefaultModsPath,
                };
                TmlVersionService.AutoDetect(v);
                data.Versions.Add(v);
                return v;
            });
            return Results.Json(created, statusCode: 201);
        });

        api.MapDelete("/versions/{id}", async (string id, JsonStore store) =>
        {
            var removed = await store.UpdateAsync(data =>
            {
                var idx = data.Versions.FindIndex(x => x.Id == id);
                if (idx < 0) return false;
                if (data.LastLaunch != null && data.LastLaunch.VersionId == id)
                    data.LastLaunch = null;
                data.Versions.RemoveAt(idx);
                return true;
            });
            return removed ? Results.Ok(new { message = "删除成功" }) : Results.Json(new { error = "版本不存在" }, statusCode: 404);
        });

        // 还原 enabled.json：改用「最近一次快照」。
        // （原先读的 enabled.json.bk 只有一代、每次切包都被覆盖，连切两个包就把用户原本的配置永久冲掉了。）
        api.MapPost("/versions/{id}/restore", async (string id, TmlProcessService procs, SnapshotService snapshots, JsonStore store) =>
        {
            var data = await store.ReadAsync();
            var version = data.Versions.FirstOrDefault(x => x.Id == id);
            if (version == null) return Results.Json(new { error = "版本不存在" }, statusCode: 404);
            if (procs.IsRunning(true)) return Results.Json(new { error = "tModLoader 正在运行，无法还原 enabled.json" }, statusCode: 409);
            if (string.IsNullOrWhiteSpace(version.ModsPath) || !Directory.Exists(version.ModsPath))
                return Results.Json(new { error = "Mods 目录无效" }, statusCode: 400);

            var latest = snapshots.List().FirstOrDefault();
            if (latest == null) return Results.Json(new { error = "还没有任何快照可还原" }, statusCode: 404);

            snapshots.Capture(version.ModsPath!, "还原前（自动）", version.Name);   // 还原本身也可回退
            var target = snapshots.Restore(latest.Id, version.ModsPath);
            return target == null
                ? Results.Json(new { error = "还原失败" }, statusCode: 500)
                : Results.Ok(new { message = "已还原到最近一次快照", target });
        });

        // ---- enabled.json 快照（每次切换整合包前自动保存，存在程序目录的 snapshots/ 下）----
        api.MapGet("/snapshots", (SnapshotService snapshots) => new
        {
            snapshots = snapshots.List(),
            root = snapshots.SnapshotRoot,
            keep = SnapshotService.KeepCount,
        });

        api.MapPost("/snapshots/restore", async (SnapshotRestoreRequest? body, TmlProcessService procs, SnapshotService snapshots, JsonStore store) =>
        {
            if (procs.IsRunning(true)) return Results.Json(new { error = "tModLoader 正在运行，无法还原 enabled.json" }, statusCode: 409);

            var data = await store.ReadAsync();
            var entry = snapshots.List().FirstOrDefault(x => x.Id == body?.Id);
            if (entry == null) return Results.Json(new { error = "快照不存在" }, statusCode: 404);

            // 写回哪个 Mods 目录：优先显式指定的版本 → 快照里记录的那个 → 任意一个有 Mods 路径的版本
            var version = !string.IsNullOrWhiteSpace(body?.VersionId)
                ? data.Versions.FirstOrDefault(x => x.Id == body!.VersionId)
                : null;
            var modsDir = version?.ModsPath;
            if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir))
                modsDir = entry.ModsDir;
            if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir))
                modsDir = data.Versions.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ModsPath))?.ModsPath;
            if (string.IsNullOrWhiteSpace(modsDir) || !Directory.Exists(modsDir))
                return Results.Json(new { error = "找不到可写入的 Mods 目录" }, statusCode: 400);

            snapshots.Capture(modsDir!, "还原前（自动）", version?.Name ?? "");   // 还原本身也可回退
            var target = snapshots.Restore(entry.Id, modsDir);
            return target == null
                ? Results.Json(new { error = "还原失败（快照已损坏？）" }, statusCode: 500)
                : Results.Ok(new { message = "已还原该快照", target });
        });

        api.MapPost("/snapshots/open_folder", (SnapshotService snapshots) =>
        {
            try
            {
                Directory.CreateDirectory(snapshots.SnapshotRoot);
                Process.Start(new ProcessStartInfo { FileName = snapshots.SnapshotRoot, UseShellExecute = true });
                return Results.Ok(new { ok = true, folder = snapshots.SnapshotRoot });
            }
            catch (Exception e) { return Results.Json(new { error = "打开失败: " + e.Message }, statusCode: 500); }
        });

        // ---- 游戏截图（存档目录下的 Captures/）----
        api.MapGet("/captures", (CapturesService captures) => new
        {
            captures = captures.List(),
            dir = captures.CapturesDir,
        });

        api.MapGet("/captures/image", (string path, CapturesService captures) =>
        {
            var full = captures.ResolveImage(path);     // 只允许截图目录内的图片，防目录穿越
            return full == null
                ? Results.Json(new { error = "截图不存在或路径非法" }, statusCode: 404)
                : Results.File(full, CapturesService.ContentTypeOf(full));
        });

        api.MapPost("/captures/open_folder", (CapturesService captures) =>
        {
            try
            {
                var dir = captures.CapturesDir;
                if (!Directory.Exists(dir)) return Results.Json(new { error = "截图目录不存在" }, statusCode: 404);
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return Results.Ok(new { ok = true, folder = dir });
            }
            catch (Exception e) { return Results.Json(new { error = "打开失败: " + e.Message }, statusCode: 500); }
        });

        // ---- 本地模组版本回滚（**只针对本地 Mods 目录**）----
        // 仓库在程序目录的 mod_snapshots/ 下：<内部名>.json（版本列表）+ <内部名>/<版本>.tmod（内容）。
        // 关键性质：**回滚永远不改变版本列表的条数**；「当前版本」不落库，靠内容命中判定。
        api.MapGet("/mod_versions", (ModVersionService mv) => mv.List());

        api.MapGet("/mod_versions/detail", (string? name, ModVersionService mv) =>
        {
            var d = string.IsNullOrWhiteSpace(name) ? null : mv.Detail(name!);
            return d is null ? Results.Json(new { error = "模组不存在" }, statusCode: 404) : Results.Json(d);
        });

        api.MapPost("/mod_versions/add", (ModVersionAddRequest? body, ModVersionService mv, TmlProcessService procs) =>
        {
            if (procs.IsRunning(true))
                return Results.Json(new { error = "tModLoader 正在运行，模组文件被占用，暂时不能改动" }, statusCode: 409);
            try { return Results.Ok(mv.AddVersion(body?.Name ?? "", body?.Path ?? "")); }
            catch (ModVersionException e) { return Results.Json(new { error = e.Message }, statusCode: 400); }
        });

        // 拖放走这条：WebView2 拿不到拖进来的文件真实路径（Chromium 没有 Electron 的 File.path），
        // 所以前端把文件本体 POST 上来，先落成临时文件再走与「按路径添加」完全相同的逻辑。
        api.MapPost("/mod_versions/upload", async (HttpRequest req, string? name, string? file, ModVersionService mv, TmlProcessService procs) =>
        {
            if (procs.IsRunning(true))
                return Results.Json(new { error = "tModLoader 正在运行，模组文件被占用，暂时不能改动" }, statusCode: 409);
            if (string.IsNullOrWhiteSpace(name))
                return Results.Json(new { error = "缺少模组名" }, statusCode: 400);

            var tmp = Path.Combine(Path.GetTempPath(), "tmm-up-" + Guid.NewGuid().ToString("N") + ".tmod");
            try
            {
                var expected = req.ContentLength;
                await using (var fs = File.Create(tmp))
                    await req.Body.CopyToAsync(fs);

                // 传了一半就断线的话，文件头多半还是好的 —— 光靠文件头校验会把**截断的 .tmod**
                // 当成正常版本归档进去。所以拿 Content-Length 对一下。
                // 只拦「收到的比声明的少」，不比相等 —— 多余字节不可能是我们造成的，宁可放过也别误杀。
                var written = new FileInfo(tmp).Length;
                if (expected is > 0 && written < expected)
                    return Results.Json(new { error = $"上传不完整（收到 {written} / {expected} 字节），请重试" }, statusCode: 400);

                return Results.Ok(mv.AddVersion(name!, tmp, file));
            }
            catch (ModVersionException e) { return Results.Json(new { error = e.Message }, statusCode: 400); }
            catch (Exception e) { return Results.Json(new { error = "上传失败: " + e.Message }, statusCode: 500); }
            finally { try { File.Delete(tmp); } catch { } }
        });

        api.MapPost("/mod_versions/rollback", (ModVersionRollbackRequest? body, ModVersionService mv, TmlProcessService procs) =>
        {
            if (procs.IsRunning(true))
                return Results.Json(new { error = "tModLoader 正在运行，模组文件被占用，暂时不能回滚" }, statusCode: 409);
            try { return Results.Ok(mv.Rollback(body?.Name ?? "", body?.Sha256 ?? "", body?.Force ?? false)); }
            catch (ModVersionException e) { return Results.Json(new { error = e.Message }, statusCode: 400); }
        });

        api.MapPost("/mod_versions/set_current", (ModVersionSetCurrentRequest? body, ModVersionService mv, TmlProcessService procs) =>
        {
            if (procs.IsRunning(true))
                return Results.Json(new { error = "tModLoader 正在运行，模组文件被占用，暂时不能仲裁" }, statusCode: 409);
            try { return Results.Ok(mv.SetCurrent(body?.Name ?? "", body?.FileName ?? "")); }
            catch (ModVersionException e) { return Results.Json(new { error = e.Message }, statusCode: 400); }
        });

        api.MapPost("/mod_versions/delete", (ModVersionDeleteRequest? body, ModVersionService mv) =>
        {
            try
            {
                var d = mv.DeleteVersion(body?.Name ?? "", body?.Sha256 ?? "");
                return d is null ? Results.Ok(new { ok = true }) : Results.Ok(d);
            }
            catch (ModVersionException e) { return Results.Json(new { error = e.Message }, statusCode: 400); }
        });

        api.MapPost("/mod_versions/open_folder", (ModVersionService mv) =>
        {
            try
            {
                Directory.CreateDirectory(mv.Root);
                Process.Start(new ProcessStartInfo { FileName = mv.Root, UseShellExecute = true });
                return Results.Ok(new { ok = true, folder = mv.Root });
            }
            catch (Exception e) { return Results.Json(new { error = "打开失败: " + e.Message }, statusCode: 500); }
        });

        api.MapPost("/versions/{id}/rename", async (string id, RenameVersionRequest? body, JsonStore store) =>
        {
            var version = await store.UpdateAsync(data =>
            {
                var v = data.Versions.FirstOrDefault(x => x.Id == id);
                if (v == null) return null;
                var name = body?.Name?.Trim();
                var bat = body?.BatPath?.Trim();
                if (name != null)
                {
                    if (name.Length == 0) throw new ApiException(400, "版本名称不能为空");
                    if (data.Versions.Any(x => x.Id != id && x.Name == name)) throw new ApiException(400, "已存在同名版本");
                    v.Name = name;
                }
                if (bat != null)
                {
                    if (bat.Length == 0) throw new ApiException(400, "启动文件路径不能为空");
                    if (!File.Exists(bat)) throw new ApiException(400, "启动文件 (.bat) 不存在");
                    v.BatPath = bat;
                    v.DllMtime = 0; // 路径变化 → 强制重新检测
                    TmlVersionService.AutoDetect(v);
                }
                return v;
            });
            if (version == null) return Results.Json(new { error = "版本不存在" }, statusCode: 404);
            return Results.Ok(new { message = "版本信息已更新", version });
        });

        // ---- 全局整合包 / 版本整合包 ----
        api.MapGet("/global_packs", async (ModsService mods, JsonStore store) => mods.GetGlobalPacks(await store.ReadAsync()));

        api.MapGet("/packs/{packId}/mods", async (string packId, HttpRequest req, ModsService mods) =>
        {
            var version = req.Query["version"].ToString();
            var (r, status, err) = await mods.PackModsAsync(packId, version);
            return status == 200 ? Results.Ok(r) : Results.Json(new { error = err }, statusCode: status);
        });

        api.MapGet("/versions/{id}/mods", async (string id, ModsService mods) =>
        {
            var (r, status, err) = await mods.VersionModsAsync(id);
            return status == 200 ? Results.Ok(r) : Results.Json(new { error = err }, statusCode: status);
        });

        // 启动前体检：enabled.json 里启用、但该版本（本地 Mods ∪ 该版本 Workshop）里找不到的模组。
        // 只用于提示，不阻断启动 —— 所以单独给一个精简响应，避免每次启动都拉整份模组列表。
        api.MapGet("/versions/{id}/missing_mods", async (string id, ModsService mods) =>
        {
            var (r, status, err) = await mods.VersionModsAsync(id);
            if (r == null) return Results.Json(new { error = err }, statusCode: status);
            return Results.Ok(new
            {
                missing = r.Missing.Select(m => m.Name).ToList(),
                missing_count = r.Missing.Count,
                enabled_count = r.Enabled.Count + r.Missing.Count,   // enabled.json 里的总数
                available_count = r.Enabled.Count + r.Disabled.Count,
                version_name = r.VersionName,
            });
        });

        api.MapPost("/versions/{id}/packs", async (string id, AddPackRequest? body, ModsService mods, JsonStore store) =>
        {
            var packId = (body?.PackId ?? "").Trim();
            if (packId.Length == 0) return Results.Json(new { error = "未指定整合包" }, statusCode: 400);
            var error = await store.UpdateAsync(data =>
            {
                var v = data.Versions.FirstOrDefault(x => x.Id == id);
                if (v == null) throw new ApiException(404, "版本不存在");
                if (!mods.PackExists(packId)) throw new ApiException(400, "整合包不存在");
                if (v.Packs.Contains(packId)) throw new ApiException(400, "已添加该整合包");
                v.Packs.Add(packId);
                return "";
            });
            if (error.Length > 0) return Results.Json(new { error }, statusCode: 404);
            return Results.Ok(new { message = "添加成功" });
        });

        api.MapDelete("/versions/{id}/packs/{packId}", async (string id, string packId, JsonStore store) =>
        {
            var error = await store.UpdateAsync(data =>
            {
                var v = data.Versions.FirstOrDefault(x => x.Id == id);
                if (v == null) throw new ApiException(404, "版本不存在");
                if (!v.Packs.Contains(packId)) throw new ApiException(400, "该版本未添加此整合包");
                v.Packs.Remove(packId);
                return "";
            });
            if (error.Length > 0) return Results.Json(new { error }, statusCode: 404);
            return Results.Ok(new { message = "移除成功" });
        });

        api.MapPost("/packs/{packId}/desc", async (string packId, SetDescRequest? body, ModsService mods, JsonStore store) =>
        {
            var desc = (body?.Desc ?? "").Trim();
            if (!mods.PackExists(packId)) return Results.Json(new { error = "整合包不存在" }, statusCode: 404);
            await store.UpdateAsync(data =>
            {
                if (desc.Length > 0) data.PackDescs[packId] = desc;
                else data.PackDescs.Remove(packId);
                return true;
            });
            return Results.Ok(new { message = "描述已更新", pack_id = packId, desc });
        });

        // ---- 设置 / 收藏 ----
        api.MapGet("/meta_settings", async (JsonStore store) => (await store.ReadAsync()).MetaSettings);

        api.MapPost("/meta_settings", async (HttpRequest req, JsonStore store) =>
        {
            Dictionary<string, JsonElement> dict;
            try
            {
                dict = await req.ReadFromJsonAsync<Dictionary<string, JsonElement>>() ?? new();
            }
            catch
            {
                dict = new();
            }
            await store.UpdateAsync(data =>
            {
                foreach (var kv in dict) data.MetaSettings[kv.Key] = kv.Value;
                return true;
            });
            return Results.Ok(new { ok = true });
        });

        api.MapGet("/favorites", async (JsonStore store) => (await store.ReadAsync()).Favorites);

        api.MapPost("/favorites", async (FavoritesRequest? body, JsonStore store) =>
        {
            if (body == null || (body.Versions == null && body.Packs == null))
                return Results.Json(new { ok = false, error = "no data" }, statusCode: 400);

            var (ok, stale) = await store.UpdateAsync(data =>
            {
                var fav = data.Favorites;
                if (body.Seq != 0 && body.Seq < (fav.Seq ?? 0)) return (false, true);
                if (body.Versions != null) fav.Versions = body.Versions.ToList();
                if (body.Packs != null) fav.Packs = body.Packs.ToList();
                if (body.Seq != 0) fav.Seq = body.Seq;
                return (true, false);
            });
            return stale ? Results.Ok(new { ok = true, stale = true }) : Results.Ok(new { ok = true });
        });

        // ---- 特殊加载项配置（只存配置；文件改名操作在阶段 4）----
        api.MapGet("/special/{packId}", async (string packId, ModsService mods, JsonStore store) =>
        {
            var data = await store.ReadAsync();
            var cfg = data.SpecialLoads.TryGetValue(packId, out var c) ? c : new SpecialLoad();
            return new
            {
                pack_id = packId,
                independent = cfg.Independent || cfg.Active,
                disabled_mods = cfg.Mods,
                mods = mods.ListLocalMods(),
            };
        });

        api.MapGet("/global_special", async (ModsService mods, JsonStore store) =>
        {
            var data = await store.ReadAsync();
            return new { active = data.GlobalSpecial.Active, disabled_mods = data.GlobalSpecial.Mods, mods = mods.ListLocalMods() };
        });

        api.MapGet("/special_status", async (JsonStore store) =>
        {
            var data = await store.ReadAsync();
            var globalActive = data.GlobalSpecial.Active;
            var packs = new Dictionary<string, object>();
            foreach (var kv in data.SpecialLoads)
            {
                var indep = kv.Value.Independent || kv.Value.Active;
                packs[kv.Key] = new { independent = indep, effective = indep || globalActive };
            }
            return new { global_active = globalActive, packs };
        });

        api.MapPost("/special/{packId}", async (string packId, SpecialSetRequest? body, ModsService mods, JsonStore store) =>
        {
            if (!mods.PackExists(packId)) return Results.Json(new { error = "整合包不存在" }, statusCode: 404);
            var indep = body?.Independent ?? false;
            var modsList = (body?.Mods ?? new List<string>())
                .Where(x => !string.IsNullOrEmpty(x)).Distinct()
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            await store.UpdateAsync(data =>
            {
                data.SpecialLoads[packId] = new SpecialLoad { Independent = indep, Mods = modsList };
                return true;
            });
            return Results.Ok(new { message = "设置已保存（启动该整合包时生效）" });
        });

        api.MapPost("/global_special", async (GlobalSpecialRequest? body, JsonStore store) =>
        {
            var active = body?.Active ?? false;
            var modsList = (body?.Mods ?? new List<string>())
                .Where(x => !string.IsNullOrEmpty(x)).Distinct()
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            await store.UpdateAsync(data =>
            {
                data.GlobalSpecial = new GlobalSpecial { Active = active, Mods = modsList };
                return true;
            });
            return Results.Ok(new { message = "全局设置已保存（未启用独立配置的整合包启动时生效）" });
        });

        // ---- 状态 / 操作 ----
        api.MapGet("/status", async (JsonStore store, ModsService mods, TmlProcessService procs) =>
        {
            var data = await store.ReadAsync();
            string? versionName = null, packName = null, versionPath = null, lastVersionId = null, lastPackId = null;
            if (data.LastLaunch != null)
            {
                var v = data.Versions.FirstOrDefault(x => x.Id == data.LastLaunch.VersionId);
                if (v != null)
                {
                    versionName = $"{v.Name} ({v.VersionInfo})";
                    versionPath = v.BatPath;
                    lastVersionId = v.Id;                 // 供前端在版本卡片上标「上次启动 / 运行中」
                    var pid = data.LastLaunch.PackId;
                    if (pid != null)
                    {
                        lastPackId = pid;
                        var p = mods.GetGlobalPacks(data).FirstOrDefault(x => x.Id == pid);
                        if (p != null) packName = p.Name;
                    }
                }
            }

            var missingVersions = data.Versions
                .Where(v => string.IsNullOrEmpty(v.BatPath) || !File.Exists(v.BatPath))
                .Select(v => new { id = v.Id, name = string.IsNullOrEmpty(v.Name) ? v.Id : v.Name });
            var seen = new HashSet<string>();
            var missingPacks = new List<object>();
            foreach (var v in data.Versions)
                foreach (var pid in v.Packs)
                {
                    if (!seen.Add(pid)) continue;
                    if (!Directory.Exists(Path.Combine(mods.GlobalModpacksPath, pid)))
                        missingPacks.Add(new { id = pid, name = pid });
                }

            return new
            {
                backend_ok = true,
                // 配置读取出过问题（损坏 / 从备份恢复）时透出给界面，别让用户以为版本被删了
                data_warning = store.LastLoadWarning,
                tmodloader_running = procs.IsRunning(),
                version_name = versionName,
                pack_name = packName,
                version_path = versionPath,
                version_id = lastVersionId,
                pack_id = lastPackId,
                missing_versions = missingVersions,
                missing_packs = missingPacks,
            };
        });

        api.MapPost("/open_modpacks", (ModsService mods) =>
        {
            try
            {
                Directory.CreateDirectory(mods.GlobalModpacksPath);
                Process.Start(new ProcessStartInfo(mods.GlobalModpacksPath) { UseShellExecute = true });
                return Results.Ok(new { message = "已打开 ModPacks 文件夹" });
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "打开失败: " + ex.Message }, statusCode: 500);
            }
        });

        // 打开版本文件夹：版本 = bat 启动脚本，其所在目录即版本文件夹
        api.MapPost("/open_version_folder", (OpenFolderRequest? body) =>
        {
            var bat = body?.BatPath?.Trim() ?? "";
            if (bat.Length == 0)
                return Results.Json(new { ok = false, error = "缺少版本启动脚本路径" }, statusCode: 400);
            var dir = Path.GetDirectoryName(bat);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return Results.Json(new { ok = false, error = "版本文件夹不存在" }, statusCode: 404);
            try
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return Results.Ok(new { ok = true, folder = dir });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "打开失败: " + ex.Message }, statusCode: 500);
            }
        });

        // 打开模组文件夹：按当前列表上下文（整合包/版本）定位 .tmod，并在资源管理器中选中该文件
        api.MapPost("/open_mod_folder", async (OpenModFolderRequest? body, ModsService mods) =>
        {
            var name = body?.Name?.Trim() ?? "";
            if (name.Length == 0)
                return Results.Json(new { ok = false, error = "缺少模组名" }, statusCode: 400);
            var tmod = await mods.FindModFileAsync(name, body?.VersionId, body?.PackId);
            if (tmod == null || !File.Exists(tmod))
                return Results.Json(new { ok = false, error = "未找到该模组的本地文件" }, statusCode: 404);
            var dir = Path.GetDirectoryName(tmod)!;
            try
            {
                OpenInExplorerSelect(tmod);   // 在资源管理器中定位并选中该文件（而非只打开目录）
                return Results.Ok(new { ok = true, folder = dir, file = tmod });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "打开失败: " + ex.Message }, statusCode: 500);
            }
        });

        // ---- 模组元数据（本地逆向优先；Steam 补全在阶段 5 接入）----
        api.MapGet("/mod_meta", async (ModsService mods) => await mods.BuildModMetaAsync());

        // 刷新进度（前端在点击「刷新模组元数据」期间轮询）
        api.MapGet("/mod_meta/progress", (ModsService mods) => Results.Ok(mods.Progress.Snapshot()));

        api.MapPost("/mod_meta/refresh", async (HttpRequest req, ModsService mods) =>
        {
            var force = false;
            try
            {
                using var doc = await JsonDocument.ParseAsync(req.Body);
                force = doc.RootElement.TryGetProperty("force", out var f) && f.GetBoolean();
            }
            catch { }
            var (cache, errors) = await mods.RefreshModMetaAsync(force);
            return Results.Ok(new
            {
                message = "模组元数据已刷新",
                updated = cache["updated"] is JsonValue uv
                    ? (uv.TryGetValue<double>(out var u) ? u : (uv.TryGetValue<long>(out var ul) ? ul : 0))
                    : 0,
                success_count = (cache["meta"] as JsonObject)?.Count ?? 0,
                failed = errors,
            });
        });

        // ---- 启动整合包 / 直接启动 ----
        api.MapPost("/versions/{versionId}/launch/{packId}", async (string versionId, string packId, TmlProcessService procs, ModsService mods, SpecialLoadService special, StatsService stats, SnapshotService snapshots, JsonStore store) =>
        {
            var data = await store.ReadAsync();
            var version = data.Versions.FirstOrDefault(x => x.Id == versionId);
            if (version == null) return Results.Json(new { error = "版本不存在" }, statusCode: 404);
            if (!version.Packs.Contains(packId)) return Results.Json(new { error = "该版本未添加此整合包" }, statusCode: 400);
            if (procs.IsRunning(true)) return Results.Json(new { error = "tModLoader 正在运行，无法启动新的实例" }, statusCode: 409);

            if (string.IsNullOrWhiteSpace(version.ModsPath) || !Directory.Exists(version.ModsPath))
                return Results.Json(new { error = "Mods 目录不存在" }, statusCode: 400);

            // ③ 路径归一化：mods_path 里出现过正斜杠/相对写法，必须转成绝对路径再拼
            string modsDir;
            try { modsDir = Path.GetFullPath(version.ModsPath); }
            catch (Exception e) { return Results.Json(new { error = $"Mods 目录无效: {e.Message}" }, statusCode: 400); }

            var target = Path.Combine(modsDir, "enabled.json");
            var src = Path.Combine(mods.GlobalModpacksPath, packId, "Mods", "enabled.json");
            if (!File.Exists(src)) return Results.Json(new { error = $"整合包文件缺失: {src}" }, statusCode: 404);

            // ① 内容校验：空数组 / 坏 JSON 的 enabled.json 会把**全部模组禁用**，必须拦下
            if (!SnapshotService.TryReadEnabledList(src, out var packNames, out var badReason))
                return Results.Json(new { error = $"整合包内容无效（{badReason}），已拒绝启动以免禁用全部模组" }, statusCode: 400);
            if (packNames.Count == 0)
                return Results.Json(new { error = "整合包内容为空（会禁用全部模组），已拒绝启动" }, statusCode: 400);

            // ② 切换前把当前配置存成时间戳快照（取代原先只有一代的 enabled.json.bk）
            var snapshot = snapshots.Capture(modsDir, $"切换前（{version.Name} / {packId}）", version.Name, packId);

            try { File.Copy(src, target, true); }
            catch (Exception e)
            {
                if (snapshot != null) snapshots.Restore(snapshot.Id, modsDir);   // 回滚到刚存的那份
                return Results.Json(new { error = $"覆盖失败: {e.Message}" }, statusCode: 500);
            }

            var spErrors = special.ApplyDisabledSet(special.PackSpecialSet(data, packId));
            var spSet = special.PackSpecialSet(data, packId).OrderBy(x => x, StringComparer.Ordinal).ToList();
            try
            {
                TmlProcessService.LaunchBat(version.BatPath);
                await store.UpdateAsync(d =>
                {
                    d.LastLaunch = new LastLaunch { VersionId = versionId, PackId = packId, Disabled = spSet, SnapshotId = snapshot?.Id };
                    return true;
                });
                await stats.RecordLaunchAsync(versionId, packId);   // 游玩统计：启动次数 +1，开始一次会话

                var message = "游戏启动成功！";
                if (spErrors.Count > 0) message += "（特殊加载部分文件操作失败: " + string.Join("; ", spErrors) + "）";

                // ④ 多版本共用同一个 Mods 目录：切包会把它们一起改掉，必须提示
                var sharers = data.Versions
                    .Where(v => !string.IsNullOrWhiteSpace(v.ModsPath) && SameDir(v.ModsPath!, modsDir))
                    .Select(v => v.Name)
                    .ToList();
                var warning = sharers.Count > 1
                    ? $"有 {sharers.Count} 个版本共用这个 Mods 目录（{string.Join("、", sharers)}），它们的启用集会被一起改掉。"
                    : null;

                return Results.Ok(new { message, warning, snapshot = snapshot?.Id });
            }
            catch (Exception e)
            {
                if (snapshot != null) snapshots.Restore(snapshot.Id, modsDir);
                return Results.Json(new { error = $"启动失败: {e.Message}" }, statusCode: 500);
            }
        });

        api.MapPost("/launch_current", async (TmlProcessService procs, SpecialLoadService special, StatsService stats, JsonStore store) =>
        {
            var data = await store.ReadAsync();
            var last = data.LastLaunch;
            if (last == null || string.IsNullOrEmpty(last.VersionId))
                return Results.Json(new { error = "没有上次启动记录，无法按上次方式启动。请先从整合包或版本启动一次游戏。" }, statusCode: 400);
            var version = data.Versions.FirstOrDefault(x => x.Id == last.VersionId);
            if (version == null) return Results.Json(new { error = "上次启动的版本已不存在" }, statusCode: 400);
            if (procs.IsRunning(true)) return Results.Json(new { error = "tModLoader 正在运行" }, statusCode: 409);
            if (!File.Exists(version.BatPath)) return Results.Json(new { error = "启动文件不存在" }, statusCode: 400);

            try
            {
                var spErrors = new List<string>();
                if (last.Disabled != null)
                    spErrors = special.ApplyDisabledSet(last.Disabled.ToHashSet(StringComparer.Ordinal));
                else if (!string.IsNullOrEmpty(last.PackId))
                    spErrors = special.ApplyDisabledSet(special.PackSpecialSet(data, last.PackId));
                TmlProcessService.LaunchBat(version.BatPath);
                await stats.RecordLaunchAsync(last.VersionId, last.PackId);   // 游玩统计
                var message = $"已用上次的启动方式启动（{version.Name}）";
                if (spErrors.Count > 0) message += "（特殊加载部分文件操作失败: " + string.Join("; ", spErrors) + "）";
                return Results.Ok(new { message });
            }
            catch (Exception e) { return Results.Json(new { error = $"启动失败: {e.Message}" }, statusCode: 500); }
        });

        api.MapPost("/versions/{versionId}/launch_direct", async (string versionId, TmlProcessService procs, StatsService stats, JsonStore store) =>
        {
            var data = await store.ReadAsync();
            var version = data.Versions.FirstOrDefault(x => x.Id == versionId);
            if (version == null) return Results.Json(new { error = "版本不存在" }, statusCode: 404);
            if (procs.IsRunning(true)) return Results.Json(new { error = "tModLoader 正在运行，无法启动新的实例" }, statusCode: 409);
            if (!File.Exists(version.BatPath)) return Results.Json(new { error = "启动文件不存在" }, statusCode: 400);
            try
            {
                TmlProcessService.LaunchBat(version.BatPath);
                await store.UpdateAsync(d =>
                {
                    d.LastLaunch = new LastLaunch { VersionId = versionId, PackId = null, Disabled = new List<string>() };
                    return true;
                });
                await stats.RecordLaunchAsync(versionId, null);   // 游玩统计
                return Results.Ok(new { message = $"已直接启动（{version.Name}），使用当前 enabled.json" });
            }
            catch (Exception e) { return Results.Json(new { error = $"启动失败: {e.Message}" }, statusCode: 500); }
        });

        // 「以特殊加载配置启动」：应用全局特殊加载配置（禁用列表内本地模组、优先 workshop 版本）后启动该版本。
        // 退出 tML 时自动还原全部 .tmod.disabled（TmlMonitorService.RestoreAllDisabled）。
        api.MapPost("/versions/{versionId}/launch_special", async (string versionId, TmlProcessService procs, SpecialLoadService special, StatsService stats, JsonStore store) =>
        {
            var data = await store.ReadAsync();
            var version = data.Versions.FirstOrDefault(x => x.Id == versionId);
            if (version == null) return Results.Json(new { error = "版本不存在" }, statusCode: 404);
            if (procs.IsRunning(true)) return Results.Json(new { error = "tModLoader 正在运行，无法启动新的实例" }, statusCode: 409);
            if (!File.Exists(version.BatPath)) return Results.Json(new { error = "启动文件不存在" }, statusCode: 400);

            var disabledSet = data.GlobalSpecial.Active
                ? data.GlobalSpecial.Mods.Where(x => !string.IsNullOrEmpty(x)).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var spErrors = special.ApplyDisabledSet(disabledSet);
                TmlProcessService.LaunchBat(version.BatPath);
                await store.UpdateAsync(d =>
                {
                    d.LastLaunch = new LastLaunch { VersionId = versionId, PackId = null, Disabled = disabledSet.ToList() };
                    return true;
                });
                await stats.RecordLaunchAsync(versionId, null);   // 游玩统计
                var message = $"已启动（{version.Name}），已应用全局特殊加载配置";
                if (!data.GlobalSpecial.Active) message += "（全局特殊加载未开启，实际等价于直接启动）";
                if (spErrors.Count > 0) message += "（特殊加载部分文件操作失败: " + string.Join("; ", spErrors) + "）";
                return Results.Ok(new { message });
            }
            catch (Exception e) { return Results.Json(new { error = $"启动失败: {e.Message}" }, statusCode: 500); }
        });

        api.MapGet("/current_enabled", async (ModsService mods) =>
        {
            var names = await mods.ReadSharedEnabled();
            return new { mods = names, count = names.Count };
        });

        api.MapPost("/kill_tmodloader", (TmlProcessService procs) =>
        {
            try
            {
                var killed = procs.Kill();
                return killed ? Results.Ok(new { message = "游戏已强制关闭" }) : Results.Json(new { error = "未找到正在运行的 tModLoader 进程" }, statusCode: 404);
            }
            catch (Exception e) { return Results.Json(new { error = $"关闭失败: {e.Message}" }, statusCode: 500); }
        });

        // ---- 联网 / 收尾 ----
        api.MapGet("/tmod_update", (TmodUpdateService svc) => svc.GetAsync());

        api.MapPost("/pick_bat", async (WindowControlService wc) =>
            new { path = await wc.PickBatAsync() });

        api.MapPost("/open_url", (OpenUrlRequest? body) =>
        {
            var url = body?.Url?.Trim() ?? "";
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                return Results.Json(new { ok = false, error = "非法链接" }, statusCode: 400);
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return Results.Ok(new { ok = true });
            }
            catch (Exception e)
            {
                return Results.Json(new { ok = false, error = "打开失败: " + e.Message }, statusCode: 500);
            }
        });

        // ---- 崩溃日志查看器 ----
        api.MapGet("/logs", async (CrashLogService logs, JsonStore store) => logs.List(await store.ReadAsync()));

        api.MapGet("/logs/content", async (string path, CrashLogService logs, JsonStore store) =>
        {
            var content = logs.Read(await store.ReadAsync(), path);
            return content == null
                ? Results.Json(new { error = "日志不存在或无权访问" }, statusCode: 404)
                : Results.Ok(content);
        });

        api.MapPost("/logs/open_folder", async (OpenLogFolderRequest? body, CrashLogService logs, JsonStore store) =>
        {
            var dir = logs.ResolveDir(await store.ReadAsync(), body?.Path ?? "");
            if (dir == null || !Directory.Exists(dir))
                return Results.Json(new { ok = false, error = "日志目录不存在" }, statusCode: 404);
            try
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return Results.Ok(new { ok = true, folder = dir });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "打开失败: " + ex.Message }, statusCode: 500);
            }
        });

        // ---- 存档管理（世界 / 人物）----
        api.MapGet("/saves", (SavesService saves) => new
        {
            worlds = saves.ListWorlds(),
            players = saves.ListPlayers(),
            backups = saves.ListBackups(),
            worlds_dir = saves.WorldsDir,
            players_dir = saves.PlayersDir,
            backup_dir = saves.BackupDir,
        });

        api.MapPost("/saves/backup", (SaveActionRequest? body, SavesService saves) =>
        {
            var path = saves.Backup(body?.Kind ?? "world", body?.Path ?? "");
            return path == null
                ? Results.Json(new { ok = false, error = "备份失败：文件不存在或路径非法" }, statusCode: 400)
                : Results.Ok(new { ok = true, message = "已备份", backup = Path.GetFileName(path) });
        });

        api.MapPost("/saves/restore", (SaveActionRequest? body, SavesService saves) =>
        {
            var target = saves.Restore(body?.Path ?? "");
            return target == null
                ? Results.Json(new { ok = false, error = "还原失败：备份不存在或路径非法" }, statusCode: 400)
                : Results.Ok(new { ok = true, message = "已还原", target });
        });

        api.MapPost("/saves/delete", (SaveActionRequest? body, SavesService saves) =>
        {
            var ok = body?.Backup == true
                ? saves.DeleteBackup(body.Path ?? "")
                : saves.DeleteSave(body?.Kind ?? "world", body?.Path ?? "");
            return ok
                ? Results.Ok(new { ok = true, message = "已删除（可在回收站找回）" })
                : Results.Json(new { ok = false, error = "删除失败" }, statusCode: 400);
        });

        api.MapPost("/saves/open_folder", (SaveActionRequest? body, SavesService saves) =>
        {
            var dir = saves.ResolveOpenDir(body?.Kind ?? "world", body?.Path);
            if (dir == null || !Directory.Exists(dir))
                return Results.Json(new { ok = false, error = "目录不存在" }, statusCode: 404);
            try
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return Results.Ok(new { ok = true, folder = dir });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, error = "打开失败: " + ex.Message }, statusCode: 500);
            }
        });

        // ---- 游玩统计 ----
        api.MapGet("/stats", async (JsonStore store) =>
        {
            var data = await store.ReadAsync();
            var s = data.Stats;
            var a = s.Active;
            object? active = a == null ? null : new
            {
                version_id = a.VersionId,
                pack_id = a.PackId,
                started_at = a.StartedAt,
                elapsed_seconds = Math.Max(0, (DateTimeOffset.UtcNow - a.StartedAt).TotalSeconds),
            };
            return new
            {
                total_seconds = s.TotalSeconds,
                version_launches = s.VersionLaunches,
                pack_launches = s.PackLaunches,
                version_seconds = s.VersionSeconds,
                pack_seconds = s.PackSeconds,
                active,
                recent = s.Recent,
            };
        });

        api.MapPost("/stats/reset", async (StatsService stats) =>
        {
            await stats.ResetAsync();
            return Results.Ok(new { ok = true, message = "统计已清空" });
        });
        // ---- 加密图标 ----
        // 图标以 {哈希}.dat 加密存储（防普通用户用看图软件打开 / 从文件名猜模组）；
        // 此路由解密后返回原始 PNG。哈希严格校验（16 位 hex）防路径遍历；no-store 防浏览器缓存明文图片。
        api.MapGet("/icon/{hash}", (string hash, HttpContext ctx, AppPaths paths) =>
        {
            if (!IconObfuscator.IsValidHash(hash))
                return Results.NotFound();
            var file = Path.Combine(paths.IconsDir, hash + ".dat");
            if (!File.Exists(file)) return Results.NotFound();
            try
            {
                var png = IconObfuscator.Decrypt(File.ReadAllBytes(file));
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.File(png, "image/png");
            }
            catch
            {
                return Results.NotFound();
            }
        });
    }

    /// <summary>在资源管理器中定位并选中文件。explorer.exe 是受保护进程(PPL)，
    /// .NET Process.Start 启动它时会因无法获取进程句柄而抛「拒绝访问」，
    /// 故改用 ShellExecute 直接交给 shell（不持有 explorer 句柄）；失败则兜底打开所在目录。
    /// ⚠️ 路径必须先规范化为反斜杠绝对路径：配置里的 bat_path 等存的是正斜杠（F:/games/…），
    /// 由此推导出的 workshop 模组路径也带正斜杠，而 explorer /select 认不出正斜杠路径，
    /// 会退化成「只打开文件夹、不选中文件」。</summary>
    private static void OpenInExplorerSelect(string path)
    {
        string full;
        try { full = Path.GetFullPath(path).Replace('/', '\\'); }
        catch { full = path; }

        var hr = ShellExecute(IntPtr.Zero, "open", "explorer.exe", $"/select,\"{full}\"", null, 1);
        if (hr.ToInt64() <= 32)   // ShellExecute 返回值 <=32 表示错误（SE_ERR_*）
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ShellExecute(IntPtr hwnd, string? lpOperation, string? lpFile,
        string? lpParameters, string? lpDirectory, int nShowCmd);

    /// <summary>两个路径是否指向同一个目录（各自归一化后按大小写不敏感比较）。</summary>
    private static bool SameDir(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>业务异常：携带 HTTP 状态码，统一转成 {error} 响应。</summary>
    public sealed class ApiException : Exception
    {
        public int StatusCode { get; }
        public ApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;
    }
}
