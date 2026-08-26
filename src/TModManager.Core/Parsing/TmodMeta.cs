namespace TModManager.Core.Parsing;

/// <summary>.tmod 逆向解析结果。</summary>
public sealed class TmodMeta
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "";
    public byte[]? IconBytes { get; set; }
}
