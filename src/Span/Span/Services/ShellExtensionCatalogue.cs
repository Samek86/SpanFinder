using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Span.Models;

namespace Span.Services;

/// <summary>
/// 右クリックの対象。Panegrid の FILE / FOLDER / BACKGROUND / DRIVE と同じビット。
/// </summary>
public static class ShellMenuScope
{
    public const byte File = 1;
    public const byte Folder = 2;
    public const byte Background = 4;
    public const byte Drive = 8;
    public const byte All = File | Folder | Background | Drive;

    public static readonly byte[] Bits = { File, Folder, Background, Drive };

    public static readonly string[] Names = { "file", "folder", "background", "drive" };

    public static byte BitOfName(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "file" => File,
        "folder" => Folder,
        "background" => Background,
        "drive" => Drive,
        _ => 0,
    };

    public static byte BitsOf(IEnumerable<string>? names)
    {
        byte bits = 0;
        if (names == null) return bits;
        foreach (var name in names)
            bits |= BitOfName(name);
        return bits;
    }

    public static List<string> NamesOf(byte bits)
    {
        var list = new List<string>(4);
        for (var i = 0; i < Bits.Length; i++)
        {
            if ((bits & Bits[i]) != 0)
                list.Add(Names[i]);
        }
        return list;
    }

    /// <summary>ドライブ直下（C: / C:\）はドライブ。それ以外はファイルかフォルダー。</summary>
    public static byte PathKind(string? path, bool isFile)
    {
        var t = (path ?? "").Trim().TrimEnd('\\', '/');
        if (t.Length == 2 && char.IsAsciiLetter(t[0]) && t[1] == ':')
            return Drive;
        return isFile ? File : Folder;
    }

    public static byte ForClick(string? path, bool isFile, bool background)
        => background ? Background : PathKind(path, isFile);

    public static string LocalizationKey(byte bit) => bit switch
    {
        File => "CtxScope_File",
        Folder => "CtxScope_Folder",
        Background => "CtxScope_Empty",
        Drive => "CtxScope_Drive",
        _ => "",
    };
}

public enum ShellTick
{
    Off,
    On,
    Mixed,
}

/// <summary>レジストリから集めたハンドラ 1 件。同じ id はスコープを合成する。</summary>
public sealed class ShellHandler
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public byte Targets { get; set; }
    public bool Builtin { get; set; }
    public string Verb { get; set; } = "";
    public string Command { get; set; } = "";
    public int Rank { get; set; }
    public string Anchor { get; set; } = "";
    public string Product { get; set; } = "";
    public string Raw { get; set; } = "";
    public List<string> StaticLabels { get; set; } = new();
    public List<string> Classes { get; set; } = new();
}

/// <summary>設定画面の 1 行。同じ製品名のハンドラをまとめたもの。</summary>
public sealed class ShellUiRow
{
    public string Id { get; set; } = "";
    public List<string> Ids { get; set; } = new();
    public List<byte> Scopes { get; set; } = new();
    public string Name { get; set; } = "";
    public string Raw { get; set; } = "";
    public string Detail { get; set; } = "";
    public byte Targets { get; set; }
    public ShellTick Tick { get; set; } = ShellTick.On;
    public ShellTick[] Subs { get; set; } = { ShellTick.On, ShellTick.On, ShellTick.On, ShellTick.On };
}

/// <summary>
/// シェル拡張カタログの純ロジック。レジストリは読まない。
/// 無効化は id → スコープ名（file/folder/background/drive）。空はすべて有効。
/// 新しい項目はマップに無いのでオン。エクスプローラーのレジストリは変更しない。
/// </summary>
public static class ShellExtensionCatalogue
{
    public const string OffStorageKey = "ShellExtensionCtxOff";
    public const string LabelsStorageKey = "ShellExtensionCtxLabels";

    public static Dictionary<string, List<string>> ParseMap(string? json)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return map;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            if (parsed == null) return map;
            foreach (var kv in parsed)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null) continue;
                var list = new List<string>();
                foreach (var raw in kv.Value)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var text = raw.Trim();
                    if (list.Exists(e => string.Equals(e, text, StringComparison.Ordinal))) continue;
                    list.Add(text);
                    if (list.Count >= 32) break;
                }
                if (list.Count > 0)
                    map[kv.Key.Trim()] = list;
            }
        }
        catch (JsonException)
        {
            return new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
        return map;
    }

    public static string SerializeMap(IReadOnlyDictionary<string, List<string>>? map)
    {
        if (map == null || map.Count == 0) return "";
        var sorted = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var kv in map)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null || kv.Value.Count == 0) continue;
            sorted[kv.Key] = kv.Value;
        }
        if (sorted.Count == 0) return "";
        return JsonSerializer.Serialize(sorted);
    }

    public static bool IsEnabled(IReadOnlyDictionary<string, List<string>>? disabled, string? id, byte scopeBit)
    {
        if (string.IsNullOrEmpty(id) || scopeBit == 0) return true;
        if (disabled == null || !disabled.TryGetValue(id, out var names)) return true;
        return (ShellMenuScope.BitsOf(names) & scopeBit) == 0;
    }

    public static void SetScope(Dictionary<string, List<string>> map, string? id, byte scopeBit, bool enabled)
    {
        if (map == null || string.IsNullOrEmpty(id) || scopeBit == 0) return;
        var cur = map.TryGetValue(id, out var names) ? ShellMenuScope.BitsOf(names) : (byte)0;
        var next = enabled ? (byte)(cur & ~scopeBit) : (byte)(cur | scopeBit);
        WriteBits(map, id, next);
    }

    /// <summary>行全体。全部オンなら全部オフ。それ以外は全部オン。各ハンドラは自分のスコープだけ書く。</summary>
    public static void ToggleMaster(Dictionary<string, List<string>> map, IReadOnlyList<string> ids, IReadOnlyList<byte> scopes)
    {
        if (map == null || ids == null || scopes == null || ids.Count == 0 || ids.Count != scopes.Count) return;
        var fullyOn = true;
        for (var i = 0; i < ids.Count; i++)
        {
            var cur = map.TryGetValue(ids[i], out var names) ? ShellMenuScope.BitsOf(names) : (byte)0;
            if ((cur & scopes[i]) != 0)
            {
                fullyOn = false;
                break;
            }
        }
        for (var i = 0; i < ids.Count; i++)
        {
            var cur = map.TryGetValue(ids[i], out var names) ? ShellMenuScope.BitsOf(names) : (byte)0;
            var next = fullyOn ? scopes[i] : (byte)(cur & ~scopes[i]);
            WriteBits(map, ids[i], (byte)(next & ShellMenuScope.All));
        }
    }

    /// <summary>そのスコープを持つハンドラをまとめて反転。混在なら全部オン。</summary>
    public static void ToggleScope(Dictionary<string, List<string>> map, IReadOnlyList<string> ids, IReadOnlyList<byte> scopes, byte bit)
    {
        if (map == null || ids == null || scopes == null || ids.Count != scopes.Count || bit == 0) return;
        var relevant = new List<int>();
        for (var i = 0; i < scopes.Count; i++)
        {
            if ((scopes[i] & bit) != 0) relevant.Add(i);
        }
        if (relevant.Count == 0) return;
        var fullyOn = true;
        foreach (var i in relevant)
        {
            var cur = map.TryGetValue(ids[i], out var names) ? ShellMenuScope.BitsOf(names) : (byte)0;
            if ((cur & bit) != 0)
            {
                fullyOn = false;
                break;
            }
        }
        foreach (var i in relevant)
        {
            var cur = map.TryGetValue(ids[i], out var names) ? ShellMenuScope.BitsOf(names) : (byte)0;
            var attached = scopes[i];
            var next = fullyOn ? (byte)((cur | bit) & attached) : (byte)((cur & ~bit) & attached);
            WriteBits(map, ids[i], (byte)(next & ShellMenuScope.All));
        }
    }

    public static void ResetAll(Dictionary<string, List<string>> map) => map?.Clear();

    public static void WriteBits(Dictionary<string, List<string>> map, string id, byte bits)
    {
        if (string.IsNullOrEmpty(id) || bits == 0)
        {
            map.Remove(id);
            return;
        }
        map[id] = ShellMenuScope.NamesOf((byte)(bits & ShellMenuScope.All));
    }

    public static List<ShellUiRow> Assemble(
        IEnumerable<ShellHandler>? handlers,
        IReadOnlyDictionary<string, List<string>>? disabled,
        IReadOnlyDictionary<string, List<string>>? learned,
        string? query)
    {
        var q = (query ?? "").Trim().ToLowerInvariant();
        var rows = new List<ShellHandler>();
        if (handlers != null)
        {
            foreach (var handler in handlers)
            {
                if (Listed(handler, learned))
                    rows.Add(handler);
            }
        }
        rows.Sort((a, b) =>
        {
            var cmp = string.Compare(TitleOf(a), TitleOf(b), StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        });

        var accs = new List<Acc>();
        foreach (var handler in rows)
        {
            var shown = Present(handler, learned);
            var key = GroupKey(shown.Name) ?? ("id:" + handler.Id);
            var title = shown.Name;
            var existing = accs.Find(a => a.Key == key);
            if (existing != null && GroupKey(shown.Name) != null)
            {
                existing.Ids.Add(handler.Id);
                existing.Scopes.Add(handler.Targets);
                existing.Targets |= handler.Targets;
                PushUnique(existing.Labels, shown.Labels);
                if (shown.Raw.Length > 0) existing.Raws.Add(shown.Raw);
                continue;
            }
            accs.Add(new Acc
            {
                Key = key,
                Title = title,
                Ids = new List<string> { handler.Id },
                Scopes = new List<byte> { handler.Targets },
                Targets = handler.Targets,
                Labels = new List<string>(shown.Labels),
                Raws = shown.Raw.Length == 0 ? new List<string>() : new List<string> { shown.Raw },
            });
        }

        var output = new List<ShellUiRow>();
        foreach (var acc in accs)
        {
            if (!HitsQuery(acc, rows, learned, q)) continue;
            var labels = acc.Labels.Where(l => FoldMenu(l) != FoldMenu(acc.Title)).ToList();
            var raw = SharedRaw(acc.Title, acc.Raws, labels);
            var (tick, subs) = GroupTicks(disabled, acc.Ids, acc.Scopes);
            string id;
            if (acc.Ids.Count == 1)
            {
                id = acc.Ids[0];
            }
            else
            {
                var sorted = acc.Ids.ToList();
                sorted.Sort(StringComparer.Ordinal);
                id = "group:" + string.Join("|", sorted);
            }
            var name = acc.Title;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = raw.Length > 0 ? raw : acc.Ids[0];
                if (FoldMenu(name) == FoldMenu(raw)) raw = "";
            }
            output.Add(new ShellUiRow
            {
                Id = id,
                Ids = acc.Ids,
                Scopes = acc.Scopes,
                Name = name,
                Raw = raw,
                Detail = string.Join(" · ", labels),
                Targets = acc.Targets,
                Tick = tick,
                Subs = subs,
            });
        }
        return output;
    }

    /// <summary>
    /// 無効なハンドラの項目を落とす。標準 verb（open, copy など）は残す。
    /// カタログがまだ無いときは、覚えたキャプションだけで落とす。
    /// 一致した文は ctx_labels に足す（COM プローブの代わり）。
    /// </summary>
    public static List<ShellMenuItem> FilterShellItems(
        IList<ShellMenuItem>? items,
        byte scope,
        IReadOnlyList<ShellHandler>? handlers,
        IReadOnlyDictionary<string, List<string>>? disabled,
        Dictionary<string, List<string>> learned,
        out bool learnedChanged)
    {
        learnedChanged = false;
        if (items == null || items.Count == 0) return new List<ShellMenuItem>();
        learned ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var changed = false;

        bool Disabled(string id)
        {
            if (scope == 0 || disabled == null || !disabled.TryGetValue(id, out var names)) return false;
            return (ShellMenuScope.BitsOf(names) & scope) != 0;
        }

        IReadOnlyList<string>? LearnedOf(string id)
            => learned.TryGetValue(id, out var list) ? list : null;

        void Note(ShellMenuItem item)
        {
            if (handlers == null) return;
            if (BuiltinVerb(item.Verb)) return;
            var label = MenuLabel(item.Text);
            if (label.Length == 0) return;
            foreach (var handler in handlers)
            {
                if (handler.Builtin || (handler.Targets & scope) == 0) continue;
                if (!MatchesHandler(handler, item.Verb, item.Text, LearnedOf(handler.Id))) continue;
                if (!learned.TryGetValue(handler.Id, out var slot))
                {
                    slot = new List<string>();
                    learned[handler.Id] = slot;
                }
                if (PushUnique(slot, new[] { label }))
                    changed = true;
            }
        }

        bool Blocked(ShellMenuItem item)
        {
            if (BuiltinVerb(item.Verb)) return false;
            if (handlers != null)
            {
                foreach (var handler in handlers)
                {
                    if (handler.Builtin || (handler.Targets & scope) == 0) continue;
                    if (!Disabled(handler.Id)) continue;
                    if (MatchesHandler(handler, item.Verb, item.Text, LearnedOf(handler.Id)))
                        return true;
                }
                return false;
            }
            if (disabled == null) return false;
            var key = MenuMatchKey(item.Text);
            var verbL = AsciiLower((item.Verb ?? "").Trim());
            foreach (var kv in disabled)
            {
                if ((ShellMenuScope.BitsOf(kv.Value) & scope) == 0) continue;
                if (!learned.TryGetValue(kv.Key, out var labels)) continue;
                foreach (var label in labels)
                {
                    if (key.Length > 0 && MenuMatchKey(label) == key) return true;
                    if (verbL.Length > 0 && AsciiLower(label) == verbL) return true;
                }
            }
            return false;
        }

        List<ShellMenuItem> Walk(IList<ShellMenuItem> src)
        {
            var list = new List<ShellMenuItem>(src.Count);
            foreach (var item in src)
            {
                if (item.IsSeparator)
                {
                    list.Add(item);
                    continue;
                }
                if (Blocked(item))
                {
                    Note(item);
                    continue;
                }
                Note(item);
                if (item.Children is { Count: > 0 })
                {
                    var kids = CollapseSeparators(Walk(item.Children));
                    if (kids.Count == 0) continue;
                    var same = kids.Count == item.Children.Count;
                    if (same)
                    {
                        for (var i = 0; i < kids.Count; i++)
                        {
                            if (!ReferenceEquals(kids[i], item.Children[i]))
                            {
                                same = false;
                                break;
                            }
                        }
                    }
                    list.Add(same ? item : CloneMenu(item, kids));
                }
                else
                {
                    list.Add(item);
                }
            }
            return CollapseSeparators(list);
        }

        var result = Walk(items);
        learnedChanged = changed;
        return result;
    }

    public static bool ItemDropped(string? verb, string? text, IReadOnlyCollection<string>? verbs, IReadOnlyCollection<string>? names)
    {
        var verbL = AsciiLower((verb ?? "").Trim());
        if (verbL.Length > 0 && BuiltinVerb(verbL)) return false;
        if (verbL.Length > 0 && verbs != null)
        {
            foreach (var candidate in verbs)
            {
                if (AsciiLower((candidate ?? "").Trim()) == verbL) return true;
            }
        }
        var key = MenuMatchKey(text ?? "");
        if (key.Length == 0 || names == null) return false;
        foreach (var name in names)
        {
            if (name == key) return true;
        }
        return false;
    }

    public static bool MatchesHandler(ShellHandler handler, string? verb, string? text, IReadOnlyList<string>? learned)
    {
        if (handler.Builtin) return false;
        var verbL = AsciiLower((verb ?? "").Trim());
        if (verbL.Length > 0 && BuiltinVerb(verbL)) return false;
        var handlerVerb = AsciiLower((handler.Verb ?? "").Trim());
        if (verbL.Length > 0 && handlerVerb.Length > 0 && verbL == handlerVerb) return true;

        var key = MenuMatchKey(text ?? "");
        if (key.Length > 0)
        {
            foreach (var label in handler.StaticLabels)
            {
                if (MenuMatchKey(label) == key) return true;
            }
            if (handlerVerb.Length > 0 && MenuMatchKey(handler.Name) == key) return true;
            if (learned != null)
            {
                foreach (var label in learned)
                {
                    if (MenuMatchKey(label) == key) return true;
                }
            }
        }

        // 短い製品名（Git など）は部分一致しない。静的キャプションと覚えた文で落とす。
        var title = TitleOf(handler);
        if (ContainsAsWord(text, title, 5)) return true;
        if (!IdentifierLike(handler.Raw) && ContainsAsWord(text, handler.Raw, 6)) return true;
        return false;
    }

    public static void Upsert(List<ShellHandler> rows, ShellHandler incoming)
    {
        var have = rows.Find(r => r.Id == incoming.Id);
        if (have == null)
        {
            rows.Add(incoming);
            return;
        }
        have.Targets |= incoming.Targets;
        have.Builtin |= incoming.Builtin;
        if (incoming.Rank >= have.Rank && !string.IsNullOrEmpty(incoming.Command))
        {
            have.Command = incoming.Command;
            have.Rank = incoming.Rank;
            have.Anchor = incoming.Anchor;
        }
        var incomingBetter = !string.IsNullOrEmpty(incoming.Name)
            && !TitleJunk(incoming.Name)
            && (string.IsNullOrEmpty(have.Name) || TitleJunk(have.Name) || ParseGuid(have.Name) != null
                || (IdentifierLike(have.Name) && !IdentifierLike(incoming.Name)));
        if (incomingBetter) have.Name = incoming.Name;
        if (string.IsNullOrEmpty(have.Verb)) have.Verb = incoming.Verb;
        if (BetterTitle(have.Product, incoming.Product)) have.Product = incoming.Product;
        if (string.IsNullOrEmpty(have.Raw)) have.Raw = incoming.Raw;
        PushUnique(have.StaticLabels, incoming.StaticLabels);
        foreach (var cls in incoming.Classes)
        {
            if (!have.Classes.Exists(e => e.Equals(cls, StringComparison.OrdinalIgnoreCase)))
                have.Classes.Add(cls);
        }
    }

    public static string TitleOf(ShellHandler handler)
    {
        var product = UsableTitle(handler.Product);
        if (product != null) return product;
        if (KeptTitle(handler.Product)) return handler.Product;
        if (string.IsNullOrEmpty(handler.Verb))
        {
            var name = UsableTitle(handler.Name);
            if (name != null) return name;
        }
        var raw = UsableTitle(handler.Raw);
        if (raw != null) return raw;
        var trimmed = (handler.Raw ?? "").Trim();
        if (KeptTitle(trimmed) && !MenuSentence(trimmed)) return trimmed;
        return "";
    }

    public static string FoldMenu(string? text) => AsciiLower(StripAccel(text ?? ""));

    public static string MenuLabel(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var cleaned = CleanLabel(text);
        var tab = cleaned.IndexOf('\t');
        if (tab >= 0) cleaned = cleaned[..tab];
        cleaned = cleaned.Trim();
        cleaned = GeneralizeFilenames(cleaned);
        cleaned = StripShortcutSuffix(cleaned);
        return LabelIsPlaceholder(cleaned) ? "" : cleaned;
    }

    public static string MenuMatchKey(string? text)
    {
        var label = MenuLabel(text);
        if (label.Length == 0) return "";
        return FoldMenu(label.Replace("...", "\u2026", StringComparison.Ordinal));
    }

    public static string? KnownProduct(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var tokens = TokensOf(text);
        if (tokens.All.Count == 0 && !text.Contains("한컴", StringComparison.Ordinal)) return null;
        if (tokens.All.Contains("hwp") || text.Contains("한컴", StringComparison.Ordinal))
            return "한글(Hwp)";
        string[] office = { "excel", "winword", "powerpnt", "powerpoint", "onenote", "msaccess", "mspub", "outlook", "msword" };
        var officeWord = tokens.All.Exists(w => office.Contains(w)) || tokens.Plain.Contains("word");
        var microsoftOffice = tokens.Plain.Contains("microsoft") && (tokens.Plain.Contains("office") || officeWord);
        if (officeWord || microsoftOffice) return "Microsoft Office";
        var scriptHost = tokens.All.Contains("wscript") || tokens.All.Contains("cscript")
            || (tokens.Plain.Contains("windows") && tokens.Plain.Contains("script") && tokens.Plain.Contains("host"));
        if (scriptHost) return "Windows Script Host";
        var compact = NormComponent(text);
        (string[] Keys, string Label)[] vendors =
        {
            (new[] { "bdzshell", "aabdzctx", "bandizip" }, "Bandizip"),
            (new[] { "lhshellext", "lockhunter" }, "LockHunter"),
            (new[] { "synofoldercontextmenu", "synologydrive" }, "Synology Drive"),
            (new[] { "hvshell", "hvcontext", "honeyview" }, "Honeyview"),
            (new[] { "tortoisesvn" }, "TortoiseSVN"),
            (new[] { "tortoisegit" }, "TortoiseGit"),
            (new[] { "7zip", "sevenzip" }, "7-Zip"),
            (new[] { "winrar" }, "WinRAR"),
            (new[] { "winmerge" }, "WinMerge"),
        };
        foreach (var (keys, label) in vendors)
        {
            var word = tokens.All.Exists(w => keys.Contains(w));
            var packed = compact.Length >= 4 && keys.Any(k => compact == k || (compact.StartsWith(k, StringComparison.Ordinal) && compact.Length - k.Length <= 3));
            if (word || packed) return label;
        }
        return null;
    }

    public static bool BuiltinVerb(string? name)
    {
        var n = AsciiLower((name ?? "").Trim().Trim('&'));
        if (n.StartsWith("windows.", StringComparison.Ordinal)) return true;
        return n is "open" or "opennew" or "openas" or "openwith" or "openwithprompt" or "opencontaining"
            or "opennewprocess" or "opennewwindow" or "explore" or "find" or "edit" or "print" or "printto"
            or "preview" or "runas" or "runasuser" or "cut" or "copy" or "paste" or "delete" or "link"
            or "rename" or "properties" or "pintohome" or "unpinfromhome" or "pintostartscreen"
            or "pintotaskbar" or "unpinfromtaskbar" or "cmd" or "powershell" or "pwsh" or "wsl" or "bash"
            or "sharing" or "share" or "previousversions" or "copyaspath" or "format" or "eject"
            or "disconnect" or "connect" or "manage" or "modernshare" or "updateencryptionsettings"
            or "encryptionmenu" or "windowsdefender" or "playto" or "casttodevice" or "giveaccessto"
            or "includeinlibrary" or "new";
    }

    public static bool IsBuiltinHandlerKey(string? name)
    {
        var n = AsciiLower((name ?? "").Trim());
        if (ParseGuid(n) != null && BuiltinClsid(ParseGuid(n)!)) return true;
        if (n is "open with" or "openwith" or "send to" or "sendto" or "sharing" or "share"
            or "previous versions" or "previousversions" or "offline files" or "offlinefiles"
            or "compatibility" or "encryption" or "encrypt" or "new" or "work folders" or "workfolders"
            or "windows defender" or "windowsdefender" or "include in library" or "library location"
            or "play to" or "playto" or "cast to device" or "pin to start" or "pintostart"
            or "pin to taskbar" or "pintotaskbar" or "unpin from taskbar" or "give access to"
            or "restore previous versions" or "extract all" or "compressedfolder" or "briefcasemenu"
            or "modern sharing" or "modernsharing" or "efs" or "offline files menu")
            return true;
        return BuiltinVerb(n.Replace(" ", "", StringComparison.Ordinal));
    }

    public static bool BuiltinClsid(string guidText)
    {
        var text = (guidText ?? "").Trim().Trim('{', '}').ToLowerInvariant();
        return text is "7ba4c740-9e81-11cf-99d3-00aa004ae837"
            or "09799afb-ad67-11d1-abcd-00c04fc30936"
            or "d969a300-e7ff-11d0-a93b-00a0c90f2719"
            or "f81e9010-6ea4-11ce-a7ff-00aa003ca9f6"
            or "596ab062-b4d2-4215-9f74-e9109b0a8153"
            or "e88dcce0-b7b3-11d1-a9f0-00aa0060fa31"
            or "b8cdcb65-b1bf-4b42-9428-1dfdb7ee92af"
            or "a2a9545d-a0c2-42b4-9708-a0b2badd77c8"
            or "90aa3a4e-1cba-4233-b8bb-535773d48449"
            or "470c0ebd-5d73-4d58-9ced-e91e22e23282"
            or "3dad6c5d-2167-4cae-9914-f99e41c12cfa"
            or "09a47860-11b0-4da5-afa5-26d86198a780"
            or "4a7ded0a-ad25-11d0-98a8-0800361b1103"
            or "e2bf9676-5f8f-435c-9bda-ad7b2c0c3c9a"
            or "474c98ee-cf3d-41f5-80e3-4aab0ab04301"
            or "a470f8cf-a1e8-4f65-8335-227475aa5c46"
            or "7ad84985-87b4-4a16-be58-8b72a5b390f7"
            or "b41db860-64e4-11d2-9906-00004b3083ff"
            or "888dca60-fc0a-11cf-8f0f-00c04fd7d062";
    }

    public static bool BuiltinComponentName(string? name)
    {
        var n = FoldMenu(name);
        if (n.Contains("windows photo viewer", StringComparison.Ordinal) || (n.Contains("photo", StringComparison.Ordinal) && n.Contains("image verbs", StringComparison.Ordinal)))
            return true;
        if (n.Contains("ole db", StringComparison.Ordinal) && (n.Contains("microsoft", StringComparison.Ordinal) || n.Contains("data link", StringComparison.Ordinal)))
            return true;
        return false;
    }

    public static bool IsBuiltinCom(string? keyName, string? guidText, string? dll, string? company)
    {
        if (BuiltinClsid(guidText ?? "") || IsBuiltinHandlerKey(keyName)) return true;
        return InboxDll(dll, company);
    }

    public static bool IsBuiltinStaticVerb(string? key, string? command, string? delegateDll)
    {
        if (BuiltinVerb(key)) return true;
        if (InboxDll(delegateDll, "")) return true;
        return !string.IsNullOrEmpty(command) && CommandInbox(command!);
    }

    public static bool InboxDll(string? dll, string? company)
    {
        if (string.IsNullOrWhiteSpace(dll)) return false;
        var path = dll.Contains('%', StringComparison.Ordinal) ? Environment.ExpandEnvironmentVariables(dll) : dll;
        var microsoft = (company ?? "").Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        if (UnderWindows(path))
            return string.IsNullOrEmpty(company) || microsoft;
        if (microsoft)
        {
            var p = path.Replace('/', '\\').ToLowerInvariant();
            if (p.Contains("\\ole db\\", StringComparison.Ordinal) || p.Contains("photoviewer", StringComparison.Ordinal)
                || p.Contains("msdasc", StringComparison.Ordinal) || p.Contains("shimgvw", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool UnderWindows(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var expanded = path.Contains('%', StringComparison.Ordinal) ? Environment.ExpandEnvironmentVariables(path) : path;
        var p = expanded.Trim().Trim('"').Replace('/', '\\').ToLowerInvariant();
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        return p.Contains("\\windows\\", StringComparison.Ordinal);
    }

    public static bool CommandInbox(string command)
    {
        if (ForeignPath(command.ToLowerInvariant())) return false;
        var exe = SplitCommand(command).Exe.ToLowerInvariant();
        var file = exe.Replace('/', '\\');
        var slash = file.LastIndexOf('\\');
        if (slash >= 0) file = file[(slash + 1)..];
        return UnderWindows(exe) || file is "cmd.exe" or "powershell.exe" or "pwsh.exe" or "wt.exe"
            or "wscript.exe" or "cscript.exe" or "rundll32.exe" or "explorer.exe"
            or "cmd" or "powershell" or "wt";
    }

    public static string FriendlyName(string? product, string? desc, params string?[] fallbacks)
    {
        foreach (var source in new[] { product, desc })
        {
            var mapped = MappedTitle(source);
            if (mapped != null) return mapped;
        }
        if (fallbacks != null)
        {
            foreach (var source in fallbacks)
            {
                var known = KnownProduct(source);
                if (known != null) return known;
            }
        }
        var picked = PolishName(PickLabel(fallbacks ?? Array.Empty<string?>()));
        var knownPicked = KnownProduct(picked);
        if (knownPicked != null) return knownPicked;
        if (ProductLike(picked) && !MenuSentence(picked) && !TitleJunk(picked)) return picked;
        return "";
    }

    public static string VerbProduct(string? product, string? desc, string? stem, string? menu, string? verb, string? owner)
    {
        var stemText = TitleJunk(stem) ? "" : (stem ?? "");
        var title = FriendlyName(product, desc, stemText);
        if (TitleJunk(title) || MenuSentence(title)) title = "";
        if (title.Length == 0)
            title = KnownProduct(menu) ?? KnownProduct(verb) ?? "";
        if (title.Length == 0)
        {
            var ownerName = PolishName(owner);
            if (KeptTitle(ownerName) && !MenuSentence(ownerName))
                title = KnownProduct(ownerName) ?? ownerName;
        }
        if (title.Length == 0)
            title = ReadableFallback(menu, verb);
        return TitleJunk(title) ? "" : title;
    }

    public static string PolishName(string? text)
    {
        var current = NormalizeMarks(CleanLabel(text ?? ""));
        while (true)
        {
            var next = NormalizeMarks(StripNameTag(StripPossessive(StripNameSuffix(current))));
            if (next == current) return current;
            current = next;
        }
    }

    public static string CleanLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (text.TrimStart().StartsWith('@')) return "";
        return StripAccel(text);
    }

    public static string FileStem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var expanded = path.Contains('%', StringComparison.Ordinal) ? Environment.ExpandEnvironmentVariables(path) : path;
        var file = expanded.Trim().Trim('"');
        var slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
        if (slash >= 0) file = file[(slash + 1)..];
        var low = file.ToLowerInvariant();
        if (low.EndsWith(".dll", StringComparison.Ordinal) || low.EndsWith(".exe", StringComparison.Ordinal))
            return file[..^4];
        return file;
    }

    public static string VersionTarget(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var expanded = command.Contains('%', StringComparison.Ordinal) ? Environment.ExpandEnvironmentVariables(command) : command;
        var (exe, args) = SplitCommand(expanded);
        var file = exe;
        var slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
        if (slash >= 0) file = file[(slash + 1)..];
        if (file.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            var dll = args.Trim().Trim('"').Split(',')[0].Trim().Trim('"');
            return RealModule(dll) ? dll : "";
        }
        return RealModule(exe) ? exe.Trim().Trim('"') : "";
    }

    public static string? ParseGuid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim().Trim('{', '}');
        var hex = new string(t.Where(c => c != '-').ToArray());
        if (hex.Length != 32 || !hex.All(Uri.IsHexDigit)) return null;
        var dashed = $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}".ToLowerInvariant();
        return Guid.TryParse(dashed, out var guid) ? guid.ToString("D") : null;
    }

    public static int Specificity(string? className)
    {
        var c = (className ?? "").ToLowerInvariant();
        if (c.StartsWith('.') || c.StartsWith(@"systemfileassociations\")) return 5;
        if (c == "drive" || c.EndsWith(@"\background")) return 4;
        if (c == "directory" || c == "folder") return 3;
        if (c == "allfilesystemobjects") return 2;
        if (c == "*") return 1;
        return 4;
    }

    public static bool MenuSentence(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return false;
        if (StripOneShortcut(t) != null || t.EndsWith("...", StringComparison.Ordinal) || t.EndsWith('\u2026'))
            return true;
        var low = FoldMenu(t);
        if (low is "configure" or "open" or "print" or "edit" or "explore" or "find" or "run" or "delete"
            or "rename" or "properties" or "share" or "copy" or "paste")
            return true;
        if (low.StartsWith("open in ", StringComparison.Ordinal) || low.StartsWith("open with ", StringComparison.Ordinal)
            || low.StartsWith("print ", StringComparison.Ordinal) || low.StartsWith("add to ", StringComparison.Ordinal)
            || low.StartsWith("configure ", StringComparison.Ordinal))
            return true;
        return t.Contains("で開く", StringComparison.Ordinal) || t.Contains("を開く", StringComparison.Ordinal) || t.Contains("인쇄", StringComparison.Ordinal);
    }

    public static bool IdentifierLike(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || ParseGuid(t) != null) return true;
        var low = AsciiLower(t);
        if (low.EndsWith(".dll", StringComparison.Ordinal) || low.EndsWith(".exe", StringComparison.Ordinal)) return true;
        if (low.EndsWith(" class", StringComparison.Ordinal) || low.EndsWith(" context menu", StringComparison.Ordinal) || low.EndsWith(" module", StringComparison.Ordinal))
            return true;
        if (t.Any(char.IsWhiteSpace)) return false;
        var shellish = low.Contains("shell", StringComparison.Ordinal) || low.Contains("ctx", StringComparison.Ordinal)
            || low.Contains("context", StringComparison.Ordinal) || low.EndsWith("ext", StringComparison.Ordinal) || low.EndsWith("ex", StringComparison.Ordinal);
        var mixed = t.Any(char.IsAsciiLetterUpper) && t.Any(char.IsAsciiLetterLower);
        return shellish && mixed;
    }

    public static bool TitleJunk(string? text)
    {
        var t = (text ?? "").Trim().Trim('"').Trim();
        if (t.Length == 0) return false;
        return ContainsPlaceholder(t) || LooksLikeCommand(t);
    }

    public static bool KeptTitle(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length > 0 && !TitleJunk(t) && !IdentifierLike(t) && ParseGuid(t) == null;
    }

    public static string ComponentRaw(string? keyName, string? fallback)
    {
        foreach (var source in new[] { keyName, fallback })
        {
            if (ParseGuid(source) != null) continue;
            var t = StripNameTag(CleanLabel(source ?? ""));
            if (t.Length == 0 || ParseGuid(t) != null) continue;
            return t;
        }
        return "";
    }

    public static string VerbRaw(string? verbKey, string? stem)
    {
        var key = ComponentRaw(verbKey, "");
        var stemRaw = ComponentRaw(stem, "");
        var stemOk = KeptTitle(stemRaw) && !MenuSentence(stemRaw);
        var keyOk = KeptTitle(key) && !MenuSentence(key);
        if (!keyOk && stemOk) return stemRaw;
        return keyOk ? key : "";
    }

    public static bool PushUnique(List<string> slot, IEnumerable<string>? incoming)
    {
        if (slot == null || incoming == null) return false;
        var added = false;
        foreach (var raw in incoming)
        {
            var label = MenuLabel(raw);
            if (label.Length == 0) continue;
            var folded = FoldMenu(label);
            if (slot.Exists(e => FoldMenu(e) == folded)) continue;
            slot.Add(label);
            added = true;
            if (slot.Count >= 8) break;
        }
        return added;
    }

    static bool Listed(ShellHandler handler, IReadOnlyDictionary<string, List<string>>? learned)
    {
        if (handler.Builtin || handler.Targets == 0 || string.IsNullOrEmpty(handler.Id)) return false;
        if (learned != null && learned.TryGetValue(handler.Id, out var seen) && seen.Count > 0) return true;
        if (ShellDormant(handler)) return false;
        return true;
    }

    static bool ShellDormant(ShellHandler handler)
    {
        var title = TitleOf(handler);
        return GenericWindows(title) || GenericWindows(handler.Product) || GenericWindows(handler.Name)
            || ScreenSaver(title) || ScreenSaver(handler.Product) || ScreenSaver(handler.Name)
            || ScreenSaver(handler.Raw) || ScreenSaver(handler.Verb);
    }

    static bool GenericWindows(string? text)
    {
        var n = FoldMenu(PolishName(text));
        return n.Contains("windows", StringComparison.Ordinal) && n.Contains("operating system", StringComparison.Ordinal);
    }

    static bool ScreenSaver(string? text)
    {
        var folded = FoldMenu(PolishName(text));
        var n = new string(folded.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return n == "screensaver" || n.Contains("スクリーンセーバー", StringComparison.Ordinal) || n.Contains("화면보호기", StringComparison.Ordinal);
    }

    static (ShellTick Tick, ShellTick[] Subs) GroupTicks(IReadOnlyDictionary<string, List<string>>? disabled, List<string> ids, List<byte> scopes)
    {
        var subs = new[] { ShellTick.On, ShellTick.On, ShellTick.On, ShellTick.On };
        for (var i = 0; i < ShellMenuScope.Bits.Length; i++)
        {
            var bit = ShellMenuScope.Bits[i];
            var relevant = 0;
            var offs = 0;
            for (var n = 0; n < ids.Count; n++)
            {
                if ((scopes[n] & bit) == 0) continue;
                relevant++;
                var cur = disabled != null && disabled.TryGetValue(ids[n], out var names) ? ShellMenuScope.BitsOf(names) : (byte)0;
                if ((cur & bit) != 0) offs++;
            }
            subs[i] = relevant == 0 || offs == 0 ? ShellTick.On : offs == relevant ? ShellTick.Off : ShellTick.Mixed;
        }
        var states = new List<ShellTick>();
        for (var n = 0; n < ids.Count; n++)
        {
            if (scopes[n] == 0) continue;
            var cur = disabled != null && disabled.TryGetValue(ids[n], out var names) ? ShellMenuScope.BitsOf(names) : (byte)0;
            states.Add(TickOf(cur, scopes[n]));
        }
        var tick = states.Count == 0 || states.TrueForAll(t => t == ShellTick.On)
            ? ShellTick.On
            : states.TrueForAll(t => t == ShellTick.Off) ? ShellTick.Off : ShellTick.Mixed;
        return (tick, subs);
    }

    static ShellTick TickOf(byte off, byte attached)
    {
        off = (byte)(off & attached);
        if (attached == 0 || off == 0) return ShellTick.On;
        return off == attached ? ShellTick.Off : ShellTick.Mixed;
    }

    sealed class Presented
    {
        public string Name = "";
        public string Raw = "";
        public List<string> Labels = new();
    }

    static Presented Present(ShellHandler handler, IReadOnlyDictionary<string, List<string>>? learned)
    {
        var name = TitleOf(handler);
        var labels = new List<string>();
        PushUnique(labels, handler.StaticLabels);
        if (!string.IsNullOrEmpty(handler.Verb))
            PushUnique(labels, new[] { handler.Name });
        if (learned != null && learned.TryGetValue(handler.Id, out var extra))
            PushUnique(labels, extra);
        var shown = labels.Where(l => name.Length == 0 || FoldMenu(l) != FoldMenu(name)).ToList();
        return new Presented { Name = name, Raw = DisplayRaw(name, handler.Raw, shown), Labels = shown };
    }

    static string DisplayRaw(string title, string? raw, List<string> labels)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return "";
        var folded = FoldMenu(raw);
        if (folded == FoldMenu(title) || labels.Exists(l => FoldMenu(l) == folded)) return "";
        return raw;
    }

    static string? GroupKey(string? title)
    {
        var t = (title ?? "").Trim();
        return t.Length == 0 ? null : FoldMenu(t);
    }

    static string SharedRaw(string title, List<string> raws, List<string> labels)
    {
        if (raws.Count == 0) return "";
        var folded = FoldMenu(raws[0]);
        if (raws.Exists(raw => FoldMenu(raw) != folded)) return "";
        return DisplayRaw(title, raws[0], labels);
    }

    static bool HitsQuery(Acc acc, List<ShellHandler> rows, IReadOnlyDictionary<string, List<string>>? learned, string q)
    {
        if (q.Length == 0) return true;
        if (acc.Title.ToLowerInvariant().Contains(q, StringComparison.Ordinal)) return true;
        if (acc.Labels.Exists(l => l.ToLowerInvariant().Contains(q, StringComparison.Ordinal))) return true;
        if (acc.Raws.Exists(r => r.ToLowerInvariant().Contains(q, StringComparison.Ordinal))) return true;
        foreach (var handler in rows)
        {
            if (!acc.Ids.Contains(handler.Id)) continue;
            if (TitleOf(handler).ToLowerInvariant().Contains(q, StringComparison.Ordinal)) return true;
            if ((handler.Raw ?? "").ToLowerInvariant().Contains(q, StringComparison.Ordinal)) return true;
            if (handler.StaticLabels.Exists(l => l.ToLowerInvariant().Contains(q, StringComparison.Ordinal))) return true;
            if (learned != null && learned.TryGetValue(handler.Id, out var extra)
                && extra.Exists(l => l.ToLowerInvariant().Contains(q, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    sealed class Acc
    {
        public string Key = "";
        public string Title = "";
        public List<string> Ids = new();
        public List<byte> Scopes = new();
        public byte Targets;
        public List<string> Labels = new();
        public List<string> Raws = new();
    }

    static bool ContainsAsWord(string? text, string? title, int minLen)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(title)) return false;
        if (MenuSentence(title)) return false;
        var foldedTitle = FoldMenu(PolishName(title));
        if (foldedTitle.Length < minLen) return false;
        var foldedText = FoldMenu(text);
        var cjk = false;
        foreach (var c in foldedTitle)
        {
            if (c > 127) { cjk = true; break; }
        }
        if (cjk) return foldedText.Contains(foldedTitle, StringComparison.Ordinal);
        var index = 0;
        while ((index = foldedText.IndexOf(foldedTitle, index, StringComparison.Ordinal)) >= 0)
        {
            var left = index == 0 || !IsAsciiWord(foldedText[index - 1]);
            var end = index + foldedTitle.Length;
            var right = end >= foldedText.Length || !IsAsciiWord(foldedText[end]);
            if (left && right) return true;
            index++;
        }
        return false;
    }

    static bool IsAsciiWord(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';

    static string? UsableTitle(string? text)
    {
        var t = (text ?? "").Trim();
        if (!KeptTitle(t) || MenuSentence(t)) return null;
        return t;
    }

    static bool BetterTitle(string? have, string? incoming)
    {
        if (!KeptTitle(incoming)) return false;
        if (!KeptTitle(have)) return true;
        var haveMenu = MenuSentence(have);
        var incomingMenu = MenuSentence(incoming);
        if (haveMenu && !incomingMenu) return true;
        if (!haveMenu && incomingMenu) return false;
        return ParseGuid(have) != null || (IdentifierLike(have) && !IdentifierLike(incoming));
    }

    static string ReadableFallback(string? menu, string? verb)
    {
        var menuText = PolishName(menu);
        if (KeptTitle(menuText)) return KnownProduct(menuText) ?? menuText;
        var verbText = PolishName(verb);
        if (KeptTitle(verbText) && !MenuSentence(verbText)) return KnownProduct(verbText) ?? verbText;
        return "";
    }

    static string? MappedTitle(string? text)
    {
        if (TitleJunk(text)) return null;
        var polished = PolishName(text);
        if (TitleJunk(polished)) return null;
        var known = KnownProduct(polished) ?? KnownProduct(text);
        if (known != null) return known;
        if (ProductLike(polished) && !MenuSentence(polished)) return polished;
        return null;
    }

    static bool ProductLike(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length >= 2 && !IdentifierLike(t);
    }

    static string PickLabel(IEnumerable<string?> candidates)
    {
        var cleaned = new List<string>();
        foreach (var source in candidates)
        {
            var t = CleanLabel(source);
            if (t.Length == 0 || ParseGuid(t) != null || TitleJunk(t)) continue;
            cleaned.Add(t);
        }
        var readable = cleaned.Find(s => !IdentifierLike(s));
        return readable ?? (cleaned.Count > 0 ? cleaned[0] : "");
    }

    static string StripAccel(string text)
    {
        var chars = text.ToCharArray();
        var output = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '&')
            {
                if (i + 1 < chars.Length && chars[i + 1] == '&')
                {
                    output.Append('&');
                    i++;
                }
                continue;
            }
            output.Append(chars[i]);
        }
        return output.ToString().Trim();
    }

    static string StripNameSuffix(string text)
    {
        var t = text.Trim();
        var low = AsciiLower(t);
        string[] suffixes = { "shell menu dll", "explorer extension library", "shell integration library", "shell extension", "context menu", "shell ext" };
        foreach (var suffix in suffixes)
        {
            if (low == suffix) return "";
            var spaced = " " + suffix;
            if (low.EndsWith(spaced, StringComparison.Ordinal))
                return t[..^spaced.Length].Trim();
        }
        return t;
    }

    static string StripPossessive(string text)
    {
        var t = text.Trim();
        if (t.Length >= 3)
        {
            var last = t[^1];
            var mark = t[^2];
            if ((last == 's' || last == 'S') && mark is '\'' or '\u2019' or '\uFF07')
            {
                var stem = t[..^2].Trim();
                if (stem.Length >= 2) return stem;
            }
        }
        return t;
    }

    static string StripNameTag(string text)
    {
        var t = text.Trim();
        if (t.Length < 3 || (t[^1] != ')' && t[^1] != '\uFF09')) return t;
        var open = t.LastIndexOfAny(new[] { '(', '\uFF08' });
        if (open <= 0) return t;
        var inner = t[(open + 1)..^1].Trim();
        var folded = new string(inner.Select(FoldWidth).ToArray());
        var low = AsciiLower(folded);
        var tag = (folded.Length == 1 && folded.All(char.IsAsciiLetter)) || low is "x86" or "x64" or "32" or "64";
        if (!tag) return t;
        var before = t[..open].Trim();
        return before.Length == 0 ? t : before;
    }

    static string NormalizeMarks(string text)
    {
        var chars = text.ToCharArray();
        var raw = new System.Text.StringBuilder(chars.Length);
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '\u00AE' or '\u2122' or '\u00A9')
            {
                var prev = i > 0 && char.IsLetterOrDigit(chars[i - 1]);
                var next = i + 1 < chars.Length && char.IsLetterOrDigit(chars[i + 1]);
                if (prev && next) raw.Append(' ');
                continue;
            }
            raw.Append(chars[i]);
        }
        return CollapseWs(raw.ToString());
    }

    static string CollapseWs(string text)
    {
        var output = new System.Text.StringBuilder(text.Length);
        var gap = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (output.Length > 0) gap = true;
                continue;
            }
            if (gap)
            {
                output.Append(' ');
                gap = false;
            }
            output.Append(c);
        }
        return output.ToString();
    }

    static string StripShortcutSuffix(string text)
    {
        var current = text.Trim();
        while (true)
        {
            var next = StripOneShortcut(current);
            if (next == null) return current;
            current = next;
        }
    }

    static string? StripOneShortcut(string text)
    {
        var t = text.TrimEnd();
        var (core, ellipsis) = SplitEllipsis(t);
        core = core.TrimEnd();
        if (core.Length == 0) return null;
        var close = core[^1];
        if (close != ')' && close != '\uFF09') return null;
        var open = core.LastIndexOfAny(new[] { '(', '\uFF08' });
        if (open <= 0) return null;
        var inner = core[(open + 1)..^1];
        if (!ShortcutToken(inner.Trim())) return null;
        var before = core[..open].TrimEnd();
        if (before.Length == 0) return null;
        var output = before + ellipsis;
        return output == t ? null : output;
    }

    static (string Core, string Ellipsis) SplitEllipsis(string text)
    {
        if (text.EndsWith('\u2026')) return (text[..^1], "\u2026");
        if (text.EndsWith("...", StringComparison.Ordinal)) return (text[..^3], "...");
        return (text, "");
    }

    static bool ShortcutToken(string text)
    {
        var folded = new string(text.Trim().Select(FoldWidth).ToArray());
        if (folded.Length == 1 && char.IsAsciiLetterOrDigit(folded[0])) return true;
        var low = AsciiLower(folded).Replace(" ", "", StringComparison.Ordinal);
        var modified = (low.StartsWith("ctrl", StringComparison.Ordinal) || low.StartsWith("control", StringComparison.Ordinal)
            || low.StartsWith("shift", StringComparison.Ordinal) || low.StartsWith("alt", StringComparison.Ordinal)
            || low.StartsWith("win", StringComparison.Ordinal)) && low.Contains('+', StringComparison.Ordinal);
        return modified && low.All(c => char.IsAsciiLetterOrDigit(c) || c == '+') && low.Length <= 24;
    }

    static char FoldWidth(char c) => c switch
    {
        >= '\uFF10' and <= '\uFF19' => (char)(c - '\uFF10' + '0'),
        >= '\uFF21' and <= '\uFF3A' => (char)(c - '\uFF21' + 'A'),
        >= '\uFF41' and <= '\uFF5A' => (char)(c - '\uFF41' + 'a'),
        _ => c,
    };

    static string GeneralizeFilenames(string text)
    {
        var chars = text.ToCharArray();
        var output = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < chars.Length; i++)
        {
            var end = FileQuoteEnd(chars, i);
            if (end != null)
            {
                output.Append(chars[i]);
                output.Append('\u2026');
                output.Append(chars[end.Value]);
                i = end.Value;
                continue;
            }
            output.Append(chars[i]);
        }
        return output.ToString();
    }

    static int? FileQuoteEnd(char[] chars, int i)
    {
        var close = chars[i] switch
        {
            '"' => '"',
            '\uFF02' => '\uFF02',
            '「' => '」',
            '『' => '』',
            '\u201C' => '\u201D',
            '\'' => '\'',
            _ => '\0',
        };
        if (close == '\0') return null;
        var end = -1;
        for (var n = i + 1; n < chars.Length; n++)
        {
            if (chars[n] == close) { end = n; break; }
        }
        if (end < 0) return null;
        var inner = new string(chars, i + 1, end - i - 1);
        if (inner.Length is >= 1 and <= 120 && (inner.Contains('.') || inner.Contains('\\') || inner.Contains('/')))
            return end;
        return null;
    }

    static bool LabelIsPlaceholder(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return true;
        foreach (var c in t)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (c is '"' or '\uFF02' or '「' or '」' or '『' or '』' or '\u201C' or '\u201D' or '\'' or '\u2019' or '\u2026' or '.')
                continue;
            return false;
        }
        return true;
    }

    static bool ContainsPlaceholder(string text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] != '%') continue;
            var n = char.ToLowerInvariant(text[i + 1]);
            if (n is '*' or 'd' or 'i' or 'l' or 'u' or 'v' or 'w' || char.IsDigit(text[i + 1]))
                return true;
        }
        return false;
    }

    static bool LooksLikeCommand(string text)
    {
        if (text.Contains('\\') || text.Contains('/')) return true;
        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':') return true;
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var low = word.Trim('"').ToLowerInvariant();
            if (low.EndsWith(".exe", StringComparison.Ordinal) || low.EndsWith(".dll", StringComparison.Ordinal)
                || low.EndsWith(".ocx", StringComparison.Ordinal) || low.EndsWith(".cpl", StringComparison.Ordinal)
                || low.EndsWith(".bat", StringComparison.Ordinal) || low.EndsWith(".cmd", StringComparison.Ordinal)
                || low.EndsWith(".com", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    static bool ForeignPath(string low)
    {
        for (var i = 0; i + 2 < low.Length; i++)
        {
            if (!char.IsAsciiLetter(low[i]) || low[i + 1] != ':' || (low[i + 2] != '\\' && low[i + 2] != '/')) continue;
            var end = low.Length;
            for (var n = i; n < low.Length; n++)
            {
                if (low[n] is '"' or ' ' or '\t') { end = n; break; }
            }
            var span = low[i..end];
            if (!UnderWindows(span)) return true;
        }
        return false;
    }

    static (string Exe, string Args) SplitCommand(string command)
    {
        var t = command.Trim();
        if (t.Length == 0) return ("", "");
        if (t[0] == '"')
        {
            var end = t.IndexOf('"', 1);
            if (end > 0) return (t[1..end], end + 1 < t.Length ? t[(end + 1)..].Trim() : "");
        }
        var space = t.IndexOf(' ');
        if (space < 0) return (t, "");
        return (t[..space], t[(space + 1)..].Trim());
    }

    static bool RealModule(string text)
    {
        var t = text.Trim().Trim('"');
        if (t.Length == 0 || ContainsPlaceholder(t)) return false;
        var low = t.ToLowerInvariant();
        return low.EndsWith(".exe", StringComparison.Ordinal) || low.EndsWith(".dll", StringComparison.Ordinal)
            || low.EndsWith(".ocx", StringComparison.Ordinal) || low.EndsWith(".cpl", StringComparison.Ordinal);
    }

    static string NormComponent(string text)
    {
        var t = AsciiLower(StripNameTag(CleanLabel(text)));
        string[] suffixes = { " context menu", " contextmenu", " class", " module", " shell extension", " shellextension" };
        foreach (var suffix in suffixes)
        {
            if (t.EndsWith(suffix, StringComparison.Ordinal))
            {
                t = t[..^suffix.Length].Trim();
                break;
            }
        }
        return new string(t.Where(char.IsAsciiLetterOrDigit).ToArray());
    }

    sealed class Tokens
    {
        public List<string> All = new();
        public List<string> Plain = new();
    }

    static Tokens TokensOf(string text)
    {
        var tokens = new Tokens();
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            if (current.Length == 0) return;
            var word = new string(current.ToString().Select(FoldWidth).ToArray());
            current.Clear();
            var low = AsciiLower(word);
            PushToken(tokens.Plain, low);
            PushToken(tokens.All, low);
            var part = new System.Text.StringBuilder();
            var chars = word.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var prev = i > 0 ? chars[i - 1] : '\0';
                if (i > 0 && char.IsAsciiLetterLower(prev) && char.IsAsciiLetterUpper(chars[i]) && part.Length > 0)
                {
                    PushToken(tokens.All, AsciiLower(part.ToString()));
                    part.Clear();
                }
                part.Append(char.ToLowerInvariant(chars[i]));
            }
            if (part.Length > 0) PushToken(tokens.All, part.ToString());
        }
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) current.Append(c);
            else Flush();
        }
        Flush();
        return tokens;
    }

    static void PushToken(List<string> output, string word)
    {
        if (word.Length > 0 && !output.Contains(word)) output.Add(word);
    }

    static string AsciiLower(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + 32);
        }
        return new string(chars);
    }

    static List<ShellMenuItem> CollapseSeparators(List<ShellMenuItem> items)
    {
        var list = new List<ShellMenuItem>(items.Count);
        foreach (var item in items)
        {
            if (item.IsSeparator && (list.Count == 0 || list[^1].IsSeparator)) continue;
            list.Add(item);
        }
        while (list.Count > 0 && list[^1].IsSeparator)
            list.RemoveAt(list.Count - 1);
        return list;
    }

    static ShellMenuItem CloneMenu(ShellMenuItem src, List<ShellMenuItem> children) => new()
    {
        Text = src.Text,
        CommandId = src.CommandId,
        Verb = src.Verb,
        IsSeparator = src.IsSeparator,
        IsDisabled = src.IsDisabled,
        IsOwnerDrawn = src.IsOwnerDrawn,
        Accelerator = src.Accelerator,
        IconPixels = src.IconPixels,
        IconWidth = src.IconWidth,
        IconHeight = src.IconHeight,
        Children = children,
    };
}
