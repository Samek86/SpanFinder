using Span.Models;
using Span.Services;

namespace Span.Tests.Services;

/// <summary>
/// シェル拡張カタログのグループ化、保存、メニューの絞り込み。
/// レジストリは読まない。Windows で実機のアプリ名を出すときは SPAN_DUMP_CTX=1。
/// </summary>
[TestClass]
public class ShellExtensionCatalogueTests
{
    [TestMethod]
    public void NewHandler_IsEnabledUntilAScopeIsStored()
    {
        var map = ShellExtensionCatalogue.ParseMap("");
        Assert.IsTrue(ShellExtensionCatalogue.IsEnabled(map, "clsid:abc", ShellMenuScope.File));
        Assert.IsTrue(ShellExtensionCatalogue.IsEnabled(map, "clsid:abc", ShellMenuScope.Drive));

        ShellExtensionCatalogue.SetScope(map, "clsid:abc", ShellMenuScope.File, enabled: false);
        Assert.IsFalse(ShellExtensionCatalogue.IsEnabled(map, "clsid:abc", ShellMenuScope.File));
        Assert.IsTrue(ShellExtensionCatalogue.IsEnabled(map, "clsid:abc", ShellMenuScope.Folder));

        var json = ShellExtensionCatalogue.SerializeMap(map);
        var again = ShellExtensionCatalogue.ParseMap(json);
        Assert.IsFalse(ShellExtensionCatalogue.IsEnabled(again, "clsid:abc", ShellMenuScope.File));
        Assert.IsTrue(json.Contains("\"file\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ParseMap_ReadsScopeArrays_AndRejectsBrokenJson()
    {
        var map = ShellExtensionCatalogue.ParseMap("{\"verb:git_gui\":[\"folder\",\"background\",\"drive\"]}");
        Assert.AreEqual((byte)(ShellMenuScope.Folder | ShellMenuScope.Background | ShellMenuScope.Drive),
            ShellMenuScope.BitsOf(map["verb:git_gui"]));
        Assert.AreEqual(0, ShellExtensionCatalogue.ParseMap("{").Count);
        Assert.AreEqual(0, ShellExtensionCatalogue.ParseMap("   ").Count);
    }

    [TestMethod]
    public void ToggleMaster_TurnsEveryAttachedScopeOff_ThenBackOn()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var ids = new List<string> { "verb:git" };
        var scopes = new List<byte> { (byte)(ShellMenuScope.Folder | ShellMenuScope.Background | ShellMenuScope.Drive) };

        ShellExtensionCatalogue.ToggleMaster(map, ids, scopes);
        Assert.IsFalse(ShellExtensionCatalogue.IsEnabled(map, "verb:git", ShellMenuScope.Folder));
        Assert.IsFalse(ShellExtensionCatalogue.IsEnabled(map, "verb:git", ShellMenuScope.Background));
        Assert.IsTrue(ShellExtensionCatalogue.IsEnabled(map, "verb:git", ShellMenuScope.File));

        ShellExtensionCatalogue.ToggleMaster(map, ids, scopes);
        Assert.IsTrue(ShellExtensionCatalogue.IsEnabled(map, "verb:git", ShellMenuScope.Folder));
        Assert.AreEqual("", ShellExtensionCatalogue.SerializeMap(map));
    }

    [TestMethod]
    public void ToggleScope_MixedGroupTurnsThatScopeOn()
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        ShellExtensionCatalogue.SetScope(map, "a", ShellMenuScope.File, enabled: false);
        var ids = new List<string> { "a", "b" };
        var scopes = new List<byte> { ShellMenuScope.File | ShellMenuScope.Folder, ShellMenuScope.Folder };

        ShellExtensionCatalogue.ToggleScope(map, ids, scopes, ShellMenuScope.Folder);
        Assert.IsFalse(ShellExtensionCatalogue.IsEnabled(map, "a", ShellMenuScope.Folder));
        Assert.IsFalse(ShellExtensionCatalogue.IsEnabled(map, "b", ShellMenuScope.Folder));
        Assert.IsFalse(ShellExtensionCatalogue.IsEnabled(map, "a", ShellMenuScope.File));

        ShellExtensionCatalogue.ToggleMaster(map, ids, scopes);
        Assert.IsTrue(ShellExtensionCatalogue.IsEnabled(map, "a", ShellMenuScope.File));
        Assert.IsTrue(ShellExtensionCatalogue.IsEnabled(map, "b", ShellMenuScope.Folder));
    }

    [TestMethod]
    public void SettingsServiceApi_PersistsAndResets()
    {
        var settings = new SettingsServiceStub();
        Assert.IsTrue(settings.IsShellExtensionEnabled("verb:git_gui", ShellMenuScope.Background));
        settings.SetShellExtensionEnabled("verb:git_gui", ShellMenuScope.Background, enabled: false);
        Assert.IsFalse(settings.IsShellExtensionEnabled("verb:git_gui", ShellMenuScope.Background));
        Assert.IsTrue(settings.IsShellExtensionEnabled("verb:git_gui", ShellMenuScope.Folder));
        Assert.IsTrue(settings.ShellExtensionCtxOff.Contains("background", StringComparison.Ordinal));

        settings.ResetAllShellExtensionsEnabled();
        Assert.AreEqual("", settings.ShellExtensionCtxOff);
        Assert.IsTrue(settings.IsShellExtensionEnabled("verb:git_gui", ShellMenuScope.Background));
    }

    [TestMethod]
    public void Assemble_GroupsHandlersThatShareAProduct_AndKeepsScopeChips()
    {
        var builtinOpen = Handler("verb:open", "Open", ShellMenuScope.File, "open", "", "Open");
        builtinOpen.Builtin = true;
        var handlers = new List<ShellHandler>
        {
            Handler("verb:excel", "Microsoft Office", ShellMenuScope.File, "openexcel", "", "Open in Excel"),
            Handler("verb:word", "Microsoft Office", ShellMenuScope.File, "openword", "", "Open in Word"),
            Handler("clsid:lock", "LockHunter", (byte)(ShellMenuScope.File | ShellMenuScope.Folder | ShellMenuScope.Drive), "", "LockHunterShellExt", "What's locking this file?"),
            builtinOpen,
            Handler("clsid:win", "Microsoft Windows Operating System", ShellMenuScope.File, "", "shell32", "Hidden"),
        };

        var rows = ShellExtensionCatalogue.Assemble(handlers, null, null, "");
        var office = rows.Single(r => r.Name == "Microsoft Office");
        Assert.AreEqual(2, office.Ids.Count);
        Assert.AreEqual(ShellMenuScope.File, office.Targets);
        StringAssert.Contains(office.Detail, "Open in Excel");
        StringAssert.Contains(office.Detail, "Open in Word");
        Assert.AreEqual(ShellTick.On, office.Tick);

        var lockHunter = rows.Single(r => r.Name == "LockHunter");
        Assert.AreEqual((byte)(ShellMenuScope.File | ShellMenuScope.Folder | ShellMenuScope.Drive), lockHunter.Targets);
        Assert.AreEqual("LockHunterShellExt", lockHunter.Raw);
        StringAssert.Contains(lockHunter.Detail, "What's locking this file?");
        Assert.IsFalse(rows.Exists(r => r.Name == "Open"));
        Assert.IsFalse(rows.Exists(r => r.Name.Contains("Operating System", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Assemble_DisableOneScope_MakesTheRowMixed()
    {
        var handlers = new[]
        {
            Handler("clsid:git", "Git", (byte)(ShellMenuScope.Folder | ShellMenuScope.Background | ShellMenuScope.Drive), "git_gui", "", "Open Git GUI here"),
        };
        var off = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["clsid:git"] = new List<string> { "background" },
        };
        var row = ShellExtensionCatalogue.Assemble(handlers, off, null, "").Single();
        Assert.AreEqual(ShellTick.Mixed, row.Tick);
        Assert.AreEqual(ShellTick.Off, row.Subs[2]);
        Assert.AreEqual(ShellTick.On, row.Subs[1]);
        Assert.AreEqual(ShellTick.On, row.Subs[3]);
    }

    [TestMethod]
    public void LearnedLabel_ListsADormantWindowsHandlerAgain()
    {
        var handler = Handler("clsid:win", "Microsoft Windows Operating System", ShellMenuScope.File, "", "shell32");
        Assert.AreEqual(0, ShellExtensionCatalogue.Assemble(new[] { handler }, null, null, "").Count);
        var learned = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["clsid:win"] = new List<string> { "Seen in a real menu" },
        };
        var row = ShellExtensionCatalogue.Assemble(new[] { handler }, null, learned, "").Single();
        StringAssert.Contains(row.Detail, "Seen in a real menu");
    }

    [TestMethod]
    public void ItemDropped_KeepsBuiltinVerbs_AndMatchesFoldedText()
    {
        var verbs = new[] { "open", "git_gui" };
        var names = new[] { ShellExtensionCatalogue.MenuMatchKey("Open Git GUI here…") };
        Assert.IsFalse(ShellExtensionCatalogue.ItemDropped("open", "Open", verbs, names));
        Assert.IsFalse(ShellExtensionCatalogue.ItemDropped("copy", "Copy", verbs, names));
        Assert.IsTrue(ShellExtensionCatalogue.ItemDropped("git_gui", "whatever", verbs, names));
        Assert.IsTrue(ShellExtensionCatalogue.ItemDropped("", "Open &Git GUI here...", Array.Empty<string>(), names));
        Assert.AreEqual(
            ShellExtensionCatalogue.MenuMatchKey("Open Git GUI here\u2026"),
            ShellExtensionCatalogue.MenuMatchKey("&Open Git GUI here..."));
    }

    [TestMethod]
    public void Filter_DropsDisabledVerb_CollapsesSeparators_KeepsOpen()
    {
        var handler = Handler(
            "verb:git_gui", "Git",
            (byte)(ShellMenuScope.Folder | ShellMenuScope.Background | ShellMenuScope.Drive),
            "git_gui", "git-gui", "Open Git GUI here");
        var off = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["verb:git_gui"] = new List<string> { "background" },
        };
        var learned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var items = new List<ShellMenuItem>
        {
            Sep(),
            Item("Open Git GUI here", "git_gui"),
            Sep(),
            Item("Open", "open"),
            Sep(),
            Item("Copy", "copy"),
        };

        var background = ShellExtensionCatalogue.FilterShellItems(items, ShellMenuScope.Background, new[] { handler }, off, learned, out var changed);
        Assert.IsFalse(background.Exists(i => i.Verb == "git_gui"));
        var kept = background.Where(i => !i.IsSeparator).Select(i => i.Text).ToList();
        CollectionAssert.AreEqual(new[] { "Open", "Copy" }, kept);
        Assert.IsTrue(background.Exists(i => i.IsSeparator));
        Assert.IsTrue(changed);
        StringAssert.Contains(learned["verb:git_gui"][0], "Open Git GUI here");

        var file = ShellExtensionCatalogue.FilterShellItems(items, ShellMenuScope.File, new[] { handler }, off, learned, out _);
        Assert.IsTrue(file.Exists(i => i.Verb == "git_gui"));
    }

    [TestMethod]
    public void Filter_ShortProductName_DoesNotSwallowUnrelatedGitText()
    {
        var handler = Handler("clsid:git", "Git", ShellMenuScope.Folder, "", "git");
        var off = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["clsid:git"] = new List<string> { "folder" },
        };
        var learned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var items = new List<ShellMenuItem> { Item("Open Git GUI here", "") };
        var left = ShellExtensionCatalogue.FilterShellItems(items, ShellMenuScope.Folder, new[] { handler }, off, learned, out _);
        Assert.AreEqual(1, left.Count);
    }

    [TestMethod]
    public void Filter_DropsWhenMenuTextContainsTheProduct()
    {
        var handler = Handler("clsid:bdz", "Bandizip", ShellMenuScope.File, "", "bdzshell");
        var off = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["clsid:bdz"] = new List<string> { "file" },
        };
        var learned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var items = new List<ShellMenuItem>
        {
            Item("Compress with Bandizip", ""),
            Item("Open", "open"),
        };
        var left = ShellExtensionCatalogue.FilterShellItems(items, ShellMenuScope.File, new[] { handler }, off, learned, out var changed);
        Assert.AreEqual(1, left.Count);
        Assert.AreEqual("Open", left[0].Text);
        Assert.IsTrue(changed);
    }

    [TestMethod]
    public void KnownProduct_MapsVendorsAndOffice()
    {
        Assert.AreEqual("Bandizip", ShellExtensionCatalogue.KnownProduct("bdzshell"));
        Assert.AreEqual("Microsoft Office", ShellExtensionCatalogue.KnownProduct("OpenInExcel"));
        Assert.AreEqual("TortoiseGit", ShellExtensionCatalogue.KnownProduct("TortoiseGit"));
        Assert.AreEqual("Honeyview", ShellExtensionCatalogue.KnownProduct("HVShell Module"));
        Assert.AreEqual("7-Zip", ShellExtensionCatalogue.KnownProduct("7-zip"));
        Assert.AreEqual("LockHunter", ShellExtensionCatalogue.KnownProduct("LHShellExt"));
    }

    [TestMethod]
    public void PathKind_DriveRoot_Folder_File_AndBackground()
    {
        Assert.AreEqual(ShellMenuScope.Drive, ShellMenuScope.PathKind(@"C:\", isFile: false));
        Assert.AreEqual(ShellMenuScope.Drive, ShellMenuScope.PathKind("D:", isFile: false));
        Assert.AreEqual(ShellMenuScope.Folder, ShellMenuScope.PathKind(@"C:\Windows", isFile: false));
        Assert.AreEqual(ShellMenuScope.File, ShellMenuScope.PathKind(@"C:\a.txt", isFile: true));
        Assert.AreEqual(ShellMenuScope.Background, ShellMenuScope.ForClick(@"C:\Windows", isFile: false, background: true));
    }

    [TestMethod]
    public void BuiltinVerb_AndInboxDll_MatchPanegridSkips()
    {
        Assert.IsTrue(ShellExtensionCatalogue.BuiltinVerb("properties"));
        Assert.IsTrue(ShellExtensionCatalogue.BuiltinVerb("windows.modernshare"));
        Assert.IsFalse(ShellExtensionCatalogue.BuiltinVerb("git_gui"));
        Assert.IsTrue(ShellExtensionCatalogue.IsBuiltinCom("Send To", "7ba4c740-9e81-11cf-99d3-00aa004ae837", "", ""));
        Assert.IsTrue(ShellExtensionCatalogue.InboxDll(@"C:\Windows\System32\shell32.dll", "Microsoft Corporation"));
        Assert.IsFalse(ShellExtensionCatalogue.InboxDll(@"C:\Program Files\Bandizip\bdzshell.dll", "Bandisoft"));
        Assert.IsFalse(ShellExtensionCatalogue.InboxDll(@"C:\Windows\System32\ThirdParty.dll", "Bandisoft"));
    }

    [TestMethod]
    public void Registry_IsSafeToQuery()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(0, ShellExtensionRegistry.GetOrScan().Count);
            Assert.IsNull(ShellExtensionRegistry.TryGetCached());
            return;
        }

        // 実機の一覧は SPAN_DUMP_CTX=1 のときだけ出す。設定ページを開いても同じログが出る。
        if (Environment.GetEnvironmentVariable("SPAN_DUMP_CTX") != "1")
            return;

        var rows = ShellExtensionCatalogue.Assemble(ShellExtensionRegistry.GetOrScan(), null, null, "");
        foreach (var row in rows)
            Console.WriteLine($"{row.Name}\t{row.Targets}\t{row.Detail}\t{row.Raw}");
    }

    static ShellHandler Handler(string id, string product, byte targets, string verb, string raw, params string[] labels)
        => new()
        {
            Id = id,
            Name = product,
            Product = product,
            Targets = targets,
            Verb = verb,
            Raw = raw,
            StaticLabels = labels.ToList(),
        };

    static ShellMenuItem Item(string text, string verb) => new() { Text = text, Verb = verb };

    static ShellMenuItem Sep() => new() { IsSeparator = true };
}
