using System;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using NDA.Integration.Shopify.Core.Dto;
using NDA.Integration.Shopify.Core.Dto.Account;
using NDA.Integration.Shopify.Core.Entities;
using NDA.Integration.Shopify.Core.Interfaces;
using NDA.Integration.Shopify.Core.Services;
using NDA.Integration.Shopify.Options;
using NDA.Packages.Diagnostics.ILoggerInterface;
using Microsoft.AspNetCore.Http;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Options;
using ShopifySharp;

namespace NDA.Integration.Shopify.Infrastructure.Shopify
{
    public class ShopifyService : IShopifyService
    {
        private readonly IShopifyStoreService _shopifyStoreService;
        private readonly ILogger _logger;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly INDAShopClient _NDAShopClient;
        private readonly ITokenClient _tokenClient;
        private readonly INDAAccountClient _accountClient;
        private readonly IShopifyChargeStore _shopifyChargeStore;
        private readonly ShopifyOptions _shopifyOptions;

        public ShopifyService(IOptions<ShopifyOptions> shopifyOptions,
            IShopifyStoreService shopifyStoreService,
            ILogger logger,
            IHttpContextAccessor httpContextAccessor,
            INDAShopClient NDAShopClient,
            ITokenClient tokenClient,
            INDAAccountClient accountClient,
            IShopifyChargeStore  shopifyChargeStore)
        {
            _shopifyStoreService = shopifyStoreService;
            _logger = logger;
            _httpContextAccessor = httpContextAccessor;
            _NDAShopClient = NDAShopClient;
            _tokenClient = tokenClient;
            _accountClient = accountClient;
            _shopifyChargeStore = shopifyChargeStore;
            _shopifyOptions = shopifyOptions.Value;
        }

        public ShopifyOptions ShopifyOptions
        {
            get { return _shopifyOptions; }
        }

        public async Task InstallAsync(string shop, string accessToken, string NDAAccountId, int NDAShopId)
        {
            var shopService = new ShopService(shop, accessToken);
            Shop shopProperties = await shopService.GetAsync();

            //Continue install
            var token = await _tokenClient.GetTokenAsync();
            if (token == null)
            {
                await AppInstallationFailed(shopService, shop, "Can't get token.");
                return;
            }

            var shopifyStores = await  _shopifyStoreService.GetShopifyStoreByDomainOrNDAShopId(shop, NDAShopId, 2);
            var shopifyStore = shopifyStores.FirstOrDefault(store => store.Shop == shop);
            if (shopifyStore is null)
            {
                var shopifyStoreWithShopId = shopifyStores.FirstOrDefault(store => store.NDAShopId == NDAShopId);
                if (shopifyStoreWithShopId != null) // otherwise the user has an empty NDA shop after registration
                {
                    (bool success, ShopDto NDAShop) = await CreateNDAShop(shopProperties, NDAAccountId, token);
                    if (!success)
                    {
                        await AppInstallationFailed(shopService, shop,$"Error while adding shop with token.");
                        return;
                    }

                    NDAShopId = NDAShop.Id;
                }

                shopifyStore = new ShopifyStore
                {
                    NDAShopId = NDAShopId,
                    Shop = shop
                };

                await CreateNewNDAIntegration(shopProperties, token, NDAShopId);
            }

            _logger.Info($"shopify store  = {shopifyStore.Shop} with NDAshopID = {shopifyStore.NDAShopId}");

            shopifyStore = await AddOrUpdateShopifyStore(shopifyStore, accessToken);
            await CreateAppUninstalledWebhook(shopifyStore);
            await CreateFulfillmentService(shopifyStore);
        }

        private Task AppInstallationFailed(ShopService shopService, string shop, string reason)
        {
            _logger.Error($"App installation for {shop} was not successful. Reason: {reason}.");
            return shopService.UninstallAppAsync();
        }

        public async Task UninstallAsync(string shopifyStoreId)
        {
            await _shopifyStoreService.RemoveShopifyStoreAsync(shopifyStoreId);
            _logger.Info($"{shopifyStoreId} was successfully uninstalled app.");
        }

        public Task<bool> ShopExistsAsync(string shop) => _shopifyStoreService.StoreExistsAsync(shop);

        public Task<bool> IsValidShopDomainAsync(string shop) => AuthorizationService.IsValidShopDomainAsync(shop);

        public string BuildAuthorizationUrl(string shop)
        {
            string redirectUrl = GetAppUrl() + ShopifyConstants.AuthorizationRedirectPath;
            Uri authUrl = AuthorizationService.BuildAuthorizationUrl(_shopifyOptions.Scopes, shop,
                _shopifyOptions.ApiKey, redirectUrl);

            return authUrl.ToString();
        }

        public async Task CancelFulfillmentAsync(string shop, string accessToken, long orderId, long fulfillmentId)
        {
            var fulfillmentService = new FulfillmentService(shop, accessToken);
            await fulfillmentService.CancelAsync(orderId, fulfillmentId);
        }

        public async Task OpenFulfillmentAsync(string shop, string accessToken, long orderId, long fulfillmentId)
        {
            var fulfillmentService = new FulfillmentService(shop, accessToken);
            await fulfillmentService.OpenAsync(orderId, fulfillmentId);
        }

        public async Task CompleteFulfillmentAsync(string shop, string accessToken, long orderId, long fulfillmentId)
        {
            var fulfillmentService = new FulfillmentService(shop, accessToken);
            try
            {
                await fulfillmentService.CompleteAsync(orderId, fulfillmentId);
            }
            catch (ShopifyException e)
            {
                _logger.Error(
                    $"Error happened when trying to complete fulfillment {fulfillmentId} for order {orderId}. Message: {e.Message}");
            }
        }

        public async Task<string> ChargeAsync(int NDAShopId, string NDAAccountId, string NDASubscriptionId, int subscriptionDurationId, string returnUrl)
        {
            var shop = await _shopifyStoreService.GetShopifyStoreByNDAShopIdAsync(NDAShopId);
            if (shop == null)
            {
                _logger.Error($"NDA shop({NDAShopId}) was not found.");
                return null;
            }

            var token = await _tokenClient.GetTokenAsync();
            (bool successful, SubscriptionDto subscription) =
                await _accountClient.GetSubscriptionAsync(token, NDASubscriptionId, subscriptionDurationId);
            if (!successful)
            {
                _logger.Error($"Failed to get subscription({NDASubscriptionId})");
                return null;
            }

            returnUrl = GetAppUrl() + returnUrl;
            if (!Uri.IsWellFormedUriString(returnUrl, UriKind.Absolute))
                return null;

            
            if (!Enum.IsDefined(typeof(ShopifyChargeType), subscription.Duration))
            {
                _logger.Error($"Unknown charge type '{subscription.Duration}'");
                return null;
            }

            try
            {
                var chargeType = (ShopifyChargeType)subscription.Duration;
                var chargeService =
                    ShopifyChargeServiceFactory.Create(shop.Shop, shop.AccessToken, chargeType);
                var confirmationUrl = await chargeService.CreateAsync(subscription.Name, subscription.Price, returnUrl,
                    _shopifyOptions.UseTestCharge);
                if (!Uri.IsWellFormedUriString(confirmationUrl, UriKind.Absolute))
                    return null;

                var uri = new Uri(confirmationUrl);
                var chargeId = long.Parse(uri.Segments[3].TrimEnd('/'));
                ShopifyCharge shopifyCharge;
                if (shop.Charge == null)
                {
                    shopifyCharge = new ShopifyCharge
                    {
                        ShopifyStoreId = shop.Id,
                        ChargeId = chargeId,
                        IsRecurringCharge = subscription.IsRecurring,
                        NDASubscriptionId = subscription.Id,
                        ChargeType = chargeType,
                        NDAAccountId = NDAAccountId,
                    };
                }
                else
                {
                    shopifyCharge = shop.Charge;
                    shopifyCharge.ChargeId = chargeId;
                    shopifyCharge.IsRecurringCharge = subscription.IsRecurring;
                    shopifyCharge.NDASubscriptionId = subscription.Id;
                    shopifyCharge.ChargeType = chargeType;
                    shopifyCharge.NDAAccountId = NDAAccountId;
                    shopifyCharge.ActivatedOn = null;
                    shopifyCharge.CreatedAt = null;
                    shopifyCharge.CancelledOn = null;
                }

                await _shopifyChargeStore.AddOrUpdateShopifyChargeAsync(shopifyCharge);

                return confirmationUrl;
            }
            catch (ShopifyException e)
            {
                _logger.Error(
                    $"Error happened when trying to create charge for {shop.Shop}. Message: {e.Message}");
                return null;
            }
        }

        public async Task<bool> ActivateChargeAsync(long chargeId)
        {
            var shopifyCharge = await _shopifyChargeStore.GetShopifyChargeByChargeIdAsync(chargeId);
            var shopifyStore = shopifyCharge?.ShopifyStore;
            if (shopifyCharge == null || shopifyStore == null)
            {
                _logger.Error($"Shopify charge({chargeId}) was not found.");
                return false;
            }

            try
            {
                if (!shopifyCharge.ChargeType.HasValue)
                    return false;


                var chargeService =
                    ShopifyChargeServiceFactory.Create(shopifyStore.Shop,
                        shopifyStore.AccessToken, shopifyCharge.ChargeType.Value);
                var charge = await chargeService.GetChargeByIdAsync(chargeId);
                switch (charge.Status)
                {
                    case "accepted":
                        await chargeService.ActivateChargeAsync(chargeId);

                        shopifyCharge.ActivatedOn = charge.ActivatedOn;
                        shopifyCharge.CreatedAt = charge.CreatedAt;
                        await _shopifyChargeStore.AddOrUpdateShopifyChargeAsync(shopifyCharge);

                        var token = await _tokenClient.GetTokenAsync();
                        _logger.Info($"Sending request to update subscription = {shopifyCharge.NDASubscriptionId} for" +
                            $" account {shopifyCharge.NDAAccountId} with token = {token}");
                        return await _accountClient.UpdateSubscriptionAsync(token, shopifyCharge.NDAAccountId,
                            shopifyCharge.NDASubscriptionId);
                    case "active":
                        return true;
                    default:
                        return false;
                }
            }
            catch (ShopifyException e)
            {
                _logger.Error(
                    $"Error happened when trying to activate charge({chargeId}) for {shopifyStore.Shop}. Message: {e.Message}");
                return false;
            }
        }

        public async Task FreezeInactiveChargesAsync()
        {
            var charges = await _shopifyChargeStore.GetRecurringChargesAsync();
            if (charges == null || Equals(!charges.Any()))
                return;

            foreach (var shopifyCharge in charges)
            {
                try
                {
                    var chargeService =
                        ShopifyChargeServiceFactory.Create(shopifyCharge.ShopifyStore.Shop,
                            shopifyCharge.ShopifyStore.AccessToken,
                            shopifyCharge.ChargeType ?? ShopifyChargeType.Monthly);
                    var chargeDto = await chargeService.GetChargeByIdAsync(shopifyCharge.ChargeId);
                    switch (chargeDto.Status)
                    {
                        case "declined":
                        case "expired":
                        case "frozen":
                        case "cancelled":
                            var token = await _tokenClient.GetTokenAsync();
                            await _accountClient.FreezeSubscriptionAsync(token, shopifyCharge.NDAAccountId);
                            shopifyCharge.IsFrozen = true;
                            shopifyCharge.CancelledOn = chargeDto.CancelledOn;
                            await _shopifyChargeStore.AddOrUpdateShopifyChargeAsync(shopifyCharge);
                            break;
                    }
                }
                catch (ShopifyException e)
                {
                    _logger.Error(
                        $"Error happened when trying to freeze charge({shopifyCharge.ChargeId}). Message: {e.Message}");
                }
            }
        }

        private async Task<string> Authorize(string shop, string code)
        {
            string accessToken;
            try
            {
                accessToken =
                    await AuthorizationService.Authorize(code, shop, _shopifyOptions.ApiKey,
                        _shopifyOptions.ApiSecretKey);
            }
            catch (ShopifyException e)
            {
                string message = $"Couldn't authorize {shop}. Reason: " + string.Join(", ", e.Errors);
                _logger.Error(message);
                throw;
            }

            return accessToken;
        }

        private async Task CreateAppUninstalledWebhook(ShopifyStore shopifyStore)
        {
            if (shopifyStore == null) throw new NullReferenceException();

            var shopifyWebhookService = new WebhookService(shopifyStore.Shop, shopifyStore.AccessToken);

            try
            {
                await shopifyWebhookService.CreateAsync(new Webhook
                {
                    Address = GetWebhookAddress(ShopifyWebhookTopic.AppUninstalled, shopifyStore.Id),
                    CreatedAt = DateTime.Now,
                    Format = ShopifyConstants.Format,
                    Topic = ShopifyWebhookTopic.AppUninstalled
                });
            }
            catch (ShopifyException e)
            {
                string message =
                    $"Couldn't create {ShopifyWebhookTopic.AppUninstalled} webhook for {shopifyStore.Shop}. Reason: " +
                    string.Join(", ", e.Errors);
                _logger.Error(message);
            }
        }

        private async Task CreateFulfillmentService(ShopifyStore shopifyStore)
        {
            if (shopifyStore == null) throw new NullReferenceException();

            FulfillmentServiceService fulfillmentServiceService =
                new FulfillmentServiceService(shopifyStore.Shop, shopifyStore.AccessToken);
            await fulfillmentServiceService.CreateAsync(
                new FulfillmentServiceEntity
                {
                    Name = _shopifyOptions.FulfillmentServiceName ?? ShopifyConstants.FulfillmentServiceName,
                    CallbackUrl = (_shopifyOptions.WebhooksParameters.UseTunnel
                                      ? _shopifyOptions.WebhooksParameters.TunnelAddress
                                      : GetAppUrl()) + $"/{ShopifyConstants.ShopifyRoute}",
                    Format = ShopifyConstants.Format,
                    InventoryManagement = true,
                    TrackingSupport = false,
                    RequiresShippingMethod = false
                });

            await CreateFulfillmentsCreateWebhook(shopifyStore);
        }

        private async Task CreateFulfillmentsCreateWebhook(ShopifyStore shopifyStore)
        {
            var shopifyWebhookService = new WebhookService(shopifyStore.Shop, shopifyStore.AccessToken);

            try
            {
                await shopifyWebhookService.CreateAsync(new Webhook
                {
                    Address = GetWebhookAddress(ShopifyWebhookTopic.FulfillmentsCreate, shopifyStore.Id),
                    CreatedAt = DateTime.Now,
                    Format = ShopifyConstants.Format,
                    Topic = ShopifyWebhookTopic.FulfillmentsCreate
                });
            }
            catch (ShopifyException e)
            {
                string message =
                    $"Couldn't create {ShopifyWebhookTopic.FulfillmentsCreate} webhook for {shopifyStore.Shop}. Reason: " +
                    string.Join(", ", e.Errors);
                _logger.Error(message);
            }
        }

        private string GetWebhookAddress(string webHookTopic, string storeId)
        {
            var route = $"/{ShopifyConstants.ShopifyWebhooksRoute}/{webHookTopic}?id={storeId}";
            var address = (_shopifyOptions.WebhooksParameters.UseTunnel
                              ? _shopifyOptions.WebhooksParameters.TunnelAddress
                              : GetAppUrl()) + route;

            return address;
        }

        private string GetAppUrl() =>
            $"{_httpContextAccessor.HttpContext.Request.Scheme}://{_httpContextAccessor.HttpContext.Request.Host.Value}";

        private async Task CheckShop(Shop shopProperties, string NDAAccountId, string token, int NDAShopId)
        {
            ShopifyStore shopifyStore = await _shopifyStoreService.GetShopifyStoreByNDAShopIdAsync(NDAShopId);
            if (shopifyStore is null) return;

            var res = await CreateNDAShop(shopProperties, NDAAccountId, token);
        }

        private Task<(bool success, ShopDto shop)> CreateNDAShop(Shop shopProperties, string NDAAccountId, string token)
        {
            var shopDto = new ShopDto
            {
                Name = shopProperties.Name,
                Email = shopProperties.Email,
                AccountId = Guid.Parse(NDAAccountId)
            };

            return _NDAShopClient.AddShopAsync(token, shopDto);
        }

        private Task CreateNewNDAIntegration(Shop shopProperties, string token, int NDAShopId)
        {
            return _NDAShopClient.AddIntegrationAsync(token, new IntegrationDto
            {
                Identifier = shopProperties.MyShopifyDomain,
                ShopId = NDAShopId,
                TypeName = ShopifyConstants.IntegrationTypeName
            });
        }

        private Task<ShopifyStore> AddOrUpdateShopifyStore(ShopifyStore shopifyStore, string accessToken)
        {
            shopifyStore.AccessToken = accessToken;
            shopifyStore.DeletionTime = null;
            shopifyStore.IsDeleted = false;

            return _shopifyStoreService.AddOrUpdateShopifyStoreAsync(shopifyStore);
        }
    }
}
