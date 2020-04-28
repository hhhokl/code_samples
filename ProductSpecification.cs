using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using NDA.API.Product.Extensions;
using NDA.API.Product.Models;

namespace NDA.API.Product.Specifications
{
    public class ProductSpecification : Specification<Entities.Products>
    {
        public override Expression<Func<Entities.Products, bool>> ToExpression()
        {
            return (product) => (product.StatusId == (int) ProductStatuses.Published);
        }

        public Expression<Func<Entities.Products, bool>> CategoryFilter(IEnumerable<int> categoriesList)
        {
            if (categoriesList != null && categoriesList.Any())
            {
                Expression<Func<Entities.Products, bool>> expression = _ => false;
                foreach (var categoryId in categoriesList)
                {
                    expression = this.Or(expression, _ => _.CategoryId == categoryId);
                }

                return expression;
            }

            return _ => true;
        }

        public Expression<Func<Entities.Products, bool>> StatusFilter(int? statusId)
        {
            if(statusId != null)
            return (product) => (product.StatusId == (int)ProductStatuses.Published);
            return _ => true;
        }

        public Expression<Func<Entities.Products, bool>> SearchFilter(string searchValue)
        {
            if (searchValue != null)
                return a => a.Title.Contains(searchValue) || a.Brand.Contains(searchValue);
            return _ => true;
        }

        public Expression<Func<Entities.Products, bool>> PremiumFilter(bool isPremium, Dictionary<int, bool> suppliers)
        {
            if (isPremium)
            {
                var supplierIds = suppliers.Where(_ => _.Value).Select(_ => _.Key).ToList();
                Expression<Func<Entities.Products, bool>> expression = _ => false;
                foreach (var suppId in supplierIds)
                {
                    expression = this.Or(expression, _ => _.SupplierId == suppId);
                }

                return expression;
            }
            else
            {
                var supplierIds = suppliers.Where(_ => !_.Value).Select(_ => _.Key).ToList();
                Expression<Func<Entities.Products, bool>> expression = _ => false;
                foreach (var suppId in supplierIds)
                {
                    expression = this.Or(expression, _ => _.SupplierId == suppId);
                }

                return expression;
            }
        }

        public override bool IsSatisfiedBy(Entities.Products pr)
        {
            return false;
        }
    }
}
