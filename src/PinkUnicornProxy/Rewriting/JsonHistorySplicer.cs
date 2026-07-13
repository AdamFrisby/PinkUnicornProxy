using System.Text.Json;

namespace PinkUnicornProxy.Rewriting;

internal static class JsonHistorySplicer
{
    public static bool ContainsDuplicateProperties(byte[] json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 256,
        });
        return ContainsDuplicateProperties(document.RootElement);
    }

    public static bool ContainsUserTrigger(
        byte[] json,
        ProviderRequestKind provider,
        IReadOnlyList<string> triggerTokens)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 256,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string historyProperty = provider == ProviderRequestKind.OpenAIResponses
            ? "input"
            : "messages";
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals(historyProperty)
                && HistoryContainsUserTrigger(property.Value, provider, triggerTokens))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryFindTopLevelValue(
        ReadOnlySpan<byte> json,
        string propertyName,
        out JsonByteRange range)
    {
        range = default;
        Utf8JsonReader reader = new(json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 256,
        });

        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        int matches = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
            {
                return false;
            }

            bool isTarget = reader.ValueTextEquals(propertyName);
            if (!reader.Read())
            {
                return false;
            }

            int start = checked((int)reader.TokenStartIndex);
            if (!reader.TrySkip())
            {
                return false;
            }

            int end = checked((int)reader.BytesConsumed);
            if (isTarget)
            {
                matches++;
                range = new JsonByteRange(start, end - start);
            }
        }

        return matches == 1;
    }

    public static byte[] Splice(
        ReadOnlySpan<byte> original,
        JsonByteRange range,
        ReadOnlySpan<byte> replacement)
    {
        byte[] result = new byte[original.Length - range.Length + replacement.Length];
        original[..range.Start].CopyTo(result);
        replacement.CopyTo(result.AsSpan(range.Start));
        original[(range.Start + range.Length)..].CopyTo(
            result.AsSpan(range.Start + replacement.Length));
        return result;
    }

    public static bool TryConcatenateArrays(
        ReadOnlySpan<byte> immutablePrefix,
        ReadOnlySpan<byte> suffix,
        out byte[]? combined)
    {
        combined = null;
        if (!HasArrayShape(immutablePrefix) || !HasArrayShape(suffix))
        {
            return false;
        }

        ReadOnlySpan<byte> prefixItems = immutablePrefix[1..^1];
        ReadOnlySpan<byte> suffixItems = suffix[1..^1];
        bool prefixHasItems = ContainsNonWhitespace(prefixItems);
        bool suffixHasItems = ContainsNonWhitespace(suffixItems);

        using MemoryStream output = new(
            immutablePrefix.Length + suffix.Length + (prefixHasItems && suffixHasItems ? 1 : 0));
        output.WriteByte((byte)'[');
        output.Write(prefixItems);
        if (prefixHasItems && suffixHasItems)
        {
            output.WriteByte((byte)',');
        }

        output.Write(suffixItems);
        output.WriteByte((byte)']');
        combined = output.ToArray();
        return true;
    }

    private static bool ContainsDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || ContainsDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (ContainsDuplicateProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasArrayShape(ReadOnlySpan<byte> value) =>
        value.Length >= 2 && value[0] == (byte)'[' && value[^1] == (byte)']';

    private static bool ContainsNonWhitespace(ReadOnlySpan<byte> value)
    {
        foreach (byte character in value)
        {
            if (character != (byte)' '
                && character != (byte)'\t'
                && character != (byte)'\r'
                && character != (byte)'\n')
            {
                return true;
            }
        }

        return false;
    }

    private static bool HistoryContainsUserTrigger(
        JsonElement history,
        ProviderRequestKind provider,
        IReadOnlyList<string> triggerTokens)
    {
        if (provider == ProviderRequestKind.OpenAIResponses
            && history.ValueKind == JsonValueKind.String)
        {
            return ContainsAny(history.GetString(), triggerTokens);
        }

        if (history.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in history.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            bool mayBeUser = item.EnumerateObject().Any(property =>
                property.NameEquals("role")
                && property.Value.ValueKind == JsonValueKind.String
                && property.Value.ValueEquals("user"));
            if (!mayBeUser)
            {
                continue;
            }

            foreach (JsonProperty property in item.EnumerateObject())
            {
                if (property.NameEquals("content")
                    && ContentContainsTrigger(property.Value, provider, triggerTokens))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContentContainsTrigger(
        JsonElement content,
        ProviderRequestKind provider,
        IReadOnlyList<string> triggerTokens)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return ContainsAny(content.GetString(), triggerTokens);
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            bool isTextBlock = block.EnumerateObject().Any(property =>
                property.NameEquals("type")
                && property.Value.ValueKind == JsonValueKind.String
                && IsTextBlockType(provider, property.Value.GetString()));
            if (!isTextBlock)
            {
                continue;
            }

            if (block.EnumerateObject().Any(property =>
                property.NameEquals("text")
                && property.Value.ValueKind == JsonValueKind.String
                && ContainsAny(property.Value.GetString(), triggerTokens)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTextBlockType(ProviderRequestKind provider, string? type) =>
        provider == ProviderRequestKind.AnthropicMessages
            ? type == "text"
            : type is "text" or "input_text" or "output_text";

    private static bool ContainsAny(string? text, IReadOnlyList<string> tokens) =>
        text is not null && tokens.Any(token => text.Contains(token, StringComparison.Ordinal));
}

internal readonly record struct JsonByteRange(int Start, int Length);
