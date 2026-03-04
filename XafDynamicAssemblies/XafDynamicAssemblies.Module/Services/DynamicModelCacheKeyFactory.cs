using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using XafDynamicAssemblies.Module.BusinessObjects;

namespace XafDynamicAssemblies.Module.Services
{
    /// <summary>
    /// Custom IModelCacheKeyFactory that includes a version counter.
    /// When RuntimeEntityTypes changes, the version increments, forcing EF Core
    /// to rebuild its cached model instead of reusing the stale one.
    /// This is critical for in-process restart (Program.cs while loop).
    /// </summary>
    public class DynamicModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
        {
            return (context.GetType(), designTime, XafDynamicAssembliesEFCoreDbContext.ModelVersion);
        }
    }
}
