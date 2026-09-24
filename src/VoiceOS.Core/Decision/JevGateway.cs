using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VoiceOS.Core.Decision;

public interface IJevGateway
{
    Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(
        object state,
        IReadOnlyDictionary<string, JevQuestionDto> questions,
        CancellationToken cancellationToken = default);
}

public sealed class TypeSafeJevGateway : IJevGateway
{
    private const string Endpoint = "https://api.typesafe.ai/v1/systemone";
    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _http;
    private readonly ILogger<TypeSafeJevGateway> _logger;

    public TypeSafeJevGateway(string apiKey, string model, HttpClient http, ILogger<TypeSafeJevGateway> logger)
    {
        _apiKey = apiKey;
        _model = model;
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, JevAnswer>> AskAsync(
        object state,
        IReadOnlyDictionary<string, JevQuestionDto> questions,
        CancellationToken cancellationToken = default)
    {
        string body;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.Authorization = new("Bearer", _apiKey);
                request.Content = JsonContent.Create(new { model = _model, state, questions });
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Jev returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (attempt == 0 && IsTransient(ex, cancellationToken))
            {
                _logger.LogWarning("Jev transient transport failure; retrying once. Type={Type} Status={Status}",
                    ex.GetType().Name, (ex as HttpRequestException)?.StatusCode);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var answers = new Dictionary<string, JevAnswer>(StringComparer.Ordinal);
            if (document.RootElement.TryGetProperty("answers", out var root))
                foreach (var property in root.EnumerateObject())
                    answers[property.Name] = JevAnswerParser.Parse(property.Value);
            return answers;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            _logger.LogError(ex, "Failed to parse bounded Jev response");
            throw new InvalidOperationException("Jev returned an invalid response.", ex);
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken cancellationToken)
        => ex is HttpRequestException http && (http.StatusCode is null
            || http.StatusCode == HttpStatusCode.TooManyRequests
            || (int)http.StatusCode == 529
            || (int)http.StatusCode >= 500)
           || ex is TaskCanceledException && !cancellationToken.IsCancellationRequested;
}

public static class JevAnswerParser
{
    public static JevAnswer Parse(JsonElement element)
    {
        var type = element.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString() ?? "choice"
            : "choice";
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        string? choice = null;
        double confidence = 0;

        if (type == "noul")
        {
            var probability = ReadProbability(element, "noul") ?? 0;
            probabilities["noul"] = probability;
            choice = probability >= .5 ? "true" : "false";
            confidence = ReadProbability(element, "confidence") ?? probability;
        }
        else if (type == "choice")
        {
            if (element.TryGetProperty("choice", out var choiceElement))
                choice = choiceElement.GetString();
            confidence = ReadProbability(element, "confidence") ?? 0;
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "type" or "choice" or "confidence" || property.Value.ValueKind != JsonValueKind.Number)
                    continue;
                var value = property.Value.GetDouble();
                if (value is >= 0 and <= 1)
                    probabilities[property.Name] = value;
            }
        }
        else
        {
            throw new InvalidOperationException($"Unsupported Jev answer type '{type}'.");
        }

        if (confidence is < 0 or > 1 || !double.IsFinite(confidence))
            throw new InvalidOperationException("Jev confidence is outside [0,1].");
        return new(type, choice, probabilities, confidence);
    }

    private static double? ReadProbability(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        var result = value.GetDouble();
        if (!double.IsFinite(result) || result is < 0 or > 1)
            throw new InvalidOperationException($"Jev probability '{name}' is outside [0,1].");
        return result;
    }
}
