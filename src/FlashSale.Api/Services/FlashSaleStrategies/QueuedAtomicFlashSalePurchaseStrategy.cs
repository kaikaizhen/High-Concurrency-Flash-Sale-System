using FlashSale.Api.Common.Constants;
using FlashSale.Api.Common.Enums;
using FlashSale.Api.Common.Exceptions;
using FlashSale.Api.Infrastructure.Messaging;
using FlashSale.Api.Models.Dtos.FlashSales;
using FlashSale.Api.Models.Messages;
using FlashSale.Api.Options;
using FlashSale.Api.Repositories.Interfaces;
using FlashSale.Api.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace FlashSale.Api.Services.FlashSaleStrategies;

/// <summary>
/// Stage 5 —— Atomic Update + 非同步建立訂單。
///
///     UPDATE Products SET Stock = Stock - @qty
///     WHERE Id = @id AND Stock >= @qty          ← 仍然同步，仍然是真相來源
///         │
///         ▼
///     Publish OrderCreated                       ← 訂單建立交給 Worker
///         │
///         ▼
///     202 Accepted
///
/// **為什麼庫存扣減不能一起非同步？**
///
/// 因為它是唯一決定「這個人有沒有買到」的判斷。放進佇列的話，
/// API 在還不知道有沒有庫存時就得先回應客戶「成功」，
/// 之後才發現賣完 —— 那是把超賣從資料錯誤變成了對客戶的謊言。
///
/// 削峰填谷只能套用在**可以容忍延遲**的工作上。
/// 扣庫存不行，建訂單可以。
///
/// **那到底省了什麼？**
///
/// 同步版本把 UPDATE 與 INSERT 包在同一個交易裡，
/// 那一列的排他鎖必須持有到 INSERT 完成並 commit 為止。
/// 這個版本的 UPDATE 是獨立交易，鎖只在單一語句期間持有 ——
/// **臨界區變短了**，同一列的排隊速度就變快。
/// 加上 INSERT 完全移出請求路徑，API 的回應時間不再包含它。
/// </summary>
public class QueuedAtomicFlashSalePurchaseStrategy : IFlashSalePurchaseStrategy
{
    private readonly IProductRepository _productRepository;
    private readonly IMessagePublisher _publisher;
    private readonly RabbitMqOptions _rabbitMqOptions;
    private readonly ILogger<QueuedAtomicFlashSalePurchaseStrategy> _logger;

    public QueuedAtomicFlashSalePurchaseStrategy(
        IProductRepository productRepository,
        IMessagePublisher publisher,
        IOptions<RabbitMqOptions> rabbitMqOptions,
        ILogger<QueuedAtomicFlashSalePurchaseStrategy> logger)
    {
        _productRepository = productRepository;
        _publisher = publisher;
        _rabbitMqOptions = rabbitMqOptions.Value;
        _logger = logger;
    }

    public FlashSaleStrategy Strategy => FlashSaleStrategy.AtomicQueued;

    public async Task<FlashSalePurchaseResult> PurchaseAsync(
        CreateFlashSaleDtoModel dto)
    {
        if (!_rabbitMqOptions.Enabled)
        {
            throw new BusinessException(
                "Message queue is disabled, AtomicQueued strategy is unavailable.");
        }

        // 1. 扣庫存 —— 同步、原子、單一語句。這一步決定成敗。
        var affected = await _productRepository
            .TryDeductStockAsync(dto.ProductId, dto.Quantity);

        if (affected == 0)
        {
            var exists = await _productRepository.GetByIdAsync(dto.ProductId);

            if (exists is null)
            {
                throw new NotFoundException("Product not found.");
            }

            throw new BusinessException("Insufficient stock.");
        }

        var messageId = Guid.NewGuid();

        var message = new OrderCreatedMessage
        {
            MessageId = messageId,
            UserId = dto.UserId,
            ProductId = dto.ProductId,
            Quantity = dto.Quantity,
            OccurredAt = DateTime.UtcNow,

            // 有客戶端的 Key 就用它 —— 重送時 MessageId 會不同，
            // 只有客戶端的 Key 能讓 Worker 認出「這是同一筆訂單」。
            IdempotencyKey = dto.IdempotencyKey ?? messageId.ToString()
        };

        try
        {
            // 2. 發布事件，並等待 Broker 確認收到。
            await _publisher.PublishAsync(
                MessagingConstants.OrderExchange,
                MessagingConstants.OrderCreatedRoutingKey,
                message);
        }
        catch (Exception ex)
        {
            // 庫存已經扣掉但訊息沒送出去 —— 這件商品會永遠賣不出去也沒有訂單。
            // 補償：把庫存加回去。
            //
            // 這裡有一個無法用補償解決的殘餘風險：如果訊息其實已經送達
            // 而只是「確認」在回程遺失，補償就會把庫存加回去、訂單卻仍會建立。
            // 要徹底解決需要 Transactional Outbox（把訊息與庫存寫在同一個
            // 資料庫交易裡，再由背景程序投遞），那超出本階段範圍。
            _logger.LogError(
                ex,
                "Publish failed after stock deduction, compensating. ProductId={ProductId} MessageId={MessageId}",
                dto.ProductId,
                message.MessageId);

            await _productRepository.RestoreStockAsync(dto.ProductId, dto.Quantity);

            throw;
        }

        return FlashSalePurchaseResult.Queued(message.MessageId);
    }
}
