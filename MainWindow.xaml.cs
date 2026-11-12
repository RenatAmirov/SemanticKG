using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.System;

namespace SemanticKG;

public sealed partial class MainWindow : Window
{
    private MindMap _map = MindMap.CreateExample();
    private MapNode? _selectedNode;
    private MapLink? _selectedLink;
    private Guid? _focusNodeId;
    private Guid? _sourceId;
    private string? _tagFilter;
    private string? _path;
    private bool _connecting;
    private bool _updatingFields = true;
    private bool _isExample = true;
    private bool _dirty;
    private long _revision;
    private bool _busy;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        SetupWindow();

        NodeTitleField.MaxLength = MindMap.TitleLimit;
        NotesField.MaxLength = MindMap.NotesLimit;
        TagField.MaxLength = MindMap.TagLimit;
        LinkLabelField.MaxLength = MindMap.LabelLimit;
        Graph.NodeSelected += ChooseNode;
        Graph.LinkSelected += SelectLink;
        Graph.BackgroundSelected += ClearSelection;
        Graph.NodeMoved += MarkChanged;
        Graph.ZoomChanged += () => ZoomText.Text = $"{Graph.Zoom:P0}";
        Root.SizeChanged += ResizePanes;

        _selectedNode = _map.Nodes[0];
        Refresh();
        UpdateFileStatus();
    }

    private void SetupWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var caption = AppWindow.TitleBar;
            caption.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            caption.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            caption.ButtonForegroundColor = GraphView.Paint(0xF0EFF9).Color;
            caption.ButtonInactiveForegroundColor = GraphView.Paint(0xA7ACC5).Color;
            caption.ButtonHoverBackgroundColor = GraphView.Paint(0x32364A).Color;
            caption.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
            caption.ButtonPressedBackgroundColor = GraphView.Paint(0x464B64).Color;
            caption.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
        }

        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(work.Width - 36, 1440);
        var height = Math.Min(work.Height - 36, 880);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height));
        AppWindow.Closing += WindowClosing;
    }

    private void ResizePanes(object sender, SizeChangedEventArgs args)
    {
        var width = args.NewSize.Width;
        NavigationColumn.Width = new GridLength(width < 850 ? 154 : width < 1120 ? 190 : 224);
        InspectorColumn.Width = new GridLength(width < 850 ? 230 : width < 1120 ? 258 : 292);
        NavigationHint.Visibility = width < 720 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Refresh()
    {
        MapTitle.Text = _map.Title;
        MapSummary.Text = $"Узлы: {_map.Nodes.Count}  ·  Связи: {_map.Links.Count}";
        EmptyState.Visibility = _map.Nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FocusToggle.IsEnabled = _selectedNode is not null || _focusNodeId is not null;
        FocusToggle.IsChecked = _focusNodeId is not null;
        RefreshConnectPrompt();
        RefreshNavigation();
        RefreshGraph();
        RefreshInspector();
    }

    private void RefreshGraph()
    {
        IEnumerable<MapNode> visible = _map.Nodes;
        if (!_connecting && _focusNodeId is Guid focusId)
        {
            var neighbours = _map.Links.Where(link => link.Contains(focusId)).Select(link => link.OtherEnd(focusId)).ToHashSet();
            neighbours.Add(focusId);
            visible = visible.Where(node => neighbours.Contains(node.Id));
        }

        Graph.ShowMap(visible, _map.Links, _selectedNode?.Id, _selectedLink?.Id);
    }

    private void RefreshNavigation()
    {
        var focusedId = Root.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is Button { Tag: Guid id }
            ? (Guid?)id : null;
        var query = SearchField.Text.Trim();
        var results = _map.Nodes.Where(node =>
            (_tagFilter is null || node.Tags.Contains(_tagFilter, StringComparer.OrdinalIgnoreCase)) &&
            (query.Length == 0 || node.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             node.Tags.Any(tag => tag.Contains(query.TrimStart('#'), StringComparison.OrdinalIgnoreCase)))).ToList();
        NodeCount.Text = results.Count == _map.Nodes.Count ? $"{results.Count}" : $"{results.Count} / {_map.Nodes.Count}";
        NodeList.Children.Clear();
        foreach (var node in results)
        {
            var button = NodeButton(node, node.Id == _selectedNode?.Id);
            button.Tag = node.Id;
            button.Click += (_, _) =>
            {
                ChooseNode(node);
                if (!_connecting)
                    Graph.Reveal(node);
            };
            NodeList.Children.Add(button);
            if (node.Id == focusedId)
                button.Focus(FocusState.Programmatic);
        }

        if (results.Count == 0 && _map.Nodes.Count > 0)
            NodeList.Children.Add(Hint("Узлы не найдены."));

        TagFilters.Children.Clear();
        var tags = _map.Nodes.SelectMany(node => node.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (tags.Count == 0)
        {
            TagFilters.Children.Add(Hint("Меток пока нет."));
            return;
        }

        AddFilter("Все", null);
        foreach (var tag in tags)
            AddFilter($"#{tag}", tag);
    }

    private void AddFilter(string label, string? tag)
    {
        var selected = string.Equals(tag, _tagFilter, StringComparison.OrdinalIgnoreCase);
        var button = Chip(label, selected);
        AutomationProperties.SetName(button, tag is null ? "Показать все узлы" : $"Фильтр по метке {tag}");
        button.Click += (_, _) =>
        {
            _tagFilter = tag;
            RefreshNavigation();
        };
        TagFilters.Children.Add(button);
    }

    private void RefreshInspector()
    {
        _updatingFields = true;
        NodeDetails.Visibility = _selectedNode is null ? Visibility.Collapsed : Visibility.Visible;
        LinkDetails.Visibility = _selectedLink is null ? Visibility.Collapsed : Visibility.Visible;
        var empty = _selectedNode is null && _selectedLink is null;
        InspectorEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DeleteArea.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        InspectorHeading.Text = empty ? "Свойства" : _selectedNode is not null ? "Свойства узла" : "Свойства связи";
        if (_selectedNode is { } node)
        {
            DeleteText.Text = "Удалить узел";
            NodeTitleField.Text = node.Title;
            NotesField.Text = node.Notes;
            TagField.Text = "";
            RefreshTags(node);
            RefreshColors(node);
            RefreshConnections(node);
        }
        else if (_selectedLink is { } link)
        {
            DeleteText.Text = "Удалить связь";
            LinkLabelField.Text = link.Label;
            LinkEndpoints.Children.Clear();
            foreach (var id in new[] { link.SourceId, link.TargetId })
            {
                var endpoint = _map.Nodes.First(node => node.Id == id);
                var button = NodeButton(endpoint, false);
                button.Click += (_, _) =>
                {
                    ChooseNode(endpoint);
                    Graph.Reveal(endpoint);
                };
                LinkEndpoints.Children.Add(button);
            }
        }
        _updatingFields = false;
    }

    private void RefreshTags(MapNode node)
    {
        NodeTags.Children.Clear();
        foreach (var tag in node.Tags)
        {
            var button = Chip($"#{tag}  ×", true);
            AutomationProperties.SetName(button, $"Снять метку {tag}");
            ToolTipService.SetToolTip(button, $"Снять метку «{tag}»");
            button.Click += (_, _) =>
            {
                node.Tags.Remove(tag);
                if (string.Equals(_tagFilter, tag, StringComparison.OrdinalIgnoreCase) &&
                    !_map.Nodes.Any(item => item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
                    _tagFilter = null;
                MarkChanged();
                RefreshTags(node);
                RefreshGraph();
                RefreshNavigation();
            };
            NodeTags.Children.Add(button);
        }
    }

    private void RefreshColors(MapNode node)
    {
        ColorOptions.Children.Clear();
        for (var index = 0; index < MindMap.ColorCount; index++)
        {
            var colorIndex = index;
            var color = MapColors.At(index);
            var selected = index == node.Color;
            var button = new Button
            {
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = GraphView.Paint(color.Surface),
                BorderBrush = GraphView.Paint(selected ? color.Accent : color.Border),
                BorderThickness = new Thickness(selected ? 2 : 1),
                Content = new Ellipse { Width = 10, Height = 10, Fill = GraphView.Paint(color.Accent) }
            };
            AutomationProperties.SetName(button, selected ? $"{color.Name}, выбран" : color.Name);
            ToolTipService.SetToolTip(button, color.Name);
            button.Click += (_, _) =>
            {
                if (node.Color == colorIndex)
                    return;
                node.Color = colorIndex;
                MarkChanged();
                RefreshColors(node);
                RefreshGraph();
                RefreshNavigation();
            };
            ColorOptions.Children.Add(button);
        }
    }

    private void RefreshConnections(MapNode node)
    {
        var links = _map.Links.Where(link => link.Contains(node.Id)).ToList();
        ConnectionsTitle.Text = $"Связи ({links.Count})";
        ConnectionList.Children.Clear();
        if (links.Count == 0)
            ConnectionList.Children.Add(Hint("Связей пока нет."));

        foreach (var link in links)
        {
            var other = _map.Nodes.First(item => item.Id == link.OtherEnd(node.Id));
            var button = NodeButton(other, false, link.Label.Length == 0 ? null : link.Label);
            AutomationProperties.SetName(button, $"Связь с узлом {other.Title} {link.Label}".Trim());
            button.Click += (_, _) => SelectLink(link);
            ConnectionList.Children.Add(button);
        }
    }

    private Button NodeButton(MapNode node, bool selected, string? subtitle = null)
    {
        var palette = MapColors.At(node.Color);
        var layout = new Grid { ColumnSpacing = 9 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = GraphView.Paint(palette.Accent),
            VerticalAlignment = VerticalAlignment.Center
        });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        layout.Children.Add(text);
        text.Children.Add(new TextBlock { Text = node.Title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        if (subtitle is not null)
            text.Children.Add(Hint(subtitle));

        var button = new Button
        {
            Style = (Style)Root.Resources["QuietButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(9, 6, 8, 6),
            Content = layout
        };
        if (selected)
        {
            button.Background = GraphView.Paint(0x2A2542);
            button.BorderBrush = GraphView.Paint(0x51446F);
        }
        AutomationProperties.SetName(button, node.Title);
        ToolTipService.SetToolTip(button, node.Title);
        return button;
    }

    private static Button Chip(string text, bool selected) => new()
    {
        Background = GraphView.Paint(selected ? 0x312849 : 0x232638),
        BorderBrush = GraphView.Paint(selected ? 0x695789 : 0x3B4055),
        Foreground = GraphView.Paint(selected ? 0xDACBFF : 0xCED2E6),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(11),
        Padding = new Thickness(8, 3, 8, 3),
        MinHeight = 25,
        Content = new TextBlock { Text = text, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis }
    };

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        Style = (Style)Root.Resources["MutedTextStyle"],
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private void ChooseNode(MapNode node)
    {
        if (_connecting)
        {
            ChooseConnectionEndpoint(node);
            return;
        }

        if (_selectedNode == node)
            return;

        _selectedNode = node;
        _selectedLink = null;
        if (_focusNodeId is not null)
            _focusNodeId = node.Id;
        Refresh();
    }

    private void SelectLink(MapLink link)
    {
        _selectedNode = null;
        _selectedLink = link;
        Refresh();
    }

    private void ClearSelection()
    {
        if (_connecting || (_selectedNode is null && _selectedLink is null))
            return;

        _selectedNode = null;
        _selectedLink = null;
        _focusNodeId = null;
        Refresh();
    }

    private void FocusClicked(object sender, RoutedEventArgs args)
    {
        _focusNodeId = FocusToggle.IsChecked == true ? _selectedNode?.Id : null;
        Refresh();
        Graph.Fit();
    }

    private void ConnectClicked(object sender, RoutedEventArgs args)
    {
        if (_connecting)
        {
            StopConnecting();
            return;
        }

        _connecting = true;
        _sourceId = _selectedNode?.Id;
        Refresh();
        if (_focusNodeId is not null)
            Graph.Fit();
    }

    private void ChooseConnectionEndpoint(MapNode node)
    {
        if (_sourceId is not Guid sourceId)
        {
            _sourceId = node.Id;
            _selectedNode = node;
            _selectedLink = null;
            Refresh();
            return;
        }

        if (node.Id == sourceId)
        {
            LinkPromptText.Text = "Выберите другой узел.";
            return;
        }

        var link = _map.FindLink(sourceId, node.Id);
        if (link is null)
        {
            link = new MapLink { SourceId = sourceId, TargetId = node.Id };
            _map.Links.Add(link);
            MarkChanged();
        }

        _connecting = false;
        _sourceId = null;
        if (_focusNodeId is not null)
            _focusNodeId = sourceId;
        SelectLink(link);
        LinkLabelField.Focus(FocusState.Programmatic);
    }

    private void StopConnecting()
    {
        _connecting = false;
        _sourceId = null;
        Refresh();
    }

    private void RefreshConnectPrompt()
    {
        Graph.Connecting = _connecting;
        ConnectButton.IsEnabled = _map.Nodes.Count >= 2;
        LinkPrompt.Visibility = _connecting ? Visibility.Visible : Visibility.Collapsed;
        LinkPromptText.Text = _sourceId is null ? "Выберите исходный узел." : "Выберите второй узел.";
        FocusToggle.IsEnabled &= !_connecting;
    }

    private async Task AddNodeAsync()
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            var title = await AskNameAsync("Новый узел", "Название узла", "Создать");
            if (title is null)
                return;

            var position = Graph.FindSpace(_map.Nodes, _selectedNode);
            var node = new MapNode { Title = title, X = position.X, Y = position.Y, Color = _map.Nodes.Count % MindMap.ColorCount };
            _map.Nodes.Add(node);
            _connecting = false;
            _sourceId = null;
            _focusNodeId = null;
            _selectedNode = node;
            _selectedLink = null;
            _tagFilter = null;
            SearchField.Text = "";
            MarkChanged();
            Refresh();
            Graph.Reveal(node);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void DeleteClicked(object sender, RoutedEventArgs args)
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            if (_selectedNode is { } node)
            {
                var count = _map.Links.Count(link => link.Contains(node.Id));
                var message = count == 0 ? $"Удалить «{node.Title}»?" : $"Узел «{node.Title}» и все его связи ({count}) будут удалены.";
                if (await Dialog("Удаление узла", message, "Удалить", "Отмена").ShowAsync() != ContentDialogResult.Primary)
                    return;

                _map.RemoveNode(node.Id);
                _focusNodeId = null;
                if (_tagFilter is not null && !_map.Nodes.Any(item => item.Tags.Contains(_tagFilter, StringComparer.OrdinalIgnoreCase)))
                    _tagFilter = null;
            }
            else if (_selectedLink is { } link)
            {
                _map.Links.Remove(link);
            }
            else
            {
                return;
            }

            _selectedNode = null;
            _selectedLink = null;
            _sourceId = null;
            _connecting = false;
            MarkChanged();
            Refresh();
        }
        finally
        {
            _busy = false;
        }
    }

    private void AddTag()
    {
        var tag = TagField.Text.Trim().TrimStart('#').Trim();
        if (_selectedNode is not { } node || tag.Length == 0)
            return;

        if (!node.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            node.Tags.Add(tag);
            MarkChanged();
            RefreshTags(node);
            RefreshGraph();
            RefreshNavigation();
        }
        TagField.Text = "";
    }

    private void NodeTitleChanged(object sender, TextChangedEventArgs args)
    {
        var title = NodeTitleField.Text.Trim();
        if (_updatingFields || _selectedNode is not { } node || title.Length == 0 || node.Title == title)
            return;

        node.Title = title;
        MarkChanged();
        RefreshGraph();
        RefreshNavigation();
    }

    private void NotesChanged(object sender, TextChangedEventArgs args)
    {
        if (_updatingFields || _selectedNode is not { } node || node.Notes == NotesField.Text)
            return;

        node.Notes = NotesField.Text;
        MarkChanged();
    }

    private void LinkLabelChanged(object sender, TextChangedEventArgs args)
    {
        var label = LinkLabelField.Text.Trim();
        if (_updatingFields || _selectedLink is not { } link || link.Label == label)
            return;

        link.Label = label;
        MarkChanged();
        RefreshGraph();
    }

    private void NodeTitleLostFocus(object sender, RoutedEventArgs args)
    {
        if (_selectedNode is { } node)
            NodeTitleField.Text = node.Title;
    }

    private void LinkLabelLostFocus(object sender, RoutedEventArgs args)
    {
        if (_selectedLink is { } link)
            LinkLabelField.Text = link.Label;
    }

    private void SearchChanged(object sender, TextChangedEventArgs args)
    {
        if (!_updatingFields)
            RefreshNavigation();
    }

    private void TagKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter)
            return;

        AddTag();
        args.Handled = true;
    }

    private void EscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_connecting || _busy)
            return;

        StopConnecting();
        args.Handled = true;
    }

    private async void AddNodeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await AddNodeAsync();
    }

    private async void AddNodeClicked(object sender, RoutedEventArgs args) => await AddNodeAsync();
    private void CancelConnectClicked(object sender, RoutedEventArgs args) => StopConnecting();
    private void AddTagClicked(object sender, RoutedEventArgs args) => AddTag();
    private void ZoomInClicked(object sender, RoutedEventArgs args) => Graph.ZoomBy(1.2);
    private void ZoomOutClicked(object sender, RoutedEventArgs args) => Graph.ZoomBy(1 / 1.2);
    private void FitClicked(object sender, RoutedEventArgs args) => Graph.Fit();
}
