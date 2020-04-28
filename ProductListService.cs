using NDA.API.Product.DAL.Interfaces;
using NDA.API.Product.Entities;
using NDA.API.Product.Interfaces;
using NDA.API.Product.Models;
using NDA.Packages.Diagnostics.ILoggerInterface;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NDA.API.Product.Services
{
    public class ProductListService : IProductListService
    {
        private readonly IUnitOfWork _unit;
        private readonly ILogger _logger;
        private readonly IMessageQueueService _messageQueueService;

        public ProductListService(
            IUnitOfWork unit,
            ILogger logger,
            IMessageQueueService messageQueueService)
        {
            _unit = unit;
            _logger = logger;
            _messageQueueService = messageQueueService;
        }

        #region AddProductToListAsync

        public async Task AddProductToListAsync(ProductListItemModel product, bool isPremiumProduct)
        {
            var response = await _messageQueueService.SendUserShopRequest(product.UserId, product.ShopId);

            if (!response.IsAssigned)
            {
                _logger.Error($"User '{product.UserId}' doesn't assigned to shop '{product.ShopId}'");
                throw new Exception($"User '{product.UserId}' doesn't assigned to shop '{product.ShopId}'");
            }

            _logger.Info("Received userShop response from shop");

            var shopProductListId = _unit.ShopProductListRepository
                .GetByCondition(x => x.ShopId == product.ShopId).FirstOrDefault().Id;

            try
            {
                await _unit.ProductListRepository.CreateAsync(new ProductLists
                { ProductId = product.ProductId, ShopProductListId = shopProductListId });
                await _unit.SaveAsync();
            }
            catch (Exception ex)
            {
                if (isPremiumProduct)
                {
                    await _messageQueueService.SendDecreasePremiumProductsCountEvent(product.AccountId);
                }

                throw ex;
            }
        }

        #endregion
    }
}
