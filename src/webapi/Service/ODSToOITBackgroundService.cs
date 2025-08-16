using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;

using EdFi.OdsApi.Sdk.Apis.All;

using eppeta.webapi.Evaluations.Data;
using eppeta.webapi.Evaluations.Models;

namespace eppeta.webapi.Service
{
    public class ODSToOITBackgroundService : BackgroundService
    {
        protected IServiceScopeFactory serviceScopeFactory;
        protected static DateTime? lastRun;

        public ODSToOITBackgroundService(IServiceScopeFactory serviceScopeFactory)
        {
            this.serviceScopeFactory = serviceScopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (ShouldStartTask())
                {
                    await ODSToOIT();
                }

                await Task.Delay(500, token);
            }
        }

        protected bool ShouldStartTask()
        {
            return lastRun == null;
            //DateTime currentTime = DateTime.Now;

            //return currentTime.Hour == StartTaskDate.Hour &&
            //       currentTime.Minute >= StartTaskDate.Minute &&
            //       currentTime.Minute <= StartTaskDate.Minute + 1;
        }

        protected async Task ODSToOIT()
        {
            lastRun = DateTime.Now;

            await using var asyncScope = serviceScopeFactory.CreateAsyncScope();

            var odsApiService = asyncScope.ServiceProvider.GetRequiredService<IODSAPIAuthenticationConfigurationService>();
            var evaluationRepository = asyncScope.ServiceProvider.GetRequiredService<IEvaluationRepository>();

            var configuration = await odsApiService.GetAuthenticatedConfiguration();
            var candidatesApi = new CandidatesApi(configuration);
            var tpdmCandidates = await candidatesApi.GetCandidatesAsync();

            var evaluationCandidates = tpdmCandidates.Select(c => new Candidate { FirstName = c.FirstName, LastName = c.LastSurname, PersonId = c.PersonReference.PersonId, SourceSystemDescriptor = c.PersonReference.SourceSystemDescriptor }).ToList();

            await evaluationRepository.UpdateCandidates(evaluationCandidates);
        }
    }
}
