using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using NDA.API.Product.DAL.Interfaces;
using NDA.API.Product.Entities;
using NDA.API.Product.Interfaces;
using NDA.API.Product.Models;

namespace NDA.API.Product.Services
{
    public class ProductSpecificationService : IProductSpecificationService
    {
        private readonly IMapper _mapper;
        private readonly IUnitOfWork _unit;

        public ProductSpecificationService(IMapper mapper, IUnitOfWork unit)
        {
            _mapper = mapper;
            _unit = unit;
        }

        public async Task<IEnumerable<SpecificationModel>> GetProdSpecificationsAsync(Entities.Products product)
        {
            var prodSpec = _mapper.Map<ProductSpecificationModel>(product);
            prodSpec = _mapper.Map(product.ProductVariants.FirstOrDefault(), prodSpec);

            var listOfAttributes = _mapper.Map<ExpandoObject>(prodSpec);
            var specifications = await _unit.ProductSpecificationRepository.GetListAsync();

            var resultModel = listOfAttributes.Where(attribute => attribute.Value != null).Join(specifications, attribute => attribute.Key, spec => spec.NormalizedName,
                (attribute, spec) => new SpecificationModel
                {
                    Name = spec.Name,
                    Value = attribute.Value,
                    Description = spec.Description
                });

            return resultModel;
        }
    }
}
