using System.Text.Json;
using PCActivityTracker.Data;

namespace PCActivityTracker.Services;

public sealed record SavedExplorerSearch(string Name, string Range, ActivitySearchQuery Conditions);

// Small encrypted preference, separate from automatic history. Dates are deliberately not retained.
public sealed class SavedExplorerSearchStore(SettingsRepository repository)
{
    private const string Key = "ExplorerSavedSearchesV1";
    public IReadOnlyList<SavedExplorerSearch> Read()
    {
        var json = repository.GetSettings().GetValueOrDefault(Key);
        if (string.IsNullOrEmpty(json)) return Array.Empty<SavedExplorerSearch>();
        if (json.Length > 256_000) throw new InvalidOperationException("保存条件のサイズが上限を超えています。");
        try
        {
            var items = JsonSerializer.Deserialize<SavedExplorerSearch[]>(json) ?? throw new JsonException();
            if (items.Length > 20) throw new JsonException();
            var normalized = items.Select(Validate).ToArray();
            if (normalized.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
                throw new JsonException();
            return normalized;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new InvalidOperationException("保存条件を読み込めません。既存データは変更していません。", error); }
    }

    public void Save(SavedExplorerSearch item)
    {
        item = Validate(item);
        var items = Read().Where(old => !old.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (items.Count >= 20) throw new ArgumentException("保存条件は最大20件です。不要な条件を削除してください。");
        items.Add(item);
        Write(items);
    }

    public void Delete(string name) => Write(Read().Where(item => !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray());

    private void Write(IReadOnlyList<SavedExplorerSearch> items)
    {
        var json = JsonSerializer.Serialize(items);
        if (json.Length > 256_000) throw new ArgumentException("保存条件全体が大きすぎます。検索文字を短くするか、不要な条件を削除してください。");
        repository.Set(Key, json);
    }

    private static SavedExplorerSearch Validate(SavedExplorerSearch item)
    {
        if (item is null || string.IsNullOrWhiteSpace(item.Name) || item.Name.Trim().Length > 60 ||
            item.Name.Any(char.IsControl) || item.Range is not ("1日" or "7日" or "1か月" or "90日") || item.Conditions is null)
            throw new ArgumentException("条件名（60文字以内）と期間を確認してください。");
        var query = item.Conditions;
        if (query.ZoomMinutes is not (1 or 5 or 10 or 15 or 30 or 60) ||
            new[] { query.Text, query.App, query.Domain, query.Creator, query.Category, query.Project, query.ExcludedCategory }
                .Any(value => value is null || value.Length > 1000 || value.Any(char.IsControl)))
            throw new ArgumentException("検索条件は各1000文字以内で指定してください。");
        return item with { Name = item.Name.Trim(), Conditions = query with { Start = DateTimeOffset.UnixEpoch, End = DateTimeOffset.UnixEpoch } };
    }
}
