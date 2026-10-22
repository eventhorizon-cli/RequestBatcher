using System.Globalization;
using System.Text.Json;

namespace RequestBatcher.Observability.IntegrationTests;

internal sealed class TempoClient
{
    private const string ApiPath = "api/datasources/proxy/uid/requestbatcher-tempo/api";
    private const string BatchName = "RequestBatcher.HandleBatch pricing-queries";
    private readonly HttpClient _client;

    public TempoClient(HttpClient client) => _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<IReadOnlyList<TempoSpan>> GetTraceAsync(string traceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        var normalizedTraceId = NormalizeId(traceId, 32);
        using var response = await _client.GetAsync($"{ApiPath}/traces/{Uri.EscapeDataString(normalizedTraceId)}",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Array.Empty<TempoSpan>();
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseTrace(document.RootElement);
    }

    public async Task<IReadOnlyList<string>> FindBatchTracesAsync(
        IReadOnlyCollection<string> callerTraceIds,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callerTraceIds);
        var ids = callerTraceIds.Select(id => NormalizeId(id, 32)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            return Array.Empty<string>();
        }

        // Tempo's TraceQL representation omits leading zeroes from link trace IDs.
        var links = string.Join(" || ", ids.Select(id => $"link:traceID = \"{id.TrimStart('0')}\""));
        var query = $"{{ span:name = \"{BatchName}\" && ({links}) }}";
        var path = $"{ApiPath}/search?q={Uri.EscapeDataString(query)}" +
                   $"&start={start.ToUnixTimeSeconds()}&end={end.ToUnixTimeSeconds()}&limit=1000";

        using var response = await _client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trace in document.RootElement.GetProperty("traces").EnumerateArray())
        {
            result.Add(ReadId(trace, "traceID", 32));
        }

        return result.ToArray();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"Tempo request {response.RequestMessage?.RequestUri} failed with " +
            $"{(int)response.StatusCode} {response.ReasonPhrase}: {body}",
            null,
            response.StatusCode);
    }

    private static IReadOnlyList<TempoSpan> ParseTrace(JsonElement root)
    {
        var result = new List<TempoSpan>();
        if (root.TryGetProperty("batches", out var batches))
        {
            foreach (var batch in batches.EnumerateArray())
            {
                if (batch.TryGetProperty("resourceSpans", out var nested))
                {
                    foreach (var resourceSpan in nested.EnumerateArray())
                    {
                        ParseResourceSpan(resourceSpan, result);
                    }
                }
                else
                {
                    ParseResourceSpan(batch, result);
                }
            }
        }
        else
        {
            foreach (var resourceSpan in root.GetProperty("resourceSpans").EnumerateArray())
            {
                ParseResourceSpan(resourceSpan, result);
            }
        }

        return result;
    }

    private static void ParseResourceSpan(JsonElement resourceSpan, ICollection<TempoSpan> result)
    {
        var resource = resourceSpan.GetProperty("resource");
        var attributes = ReadAttributes(resource.GetProperty("attributes"));
        var serviceName = attributes["service.name"];
        var scopes = resourceSpan.TryGetProperty("scopeSpans", out var scopeSpans)
            ? scopeSpans
            : resourceSpan.GetProperty("instrumentationLibrarySpans");

        foreach (var scope in scopes.EnumerateArray())
        {
            var scopeInfo = scope.TryGetProperty("scope", out var scopeProperty)
                ? scopeProperty
                : scope.GetProperty("instrumentationLibrary");
            var scopeName = scopeInfo.GetProperty("name").GetString()!;
            foreach (var span in scope.GetProperty("spans").EnumerateArray())
            {
                result.Add(ParseSpan(span, serviceName, scopeName));
            }
        }
    }

    private static TempoSpan ParseSpan(JsonElement span, string serviceName, string scopeName)
    {
        var traceId = ReadId(span, "traceId", 32);
        var spanId = ReadId(span, "spanId", 16);
        var parentSpanId = span.TryGetProperty("parentSpanId", out var parent)
            ? NormalizeId(parent.GetString()!, 16)
            : string.Empty;
        if (parentSpanId.All(static character => character == '0'))
        {
            parentSpanId = string.Empty;
        }

        var statusError = false;
        if (span.TryGetProperty("status", out var status) && status.TryGetProperty("code", out var code))
        {
            statusError = code.ValueKind == JsonValueKind.Number
                ? code.GetInt32() == 2
                : code.GetString()!.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
        }

        var links = new List<(string TraceId, string SpanId)>();
        if (span.TryGetProperty("links", out var linkArray))
        {
            foreach (var link in linkArray.EnumerateArray())
            {
                links.Add((ReadId(link, "traceId", 32), ReadId(link, "spanId", 16)));
            }
        }

        return new TempoSpan(
            serviceName,
            scopeName,
            span.GetProperty("name").GetString()!,
            traceId,
            spanId,
            parentSpanId,
            ReadKind(span.GetProperty("kind")),
            ReadInt64(span.GetProperty("startTimeUnixNano")),
            ReadInt64(span.GetProperty("endTimeUnixNano")),
            ReadAttributes(span.GetProperty("attributes")),
            links,
            statusError);
    }

    private static Dictionary<string, string> ReadAttributes(JsonElement attributes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var attribute in attributes.EnumerateArray())
        {
            result[attribute.GetProperty("key").GetString()!] = ReadValue(attribute.GetProperty("value"));
        }

        return result;
    }

    private static string ReadValue(JsonElement value)
    {
        foreach (var name in new[] { "stringValue", "intValue", "boolValue", "doubleValue" })
        {
            if (value.TryGetProperty(name, out var scalar))
            {
                return scalar.ValueKind == JsonValueKind.String ? scalar.GetString()! : scalar.GetRawText();
            }
        }

        throw new JsonException("Tempo attribute value has no supported OTLP scalar value.");
    }

    private static string ReadId(JsonElement element, string propertyName, int width) =>
        NormalizeId(element.GetProperty(propertyName).GetString()!, width);

    private static string NormalizeId(string value, int width)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException("Tempo trace or span ID is empty.");
        }

        value = value.Trim();
        if (value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
        {
            if (value.Length > width)
            {
                throw new JsonException($"Tempo ID is longer than {width} hex characters.");
            }

            return value.ToLowerInvariant().PadLeft(width, '0');
        }

        try
        {
            var hex = Convert.ToHexString(Convert.FromBase64String(value));
            if (hex.Length != width)
            {
                throw new JsonException($"Tempo base64 ID does not contain {width / 2} bytes.");
            }

            return hex.ToLowerInvariant();
        }
        catch (FormatException exception)
        {
            throw new JsonException("Tempo ID is neither standard hex nor base64.", exception);
        }
    }

    private static string ReadKind(JsonElement value)
    {
        var kind = value.ValueKind == JsonValueKind.Number
            ? value.GetInt32().ToString(CultureInfo.InvariantCulture)
            : value.GetString()!;
        if (int.TryParse(kind, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number switch
            {
                1 => "SPAN_KIND_INTERNAL",
                2 => "SPAN_KIND_SERVER",
                3 => "SPAN_KIND_CLIENT",
                4 => "SPAN_KIND_PRODUCER",
                5 => "SPAN_KIND_CONSUMER",
                _ => "SPAN_KIND_UNSPECIFIED"
            };
        }

        kind = kind.ToUpperInvariant();
        return kind.StartsWith("SPAN_KIND_", StringComparison.Ordinal) ? kind : $"SPAN_KIND_{kind}";
    }

    private static long ReadInt64(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetInt64(),
        JsonValueKind.String => long.Parse(value.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new JsonException("Tempo timestamp must be a number or numeric string.")
    };
}
