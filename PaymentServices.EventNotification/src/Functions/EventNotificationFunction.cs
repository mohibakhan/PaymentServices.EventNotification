using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using PaymentServices.EventNotification.Models;
using PaymentServices.EventNotification.Repositories;
using PaymentServices.EventNotification.Services;
using PaymentServices.Shared.Enums;
using PaymentServices.Shared.Infrastructure;
using PaymentServices.Shared.Messages;

namespace PaymentServices.EventNotification.Functions;

/// <summary>
/// Service Bus Trigger — subscribed to event-notification subscription.
/// Handles ALL terminal states:
///   Success:  TransferCompleted
///   Pending:  KycManualReview, TmsComplianceAlert
///   Failed:   AccountResolutionFailed, KycFailed, TmsFailed, TransferFailed
///
/// Posts result to RTPSend webhook with retry + exponential backoff.
/// Dead letters if all retries exhausted.
/// </summary>
public sealed class EventNotificationFunction
{
    private readonly IRtpSendWebhookService _webhookService;
    private readonly ITransactionStateRepository _transactionStateRepository;
    private readonly ILogger<EventNotificationFunction> _logger;

    public EventNotificationFunction(
        IRtpSendWebhookService webhookService,
        ITransactionStateRepository transactionStateRepository,
        ILogger<EventNotificationFunction> logger)
    {
        _webhookService = webhookService;
        _transactionStateRepository = transactionStateRepository;
        _logger = logger;
    }

    [Function(nameof(EventNotificationFunction))]
    public async Task RunAsync(
        [ServiceBusTrigger(
            topicName: "%app:AppSettings:SERVICE_BUS_TOPIC%",
            subscriptionName: "%app:AppSettings:SERVICE_BUS_NOTIFICATION_SUBSCRIPTION%",
            Connection = "app:AppSettings:SERVICE_BUS_CONNSTRING")]
        ServiceBusReceivedMessage serviceBusMessage,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        PaymentMessage? message = null;

        try
        {
            message = ServiceBusPublisher.Deserialize(serviceBusMessage);

            _logger.LogInformation(
                "EventNotification received. EvolveId={EvolveId} State={State} CorrelationId={CorrelationId}",
                message.EvolveId, message.State, message.CorrelationId);

            // Build webhook payload from terminal state
            var payload = WebhookPayloadMapper.FromMessage(message);

            // Post to RTPSend webhook with retry + exponential backoff
            await _webhookService.NotifyAsync(payload, cancellationToken);

            // Update Cosmos transaction state to NotificationSent
            await _transactionStateRepository.UpdateStateAsync(
                message.EvolveId,
                TransactionState.NotificationSent,
                cancellationToken: cancellationToken);

            _logger.LogInformation(
                "EventNotification sent successfully. EvolveId={EvolveId} Status={Status}",
                message.EvolveId, payload.Status);

            await messageActions.CompleteMessageAsync(serviceBusMessage, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation — let Service Bus retry naturally
            _logger.LogWarning(
                "EventNotification cancelled. EvolveId={EvolveId}",
                message?.EvolveId ?? "unknown");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "EventNotification failed after all retries. EvolveId={EvolveId} CorrelationId={CorrelationId}",
                message?.EvolveId ?? "unknown", message?.CorrelationId ?? "unknown");

            // Update Cosmos to NotificationFailed for visibility
            if (message is not null)
            {
                try
                {
                    await _transactionStateRepository.UpdateStateAsync(
                        message.EvolveId,
                        TransactionState.NotificationFailed,
                        tx => tx.FailureReason = $"Webhook delivery failed: {ex.Message}",
                        cancellationToken);
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx,
                        "Failed to update NotificationFailed state. EvolveId={EvolveId}",
                        message.EvolveId);
                }
            }

            // Dead letter — requires manual investigation
            await messageActions.DeadLetterMessageAsync(
                serviceBusMessage,
                deadLetterReason: "WebhookDeliveryFailed",
                deadLetterErrorDescription: ex.Message,
                cancellationToken: cancellationToken);
        }
    }
}
