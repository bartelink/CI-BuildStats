using BuildStats.Core;
using BuildStats.Web.Config;

namespace BuildStats.Web.Controllers
{
    public sealed class TravisCIController : BuildHistoryController
    {
        public TravisCIController(IBuildStatistics buildStatistics, IChartConfig chartConfig) 
            : base(new TravisCIFactory().CreateBuildHistoryClient(), buildStatistics, chartConfig)
        {
        }
    }
}