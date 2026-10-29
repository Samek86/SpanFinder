using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Span.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Span.Views;

public sealed partial class SettingsModeView
{
    private readonly HashSet<string> _shellExpanded = new(StringComparer.Ordinal);
    private List<ShellHandler>? _shellHandlers;
    private int _shellLoadVersion;
    private bool _syncingShellRows;

    /// <summary>ページの文言。行が既にあればスコープ名も付け直す。</summary>
    private void LocalizeShellExtensionPage()
    {
        if (_loc == null || ContextMenuTitle == null) return;
        ContextMenuTitle.Text = _loc.Get("Settings_RightClickMenu");
        ContextMenuDesc.Text = _loc.Get("Settings_RightClickMenuDesc");
        ContextMenuResetButton.Content = _loc.Get("Settings_RightClickMenuReset");
        ContextMenuLoadingText.Text = _loc.Get("Settings_RightClickMenuLoading");
        ContextMenuEmptyText.Text = _loc.Get("Settings_RightClickMenuEmpty");
        if (_shellHandlers != null)
            RenderShellExtensionRowsFromCache();
    }

    /// <summary>左ナビでこのページを開いたとき。カタログは裏で読み、UI は待たない。</summary>
    private void LoadShellExtensionPage()
    {
        if (_shellHandlers != null)
        {
            ContextMenuLoadingText.Visibility = Visibility.Collapsed;
            RenderShellExtensionRowsFromCache();
            return;
        }

        var version = ++_shellLoadVersion;
        ContextMenuLoadingText.Visibility = Visibility.Visible;
        ContextMenuEmptyText.Visibility = Visibility.Collapsed;
        ShellExtensionRowsPanel.Children.Clear();

        Task.Run(() =>
        {
            var handlers = ShellExtensionRegistry.GetOrScan().ToList();
            var rows = ShellExtensionCatalogue.Assemble(
                handlers,
                ShellExtensionCatalogue.ParseMap(_settings.ShellExtensionCtxOff),
                ShellExtensionCatalogue.ParseMap(_settings.ShellExtensionCtxLabels),
                "");
            return (handlers, rows);
        }).ContinueWith(task =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (version != _shellLoadVersion) return;
                ContextMenuLoadingText.Visibility = Visibility.Collapsed;
                if (task.IsFaulted)
                {
                    Helpers.DebugLogger.Log($"[ShellExt] page load failed: {task.Exception?.GetBaseException().Message}");
                    _shellHandlers = new List<ShellHandler>();
                    RenderShellExtensionRows(new List<ShellUiRow>());
                    return;
                }
                _shellHandlers = task.Result.handlers;
                RenderShellExtensionRows(task.Result.rows);
            });
        });
    }

    private void OnShellExtensionReset(object sender, RoutedEventArgs e)
    {
        _settings.ResetAllShellExtensionsEnabled();
        if (_shellHandlers != null)
            DispatcherQueue.TryEnqueue(RenderShellExtensionRowsFromCache);
    }

    private void RenderShellExtensionRowsFromCache()
    {
        if (_shellHandlers == null || ShellExtensionRowsPanel == null) return;
        var rows = ShellExtensionCatalogue.Assemble(
            _shellHandlers,
            ShellExtensionCatalogue.ParseMap(_settings.ShellExtensionCtxOff),
            ShellExtensionCatalogue.ParseMap(_settings.ShellExtensionCtxLabels),
            "");
        RenderShellExtensionRows(rows);
    }

    private void RenderShellExtensionRows(IReadOnlyList<ShellUiRow> rows)
    {
        if (ShellExtensionRowsPanel == null) return;
        _syncingShellRows = true;
        try
        {
            ShellExtensionRowsPanel.Children.Clear();
            ContextMenuEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var row in rows)
                ShellExtensionRowsPanel.Children.Add(BuildShellExtensionCard(row));
        }
        finally
        {
            _syncingShellRows = false;
        }
    }

    private Border BuildShellExtensionCard(ShellUiRow row)
    {
        var expanded = _shellExpanded.Contains(row.Id);
        var primary = GetThemeBrush("SpanTextPrimaryBrush");
        var secondary = GetThemeBrush("SpanTextSecondaryBrush");
        var tertiary = GetThemeBrush("SpanTextTertiaryBrush");
        var border = GetThemeBrush("SpanBorderSubtleBrush");

        var card = new Border
        {
            Background = GetThemeBrush("SpanBgLayer1Brush"),
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var body = new StackPanel();
        var head = new Grid { ColumnSpacing = 4 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 80 });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var master = new CheckBox
        {
            IsThreeState = true,
            IsChecked = TickBox(row.Tick),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 0,
        };
        var ids = row.Ids;
        var scopes = row.Scopes;
        master.Click += (_, _) =>
        {
            if (_syncingShellRows) return;
            var map = ShellExtensionCatalogue.ParseMap(_settings.ShellExtensionCtxOff);
            ShellExtensionCatalogue.ToggleMaster(map, ids, scopes);
            _settings.ShellExtensionCtxOff = ShellExtensionCatalogue.SerializeMap(map);
            DispatcherQueue.TryEnqueue(RenderShellExtensionRowsFromCache);
        };
        Helpers.CursorHelper.SetHandCursor(master);
        Grid.SetColumn(master, 0);
        head.Children.Add(master);

        var chevron = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 2, 0),
            MinWidth = 0,
            MinHeight = 0,
            Width = 28,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon
            {
                Glyph = expanded ? "\uE70D" : "\uE76C",
                FontSize = 12,
                Foreground = secondary,
            },
        };
        var rowId = row.Id;
        chevron.Click += (_, _) =>
        {
            if (!_shellExpanded.Add(rowId))
                _shellExpanded.Remove(rowId);
            DispatcherQueue.TryEnqueue(RenderShellExtensionRowsFromCache);
        };
        Helpers.CursorHelper.SetHandCursor(chevron);
        Grid.SetColumn(chevron, 1);
        head.Children.Add(chevron);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        text.Children.Add(new TextBlock
        {
            Text = row.Name,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = primary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (row.Detail.Length > 0 || row.Raw.Length > 0)
        {
            var detail = new TextBlock
            {
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            };
            if (row.Detail.Length > 0)
                detail.Inlines.Add(new Run { Text = row.Detail, Foreground = secondary });
            if (row.Raw.Length > 0)
            {
                if (detail.Inlines.Count > 0)
                    detail.Inlines.Add(new Run { Text = "  ", Foreground = tertiary });
                detail.Inlines.Add(new Run { Text = row.Raw, Foreground = tertiary });
            }
            text.Children.Add(detail);
        }
        Grid.SetColumn(text, 2);
        head.Children.Add(text);

        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var bit in ShellMenuScope.Bits)
        {
            if ((row.Targets & bit) == 0) continue;
            var key = ShellMenuScope.LocalizationKey(bit);
            chips.Children.Add(new Border
            {
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                Child = new TextBlock
                {
                    Text = _loc?.Get(key) ?? key,
                    FontSize = 11,
                    Foreground = secondary,
                },
            });
        }
        Grid.SetColumn(chips, 3);
        head.Children.Add(chips);
        body.Children.Add(head);

        if (expanded)
        {
            var scopesPanel = new StackPanel { Margin = new Thickness(36, 6, 0, 0), Spacing = 2 };
            for (var i = 0; i < ShellMenuScope.Bits.Length; i++)
            {
                var bit = ShellMenuScope.Bits[i];
                if ((row.Targets & bit) == 0) continue;
                var key = ShellMenuScope.LocalizationKey(bit);
                var box = new CheckBox
                {
                    Content = _loc?.Get(key) ?? key,
                    IsThreeState = true,
                    IsChecked = TickBox(row.Subs[i]),
                    MinWidth = 0,
                };
                var scopeBit = bit;
                box.Click += (_, _) =>
                {
                    if (_syncingShellRows) return;
                    var map = ShellExtensionCatalogue.ParseMap(_settings.ShellExtensionCtxOff);
                    ShellExtensionCatalogue.ToggleScope(map, ids, scopes, scopeBit);
                    _settings.ShellExtensionCtxOff = ShellExtensionCatalogue.SerializeMap(map);
                    DispatcherQueue.TryEnqueue(RenderShellExtensionRowsFromCache);
                };
                Helpers.CursorHelper.SetHandCursor(box);
                scopesPanel.Children.Add(box);
            }
            body.Children.Add(scopesPanel);
        }

        card.Child = body;
        return card;
    }

    private static bool? TickBox(ShellTick tick) => tick switch
    {
        ShellTick.On => true,
        ShellTick.Off => false,
        _ => null,
    };
}
