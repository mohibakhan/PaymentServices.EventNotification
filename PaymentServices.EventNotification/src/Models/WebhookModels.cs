using System.Text.Json.Serialization;
using PaymentServices.Shared.Enums;
using PaymentServices.Shared.Messages;

namespace PaymentServices.EventNotification.Models;

/// <summary>
/// Payload sent to RTPSend webhook.
/// Mirrors the original Node tptch/send response contract exactly.
/// </summary>
public sealed class RtpSendWebhookPayload
{
    /// <summary>
    /// "True" = success, "False" = failed, "Pending" = manual review / compliance alert.
    /// Matches original Node string values.
    /// </summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("evolveId")]
    public required string EvolveId { get; init; }

    [JsonPropertyName("fintechId")]
    public required string FintechId { get; init; }

    [JsonPropertyName("eveTransactionId")]
    public string? EveTransactionId { get; init; }

    [JsonPropertyName("gluId_s")]
    public string? GluIdSource { get; init; }

    [JsonPropertyName("gluId_d")]
    public string? GluIdDestination { get; init; }

    [JsonPropertyName("transactionFlags")]
    public IReadOnlyList<string> TransactionFlags { get; init; } = [];

    [JsonPropertyName("failureReason")]
    public string? FailureReason { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }
}

/// <summary>
/// Maps a <see cref="PaymentMessage"/> terminal state to the webhook payload.
/// </summary>
public static class WebhookPayloadMapper
{
    public static RtpSendWebhookPayload FromMessage(PaymentMessage message)
    {
        var status = message.State switch
        {
            TransactionState.TransferCompleted => "True",
            TransactionState.KycManualReview => "Pending",
            TransactionState.TmsComplianceAlert => "Pending",
            TransactionState.AccountResolutionFailed => "False",
            TransactionState.KycFailed => "False",
            TransactionState.TmsFailed => "False",
            TransactionState.TransferFailed => "False",
            _ => "False"
        };

        return new RtpSendWebhookPayload
        {
            Status = status,
            EvolveId = message.EvolveId,
            FintechId = message.FintechId,
            EveTransactionId = message.EveTransactionId,
            GluIdSource = message.GluIdSource,
            GluIdDestination = message.GluIdDestination,
            TransactionFlags = message.TransactionFlags,
            FailureReason = message.FailureReason,
            CorrelationId = message.CorrelationId
        };
    }
}
