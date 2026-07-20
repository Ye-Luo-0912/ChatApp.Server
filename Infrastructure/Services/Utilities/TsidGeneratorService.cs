using Core.Interfaces;
using TSID.Creator.NET;

namespace Infrastructure.Services.Utilities
{

    /// <summary>
    /// TSID 生成器，负责生成全局唯一的时间排序 ID（TSID），以确保在分布式环境中生成的 ID 不会发生冲突，并且具有时间排序特性，便于数据库索引和查询优化。
    /// </summary>
    public sealed class TsidGeneratorService : ITsidGenerator
    {
        public long GenerateTsid()
        {
            return TsidCreator.GetTsid().ToLong();
        }
    }
}
