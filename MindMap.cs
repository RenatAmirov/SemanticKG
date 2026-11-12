using System.Text.Json;

namespace SemanticKG;

internal sealed class MindMap
{
    public const int TitleLimit = 100;
    public const int TagLimit = 24;
    public const int LabelLimit = 60;
    public const int NotesLimit = 4000;
    public const int ColorCount = 6;

    public int Version { get; init; } = 1;
    public string Title { get; set; } = "";
    public List<MapNode> Nodes { get; init; } = [];
    public List<MapLink> Links { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<MindMap> OpenAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var map = await JsonSerializer.DeserializeAsync<MindMap>(stream, JsonOptions)
            ?? throw new InvalidDataException("Файл не содержит карту.");
        map.Validate();
        return map;
    }

    public async Task SaveAsync(string path)
    {
        var contents = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public MapLink? FindLink(Guid first, Guid second) =>
        Links.Find(link => (link.SourceId == first && link.TargetId == second) ||
                           (link.SourceId == second && link.TargetId == first));

    public void RemoveNode(Guid id)
    {
        Links.RemoveAll(link => link.Contains(id));
        Nodes.RemoveAll(node => node.Id == id);
    }

    private void Validate()
    {
        if (Version != 1)
            throw new InvalidDataException("Эта версия файла карты не поддерживается.");

        if (string.IsNullOrWhiteSpace(Title) || Title.Length > TitleLimit || Nodes is null || Links is null)
            throw new InvalidDataException("В файле отсутствуют данные карты или её название.");

        var nodeIds = new HashSet<Guid>();
        foreach (var node in Nodes)
        {
            if (node is null || node.Id == Guid.Empty || !nodeIds.Add(node.Id) ||
                string.IsNullOrWhiteSpace(node.Title) || node.Title.Length > TitleLimit ||
                node.Notes is null || node.Notes.Length > NotesLimit ||
                node.Color is < 0 or >= ColorCount || !double.IsFinite(node.X) || !double.IsFinite(node.Y) ||
                Math.Abs(node.X) > 1e9 || Math.Abs(node.Y) > 1e9 || node.Tags is null ||
                node.Tags.Any(tag => string.IsNullOrWhiteSpace(tag) || tag.Length > TagLimit) ||
                node.Tags.Distinct(StringComparer.OrdinalIgnoreCase).Count() != node.Tags.Count)
            {
                throw new InvalidDataException("В файле есть узел с некорректными данными.");
            }
        }

        var linkIds = new HashSet<Guid>();
        var pairs = new HashSet<(Guid, Guid)>();
        foreach (var link in Links)
        {
            if (link is null || link.Id == Guid.Empty || !linkIds.Add(link.Id) ||
                link.SourceId == link.TargetId || !nodeIds.Contains(link.SourceId) || !nodeIds.Contains(link.TargetId) ||
                link.Label is null || link.Label.Length > LabelLimit)
            {
                throw new InvalidDataException("В файле есть некорректная связь.");
            }

            var pair = link.SourceId.CompareTo(link.TargetId) < 0
                ? (link.SourceId, link.TargetId)
                : (link.TargetId, link.SourceId);
            if (!pairs.Add(pair))
                throw new InvalidDataException("В файле есть повторяющаяся связь.");
        }
    }

    public static MindMap CreateExample()
    {
        var map = new MindMap { Title = "Запуск продукта" };
        MapNode Node(string title, double x, double y, int color, string notes, params string[] tags)
        {
            var node = new MapNode { Title = title, X = x, Y = y, Color = color, Notes = notes, Tags = [.. tags] };
            map.Nodes.Add(node);
            return node;
        }

        void Link(MapNode source, MapNode target, string label = "") =>
            map.Links.Add(new MapLink { SourceId = source.Id, TargetId = target.Id, Label = label });

        var launch = Node("Запуск продукта", 310, 217, 0,
            "Подготовить первую версию и проверить, решает ли она задачу пользователей.", "продукт", "приоритет");
        var research = Node("Исследование", 45, 74, 1,
            "Провести интервью и выделить повторяющиеся потребности.", "данные");
        var strategy = Node("Стратегия", 310, 30, 2,
            "Определить аудиторию, ценность продукта и цели запуска.", "продукт");
        var design = Node("Дизайн", 575, 74, 3,
            "Собрать прототип и провести тестирование сценариев.", "команда");
        var development = Node("Разработка", 575, 356, 4,
            "Оценить объём первой версии и согласовать этапы реализации.", "команда", "приоритет");
        var marketing = Node("Маркетинг", 310, 400, 5,
            "Подготовить страницу продукта и план анонса.", "команда");
        var metrics = Node("Метрики", 45, 356, 1,
            "Согласовать показатели, по которым оценим первый релиз.", "данные");

        Link(launch, research, "изучить");
        Link(launch, strategy, "определить");
        Link(launch, design);
        Link(launch, development, "реализовать");
        Link(launch, marketing);
        Link(launch, metrics, "измерить");
        Link(research, strategy, "данные");
        Link(design, development, "макеты");
        return map;
    }
}

internal sealed class MapNode
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Tags { get; init; } = [];
    public int Color { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

internal sealed class MapLink
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceId { get; init; }
    public Guid TargetId { get; init; }
    public string Label { get; set; } = "";

    public bool Contains(Guid id) => SourceId == id || TargetId == id;

    public Guid OtherEnd(Guid id) => SourceId == id ? TargetId : SourceId;
}
