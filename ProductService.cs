using AutoMapper;
using NDA.API.Product.Attributes;
using NDA.API.Product.DAL.Interfaces;
using NDA.API.Product.Entities;
using NDA.API.Product.Interfaces;
using NDA.API.Product.Models;
using NDA.API.Product.ProductExport;
using NDA.API.Product.ProductImport;
using NDA.Packages.Diagnostics.ILoggerInterface;
using NDA.Packages.ServiceBus.Contracts.IntegrationEvents;
using NDA.Packages.ServiceBus.Contracts.IntegrationEvents.SharedModels;
using NDA.Packages.ServiceBus.Contracts.ResponseRequestCommunications.Supplier;
using NDA.Packages.ServiceBus.MassTransit.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NDA.Packages.ServiceBus.Contracts.Enums;
using static NDA.API.Product.Constants.SqlScriptConstant;
using ImportType = NDA.Packages.ServiceBus.Contracts.Enums.ImportType;
using ProductStatuses = NDA.API.Product.Models.ProductStatuses;
using NDA.Packages.ServiceBus.Contracts.ResponseRequestCommunications.Product;
using NDA.API.Product.ProductImport.TableTypes;
using System.Diagnostics;
using System.Data;

namespace NDA.API.Product.Services
{
    public class ProductService : IProductService
    {
        private readonly IUnitOfWork unit;
        private readonly ProductListFromXml _productListFromXml;
        private readonly IMapper _mapper;
        private readonly IServiceBusClient _serviceBusClient;
        private readonly IProductSpecificationService _productSpecificationService;
        private readonly ILogger _logger;
        private readonly IMessageQueueService _messageQueueService;

        public ProductService(
            IUnitOfWork unit,
            IMapper mapper,
            IServiceBusClient serviceBusClient,
            IProductSpecificationService productSpecificationService,
            ILogger logger,
            IMessageQueueService messageQueueService)
        {
            this.unit = unit;
            _productListFromXml = new ProductListFromXml(logger);
            _mapper = mapper;
            _serviceBusClient = serviceBusClient;
            _productSpecificationService = productSpecificationService;
            _logger = logger;
            _messageQueueService = messageQueueService;
        }

        public void Create(Entities.Products item)
        {
            try
            {
                unit.ProductsRepository.Create(item);
                unit.Save();
            }
            catch (Exception ex)
            {

            }
        }

        public async Task CreateAsync(Entities.Products item)
        {
            try
            {
                await unit.ProductsRepository.CreateAsync(item);
                await unit.SaveAsync();
            }
            catch (Exception ex)
            {

            }
        }

        public void Delete(long id)
        {
            try
            {
                unit.ProductsRepository.Delete(id);
                unit.Save();
            }
            catch (Exception ex)
            {

            }
        }

        public async Task DeleteAsync(long id)
        {
            try
            {
                unit.ProductsRepository.Delete(id);
                await unit.SaveAsync();
            }
            catch (Exception ex)
            {

            }
        }

        public Entities.Products Get(long id)
        {
            try
            {
                return unit.ProductsRepository.Get(id);
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        public Entities.Products GetByProductNumber(string productNumber, long supplierId)
        {
            try
            {
                return unit.ProductsRepository
                    .GetByCondition(p => p.SupplierId == supplierId && p.ProductNumber == productNumber, includeProperties: "ProductMedia")
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        public async Task<int> GetStatus(long id)
        {
            try
            {
                var variant = await unit.ProductVariantsRepository.GetAllQueryable().Where(x => x.Id == id)
                    .FirstOrDefaultAsync();
                return variant.StatusId.HasValue ? variant.StatusId.Value : -1;
            }
            catch (Exception ex)
            {
                _logger.Error("Error while getting product status");
                return -1;
            }
        }


        public async Task<Models.Product> GetAsync(long id)
        {
            GetPremiumSuppliersListResponce supplierIds = await PublishGetSupplierStatusEvent();

            try
            {
                var product = unit.ProductsRepository.GetAllQueryable().Where(x => x.Id == id)
                    .Include(_ => _.ProductVariants)
                    .ThenInclude(_ => _.ProductVariantAvailability)
                    .Include(_ => _.ProductMedia)
                    .FirstOrDefault();
                var shopIdListWithProduct = unit.ProductListRepository.GetAllQueryable().Where(_ => _.ProductId == id)
                    .Select(_ => _.ShopProductList.ShopId).ToList();
                var productDto = _mapper.Map<Entities.Products, Models.Product>(product);
                productDto.ShopIdLIst = shopIdListWithProduct;

                if (supplierIds != null && supplierIds.PremiumSuppliersList.Any())
                {
                    if (supplierIds.PremiumSuppliersList.TryGetValue(product.SupplierId.Value, out bool isPremium)) productDto.IsPremium = isPremium;
                }
                var unlockedRetailers = unit.UnlockedProductsRepository.GetAllQueryable().Where(_ => _.SupplierId == product.SupplierId).ToList();
                if (unlockedRetailers != null && unlockedRetailers.Count > 0)
                {
                    productDto.UnlockedShopIds = unlockedRetailers.Select(_ => _.ShopId);
                }

                productDto.Specification = await _productSpecificationService.GetProdSpecificationsAsync(product);

                return productDto;
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message, ex);
                return null;
            }
        }

        public IEnumerable<Entities.Products> GetList()
        {
            try
            {
                return unit.ProductsRepository.GetList();
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        public async Task<IEnumerable<CategoryModel>> GetCategoriesAsync()
        {
            try
            {
                var categories = await unit.CategoriesRepository.GetListAsync();
                return _mapper.Map<IEnumerable<Categories>, IEnumerable<CategoryModel>>(categories);

            }
            catch (Exception ex)
            {
                return null;
            }
        }

        private string BuildSuppliersString(ProductParameters parameters, GetPremiumSuppliersListResponce supplierIds)
        {

            var supplierIdsStr = string.Empty;
            if (supplierIds != null && supplierIds.PremiumSuppliersList.Any())
            {
                IEnumerable<KeyValuePair<int, bool>> filteredSuppliers = supplierIds.PremiumSuppliersList;
                if (parameters.IsPremium.HasValue)
                {
                    filteredSuppliers = supplierIds.PremiumSuppliersList.Where(supplier => supplier.Value == parameters.IsPremium);
                }
                if (!string.IsNullOrWhiteSpace(parameters.SupplierIDs))
                {
                    var suppliers = parameters.SupplierIDs.Split(",").ToList();
                    filteredSuppliers = filteredSuppliers.Where(supplier => suppliers.Contains(supplier.Key.ToString()));
                }
                var result = filteredSuppliers.Select(s => s.Key);
                supplierIdsStr = string.Join(",", result);
            }
            return supplierIdsStr;
        }

        public async Task<Models.ProductModel> GetListAsync(ProductParameters parameters)
        {
            try
            {
                GetPremiumSuppliersListResponce supplierIds = await PublishGetSupplierStatusEvent();

                var supplierIdsStr = BuildSuppliersString(parameters, supplierIds);
                if (string.IsNullOrEmpty(supplierIdsStr)) return new Models.ProductModel();

                var productIds = SearchProducts<long>(parameters, supplierIdsStr);
                if (productIds == null) return new Models.ProductModel();


                var products = await unit.ProductsRepository.GetAllQueryable().Where(product => productIds.Contains(product.Id))
                    .Include("ProductMedia")
                    .Include("ProductVariants.ProductVariantAvailability").ToListAsync();

                var productsDto = _mapper.Map<IEnumerable<Entities.Products>, IEnumerable<Models.Product>>(products);
                productsDto = productsDto.OrderBy(product => productIds.IndexOf(product.Id)); //sort in the same order as we get from DB

                foreach (var product in productsDto)
                {
                    product.ShopIdLIst = unit.ProductListRepository.GetAllQueryable().Where(_ => _.ProductId == product.Id)
                        .Select(_ => _.ShopProductList.ShopId).ToList();
                    var unlockedRetailers = unit.UnlockedProductsRepository.GetAllQueryable().Where(_ => _.SupplierId == product.SupplierId).ToList();
                    if (unlockedRetailers != null && unlockedRetailers.Count > 0)
                    {
                        product.UnlockedShopIds = unlockedRetailers.Select(_ => _.ShopId);
                    }
                    if (parameters.IsPremium.HasValue)
                    {
                        product.IsPremium = parameters.IsPremium.Value;
                    }
                    else
                    {
                        product.IsPremium = supplierIds.PremiumSuppliersList.FirstOrDefault(_ => _.Key == product.SupplierId).Value;
                    }
                }
                var countResult = SearchProducts<int>(parameters, supplierIdsStr, true);
                var count = countResult != null ? countResult.FirstOrDefault() : 0;
                _logger.Info($"Received '{count}' products");
                return new Models.ProductModel()
                {
                    Products = productsDto,
                    Length = count
                };
            }
            catch (Exception ex)
            {
                _logger.Error("Error while getting product list", ex);

                return null;
            }
        }

        public void Update(Entities.Products item)
        {
            try
            {
                unit.ProductsRepository.Update(item);
                unit.Save();
            }
            catch (Exception ex)
            {

            }
        }

        public async Task UpdateAsync(Entities.Products item)
        {
            try
            {
                unit.ProductsRepository.Update(item);
                await unit.SaveAsync();
            }
            catch (Exception ex)
            {

            }
        }

        public async Task UpdateProductAvailability(ProductDeltasFromImporter importInfo)
        {
            try
            {
                ProductStockUpdatedEvent updatedStockEvent = new ProductStockUpdatedEvent();
                List<ProductStockModel> stocks = new List<ProductStockModel>();

                await _productListFromXml.CreateProductListFromXml(importInfo, 2000);
                var products = _productListFromXml.Products;
                _logger.Info($"Counted {products.Count} products from Import with SessionID = {importInfo.SessionId}.");
                int productsCount = 0;
                Stopwatch sw = new Stopwatch();
                sw.Start();
                foreach (var container in products)
                {
                    int rowCount = await UpsertProducts(container, ImportType.Availability);
                    productsCount += container.Availabilities.Count;
                }
                sw.Stop();
                _logger.Info($"Product stock import: Products imported '{productsCount}', in '{sw.ElapsedMilliseconds}' milliseconds for SupplierId '{importInfo.SupplierId}' successfully.");
                _logger.Info($"Product availability import: Products in feed '{importInfo.ProductNumbersFromFeed?.Count}'");
                if (importInfo.ProductNumbersFromFeed != null && importInfo.ProductNumbersFromFeed.Count > 0)
                {
                    await CheckProducts(importInfo.ProductNumbersFromFeed.Aggregate((i, j) => i + "," + j), importInfo.SupplierId);
                    _logger.Info("Product availability import: Products checked for outdate");
                }

                var existing = unit.ProductVariantsRepository.GetAllQueryable()
                        .Where(v => v.Product.SupplierId == importInfo.SupplierId).Include(i => i.Product).ToList();
                foreach (var container in products)
                {
                    foreach (var availability in container.Availabilities)
                    {
                        long variantId = existing.Where(p => p.ProductNumber == availability.ProductNumber).Select(p => p.Id).FirstOrDefault();
                        stocks.Add(new ProductStockModel
                        {
                            ProductVariantId = variantId.ToString(),
                            ProductStock = availability.SupplierStock
                        });
                    }
                }
                updatedStockEvent.Stocks = stocks;
                _logger.Info($"Sending product stocks. SupplierId '{importInfo.SupplierId}'. Stocks count: {updatedStockEvent.Stocks.Count()}");
                await _serviceBusClient.Publish(updatedStockEvent);
            }
            catch (Exception ex)
            {
                _logger.Error($"Product availability import: SupplierId '{importInfo.SupplierId}'. Message '{ex.Message}'", ex);
                Console.WriteLine(ex.Message);
            }
        }

        public async Task<IEnumerable<Entities.Products>> GetProducts(int shopId)
        {
            try
            {
                var shopProductListId = unit.ShopProductListRepository.GetByCondition(x => x.ShopId == shopId)
                    .FirstOrDefault().Id;
                var productToExport = await unit.ProductsRepository.GetAllQueryable()
                    .Where(x => x.ProductLists.Any(y => y.ShopProductListId == shopProductListId)
                    && x.StatusId == (int)ProductStatuses.Published && x.ProductVariants.FirstOrDefault().StatusId == (int)ProductStatuses.Published)
                    .Include(_ => _.ProductVariants)
                    .ThenInclude(_ => _.ProductVariantAvailability)
                    .Include(_ => _.ProductMedia).ToListAsync();
                return productToExport;
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message, ex);
                return null;
            }
        }

        public async Task<byte[]> ExportProducts(int shopId, ProductExportTypes exportType)
        {
            var path = Path.Combine(Path.GetTempPath(), $"NDA_export_{exportType}_{DateTime.UtcNow:yyyy-MM-dd-HHmm}.csv");
            var shopProductListId = unit.ShopProductListRepository.GetByCondition(x => x.ShopId == shopId)
                .FirstOrDefault().Id;
            var productToExport = await unit.ProductsRepository.GetAllQueryable()
                .Where(x => x.ProductLists.Any(y => y.ShopProductListId == shopProductListId)
                && x.StatusId == (int)ProductStatuses.Published && x.ProductVariants.FirstOrDefault().StatusId == (int)ProductStatuses.Published)
                .Include(_ => _.ProductVariants)
                .ThenInclude(_ => _.ProductVariantAvailability)
                .Include(_ => _.ProductMedia).ToListAsync();
            if (exportType == ProductExportTypes.Shopify)
                PrepareShopify(path, productToExport);
            else if (exportType == ProductExportTypes.Woocommerce)
                PrepareWooCommerce(path, productToExport);

            var byteArray = File.ReadAllBytes(path);
            return byteArray;
        }

        private void PrepareWooCommerce(string path, List<Entities.Products> productToExport)
        {
            var exportUnit = _mapper.Map<IEnumerable<Entities.Products>, IEnumerable<WooCommerceExportModel>>(productToExport);
            var productExportModelPropertiesList = typeof(WooCommerceExportModel).GetProperties();
            var headerRow = GetHeaderRow(productExportModelPropertiesList);
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine(headerRow);
                foreach (var product in exportUnit)
                {
                    string productRow = GetRow(productExportModelPropertiesList, product);
                    writer.WriteLine(productRow);
                }
            }
        }

        private void PrepareShopify(string path, List<Entities.Products> productToExport)
        {
            var exportUnit = _mapper.Map<IEnumerable<Entities.Products>, IEnumerable<ShopifyProductExportModel>>(productToExport);
            using (var writer = new StreamWriter(path))
            {
                var productExportModelPropertiesList = typeof(ShopifyProductExportModel).GetProperties();
                var headerRow = GetHeaderRow(productExportModelPropertiesList);
                writer.WriteLine(headerRow);
                foreach (var product in exportUnit)
                {
                    var images = product.ImageSrc.ToList();
                    if (images.Count == 0)
                    {
                        writer.WriteLine(GetRow(productExportModelPropertiesList, product));
                    }
                    else
                    {
                        for (int i = 0; i < images.Count; i++)
                        {
                            if (i == 0)
                                writer.WriteLine(GetRow(productExportModelPropertiesList, product));
                            else
                            {
                                writer.WriteLine(GetImageRow(productExportModelPropertiesList, product, i));
                            }
                        }
                    }
                }
            }
        }

        private string GetRow(PropertyInfo[] propertiesOfProductModel, object obj)
        {
            var stringBuilder = new StringBuilder();
            foreach (var property in propertiesOfProductModel)
            {
                if (property.Name == "ImageSrc")
                {
                    List<string> array = (List<string>)property.GetValue(obj);
                    stringBuilder.Append(@"""");
                    stringBuilder.Append(array[0]);
                    stringBuilder.Append(@""",");
                }
                else
                {
                    stringBuilder.Append(@"""");
                    stringBuilder.Append(property.GetValue(obj));
                    stringBuilder.Append(@""",");
                }
            }
            stringBuilder.Length--;
            return stringBuilder.ToString();
        }

        private string GetImageRow(PropertyInfo[] propertiesOfProductModel, object obj, int index)
        {
            var stringBuilder = new StringBuilder();
            foreach (var property in propertiesOfProductModel)
            {
                switch (property.Name)
                {
                    case "Handle":
                        stringBuilder.Append(@"""");
                        stringBuilder.Append(property.GetValue(obj));
                        stringBuilder.Append(@""",");
                        break;
                    case "ImageSrc":
                        {
                            List<string> array = (List<string>)property.GetValue(obj);
                            stringBuilder.Append(@"""");
                            stringBuilder.Append(array[index]);
                            stringBuilder.Append(@""",");
                        }
                        break;
                    default:
                        {
                            stringBuilder.Append(",");
                        }
                        break;
                }
            }
            stringBuilder.Length--;
            return stringBuilder.ToString();
        }

        private string GetHeaderRow(PropertyInfo[] propertiesOfProductModel)
        {
            var stringBuilder = new StringBuilder();
            foreach (var property in propertiesOfProductModel)
            {
                ColumnNameAttribute attr = property.GetCustomAttribute(typeof(ColumnNameAttribute)) as ColumnNameAttribute;
                if (attr != null)
                {
                    stringBuilder.Append(attr.Name);
                    stringBuilder.Append(',');
                }
            }
            stringBuilder.Length--;
            return stringBuilder.ToString();
        }

        public Dictionary<string, int> GetAvailability(string productVariantId = null)
        {
            try
            {
                if (!String.IsNullOrEmpty(productVariantId))
                {
                    var productList = unit.ProductVariantAvailabilityRepository.GetAllQueryable()
                        .Where(_ => _.ProductVariantId == Int64.Parse(productVariantId))
                        .ToDictionary(_ => _.ProductVariantId.ToString(), v => v.SupplierStock);
                    return productList;
                }
                else
                {
                    var productList = unit.ProductVariantAvailabilityRepository.GetAllQueryable()
                        .ToDictionary(_ => _.ProductVariantId.ToString(), v => v.SupplierStock);
                    return productList;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(ex.Message);
            }
        }

        public async Task UpdateProductsMetaFromImporter(ProductDeltasFromImporter metaDeltasInfo)
        {
            if (metaDeltasInfo == null)
            {
                _logger.Error("Error during import/update products meta data. Meta information is empty");
                throw new NullReferenceException("metaDelta information is empty.");
            }

            var productsUpdatedEvent = new ProductsUpdatedEvent
            {
                SupplierId = metaDeltasInfo.SupplierId,
                SessionId = metaDeltasInfo.SessionId,
                ImportType = ImportType.Meta
            };
            try
            {
                _logger.Info($"Prepearing new products from ImportType {metaDeltasInfo.ImportType} with SessionID = {metaDeltasInfo.SessionId}.");
                await _productListFromXml.CreateProductListFromXml(metaDeltasInfo, 1000);
                var products = _productListFromXml.Products;
                Stopwatch sw = new Stopwatch();
                sw.Start();
                int productsCount = 0;
                foreach (var container in products)
                {
                    var events = PrepareProductImagesResizing(container, metaDeltasInfo.SupplierId);

                    int rowCount = await UpsertProducts(container, ImportType.Meta);
                    productsCount += container.Products.Count;

                    await SendProductWithoutImagesAddedEvents(events, metaDeltasInfo.SupplierId);
                }
                sw.Stop();
                _logger.Info($"Product meta import: Products imported '{productsCount}', in '{sw.ElapsedMilliseconds}' milliseconds for SupplierId '{metaDeltasInfo.SupplierId}' successfully.");
                if (metaDeltasInfo.ProductNumbersFromFeed != null && metaDeltasInfo.ProductNumbersFromFeed.Count > 0)
                {
                    await CheckProducts(metaDeltasInfo.ProductNumbersFromFeed.Aggregate((i, j) => i + "," + j), metaDeltasInfo.SupplierId);
                    _logger.Info("Product meta import: Products checked for outdate");
                }
                productsUpdatedEvent.IsSucceded = true;
                await _serviceBusClient.Publish(productsUpdatedEvent);
            }
            catch (Exception ex)
            {
                productsUpdatedEvent.IsSucceded = false;
                await _serviceBusClient.Publish(productsUpdatedEvent);
                _logger.Error("Product meta import: Error update products Meta. " + ex.Message, ex);
                Console.WriteLine(ex.Message);
            }
        }

        #region PrepareProductImagesResizing

        public List<ProductWithoutImagesAddedEvent> PrepareProductImagesResizing(ProductsContainer container, long supplierId)
        {
            var events = new List<ProductWithoutImagesAddedEvent>();

            foreach (var containerProduct in container.Products)
            {
                var product = GetByProductNumber(containerProduct.ProductNumber, containerProduct.SupplierId);

                if (product == null) continue;

                var unresizedProductImages = GetUnresizedProductImagesMedia(container.Medias, product);

                if (unresizedProductImages.Count == 0) continue;

                var productWithoutImagesAddedEvent = new ProductWithoutImagesAddedEvent
                {
                    SupplierImagesUrls = unresizedProductImages.Select(m => m.URL).ToList(),
                    ProductId = product.Id,
                    SupplierId = supplierId
                };

                events.Add(productWithoutImagesAddedEvent);
            }

            return events;
        }

        #endregion

        #region SendProductWithoutImagesAddedEvents

        private async Task SendProductWithoutImagesAddedEvents(List<ProductWithoutImagesAddedEvent> events, long supplierId)
        {
            var totalPhotosCount = 0;
            
            foreach (var productWithoutImagesAddedEvent in events)
            {
                totalPhotosCount = productWithoutImagesAddedEvent.SupplierImagesUrls.Count;

                await _serviceBusClient.Publish(productWithoutImagesAddedEvent);
            }

            _logger.Info($"{totalPhotosCount} photos were sent for resizing for product supplier ID {supplierId}");
        }

        #endregion

        #region GetUnresizedProductImagesMedia

        public List<MediaType> GetUnresizedProductImagesMedia(MediaTypeCollection medias, Entities.Products product)
        {
            var inputProductMediaImages = new List<MediaType>();

            foreach (var media in medias)
            {
                var isImage = media.ProductMediaTypeId == 1;

                if (!string.IsNullOrEmpty(media.URL) && isImage && media.ProductNumber == product.ProductNumber)
                {
                    inputProductMediaImages.Add(media);
                }
            }

            var unresizedProductMediaImages = inputProductMediaImages.Where(m =>
                product.ProductMedia.All(pm => pm.SupplierUrl != m.URL))
                .ToList();

            return unresizedProductMediaImages;
        }

        #endregion

        public async Task UpdateProductsPriceFromImporter(ProductDeltasFromImporter priceDeltasInfo)
        {
            try
            {
                var allProducts = unit.ProductsRepository.GetAllQueryable();
                _logger.Info($"Start price update for Supplier = {priceDeltasInfo.SupplierId}");
                await _productListFromXml.CreateProductListFromXml(priceDeltasInfo, 2000);
                var products = _productListFromXml.Products;
                _logger.Info($"Counted {products.Count} products from Import with SessionID = {priceDeltasInfo.SessionId}.");
                Stopwatch sw = new Stopwatch();
                sw.Start();
                int productsCount = 0;
                foreach (var container in products)
                {
                    int rowCount = await UpsertProducts(container, ImportType.Price);
                    productsCount += container.Variants.Count;
                }
                sw.Stop();
                _logger.Info($"Product price import: Products imported '{productsCount}', in '{sw.ElapsedMilliseconds}' milliseconds for SupplierId '{priceDeltasInfo.SupplierId}' successfully.");
                _logger.Info($"Products in feed '{priceDeltasInfo.ProductNumbersFromFeed?.Count}'");
                if (priceDeltasInfo.ProductNumbersFromFeed != null && priceDeltasInfo.ProductNumbersFromFeed.Count > 0)
                {
                    await CheckProducts(priceDeltasInfo.ProductNumbersFromFeed.Aggregate((i, j) => i + "," + j), priceDeltasInfo.SupplierId);
                    _logger.Info("Product price import: Products checked for outdate");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message);
                Console.WriteLine(ex.Message);
            }
        }

        //public async Task AddProductToListAsync(ProductListItemModel product)
        //{
        //    var shopProductListId = unit.ProductListRepository.
        //    await unit.ProductListRepository.CreateAsync(new ProductLists
        //    { ProductId = product.ProductId, ShopProductListId = product.ShopProductListId });
        //}

        public async Task<bool> IsPremiumProduct(long productId)
        {
            _logger.Info($"Checking if the product with ID = {productId} is premium");

            var product = Get(productId);

            if (product == null || !product.SupplierId.HasValue)
            {
                throw new KeyNotFoundException();
            }

            var supplierIds = await PublishGetSupplierStatusEvent();

            if (supplierIds.PremiumSuppliersList.ContainsKey(product.SupplierId.Value))
            {
                return supplierIds.PremiumSuppliersList[product.SupplierId.Value];
            };

            return false;
        }

        public async Task<Models.ProductModel> GetProductsForShopProductListAsync(Guid shopListId)
        {
            try
            {
                var products = unit.ProductsRepository.GetAllQueryable()
                    .Include(_ => _.ProductVariants)
                    .ThenInclude(_ => _.ProductVariantAvailability)
                    .Include(a => a.ProductLists).Where(b =>
                        b.ProductLists.Any(x => x.ShopProductListId == shopListId)).ToList();
                IEnumerable<Models.Product> productsDto = _mapper.Map<IEnumerable<Entities.Products>, IEnumerable<Models.Product>>(products);
                var count = productsDto.Count();
                return new Models.ProductModel()
                {
                    Products = productsDto,
                    Length = count
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return null;
            }
        }
        //get products for productlist for current shop by shop id (delete this method when multiple lists for shop will be implemented
        public Models.ProductModel GetProductsForShopProductList(CatalogPageModel page, int shopId, string searchTerm)
        {
            try
            {
                var productListId = unit.ShopProductListRepository.GetByCondition(x => x.ShopId == shopId)
                    .FirstOrDefault().Id;
                List<Entities.Products> allListProducts = null;
                if (!string.IsNullOrEmpty(searchTerm))
                {
                    int legaceProductId = int.TryParse(searchTerm, out int legId) ? legId : -1;
                    allListProducts = unit.ProductsRepository.GetAllQueryable()
                        .Include(_ => _.ProductVariants)
                        .ThenInclude(_ => _.ProductVariantAvailability)
                        .Include(a => a.ProductLists).Where(b =>
                            b.ProductLists.Any(x => x.ShopProductListId == productListId) && (b.ProductNumber == searchTerm || b.LegacyId == legaceProductId
                            || b.Brand.Contains(searchTerm) || b.Title.Contains(searchTerm))).ToList();
                }
                else
                {
                    allListProducts = unit.ProductsRepository.GetAllQueryable()
                        .Include(_ => _.ProductVariants)
                        .ThenInclude(_ => _.ProductVariantAvailability)
                        .Include(a => a.ProductLists).Where(b =>
                            b.ProductLists.Any(x => x.ShopProductListId == productListId)).ToList();
                }
                var count = allListProducts.Count();
                var productForPage = allListProducts.Skip((page.Page - 1) * page.PageSize).Take(page.PageSize);
                IEnumerable<Models.Product> productsDto = _mapper.Map<IEnumerable<Entities.Products>, IEnumerable<Models.Product>>(productForPage);

                return new Models.ProductModel()
                {
                    Products = productsDto,
                    Length = count
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return null;
            }
        }

        public Dictionary<int, string> GetProductExportTypes()
        {
            var dict = Enum.GetValues(typeof(ProductExportTypes))
                .Cast<ProductExportTypes>()
                .ToDictionary(t => (int)t, t => t.ToString());
            return dict;
        }

        private Func<IQueryable<Entities.Products>, IOrderedQueryable<Entities.Products>> Sort(SortModel sort)
        {
            Func<IQueryable<Entities.Products>, IOrderedQueryable<Entities.Products>> orderBy = null;
            switch (sort.SortBy)
            {
                case SortBy.Price:
                    if (sort.SortOrder == SortDirection.Asc)
                        orderBy = o => o.OrderBy(s => s.ProductVariants.FirstOrDefault().RetailerPurchasePrice);
                    else orderBy = o => o.OrderByDescending(s => s.ProductVariants.FirstOrDefault().RetailerPurchasePrice);
                    break;

                case SortBy.UpdatedAt:
                    if (sort.SortOrder == SortDirection.Asc)
                        orderBy = o => o.OrderBy(s => s.UpdatedAt);
                    else orderBy = o => o.OrderByDescending(s => s.UpdatedAt);
                    break;
                default: orderBy = o => o.OrderBy(s => s.Id); break;
            }
            return orderBy;
        }

        //=================METHOD FOR BULK UPDATE FROM A FILE================
        //public async Task UpdateProductStockWithCsv(ProductAvailabilityDeltasFromImporter importInfo)
        //{
        //    try
        //    {
        //        var productToImport = await _productListFromXml.(importInfo);
        //        foreach (var file in csvTempFiles)
        //        {
        //            int supplierId = importInfo.SupplierId;
        //            SqlHelper.SqlHelper _sqlHelper = new SqlHelper.SqlHelper();
        //            string tempTableName = $"temp_availability_{supplierId}";
        //            string tableName = "dbo.ProductStocks";
        //            await unit.ProductsRepository.ExecuteSqlCommandAsync(_sqlHelper.GetCreateTempTableQuery("CreateImportTable.sql", tempTableName));
        //            await unit.ProductsRepository.ExecuteSqlCommandAsync(_sqlHelper.GetLoadFromFileQuery("LoadData.sql", file, tempTableName));
        //            await unit.ProductsRepository.ExecuteSqlCommandAsync(_sqlHelper.GetUpdateTableFromTempTableQuery("UpdateData.sql", tableName, tempTableName, supplierId));
        //            await unit.ProductsRepository.ExecuteSqlCommandAsync(_sqlHelper.GetDropTempTableQuery("DropImportTable.sql", tempTableName));
        //            await unit.SaveAsync();
        //            File.Delete(file);
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Console.WriteLine(ex.Message);
        //    }
        //}
        public void RemoveProductFromListAsync(int shopId, long productId)
        {
            try
            {
                var productListEntryId = unit.ProductListRepository.GetAllQueryable().Include(_ => _.ShopProductList)
                    .FirstOrDefault(_ => _.ProductId == productId && _.ShopProductList.ShopId == shopId).Id;
                unit.ProductListRepository.Delete(productListEntryId);
                unit.Save();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("product was not deleted");
            }
        }

        public ProductVariants GetProductVariants(long productVariantId)
        {
            return unit.ProductVariantsRepository.Get(productVariantId);
        }

        public async Task<IEnumerable<ProductVariants>> GetProductsInfo(IEnumerable<long> variantIDs)
        {
            return await unit.ProductVariantsRepository.GetAllQueryable()
                .Where(v => variantIDs.Contains(v.Id))
                .Include(_ => _.Product).ThenInclude(_ => _.ProductMedia)
               .ToListAsync();
        }

        #region CountPremiumProductsForAccount

        public async Task<bool> CountPremiumProductsForAccount()
        {
            var shopPremiumProductsCountDict = new Dictionary<int, int>();
            var accountPremiumProductsCountDict = new Dictionary<Guid, int>();

            var response = await PublishGetSupplierStatusEvent();
            var premiumSuppliers = response.PremiumSuppliersList;

            var shopsProductLists = unit.ShopProductListRepository
                .GetAllQueryable()
                .Include(spl => spl.ProductLists)
                .ThenInclude(pl => pl.Product)
                .ToList();

            foreach (var shopProductList in shopsProductLists)
            {
                var premiumProductsCount = shopProductList.ProductLists
                    .Select(pl => pl.Product)
                    .Count(p => premiumSuppliers.ContainsKey(p.SupplierId.Value));

                shopPremiumProductsCountDict.Add(shopProductList.ShopId, premiumProductsCount);
            }

            _logger.Info($"Shops count for premium products count update: {shopPremiumProductsCountDict.Count}");

            foreach (var elem in shopPremiumProductsCountDict)
            {
                try
                {
                    var shop = await _messageQueueService.SendGetShopRequest(elem.Key);

                    if (shop.AccountId == null)
                    {
                        continue;
                    }

                    if (accountPremiumProductsCountDict.ContainsKey(shop.AccountId))
                    {
                        accountPremiumProductsCountDict[shop.AccountId] += elem.Value;
                    }
                    else
                    {
                        accountPremiumProductsCountDict.Add(shop.AccountId, elem.Value);
                    }
                }
                catch (Exception)
                {
                    continue;
                }
            }

            _logger.Info($"Message with account premium products count is sent. Count: {accountPremiumProductsCountDict.Count}");

            var result = await _messageQueueService.SendUpdateAccountsPremiumProductsCountRequest(accountPremiumProductsCountDict);
            return result.IsUpdated;
        }

        #endregion

        private Task<GetPremiumSuppliersListResponce> PublishGetSupplierStatusEvent()
        {
            var request = new GetPremiumSuppliersListRequest();
            request.IsPremium = true;
            return _serviceBusClient.SendRequst<GetPremiumSuppliersListRequest, GetPremiumSuppliersListResponce>(request);
        }

        private int GetProductsCount(FilterModel search, string supplierIdsStr)
        {
            (string name, object value)[] productCountParams =
            {
                (StoredProcedure.SearchProductCount.CategoryId, search.CategoryId ?? 0),
                (StoredProcedure.SearchProductCount.SearchTerm, !string.IsNullOrWhiteSpace(search.SearchValue) ? $"'{search.SearchValue}'" : search.SearchValue),
                (StoredProcedure.SearchProductCount.SupplierIds, $"'{supplierIdsStr}'"),
                (StoredProcedure.SearchProductCount.IsAvailable, search.IsAvailable)
            };
            var countResultList = unit.ProductsRepository.ExecuteSqlScript<int>(SqlCommand.GetExecuteCommand(StoredProcedure.SearchProductCount.Name), productCountParams);
            return countResultList != null ? countResultList.FirstOrDefault() : 0;
        }

        private IEnumerable<T> SearchProducts<T>(ProductParameters parameters, string supplierIdsStr, bool isCount = false)
        {
            (string name, object value)[] searchProductParams =
            {
                (StoredProcedure.SearchProducts.CategoryId, parameters.CategoryId ?? 0),
                (StoredProcedure.SearchProducts.SupplierIds, $"'{supplierIdsStr}'"),
                (StoredProcedure.SearchProducts.IsAvailable, parameters.IsAvailable),
                (StoredProcedure.SearchProducts.SearchParam, parameters.SearchParam),
                (StoredProcedure.SearchProducts.SearchTerm, !string.IsNullOrWhiteSpace(parameters.SearchValue) ? $"'{parameters.SearchValue}'" : parameters.SearchValue),
                (StoredProcedure.SearchProducts.SortBy, parameters.SortBy.ToString()),
                (StoredProcedure.SearchProducts.IsDesc, parameters.SortOrder == SortDirection.Desc),
                (StoredProcedure.SearchProducts.Skip, (parameters.Page - 1) * parameters.PageSize),
                (StoredProcedure.SearchProducts.Take, parameters.PageSize),
                (StoredProcedure.SearchProducts.IsCount, isCount ? 1 : 0)
            };

            return unit.ProductsRepository.ExecuteSqlScript<T>(SqlCommand.GetExecuteCommand(StoredProcedure.SearchProducts.Name), searchProductParams);
        }

        private async Task<int> UpsertProducts(ProductsContainer products, ImportType importType)
        {
            (string name, object value, SqlDbType type)[] upsertParameters =
            {
                (StoredProcedure.UpsertProducts.Products, products.Products, SqlDbType.Structured),
                (StoredProcedure.UpsertProducts.Variants, products.Variants, SqlDbType.Structured),
                (StoredProcedure.UpsertProducts.Medias, products.Medias, SqlDbType.Structured),
                (StoredProcedure.UpsertProducts.Availability, products.Availabilities, SqlDbType.Structured),
                (StoredProcedure.UpsertProducts.UpsertType, (int)importType, SqlDbType.Int)
            };
            return await unit.ProductsRepository.ExecuteNonQueryStoredProcedureAsync(StoredProcedure.UpsertProducts.Name, upsertParameters);
        }

        private async Task<int> CheckProducts(string productNumbers, int supplierId)
        {
            (string name, object value, SqlDbType type)[] checkParameters =
            {
                (StoredProcedure.CheckProducts.SupplierId, supplierId, SqlDbType.Int),
                (StoredProcedure.CheckProducts.ProductNumberList, productNumbers, SqlDbType.VarChar)
            };
            return await unit.ProductsRepository.ExecuteNonQueryStoredProcedureAsync(StoredProcedure.CheckProducts.Name, checkParameters);
        }

        public async Task<int?> GetSupplierId(long productId, OrderType type)
        {
            return await unit.ProductVariantsRepository.GetAllQueryable()
                      .Where(p => p.Id == productId)
                      .Include(_ => _.Product)
                      .Select(p => p.Product.SupplierId).SingleOrDefaultAsync();
        }

        public async Task<string> GetProductNumber(long productId, int type)
        {
            return await unit.ProductVariantsRepository.GetAllQueryable()
                      .Where(p => p.Id == productId)
                      .Include(_ => _.Product)
                      .Select(p => p.Product.ProductNumber).SingleOrDefaultAsync();
        }

        public async Task<Models.Product> GetByVariantAsync(long id)
        {
            GetPremiumSuppliersListResponce supplierIds = await PublishGetSupplierStatusEvent();

            try
            {
                var variant = unit.ProductVariantsRepository.GetAllQueryable().Where(x => x.Id == id)
                    .Include(_ => _.Product)
                    .ThenInclude(_ => _.ProductMedia)
                    .Include(_ => _.ProductVariantAvailability)
                    .FirstOrDefault();
                var shopIdListWithProduct = unit.ProductListRepository.GetAllQueryable().Where(_ => _.ProductId == id)
                    .Select(_ => _.ShopProductList.ShopId).ToList();
                var productDto = _mapper.Map<Entities.Products, Models.Product>(variant.Product);
                productDto.ShopIdLIst = shopIdListWithProduct;

                if (supplierIds != null && supplierIds.PremiumSuppliersList.Any())
                {
                    if (supplierIds.PremiumSuppliersList.TryGetValue(variant.Product.SupplierId.Value, out bool isPremium)) productDto.IsPremium = isPremium;
                }

                productDto.Specification = await _productSpecificationService.GetProdSpecificationsAsync(variant.Product);

                return productDto;
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message, ex);
                return null;
            }
        }

        public async Task<object> GetExportProductList(int shopId, CatalogPageModel page)
        {
            var productList = await unit.ProductListRepository.GetByConditionAsync(c => c.ShopProductList.ShopId == shopId,
                includeProperties: "Product.ProductVariants.ProductVariantAvailability,ShopProductList", pageNumber: page.Page, pageSize: page.PageSize);
            var productListId = productList.Select(s => s.ProductId);
            _logger.Info($"Sending request to export api for shop id {shopId}");
            var exportedModel = _mapper.Map<IEnumerable<ProductExportModel>>(productList);
            var exportedProducts = await SendExportedProductsRequest(productListId, shopId);
            if (exportedProducts != null)
            {
                _logger.Info($"Received exported products {exportedProducts?.Count()}");
                foreach (var item in exportedProducts)
                {
                    var expItem = exportedModel.FirstOrDefault(s => s.ProductId == item.ProductId);
                    expItem.ProductVarinatId = item.ProductVarinatId;
                    expItem.ShopId = shopId;
                    if (string.IsNullOrEmpty(expItem.Type))
                        expItem.Type = item.Type;
                    else
                    {
                        if (!expItem.Type.Contains(item.Type))
                            expItem.Type += ", " + item.Type;
                    }
                    if (expItem.ExportDate < item.ExportDate)
                        expItem.ExportDate = item.ExportDate;
                }
            }
            return new
            {
                Products = exportedModel,
                Total = unit.ProductListRepository.GetAllQueryable()
                        .Where(c => c.ShopProductList.ShopId == shopId)
                        .Include(i => i.ShopProductList).Count()
            };
        }

        private async Task<IEnumerable<ExportedProductModel>> SendExportedProductsRequest(IEnumerable<long> productIdList, int shopId)
        {
            _logger.Info($"Sending exported products request with shopId = {shopId}");

            var request = new GetExportedProductsRequest
            {
                ProductIDList = productIdList,
                ShopId = shopId
            };

            GetExportedProductsResponse response = null;
            try
            {
                response = await _serviceBusClient.SendRequst<GetExportedProductsRequest, GetExportedProductsResponse>(request);
            }
            catch (Exception ex)
            {

            }
            return response?.ExportedProducts;
        }

        public async Task LockProducts(int supplierId)
        {
            try
            {
                _logger.Info("Getting premium suppliers.");
                GetPremiumSuppliersListResponce supplierIds = await PublishGetSupplierStatusEvent();
                if (supplierIds.PremiumSuppliersList.TryGetValue(supplierId, out bool isPremium))
                {
                    if (isPremium)
                    {
                        _logger.Info($"Set products to VIP status by supplierId: '{supplierId}'");
                        string sqlCmd = $"Update Products set VIP = 1 where SupplierId = {supplierId}";
                        await unit.ProductsRepository.ExecuteSqlCommandAsync(sqlCmd);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error lock products for supplierId: '{supplierId}'.", ex);
            }
        }

        public async Task LockProductsForRetailer(int supplierId, int shopId)
        {
            try
            {
                _logger.Info("Getting premium suppliers.");
                GetPremiumSuppliersListResponce supplierIds = await PublishGetSupplierStatusEvent();
                if (supplierIds.PremiumSuppliersList.TryGetValue(supplierId, out bool isPremium))
                {
                    if (isPremium)
                    {
                        _logger.Info($"Set products to VIP status by supplierId: '{supplierId}'");
                        string sqlCmd = $"Update Products set VIP = 1 where SupplierId = {supplierId}";
                        await unit.ProductsRepository.ExecuteSqlCommandAsync(sqlCmd);
                        sqlCmd = $"Delete from UnlockedProducts where SupplierId = {supplierId} and ShopId = {shopId}";
                        await unit.UnlockedProductsRepository.ExecuteSqlCommandAsync(sqlCmd);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error lock products for shopId: {shopId} by supplierId: '{supplierId}'.", ex);
            }
        }

        public async Task UnlockProducts(int supplierId, int shopId)
        {
            try
            {
                var existingItems = await unit.UnlockedProductsRepository.GetByConditionAsync(u => u.SupplierId == supplierId && u.ShopId == shopId);
                if (existingItems == null || existingItems.Count() == 0)
                {
                    unit.UnlockedProductsRepository.Create(new UnlockedProducts
                    {
                        ShopId = shopId,
                        SupplierId = supplierId
                    });
                    await unit.SaveAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Error unlock products for supplierId: '{supplierId}', shopId: '{shopId}'.", ex);
            }
        }

        public async Task<IEnumerable<Models.ExportProductModel>> GetProducts(int shopId, IEnumerable<long> variantIDs)
        {
            try
            {
                (string name, object value, SqlDbType type)[] searchProductParams =
                {
                    (StoredProcedure.ExportProducts.ShopId, shopId, SqlDbType.Int),
                    (StoredProcedure.ExportProducts.VariantIDs, $"{string.Join(",",variantIDs)}", SqlDbType.VarChar),
                 };
                _logger.Info($"Send '{variantIDs.Count()}' variant IDs");
                _logger.Info($"'{searchProductParams.LastOrDefault().name}', '{searchProductParams.LastOrDefault().value}'");
                var products = await unit.ProductsRepository.ExecuteStoredProcedureAsync<Models.ExportProductModel>(StoredProcedure.ExportProducts.Name, searchProductParams);
                _logger.Info($"Received '{products.Count()}' products for ShopId - '{shopId}'.");
                return products;
            }
            catch (Exception ex)
            {
                _logger.Error(ex.Message, ex);
                return null;
            }
        }

        public async Task ImportLegacyIds(IEnumerable<LegacyIdsModel> legacyIds)
        {
            List<int> supplierIds = new List<int> { 17, 25, 28, 29, 36, 40, 44, 45, 48, 53, 54, 60, 63, 64, 65, 66, 68, 69, 71, 75, 76, 90, 101, 102, 104, 107, 111, 116, 117, 118, 127, 129, 130, 151, 152, 179, 186, 188, 198, 201, 211, 221, 222, 239, 242, 246, 269, 292, 296, 298, 307, 311, 313, 315, 319, 162, 94, 93, 95, 99, 123, 124, 320, 170, 202, 77, 288 };
            var legacyProducts = legacyIds.ToList();
            foreach (var supplier in supplierIds)
            {
                var allProducts = unit.ProductsRepository.GetAllQueryable().Where(_ => _.SupplierId == supplier).ToList();
                var productsToChange = new List<Entities.Products>();
                foreach (var legacyId in legacyProducts)
                {
                    var existingProduct = allProducts.FirstOrDefault(_ => _.SupplierId == legacyId.SupplierID && _.ProductNumber == legacyId.ProductNumber);
                    if (existingProduct != null)
                    {
                        existingProduct.LegacyId = (int?)legacyId.ID;
                    }
                }
                await unit.SaveAsync();
            }
        }

        public string[] GetSearchParameters()
        {
            return new string[] { "ID", "VariantId", "ProductNumber", "Title", "Brand", "Description" };
        }

        public async Task CreateResizedProductMedia(List<ResizedImage> resizedImages, long productId)
        {
            try
            {
                var productMedias = new ResizedImageMediaTypeCollection();

                foreach (var resizedImage in resizedImages)
                {
                    productMedias.Add(new ResizedImageMediaType
                    {
                        ProductId = productId,
                        ProductMediaTypeId = 1,
                        UrlRoot = resizedImage.ResizedUrlRoot,
                        URL = resizedImage.ResizedUrlRoot + ".jpg",
                        SupplierUrl = resizedImage.SupplierUrl
                    });
                }

                (string name, object value, SqlDbType type)[] parameters =
                {
                    (StoredProcedure.UpsertProductMedia.Media, productMedias, SqlDbType.Structured),
                };

                await unit.ProductsRepository.ExecuteNonQueryStoredProcedureAsync(StoredProcedure.UpsertProductMedia.Name, parameters);

                _logger.Info($"Resized photo media for the product with Id = {productId} successfully created");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create photo media for the product with Id = {productId}. {ex.Message}", ex);
            }
        }
    }
}
