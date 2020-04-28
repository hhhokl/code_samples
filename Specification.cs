using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace NDA.API.Product.Specifications
{
    public abstract class Specification<T> : ISpecification<T>
    where T : class
    {
        public virtual bool IsSatisfiedBy(T obj)
        {
            return true;
        }

        public abstract Expression<Func<T, bool>> ToExpression();
    }
}
