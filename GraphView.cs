using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace SemanticKG;

public sealed class GraphView : Grid
{
    private const double CardWidth = 172;
    private const double CardHeight = 63;
    private const double FitPadding = 48;
    private readonly Canvas _scene = new();
    private readonly Image _dots = new() { Stretch = Stretch.None, IsHitTestVisible = false };
    private readonly RectangleGeometry _clip = new();
    private readonly Dictionary<Guid, NodeVisual> _nodes = [];
    private readonly List<LinkVisual> _links = [];
    private double _left;
    private double _top;
    private bool _fitPending = true;
    private uint? _pointerId;
    private Point _pressedAt;
    private Point _startPosition;
    private MapNode? _dragNode;
    private bool _dragged;

    internal event Action<MapNode>? NodeSelected;
    internal event Action<MapLink>? LinkSelected;
    internal event Action? BackgroundSelected;
    internal event Action? NodeMoved;
    internal event Action? ZoomChanged;

    internal double Zoom { get; private set; } = 1;
    internal bool Connecting { get; set; }

    public GraphView()
    {
        Background = Paint(0x131624);
        Clip = _clip;
        _dots.HorizontalAlignment = HorizontalAlignment.Left;
        _dots.VerticalAlignment = VerticalAlignment.Top;
        var background = new Canvas { IsHitTestVisible = false };
        background.Children.Add(_dots);
        Children.Add(background);
        Children.Add(_scene);
        SizeChanged += ResizeViewport;
        PointerPressed += (_, args) => BeginGesture(args, null);
        PointerMoved += MoveGesture;
        PointerReleased += EndGesture;
        PointerCaptureLost += (_, args) => CancelGesture(args.Pointer.PointerId);
        PointerCanceled += (_, args) => CancelGesture(args.Pointer.PointerId);
        PointerWheelChanged += (_, args) =>
        {
            var point = args.GetCurrentPoint(this);
            ZoomAt(Math.Pow(1.15, point.Properties.MouseWheelDelta / 120.0), point.Position);
            args.Handled = true;
        };
    }

    internal void ShowMap(IEnumerable<MapNode> nodes, IEnumerable<MapLink> links, Guid? selectedNode, Guid? selectedLink)
    {
        _scene.Children.Clear();
        _nodes.Clear();
        _links.Clear();
        foreach (var node in nodes)
            _nodes.Add(node.Id, MakeNode(node, node.Id == selectedNode));

        foreach (var link in links)
        {
            if (_nodes.TryGetValue(link.SourceId, out var source) && _nodes.TryGetValue(link.TargetId, out var target))
                _links.Add(MakeLink(link, source.Node, target.Node, link.Id == selectedLink, selectedNode));
        }

        foreach (var visual in _nodes.Values.OrderBy(node => node.Node.Id == selectedNode))
            _scene.Children.Add(visual.Card);

        UpdatePositions();
    }

    internal void Fit()
    {
        if (ActualWidth <= 1 || ActualHeight <= 1)
        {
            _fitPending = true;
            return;
        }

        _fitPending = false;
        if (_nodes.Count == 0)
        {
            Zoom = 1;
            _left = ActualWidth / 2;
            _top = ActualHeight / 2;
        }
        else
        {
            var minX = _nodes.Values.Min(item => item.Node.X);
            var minY = _nodes.Values.Min(item => item.Node.Y);
            var width = _nodes.Values.Max(item => item.Node.X) - minX + CardWidth;
            var height = _nodes.Values.Max(item => item.Node.Y) - minY + CardHeight;
            var horizontalPadding = Math.Min(FitPadding, ActualWidth / 5);
            var verticalPadding = Math.Min(FitPadding, ActualHeight / 5);
            Zoom = Math.Min(1.12, Math.Min((ActualWidth - 2 * horizontalPadding) / width,
                (ActualHeight - 2 * verticalPadding) / height));
            _left = (ActualWidth - width * Zoom) / 2 - minX * Zoom;
            _top = (ActualHeight - height * Zoom) / 2 - minY * Zoom;
        }

        UpdatePositions();
        ZoomChanged?.Invoke();
    }

    internal void ZoomBy(double factor) => ZoomAt(factor, new Point(ActualWidth / 2, ActualHeight / 2));

    internal void Reveal(MapNode node)
    {
        var x = ScreenX(node.X);
        var y = ScreenY(node.Y);
        if (x >= 12 && y >= 12 && x + CardWidth * Zoom <= ActualWidth - 12 &&
            y + CardHeight * Zoom <= ActualHeight - 12)
            return;

        _left = ActualWidth / 2 - (node.X + CardWidth / 2) * Zoom;
        _top = ActualHeight / 2 - (node.Y + CardHeight / 2) * Zoom;
        UpdatePositions();
    }

    internal Point FindSpace(IReadOnlyList<MapNode> nodes, MapNode? near)
    {
        const double stepX = CardWidth + 32;
        const double stepY = CardHeight + 32;
        var origin = near is null
            ? new Point((ActualWidth / 2 - _left) / Zoom - CardWidth / 2, (ActualHeight / 2 - _top) / Zoom - CardHeight / 2)
            : new Point(near.X + stepX, near.Y);

        for (var radius = 0; ; radius++)
        {
            for (var row = -radius; row <= radius; row++)
            {
                for (var column = -radius; column <= radius; column++)
                {
                    if (Math.Max(Math.Abs(row), Math.Abs(column)) != radius)
                        continue;

                    var candidate = new Point(origin.X + column * stepX, origin.Y + row * stepY);
                    if (nodes.All(node => Math.Abs(node.X - candidate.X) >= CardWidth + 12 ||
                                          Math.Abs(node.Y - candidate.Y) >= CardHeight + 12))
                        return candidate;
                }
            }
        }
    }

    private void ResizeViewport(object sender, SizeChangedEventArgs args)
    {
        _clip.Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height);
        DrawDots();
        if (_fitPending)
        {
            Fit();
            return;
        }

        _left += (args.NewSize.Width - args.PreviousSize.Width) / 2;
        _top += (args.NewSize.Height - args.PreviousSize.Height) / 2;
        UpdatePositions();
    }

    private void DrawDots()
    {
        var width = (int)Math.Ceiling(ActualWidth);
        var height = (int)Math.Ceiling(ActualHeight);
        if (width <= 0 || height <= 0)
            return;

        const int interval = 20;
        var pixels = new byte[width * height * 4];
        for (var y = interval / 2; y < height; y += interval)
        {
            for (var x = interval / 2; x < width; x += interval)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = 0x54;
                pixels[offset + 1] = 0x42;
                pixels[offset + 2] = 0x39;
                pixels[offset + 3] = 0xFF;
            }
        }

        var bitmap = new WriteableBitmap(width, height);
        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels);
        bitmap.Invalidate();
        _dots.Source = bitmap;
    }

    private NodeVisual MakeNode(MapNode node, bool selected)
    {
        var palette = MapColors.At(node.Color);
        var scale = new ScaleTransform();
        var card = new Border
        {
            Width = CardWidth,
            Height = CardHeight,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = Paint(selected ? palette.Accent : palette.Border),
            Background = Paint(selected ? palette.Selected : palette.Surface),
            Padding = new Thickness(11, 0, 9, 0),
            RenderTransform = scale
        };
        AutomationProperties.SetName(card, node.Title);
        ToolTipService.SetToolTip(card, node.Title);
        card.PointerPressed += (_, args) => BeginGesture(args, node);

        var layout = new Grid { ColumnSpacing = 9 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var marker = new Border
        {
            Width = 23,
            Height = 23,
            CornerRadius = new CornerRadius(7),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Paint(palette.Badge),
            Child = new Ellipse
            {
                Width = 8,
                Height = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = Paint(palette.Accent)
            }
        };
        layout.Children.Add(marker);

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        layout.Children.Add(text);
        text.Children.Add(new TextBlock
        {
            Text = node.Title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Paint(0xF3F0FC),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (node.Tags.Count > 0)
        {
            var tag = node.Tags.Count == 1 ? $"#{node.Tags[0]}" : $"#{node.Tags[0]}  +{node.Tags.Count - 1}";
            text.Children.Add(new TextBlock
            {
                Text = tag,
                FontSize = 11,
                Foreground = Paint(palette.Accent),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        card.Child = layout;
        return new NodeVisual(node, card, scale);
    }

    private LinkVisual MakeLink(MapLink link, MapNode source, MapNode target, bool selected, Guid? selectedNode)
    {
        var visibleCurve = new Curve();
        var hitCurve = new Curve();
        var stroke = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = visibleCurve.Geometry,
            Stroke = Paint(selected ? 0xC3ACFF : selectedNode is Guid id && link.Contains(id) ? 0x7970A2 : 0x515976),
            StrokeThickness = selected ? 2.7 : 1.6,
            IsHitTestVisible = false
        };
        var hitArea = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = hitCurve.Geometry,
            Stroke = new SolidColorBrush(Colors.Transparent),
            StrokeThickness = 13
        };
        hitArea.PointerPressed += (_, args) => ChooseLink(args, link);
        _scene.Children.Add(stroke);
        _scene.Children.Add(hitArea);

        Border? label = null;
        if (link.Label.Length > 0)
        {
            var offset = new TranslateTransform();
            label = new Border
            {
                Padding = new Thickness(7, 3, 7, 3),
                MaxWidth = 150,
                CornerRadius = new CornerRadius(5),
                Background = Paint(selected ? 0x362B56 : 0x1C2031),
                BorderBrush = Paint(selected ? 0x9A7DE0 : 0x343A52),
                BorderThickness = new Thickness(1),
                RenderTransform = offset,
                Child = new TextBlock
                {
                    Text = link.Label,
                    FontSize = 11,
                    Foreground = Paint(selected ? 0xF1E6FF : 0xBCC1DB),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            ToolTipService.SetToolTip(label, link.Label);
            label.PointerPressed += (_, args) => ChooseLink(args, link);
            label.SizeChanged += (_, args) =>
            {
                offset.X = -args.NewSize.Width / 2;
                offset.Y = -args.NewSize.Height / 2;
            };
            _scene.Children.Add(label);
        }

        return new LinkVisual(source, target, visibleCurve, hitCurve, label);
    }

    private void ChooseLink(PointerRoutedEventArgs args, MapLink link)
    {
        if (!IsPrimaryPress(args.GetCurrentPoint(this)) || Connecting)
            return;

        args.Handled = true;
        LinkSelected?.Invoke(link);
    }

    private void BeginGesture(PointerRoutedEventArgs args, MapNode? node)
    {
        var point = args.GetCurrentPoint(this);
        if (_pointerId.HasValue || !IsPrimaryPress(point))
            return;

        args.Handled = true;
        if (!CapturePointer(args.Pointer))
        {
            SelectAt(node);
            return;
        }

        _pointerId = point.PointerId;
        _pressedAt = point.Position;
        _dragged = false;
        _dragNode = node;
        _startPosition = node is null ? new Point(_left, _top) : new Point(node.X, node.Y);
    }

    private void MoveGesture(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this);
        if (_pointerId != point.PointerId || (Connecting && _dragNode is not null))
            return;

        var dx = point.Position.X - _pressedAt.X;
        var dy = point.Position.Y - _pressedAt.Y;
        if (!_dragged && Math.Abs(dx) < 4 && Math.Abs(dy) < 4)
            return;

        _dragged = true;
        if (_dragNode is null)
        {
            _left = _startPosition.X + dx;
            _top = _startPosition.Y + dy;
        }
        else
        {
            _dragNode.X = Math.Clamp(_startPosition.X + dx / Zoom, -1e9, 1e9);
            _dragNode.Y = Math.Clamp(_startPosition.Y + dy / Zoom, -1e9, 1e9);
        }

        UpdatePositions();
        args.Handled = true;
    }

    private void EndGesture(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerId != args.Pointer.PointerId)
            return;

        var node = _dragNode;
        var moved = _dragged;
        _pointerId = null;
        _dragNode = null;
        ReleasePointerCapture(args.Pointer);
        args.Handled = true;

        if (node is not null && moved)
            NodeMoved?.Invoke();
        if (node is not null || !moved)
            SelectAt(node);
    }

    private void SelectAt(MapNode? node)
    {
        if (node is null)
            BackgroundSelected?.Invoke();
        else
            NodeSelected?.Invoke(node);
    }

    private void CancelGesture(uint pointerId)
    {
        if (_pointerId != pointerId)
            return;

        var movedNode = _dragNode is not null && _dragged;
        _pointerId = null;
        _dragNode = null;
        if (movedNode)
            NodeMoved?.Invoke();
    }

    private void ZoomAt(double factor, Point anchor)
    {
        if (_pointerId.HasValue)
            return;

        var lowerBound = Math.Min(0.15, Zoom);
        var next = Math.Clamp(Zoom * factor, lowerBound, 2.5);
        var ratio = next / Zoom;
        _left = anchor.X - (anchor.X - _left) * ratio;
        _top = anchor.Y - (anchor.Y - _top) * ratio;
        Zoom = next;
        UpdatePositions();
        ZoomChanged?.Invoke();
    }

    private void UpdatePositions()
    {
        foreach (var visual in _nodes.Values)
        {
            Canvas.SetLeft(visual.Card, ScreenX(visual.Node.X));
            Canvas.SetTop(visual.Card, ScreenY(visual.Node.Y));
            visual.Scale.ScaleX = Zoom;
            visual.Scale.ScaleY = Zoom;
        }

        foreach (var visual in _links)
            PositionLink(visual);
    }

    private void PositionLink(LinkVisual link)
    {
        var dx = link.Target.X - link.Source.X;
        var dy = link.Target.Y - link.Source.Y;
        var horizontal = Math.Abs(dx) / CardWidth > Math.Abs(dy) / CardHeight;
        var direction = (horizontal ? dx : dy) >= 0 ? 1 : -1;
        var edgeX = horizontal ? direction * CardWidth / 2 : 0;
        var edgeY = horizontal ? 0 : direction * CardHeight / 2;
        var start = new Point(ScreenX(link.Source.X + CardWidth / 2 + edgeX), ScreenY(link.Source.Y + CardHeight / 2 + edgeY));
        var end = new Point(ScreenX(link.Target.X + CardWidth / 2 - edgeX), ScreenY(link.Target.Y + CardHeight / 2 - edgeY));
        var bend = Math.Max(14 * Zoom, Math.Abs(horizontal ? end.X - start.X : end.Y - start.Y) * 0.54) * direction;
        var control1 = horizontal ? new Point(start.X + bend, start.Y) : new Point(start.X, start.Y + bend);
        var control2 = horizontal ? new Point(end.X - bend, end.Y) : new Point(end.X, end.Y - bend);
        link.VisibleCurve.SetPoints(start, control1, control2, end);
        link.HitCurve.SetPoints(start, control1, control2, end);

        if (link.Label is not null)
        {
            link.Label.Visibility = Zoom < 0.48 ? Visibility.Collapsed : Visibility.Visible;
            Canvas.SetLeft(link.Label, (start.X + end.X) / 2);
            Canvas.SetTop(link.Label, (start.Y + end.Y) / 2);
        }
    }

    private double ScreenX(double x) => x * Zoom + _left;
    private double ScreenY(double y) => y * Zoom + _top;

    private static bool IsPrimaryPress(PointerPoint point)
    {
        var buttons = point.Properties;
        return buttons.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed ||
               (buttons.PointerUpdateKind == PointerUpdateKind.Other && !buttons.IsRightButtonPressed &&
                !buttons.IsMiddleButtonPressed && !buttons.IsXButton1Pressed && !buttons.IsXButton2Pressed &&
                !buttons.IsBarrelButtonPressed && !buttons.IsEraser);
    }

    internal static SolidColorBrush Paint(int rgb) => new(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    private sealed record NodeVisual(MapNode Node, Border Card, ScaleTransform Scale);
    private sealed record LinkVisual(MapNode Source, MapNode Target, Curve VisibleCurve, Curve HitCurve, Border? Label);

    private sealed class Curve
    {
        private readonly PathFigure _figure = new();
        private readonly BezierSegment _segment = new();

        public Curve()
        {
            _figure.Segments.Add(_segment);
            Geometry.Figures.Add(_figure);
        }

        public PathGeometry Geometry { get; } = new();

        public void SetPoints(Point start, Point control1, Point control2, Point end)
        {
            _figure.StartPoint = start;
            _segment.Point1 = control1;
            _segment.Point2 = control2;
            _segment.Point3 = end;
        }
    }
}

internal readonly record struct MapColors(string Name, int Accent, int Surface, int Border, int Badge, int Selected)
{
    private static readonly MapColors[] Palettes =
    [
        new("Фиолетовый", 0xC1ADFF, 0x27213D, 0x665488, 0x4B3876, 0x35274F),
        new("Бирюзовый", 0x82E1DB, 0x1B333D, 0x3E7078, 0x26515A, 0x234750),
        new("Янтарный", 0xF6CC82, 0x362D2C, 0x7C654B, 0x62472D, 0x4B3728),
        new("Розовый", 0xF7AFCB, 0x382638, 0x7D4E6A, 0x5E3656, 0x4D2E48),
        new("Синий", 0xA7C2FF, 0x222F48, 0x4C6494, 0x354875, 0x293D61),
        new("Зелёный", 0xA5DB9B, 0x23362F, 0x4B7457, 0x375640, 0x2D4637)
    ];

    public static MapColors At(int index) => Palettes[index];
}
