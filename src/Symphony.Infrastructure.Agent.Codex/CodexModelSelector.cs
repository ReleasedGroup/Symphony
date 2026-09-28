using System.Text.Json;

namespace Symphony.Infrastructure.Agent.Codex;

internal static class CodexModelSelector
{
    public static string? SelectOverride(JsonElement catalogResponse, string? selectedModel)
    {
        if (catalogResponse.ValueKind != JsonValueKind.Object ||
            !catalogResponse.TryGetProperty("result", out var catalogResult) ||
            catalogResult.ValueKind != JsonValueKind.Object ||
            !catalogResult.TryGetProperty("data", out var models) ||
            models.ValueKind != JsonValueKind.Array ||
            catalogResult.TryGetProperty("nextCursor", out var nextCursor) &&
            nextCursor.ValueKind == JsonValueKind.String)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(selectedModel))
        {
            return null;
        }

        foreach (var item in models.EnumerateArray())
        {
            if (string.Equals(GetModelId(item), selectedModel, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("isDefault", out var isDefault) &&
                isDefault.ValueKind == JsonValueKind.True &&
                !IsHidden(item))
            {
                return GetModelId(item);
            }
        }

        foreach (var item in models.EnumerateArray())
        {
            if (!IsHidden(item))
            {
                return GetModelId(item);
            }
        }

        return null;
    }

    private static string? GetModelId(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in new[] { "id", "model" })
        {
            if (item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static bool IsHidden(JsonElement item)
        => item.ValueKind == JsonValueKind.Object &&
           item.TryGetProperty("hidden", out var value) && value.ValueKind == JsonValueKind.True;
}
