using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace NDA.API.Product.Specifications
{
    public interface ISpecification<T>
    {
        bool IsSatisfiedBy(T obj);
        Expression<Func<T, bool>> ToExpression();
    }
}
