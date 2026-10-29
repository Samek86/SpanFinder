using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Span.Helpers;

namespace Span.Services;

/// <summary>
/// インストール済みのコンテキストメニューをレジストリから読む。書き込まない。
/// HKCR / HKCU\Software\Classes / HKLM\Software\Classes / Wow6432Node。
/// 固定クラス（*、Directory、Folder、Drive、Background）と拡張子・ProgID・SystemFileAssociations。
/// COM の中身を CoCreate しない。キャプションは MuiVerb と、あとから覚えた文。
///
/// Windows でアプリ名を出す: 環境変数 SPAN_DUMP_CTX=1 を付けて起動し、
/// 右クリックメニュー項目のページを開く。デバッグログに [ShellExt] 行が出る。
/// </summary>
public static class ShellExtensionRegistry
{
    const int ClassCap = 8000;
    const int HandlerCap = 512;
    const int VerbCap = 512;
    const int CascadeCap = 64;

    static readonly object Gate = new();
    static List<ShellHandler>? _cache;
    static int _scanStarted;

    static readonly (string Rel, byte Bits)[] Fixed =
    {
        ("*", ShellMenuScope.File),
        ("AllFilesystemObjects", (byte)(ShellMenuScope.File | ShellMenuScope.Folder | ShellMenuScope.Drive)),
        ("Directory", (byte)(ShellMenuScope.Folder | ShellMenuScope.Drive)),
        (@"Directory\Background", ShellMenuScope.Background),
        ("Folder", (byte)(ShellMenuScope.Folder | ShellMenuScope.Drive)),
        (@"Folder\Background", ShellMenuScope.Background),
        ("Drive", ShellMenuScope.Drive),
    };

    public static bool IsReady
    {
        get { lock (Gate) return _cache != null; }
    }

    public static IReadOnlyList<ShellHandler>? TryGetCached()
    {
        lock (Gate) return _cache;
    }

    public static void StartScanInBackground()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Interlocked.Exchange(ref _scanStarted, 1) != 0) return;
        Task.Run(() =>
        {
            try { GetOrScan(); }
            catch (Exception ex)
            {
                try { DebugLogger.Log($"[ShellExt] background scan failed: {ex.GetType().Name}: {ex.Message}"); } catch { }
            }
        });
    }

    public static IReadOnlyList<ShellHandler> GetOrScan()
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<ShellHandler>();
        lock (Gate)
        {
            if (_cache != null) return _cache;
            try
            {
                var started = Stopwatch.StartNew();
                _cache = ScanAll();
                try { DebugLogger.Log($"[ShellExt] scan {_cache.Count} handlers in {started.ElapsedMilliseconds} ms"); } catch { }
                DumpIfRequested(_cache);
            }
            catch (Exception ex)
            {
                try { DebugLogger.Log($"[ShellExt] scan failed: {ex.GetType().Name}: {ex.Message}"); } catch { }
                _cache = new List<ShellHandler>();
            }
            return _cache;
        }
    }

    static void DumpIfRequested(List<ShellHandler> handlers)
    {
        if (Environment.GetEnvironmentVariable("SPAN_DUMP_CTX") != "1") return;
        var rows = ShellExtensionCatalogue.Assemble(handlers, null, null, "");
        foreach (var row in rows)
        {
            try
            {
                DebugLogger.Log($"[ShellExt] {row.Name} | {row.Detail} | {row.Raw} | targets={row.Targets} | ids={string.Join(",", row.Ids)}");
            }
            catch { }
        }
    }

    static List<ShellHandler> ScanAll()
    {
        var rows = new List<ShellHandler>();
        var dlls = new Dictionary<string, (string Desc, string Product, string Company)>(StringComparer.OrdinalIgnoreCase);
        var clsids = new Dictionary<string, (string Name, string Dll, string Company)>(StringComparer.OrdinalIgnoreCase);
        var approved = ReadApproved();
        foreach (var (key, prefix) in OpenRoots())
        {
            using (key)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (rel, bits) in Fixed)
                    ScanClass(key, prefix, rel, bits, rows, dlls, clsids, approved, seen);
                ScanExtensions(key, prefix, rows, dlls, clsids, approved, seen);
            }
        }
        return rows;
    }

    static List<(RegistryKey Key, string Prefix)> OpenRoots()
    {
        var list = new List<(RegistryKey Key, string Prefix)>();
        try
        {
            list.Add((RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64), ""));
            list.Add((RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64), @"Software\Classes"));
            list.Add((RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), @"Software\Classes"));
            list.Add((RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), @"Software\Classes"));
            list.Add((RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64), "Wow6432Node"));
            return list;
        }
        catch
        {
            foreach (var item in list) item.Key.Dispose();
            throw;
        }
    }

    static void ScanExtensions(
        RegistryKey root, string prefix, List<ShellHandler> rows,
        Dictionary<string, (string Desc, string Product, string Company)> dlls,
        Dictionary<string, (string Name, string Dll, string Company)> clsids,
        Dictionary<string, string> approved, HashSet<string> seen)
    {
        // prefix が空のときは HKCR 本体。ここで Dispose すると後続のクラス走査が壊れる。
        RegistryKey? owned = null;
        RegistryKey key;
        if (string.IsNullOrEmpty(prefix))
        {
            key = root;
        }
        else
        {
            owned = OpenAt(root, prefix);
            if (owned == null) return;
            key = owned;
        }
        using var lease = owned;
        string[] names;
        try { names = key.GetSubKeyNames(); }
        catch { return; }
        var count = Math.Min(names.Length, ClassCap);
        for (var i = 0; i < count; i++)
        {
            var name = names[i];
            if (name.StartsWith('.'))
            {
                ScanClass(root, prefix, name, ShellMenuScope.File, rows, dlls, clsids, approved, seen);
                var full = JoinReg(prefix, name);
                using var ext = OpenAt(root, full);
                if (ext != null)
                {
                    var prog = KeySz(ext, null);
                    var perceived = KeySz(ext, "PerceivedType");
                    if (ProgidLike(prog))
                        ScanClass(root, prefix, prog, ShellMenuScope.File, rows, dlls, clsids, approved, seen);
                    if (perceived.Length > 0 && !perceived.Contains('\\') && !perceived.Contains('/'))
                        ScanClass(root, prefix, $@"SystemFileAssociations\{perceived}", ShellMenuScope.File, rows, dlls, clsids, approved, seen);
                }
                ScanClass(root, prefix, $@"SystemFileAssociations\{name}", ShellMenuScope.File, rows, dlls, clsids, approved, seen);
            }
            else if (name.Equals("SystemFileAssociations", StringComparison.OrdinalIgnoreCase))
            {
                using var assoc = OpenAt(root, JoinReg(prefix, name));
                if (assoc == null) continue;
                string[] children;
                try { children = assoc.GetSubKeyNames(); }
                catch { continue; }
                var childCount = Math.Min(children.Length, 4000);
                for (var j = 0; j < childCount; j++)
                    ScanClass(root, prefix, $@"SystemFileAssociations\{children[j]}", ShellMenuScope.File, rows, dlls, clsids, approved, seen);
            }
        }
    }

    static void ScanClass(
        RegistryKey root, string prefix, string rel, byte targets, List<ShellHandler> rows,
        Dictionary<string, (string Desc, string Product, string Company)> dlls,
        Dictionary<string, (string Name, string Dll, string Company)> clsids,
        Dictionary<string, string> approved, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(rel)) return;
        if (!seen.Add(prefix + "|" + rel)) return;
        var full = JoinReg(prefix, rel);
        try
        {
            ScanHandlers(root, full, rel, targets, rows, dlls, clsids, approved);
            ScanVerbs(root, full, rel, targets, rows, dlls, clsids);
        }
        catch (Exception ex)
        {
            try { DebugLogger.Log($"[ShellExt] class '{rel}' skipped: {ex.GetType().Name}"); } catch { }
        }
    }

    static void ScanHandlers(
        RegistryKey root, string full, string rel, byte targets, List<ShellHandler> rows,
        Dictionary<string, (string Desc, string Product, string Company)> dlls,
        Dictionary<string, (string Name, string Dll, string Company)> clsids,
        Dictionary<string, string> approved)
    {
        using var key = OpenAt(root, full + @"\shellex\ContextMenuHandlers");
        if (key == null) return;
        string[] names;
        try { names = key.GetSubKeyNames(); }
        catch { return; }
        var count = Math.Min(names.Length, HandlerCap);
        for (var i = 0; i < count; i++)
        {
            var name = names[i];
            var value = "";
            using (var sub = OpenAt(root, full + @"\shellex\ContextMenuHandlers\" + name))
            {
                if (sub != null) value = KeySz(sub, null);
            }
            var guid = ShellExtensionCatalogue.ParseGuid(value) ?? ShellExtensionCatalogue.ParseGuid(name);
            if (guid == null) continue;
            var (legacy, dll, company) = ClsidMeta(guid, approved, dlls, clsids);
            var (desc, product, _) = string.IsNullOrEmpty(dll) ? ("", "", "") : FileVersion(dll, dlls);
            var stem = ShellExtensionCatalogue.FileStem(dll);
            var shown = ShellExtensionCatalogue.FriendlyName(product, desc, legacy, name, stem);
            var display = shown.Length == 0 ? legacy : shown;
            if ((display.Length == 0 || ShellExtensionCatalogue.IdentifierLike(display) || ShellExtensionCatalogue.MenuSentence(display))
                && ShellExtensionCatalogue.ParseGuid(name) == null
                && !ShellExtensionCatalogue.IdentifierLike(name)
                && !ShellExtensionCatalogue.MenuSentence(name))
            {
                display = ShellExtensionCatalogue.CleanLabel(name);
            }
            if (display.Length == 0)
                display = ShellExtensionCatalogue.ParseGuid(name) == null ? ShellExtensionCatalogue.CleanLabel(name) : guid;
            var builtin = ShellExtensionCatalogue.IsBuiltinCom(name, guid, dll, company)
                || ShellExtensionCatalogue.BuiltinComponentName(legacy)
                || ShellExtensionCatalogue.BuiltinComponentName(name);
            var productTitle = "";
            if (!ShellExtensionCatalogue.MenuSentence(display)
                && ProductLike(display)
                && (ShellExtensionCatalogue.IdentifierLike(legacy) || !legacy.Equals(display, StringComparison.OrdinalIgnoreCase)))
            {
                productTitle = display;
            }
            ShellExtensionCatalogue.Upsert(rows, new ShellHandler
            {
                Id = "clsid:" + guid,
                Name = display,
                Targets = targets,
                Builtin = builtin,
                Rank = ShellExtensionCatalogue.Specificity(rel),
                Anchor = rel,
                Product = productTitle,
                Raw = ShellExtensionCatalogue.ComponentRaw(name, legacy),
                Classes = new List<string> { rel },
            });
        }
    }

    static void ScanVerbs(
        RegistryKey root, string full, string rel, byte targets, List<ShellHandler> rows,
        Dictionary<string, (string Desc, string Product, string Company)> dlls,
        Dictionary<string, (string Name, string Dll, string Company)> clsids)
    {
        using var key = OpenAt(root, full + @"\shell");
        if (key == null) return;
        string[] names;
        try { names = key.GetSubKeyNames(); }
        catch { return; }
        var count = Math.Min(names.Length, VerbCap);
        for (var i = 0; i < count; i++)
        {
            var name = names[i];
            if (name.Equals("command", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ddeexec", StringComparison.OrdinalIgnoreCase)
                || name.Equals("droptarget", StringComparison.OrdinalIgnoreCase)
                || name.Equals("shellex", StringComparison.OrdinalIgnoreCase))
                continue;
            var sub = full + @"\shell\" + name;
            using var verbKey = OpenAt(root, sub);
            if (verbKey == null) continue;
            if (ValueExists(verbKey, "LegacyDisable") || ValueExists(verbKey, "ProgrammaticAccessOnly"))
                continue;
            var display = ResolveMaybeIndirect(KeySz(verbKey, null));
            var mui = ResolveMaybeIndirect(KeySz(verbKey, "MuiVerb"));
            var command = "";
            using (var commandKey = OpenAt(root, sub + @"\command"))
            {
                if (commandKey != null) command = KeySz(commandKey, null);
            }
            var delegateText = KeySz(verbKey, "DelegateExecute");
            var delegateGuid = ShellExtensionCatalogue.ParseGuid(delegateText);
            var delegateDll = "";
            if (delegateGuid != null)
                delegateDll = ClsidMeta(delegateGuid, EmptyApproved, dlls, clsids).Dll;
            if (command.Length == 0 && delegateGuid == null) continue;

            var friendlyRaw = display.Length == 0 ? name : display;
            var friendly = ShellExtensionCatalogue.CleanLabel(friendlyRaw);
            if (friendly.Length == 0) friendly = ShellExtensionCatalogue.CleanLabel(name);
            var builtin = ShellExtensionCatalogue.IsBuiltinStaticVerb(name, command, delegateDll)
                || ShellExtensionCatalogue.BuiltinComponentName(friendly);
            var target = command.Length == 0 ? delegateDll : ShellExtensionCatalogue.VersionTarget(command);
            var (desc, product, _) = string.IsNullOrEmpty(target) ? ("", "", "") : FileVersion(target, dlls);
            var stem = ShellExtensionCatalogue.FileStem(target);
            var owner = OwnerName(root, full, rel, dlls);
            var productTitle = ShellExtensionCatalogue.VerbProduct(product, desc, stem, friendly, name, owner);
            var labels = new List<string>();
            ShellExtensionCatalogue.PushUnique(labels, new[] { ShellExtensionCatalogue.MenuLabel(mui), friendly });
            ShellExtensionCatalogue.PushUnique(labels, CascadeLabels(root, sub));
            ShellExtensionCatalogue.Upsert(rows, new ShellHandler
            {
                Id = "verb:" + ShellExtensionCatalogue.FoldMenu(name),
                Name = friendly,
                Targets = targets,
                Builtin = builtin,
                Verb = name,
                Command = command,
                Rank = ShellExtensionCatalogue.Specificity(rel),
                Anchor = rel,
                Product = productTitle,
                Raw = ShellExtensionCatalogue.VerbRaw(name, stem),
                StaticLabels = labels,
                Classes = new List<string> { rel },
            });
        }
    }

    static List<string> CascadeLabels(RegistryKey root, string verbKey)
    {
        var output = new List<string>();
        using var key = OpenAt(root, verbKey + @"\shell");
        if (key == null) return output;
        string[] names;
        try { names = key.GetSubKeyNames(); }
        catch { return output; }
        var count = Math.Min(names.Length, CascadeCap);
        for (var i = 0; i < count; i++)
        {
            var name = names[i];
            if (name.Equals("command", StringComparison.OrdinalIgnoreCase)) continue;
            using var child = OpenAt(root, verbKey + @"\shell\" + name);
            if (child == null) continue;
            if (ValueExists(child, "LegacyDisable") || ValueExists(child, "ProgrammaticAccessOnly")) continue;
            var mui = ResolveMaybeIndirect(KeySz(child, "MuiVerb"));
            var display = ResolveMaybeIndirect(KeySz(child, null));
            var raw = mui.Length > 0 ? mui : display.Length > 0 ? display : name;
            var label = ShellExtensionCatalogue.MenuLabel(raw);
            if (label.Length == 0) continue;
            if (output.Exists(e => ShellExtensionCatalogue.FoldMenu(e) == ShellExtensionCatalogue.FoldMenu(label))) continue;
            output.Add(label);
            if (output.Count >= 8) break;
        }
        return output;
    }

    static string OwnerName(RegistryKey root, string full, string rel, Dictionary<string, (string Desc, string Product, string Company)> dlls)
    {
        if (GenericClass(rel)) return "";
        var slash = rel.IndexOf('\\');
        if (slash > 0 && rel[..slash].Equals("Applications", StringComparison.OrdinalIgnoreCase))
        {
            var stem = ShellExtensionCatalogue.FileStem(rel[(slash + 1)..]);
            if (ShellExtensionCatalogue.KeptTitle(stem) && !ShellExtensionCatalogue.MenuSentence(stem))
                return ShellExtensionCatalogue.KnownProduct(stem) ?? stem;
        }
        var open = "";
        using (var commandKey = OpenAt(root, full + @"\shell\open\command"))
        {
            if (commandKey != null) open = KeySz(commandKey, null);
        }
        var target = ShellExtensionCatalogue.VersionTarget(open);
        if (target.Length > 0)
        {
            var (desc, product, _) = FileVersion(target, dlls);
            var stemOwned = ShellExtensionCatalogue.FileStem(target);
            var stem = ShellExtensionCatalogue.TitleJunk(stemOwned) ? "" : stemOwned;
            var title = ShellExtensionCatalogue.FriendlyName(product, desc, stem);
            if (ShellExtensionCatalogue.KeptTitle(title) && !ShellExtensionCatalogue.MenuSentence(title))
                return title;
        }
        if (ProgidLike(rel) && !rel.Contains('\\'))
        {
            using var prog = OpenAt(root, full);
            if (prog != null)
            {
                var name = ShellExtensionCatalogue.PolishName(KeySz(prog, null));
                if (ShellExtensionCatalogue.KeptTitle(name) && !ShellExtensionCatalogue.MenuSentence(name))
                    return ShellExtensionCatalogue.KnownProduct(name) ?? name;
            }
        }
        return "";
    }

    static bool GenericClass(string rel)
    {
        var n = rel.Trim().Trim('\\').ToLowerInvariant();
        return n is "*" or "directory" or "folder" or "drive" or "allfilesystemobjects" or "unknown"
            || n.StartsWith(@"systemfileassociations\", StringComparison.Ordinal)
            || n.EndsWith(@"\background", StringComparison.Ordinal);
    }

    static bool ProgidLike(string text)
    {
        var t = text.Trim();
        return t.Length > 0 && t.Length < 128 && !t.StartsWith('.') && !t.Contains('\\') && !t.Contains('/')
            && ShellExtensionCatalogue.ParseGuid(t) == null;
    }

    static bool ProductLike(string text)
    {
        var t = text.Trim();
        return t.Length >= 2 && !ShellExtensionCatalogue.IdentifierLike(t);
    }

    static readonly Dictionary<string, string> EmptyApproved = new(StringComparer.OrdinalIgnoreCase);

    static (string Name, string Dll, string Company) ClsidMeta(
        string guid,
        Dictionary<string, string> approved,
        Dictionary<string, (string Desc, string Product, string Company)> dlls,
        Dictionary<string, (string Name, string Dll, string Company)> clsids)
    {
        if (clsids.TryGetValue(guid, out var cached)) return cached;
        var (defaultName, localized, dll) = ReadClsid(guid);
        approved.TryGetValue(guid, out var approvedName);
        var (desc, product, company) = string.IsNullOrEmpty(dll) ? ("", "", "") : FileVersion(dll, dlls);
        // 組み込み判定は製品名へ言い換える前の文字列で行う。ここは表示候補の選択だけ。
        var name = PickClsidName(localized, product, desc, approvedName ?? "", defaultName);
        if (name.Length == 0) name = ShellExtensionCatalogue.FileStem(dll);
        var meta = (name, dll, company);
        clsids[guid] = meta;
        return meta;
    }

    static string PickClsidName(params string[] values)
    {
        var cleaned = new List<string>();
        foreach (var value in values)
        {
            var text = ShellExtensionCatalogue.CleanLabel(value);
            if (text.Length == 0 || ShellExtensionCatalogue.ParseGuid(text) != null || ShellExtensionCatalogue.TitleJunk(text))
                continue;
            cleaned.Add(text);
        }
        var readable = cleaned.Find(text => !ShellExtensionCatalogue.IdentifierLike(text));
        return readable ?? (cleaned.Count > 0 ? cleaned[0] : "");
    }

    static (string Name, string Localized, string Dll) ReadClsid(string guid)
    {
        var braced = "{" + guid + "}";
        (RegistryHive Hive, string Path, RegistryView View)[] places =
        {
            (RegistryHive.ClassesRoot, $@"CLSID\{braced}", RegistryView.Registry64),
            (RegistryHive.CurrentUser, $@"Software\Classes\CLSID\{braced}", RegistryView.Registry64),
            (RegistryHive.CurrentUser, $@"Software\Classes\Wow6432Node\CLSID\{braced}", RegistryView.Registry64),
            (RegistryHive.LocalMachine, $@"Software\Classes\CLSID\{braced}", RegistryView.Registry64),
            (RegistryHive.LocalMachine, $@"Software\Classes\Wow6432Node\CLSID\{braced}", RegistryView.Registry64),
            (RegistryHive.LocalMachine, $@"Software\Wow6432Node\Classes\CLSID\{braced}", RegistryView.Registry64),
            (RegistryHive.ClassesRoot, $@"Wow6432Node\CLSID\{braced}", RegistryView.Registry64),
            (RegistryHive.LocalMachine, $@"Software\Classes\CLSID\{braced}", RegistryView.Registry32),
        };
        var name = "";
        var localized = "";
        var dll = "";
        foreach (var place in places)
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(place.Hive, place.View);
                using var key = hive.OpenSubKey(place.Path, false);
                if (key == null) continue;
                if (name.Length == 0)
                {
                    var value = KeySz(key, null);
                    if (ShellExtensionCatalogue.ParseGuid(value) == null) name = value;
                }
                if (localized.Length == 0)
                {
                    var value = KeySz(key, "LocalizedString");
                    if (value.Length > 0) localized = ResolveMaybeIndirect(value);
                }
                if (dll.Length == 0)
                {
                    using var server = key.OpenSubKey("InprocServer32", false);
                    if (server != null) dll = KeySz(server, null);
                    if (dll.Length == 0)
                    {
                        using var local = key.OpenSubKey("LocalServer32", false);
                        if (local != null) dll = KeySz(local, null);
                    }
                }
                if (dll.Length > 0 && localized.Length > 0) break;
            }
            catch { }
        }
        return (name, localized, dll);
    }

    static Dictionary<string, string> ReadApproved()
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] paths =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved",
        };
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                foreach (var path in paths)
                {
                    using var key = hive.OpenSubKey(path, false);
                    if (key == null) continue;
                    foreach (var name in key.GetValueNames())
                    {
                        var guid = ShellExtensionCatalogue.ParseGuid(name);
                        if (guid == null || output.ContainsKey(guid)) continue;
                        var label = KeySzValue(key, name);
                        if (label.Length > 0) output[guid] = label;
                    }
                }
            }
            catch { }
        }
        return output;
    }

    static (string Desc, string Product, string Company) FileVersion(string path, Dictionary<string, (string Desc, string Product, string Company)> cache)
    {
        if (path.Contains('%', StringComparison.Ordinal))
            path = Environment.ExpandEnvironmentVariables(path);
        path = path.Trim().Trim('"');
        if (cache.TryGetValue(path, out var cached)) return cached;
        var desc = "";
        var product = "";
        var company = "";
        try
        {
            if (path.Length > 0 && File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                desc = info.FileDescription ?? "";
                product = info.ProductName ?? "";
                company = info.CompanyName ?? "";
            }
        }
        catch { }
        var tuple = (desc, product, company);
        cache[path] = tuple;
        return tuple;
    }

    static RegistryKey? OpenAt(RegistryKey root, string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return root.OpenSubKey(path, false); }
        catch { return null; }
    }

    static string KeySz(RegistryKey key, string? name)
    {
        try
        {
            var value = name == null ? key.GetValue("") : key.GetValue(name);
            return ValueText(value);
        }
        catch { return ""; }
    }

    static string KeySzValue(RegistryKey key, string name)
    {
        try { return ValueText(key.GetValue(name)); }
        catch { return ""; }
    }

    static string ValueText(object? value) => value switch
    {
        string text => text.Trim().TrimEnd('\0'),
        string[] many when many.Length > 0 => (many[0] ?? "").Trim().TrimEnd('\0'),
        _ => "",
    };

    static bool ValueExists(RegistryKey key, string name)
    {
        try { return key.GetValue(name) != null; }
        catch { return false; }
    }

    static string JoinReg(string prefix, string rel)
    {
        if (string.IsNullOrEmpty(prefix)) return rel;
        prefix = prefix.TrimEnd('\\');
        rel = rel.TrimStart('\\');
        return rel.Length == 0 ? prefix : prefix + "\\" + rel;
    }

    static string ResolveMaybeIndirect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (!text.TrimStart().StartsWith('@')) return text.Trim();
        return ResolveIndirect(text.Trim());
    }

    static string ResolveIndirect(string text)
    {
        if (!OperatingSystem.IsWindows()) return "";
        try
        {
            var buffer = new StringBuilder(1024);
            if (SHLoadIndirectString(text, buffer, buffer.Capacity, IntPtr.Zero) == 0)
            {
                var resolved = buffer.ToString().Trim();
                if (resolved.Length > 0 && !resolved.StartsWith('@')) return resolved;
            }
        }
        catch { }
        return "";
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SHLoadIndirectStringW")]
    static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);
}
