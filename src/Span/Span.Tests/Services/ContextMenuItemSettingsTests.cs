using Span.Services;

namespace Span.Tests.Services;

[TestClass]
public class ContextMenuItemSettingsTests
{
    [TestMethod]
    public void EmptyStorage_ShowsEveryItem()
    {
        Assert.IsTrue(ContextMenuItemSettings.IsVisible("", "OpenTerminal"));
        Assert.IsTrue(ContextMenuItemSettings.IsVisible(null, "CopyPath"));
        Assert.IsTrue(ContextMenuItemSettings.IsVisible("  ", "AddToFavorites"));
    }

    [TestMethod]
    public void HiddenId_IsNotVisible_AndRoundTrips()
    {
        var stored = ContextMenuItemSettings.SetVisible("", "OpenTerminal", visible: false);
        Assert.AreEqual("OpenTerminal", stored);
        Assert.IsFalse(ContextMenuItemSettings.IsVisible(stored, "OpenTerminal"));
        Assert.IsTrue(ContextMenuItemSettings.IsVisible(stored, "CopyPath"));

        var restored = ContextMenuItemSettings.SetVisible(stored, "OpenTerminal", visible: true);
        Assert.AreEqual("", restored);
        Assert.IsTrue(ContextMenuItemSettings.IsVisible(restored, "OpenTerminal"));
    }

    [TestMethod]
    public void RemoveFromFavorites_UsesAddToFavoritesSetting()
    {
        var stored = ContextMenuItemSettings.SetVisible("", "AddToFavorites", visible: false);
        Assert.IsFalse(ContextMenuItemSettings.IsVisible(stored, "AddToFavorites"));
        Assert.IsFalse(ContextMenuItemSettings.IsVisible(stored, "RemoveFromFavorites"));

        var cleared = ContextMenuItemSettings.SetVisible(stored, "RemoveFromFavorites", visible: true);
        Assert.AreEqual("", cleared);
    }

    [TestMethod]
    public void JsonArray_IsParsed()
    {
        const string json = "[\"CopyPath\", \"OpenInExplorer\"]";
        Assert.IsFalse(ContextMenuItemSettings.IsVisible(json, "CopyPath"));
        Assert.IsFalse(ContextMenuItemSettings.IsVisible(json, "OpenInExplorer"));
        Assert.IsTrue(ContextMenuItemSettings.IsVisible(json, "Eject"));
    }

    [TestMethod]
    public void MultipleIds_AreStoredSorted()
    {
        var stored = ContextMenuItemSettings.SetVisible("", "Eject", false);
        stored = ContextMenuItemSettings.SetVisible(stored, "CopyPath", false);
        Assert.AreEqual("CopyPath,Eject", stored);
    }

    [TestMethod]
    public void Stub_PersistsVisibilityAndRaisesChange()
    {
        var stub = new SettingsServiceStub();
        string? changedKey = null;
        stub.SettingChanged += (key, _) => changedKey = key;

        Assert.IsTrue(stub.IsContextMenuItemVisible("Format"));
        stub.SetContextMenuItemVisible("Format", false);

        Assert.AreEqual(ContextMenuItemSettings.StorageKey, changedKey);
        Assert.IsFalse(stub.IsContextMenuItemVisible("Format"));
        Assert.AreEqual("Format", stub.HiddenContextMenuItems);
    }
}
