using System.Collections.Generic;

namespace Win11Toolbox.Services;

/// <summary>一行「键 + 值」。用类而不是元组，才能走 JSON 缓存。</summary>
public sealed class InfoItem
{
    public InfoItem()
    {
    }

    public InfoItem(string key, string value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; set; } = "—";
    public string Value { get; set; } = "—";

    public void Deconstruct(out string key, out string value)
    {
        key = Key;
        value = Value;
    }
}

public sealed class GpuBlock
{
    public string Title { get; set; } = "—";
    public List<InfoItem> Rows { get; set; } = new();
}
