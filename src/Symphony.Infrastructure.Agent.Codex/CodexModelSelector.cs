using System.Text.Json;

namespace Symphony.Infrastructure.Agent.Codex;

internal static class CodexModelSelector
{
    internal sealed record Model(string Id, bool IsDefault, bool IsHidden);

    internal sealed record Page(IReadOnlyList<Model> Models, string? NextCursor);

    public static Page? ParsePage(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("result", out var result) ||
            result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? nextCursor = null;
        if (result.TryGetProperty("nextCursor", out var cursor))
        {
            if (cursor.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                return null;
            }
            nextCursor = cursor.ValueKind == JsonValueKind.String ? cursor.GetString() : null;
        }

        var models = new List<Model>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = GetModelId(item);
            if (!string.IsNullOrWhiteSpace(id))
            {
                models.Add(new Model(id,
                    item.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True,
                    item.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True));
            }
        }

        return new Page(models, nextCursor);
    }

    public static string? SelectOverride(IReadOnlyList<Page> pages, string? selectedModel)
    {
        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            return null;
        }

        var models = pages.SelectMany(page => page.Models).ToArray();
        if (models.Any(model => model.Id.Equals(selectedModel, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return models.FirstOrDefault(model => model.IsDefault && !model.IsHidden)?.Id
            ?? models.FirstOrDefault(model => !model.IsHidden)?.Id;
    }

    private static string? GetModelId(JsonElement item)
    {
        foreach (var key in new[] { "id", "model" })
        {
            if (item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
