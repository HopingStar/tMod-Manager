// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 HopingStar

namespace TModManager.Core.Models;

using System.Text.Json.Nodes;

/// <summary>全局整合包信息。</summary>
public sealed class PackInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
}

public sealed class AddVersionRequest
{
    public string Name { get; set; } = "";
    public string BatPath { get; set; } = "";
}

public sealed class RenameVersionRequest
{
    public string? Name { get; set; }
    public string? BatPath { get; set; }
}

public sealed class AddPackRequest
{
    public string PackId { get; set; } = "";
}

public sealed class SetDescRequest
{
    public string Desc { get; set; } = "";
}

public sealed class OpenUrlRequest
{
    public string? Url { get; set; }
}

public sealed class OpenFolderRequest
{
    public string? BatPath { get; set; }
}

public sealed class OpenModFolderRequest
{
    public string? Name { get; set; }

    /// <summary>当前模组列表弹窗上下文：整合包 id（pack 模式）或版本 id（version 模式），用于按上下文定位 .tmod。</summary>
    public string? PackId { get; set; }
    public string? VersionId { get; set; }
}

public sealed class FavoritesRequest
{
    public List<string>? Versions { get; set; }
    public List<string>? Packs { get; set; }
    public long Seq { get; set; }
}

/// <summary>本地 Mods 目录模组（list_local_mods）。</summary>
public sealed class LocalModInfo
{
    public string Name { get; set; } = "";
    public bool Disabled { get; set; }
}

public sealed class SpecialSetRequest
{
    public bool Independent { get; set; }
    public List<string> Mods { get; set; } = new();
}

public sealed class GlobalSpecialRequest
{
    public bool Active { get; set; }
    public List<string> Mods { get; set; } = new();
}

/// <summary>模组列表条目（对齐 Python build() 返回字段）。数值字段用 JsonNode 保留 Steam 原始类型（如 file_size 可能是字符串）。</summary>
public sealed class ModEntry
{
    public string Name { get; set; } = "";
    public bool HasLocal { get; set; }
    public bool HasWorkshop { get; set; }
    public bool InPack { get; set; }
    public bool Conflict { get; set; }
    public int? UpdateDaysAgo { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Description { get; set; } = "";
    public JsonNode? Subscriptions { get; set; }
    public JsonNode? Favorited { get; set; }
    public JsonNode? Views { get; set; }
    public JsonNode? TimeCreated { get; set; }
    public JsonNode? FileSize { get; set; }
    public JsonNode? Visibility { get; set; }
    public JsonNode? Banned { get; set; }
}

/// <summary>模组列表接口结果（pack_mods / version_mods）。</summary>
public sealed class ModsListResult
{
    public string PackId { get; set; } = "";
    public string Source { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string? VersionName { get; set; }
    public List<ModEntry> Enabled { get; set; } = new();
    public List<ModEntry> Disabled { get; set; } = new();
    public List<ModEntry> Missing { get; set; } = new();
}

/// <summary>Workshop 扫描结果条目。</summary>
public sealed class WorkshopMod
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
}

/// <summary>模组元数据条目（/api/mod_meta）。</summary>
public sealed class ModMetaItem
{
    public string Name { get; set; } = "";
    public bool Disabled { get; set; }
    public string? WorkshopId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Version { get; set; } = "";
    public int? UpdateDaysAgo { get; set; }
    public List<string> Tags { get; set; } = new();
    public string Description { get; set; } = "";
    public JsonNode? Subscriptions { get; set; }
    public JsonNode? Favorited { get; set; }
    public JsonNode? Views { get; set; }
    public JsonNode? TimeCreated { get; set; }
    public JsonNode? FileSize { get; set; }
    public JsonNode? Visibility { get; set; }
    public JsonNode? Banned { get; set; }
}

/// <summary>/api/mod_meta 响应。</summary>
public sealed class ModMetaResponse
{
    public List<ModMetaItem> Mods { get; set; } = new();
    public bool NeedsRefresh { get; set; }
    public double Updated { get; set; }
}

/// <summary>崩溃日志：打开某条日志所在目录。</summary>
public sealed class OpenLogFolderRequest
{
    /// <summary>日志文件路径；为空时回退到第一个候选日志目录。</summary>
    public string? Path { get; set; }
}

/// <summary>存档操作请求（备份 / 还原 / 删除 / 打开目录共用）。</summary>
public sealed class SaveActionRequest
{
    /// <summary>world | player</summary>
    public string Kind { get; set; } = "world";

    /// <summary>目标文件路径（存档本体或备份 zip）。</summary>
    public string? Path { get; set; }

    /// <summary>true = Path 指向的是备份 zip（删除时区分删备份还是删存档）。</summary>
    public bool Backup { get; set; }
}

/// <summary>还原 enabled.json 快照。</summary>
public sealed class SnapshotRestoreRequest
{
    /// <summary>快照 id（= snapshots/ 下的目录名）。</summary>
    public string? Id { get; set; }

    /// <summary>写入哪个版本的 Mods 目录；缺省则用快照里记录的那个。</summary>
    public string? VersionId { get; set; }
}

/// <summary>把某个 .tmod 加进某模组的版本列表（拖入）。</summary>
public sealed class ModVersionAddRequest
{
    /// <summary>目标模组的内部 ID。</summary>
    public string? Name { get; set; }

    /// <summary>要归档的 .tmod 绝对路径。</summary>
    public string? Path { get; set; }
}

/// <summary>回滚到某一版。</summary>
public sealed class ModVersionRollbackRequest
{
    public string? Name { get; set; }

    /// <summary>目标版本的内容哈希（版本号可能重复，哈希才是唯一键）。</summary>
    public string? Sha256 { get; set; }

    /// <summary>当前那份未归档时是否强行覆盖（默认拒绝，避免用户误丢内容）。</summary>
    public bool Force { get; set; }
}

/// <summary>同名冲突仲裁：保留哪一个文件当当前版本。</summary>
public sealed class ModVersionSetCurrentRequest
{
    public string? Name { get; set; }
    public string? FileName { get; set; }
}

/// <summary>从版本列表里删掉一条。</summary>
public sealed class ModVersionDeleteRequest
{
    public string? Name { get; set; }
    public string? Sha256 { get; set; }
}
