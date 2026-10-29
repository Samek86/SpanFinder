using System;
using System.Collections.Generic;

namespace Span.Services;

/// <summary>
/// 組み込みコンテキストメニュー項目の表示設定。
/// LocalSettings には非表示にする項目 ID をカンマ区切り（または JSON 配列）で保存する。
/// 空ならすべて表示。RemoveFromFavorites は AddToFavorites と同じ設定で制御する。
/// Open / Cut / Copy / Paste / Delete / Rename / Properties は対象外で常時表示。
/// </summary>
internal static class ContextMenuItemSettings
{
    public const string StorageKey = "HiddenContextMenuItems";

    /// <summary>設定画面に出す項目。(保存 ID, 既存のローカライズキー)</summary>
    public static readonly (string Id, string LocalizationKey)[] ToggleableItems =
    {
        ("OpenInNewTab", "OpenInNewTab"),
        ("OpenTerminal", "OpenTerminal"),
        ("OpenWith", "OpenWith"),
        ("RunAsAdmin", "RunAsAdmin"),
        ("CompressToZip", "CompressToZip"),
        ("ExtractHere", "ExtractHere"),
        ("ExtractTo", "ExtractTo"),
        ("CopyPath", "CopyPath"),
        ("OpenInExplorer", "OpenInExplorer"),
        ("AddToFavorites", "AddToFavorites"),
        ("FolderTag", "Menu_FolderTag"),
        ("Eject", "Eject"),
        ("Format", "Format"),
        ("DiskCleanup", "DiskCleanup"),
        ("DisconnectDrive", "DisconnectDrive"),
        ("EditConnection", "EditConnection"),
        ("RemoveConnection", "RemoveConnection"),
    };

    public static string CanonicalId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        if (string.Equals(id, "RemoveFromFavorites", StringComparison.Ordinal))
            return "AddToFavorites";
        return id;
    }

    public static bool IsVisible(string? stored, string? id)
    {
        if (string.IsNullOrEmpty(id)) return true;
        return !Parse(stored).Contains(CanonicalId(id));
    }

    public static string SetVisible(string? stored, string? id, bool visible)
    {
        if (string.IsNullOrEmpty(id)) return stored ?? "";
        var canon = CanonicalId(id);
        if (canon.Length == 0) return stored ?? "";

        var hidden = Parse(stored);
        if (visible) hidden.Remove(canon);
        else hidden.Add(canon);
        if (hidden.Count == 0) return "";

        var list = new List<string>(hidden);
        list.Sort(StringComparer.Ordinal);
        return string.Join(",", list);
    }

    public static HashSet<string> Parse(string? stored)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(stored)) return set;

        var text = stored.Trim();
        if (text.StartsWith('[') && text.EndsWith(']'))
            text = text[1..^1].Replace("\"", "", StringComparison.Ordinal);

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var canon = CanonicalId(part);
            if (canon.Length > 0)
                set.Add(canon);
        }
        return set;
    }
}
