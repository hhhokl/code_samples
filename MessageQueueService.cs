using NDA.API.Product.Interfaces;
using NDA.Packages.Diagnostics.ILoggerInterface;
using NDA.Packages.ServiceBus.Contracts.IntegrationEvents;
using NDA.Packages.ServiceBus.Contracts.ResponseRequestCommunications;
using NDA.Packages.ServiceBus.Contracts.ResponseRequestCommunications.Account;
using NDA.Packages.ServiceBus.Contracts.ResponseRequestCommunications.Shop;
using NDA.Packages.ServiceBus.MassTransit.Infrastructure;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NDA.API.Product.Services
{
    public class MessageQueueService : IMessageQueueService
    {
        private readonly IServiceBusClient _serviceBusClient;
        private readonly ILogger _logger;

        public MessageQueueService(
            IServiceBusClient serviceBusClient,
            ILogger logger)
        {
            _serviceBusClient = serviceBusClient;
            _logger = logger;
        }

        #region SendIncreasePremiumProductsCountRequest

        public async Task<IncreasePremiumProductsCountResponse> SendIncreasePremiumProductsCountRequest(Guid accountId)
        {
            var request = new IncreasePremiumProductsCountRequest
            {
                AccountId = accountId
            };

            return await _serviceBusClient.SendRequst<IncreasePremiumProductsCountRequest, IncreasePremiumProductsCountResponse>(request);
        }

        #endregion

        #region SendDecreasePremiumProductsCountEvent

        public async Task SendDecreasePremiumProductsCountEvent(Guid accountId)
        {
            var decreaseEvent = new DecreasePremiumProductsCountEvent
            {
                AccountId = accountId
            };

            await _serviceBusClient.Publish(decreaseEvent);
        }

        #endregion

        #region SendUserShopRequest

        public async Task<GetUserShopResponse> SendUserShopRequest(Guid userId, int shopId)
        {
            GetUserShopRequest request = new GetUserShopRequest
            {
                UserId = userId,
                ShopId = shopId
            };

            _logger.Info("Sending userShop request to shop service");
            return await _serviceBusClient.SendRequst<GetUserShopRequest, GetUserShopResponse>(request);
        }

        #endregion

        #region SendGetShopRequest

        public async Task<GetShopResponse> SendGetShopRequest(int shopId)
        {
            var request = new GetShopRequest
            {
                ShopId = shopId
            };

            return await _serviceBusClient.SendRequst<GetShopRequest, GetShopResponse>(request);
        }

        #endregion

        #region SendUpdateAccountsPremiumProductsCountRequest

        public async Task<UpdateAccountsPremiumProductsCountResponse> SendUpdateAccountsPremiumProductsCountRequest(Dictionary<Guid, int> accountsPremiumProductsCount)
        {
            var request = new UpdateAccountsPremiumProductsCountRequest
            {
                AccountsPremiumProductsCount = accountsPremiumProductsCount
            };

            return await _serviceBusClient.SendRequst<UpdateAccountsPremiumProductsCountRequest, UpdateAccountsPremiumProductsCountResponse>(request);
        }

        #endregion
    }
}
