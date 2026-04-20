using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentServices.EventNotification.Models;

namespace PaymentServices.EventNotification.Services;

public interface IRtpSendWebhookService
{
    /// <summary>
    /// Posts the payment result to RTPSend webhook.
    /// Retries with exponential backoff on failure.
    /// Throws if all retries exhausted.
    /// </summary>
    Task NotifyAsync(
        RtpSendWebhookPayload payload,
        CancellationToken cancellationToken = default);
}

public sealed class RtpSendWebhookService : IRtpSendWebhookService
{
    private readonly HttpClient _httpClient;
    private readonly EventNotificationSettings _settings;
    private readonly ILogger<RtpSendWebhookService> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public RtpSendWebhookService(
        HttpClient httpClient,
        IOptions<EventNotificationSettings> settings,
        ILogger<RtpSendWebhookService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;

        // Configure timeout
        _httpClient.Timeout = TimeSpan.FromSeconds(
            _settings.RTP_SEND_WEBHOOK_TIMEOUT_SECONDS > 0
                ? _settings.RTP_SEND_WEBHOOK_TIMEOUT_SECONDS
                : 30);
    }

    public async Task NotifyAsync(
        RtpSendWebhookPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.RTP_SEND_WEBHOOK_URL))
            throw new InvalidOperationException("RTP_SEND_WEBHOOK_URL is not configured.");

        var maxRetries = _settings.RTP_SEND_WEBHOOK_MAX_RETRIES > 0
            ? _settings.RTP_SEND_WEBHOOK_MAX_RETRIES
            : 3;

        var body = JsonSerializer.Serialize(payload, _jsonOptions);
        Exception? lastException = null;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation(
                    "Calling RTPSend webhook. EvolveId={EvolveId} Status={Status} Attempt={Attempt}/{MaxRetries}",
                    payload.EvolveId, payload.Status, attempt, maxRetries);

                using var request = new HttpRequestMessage(
                    HttpMethod.Post, _settings.RTP_SEND_WEBHOOK_URL);

                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                // Add API key header if configured
                if (!string.IsNullOrWhiteSpace(_settings.RTP_SEND_WEBHOOK_API_KEY))
                {
                    request.Headers.Add(
                        _settings.RTP_SEND_WEBHOOK_API_KEY_HEADER,
                        _settings.RTP_SEND_WEBHOOK_API_KEY);
                }

                var response = await _httpClient.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "RTPSend webhook succeeded. EvolveId={EvolveId} StatusCode={StatusCode}",
                        payload.EvolveId, (int)response.StatusCode);
                    return;
                }

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "RTPSend webhook returned non-success. EvolveId={EvolveId} StatusCode={StatusCode} Body={Body} Attempt={Attempt}/{MaxRetries}",
                    payload.EvolveId, (int)response.StatusCode, responseBody, attempt, maxRetries);

                lastException = new HttpRequestException(
                    $"Webhook returned {(int)response.StatusCode}: {responseBody}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogWarning(ex,
                    "RTPSend webhook call failed. EvolveId={EvolveId} Attempt={Attempt}/{MaxRetries}",
                    payload.EvolveId, attempt, maxRetries);
            }

            // Exponential backoff before next retry
            if (attempt < maxRetries)
            {
                var delay = TimeSpan.FromMilliseconds(
                    200 * Math.Pow(2, attempt) * (0.5 + Random.Shared.NextDouble() * 0.5));

                _logger.LogInformation(
                    "Retrying webhook in {DelayMs}ms. EvolveId={EvolveId}",
                    delay.TotalMilliseconds, payload.EvolveId);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"RTPSend webhook failed after {maxRetries} attempts for EvolveId={payload.EvolveId}",
            lastException);
    }
}
