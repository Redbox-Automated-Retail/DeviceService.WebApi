using DeviceService.ComponentModel;
using DeviceService.ComponentModel.Analytics;
using Microsoft.Extensions.Logging;

namespace DeviceService.WebApi.Services
{
    public class AnalyticsService : IAnalyticsService
    {
        private ILogger<AnalyticsService> _logger;
        private IApplicationSettings _applicationSettings;

        public AnalyticsService(
          IApplicationSettings applicationSettings,
          ILogger<AnalyticsService> logger)
        {
            this._logger = logger;
            this._applicationSettings = applicationSettings;
        }

        public void StartWebHost() => this._logger.LogInformation("Analytics: Start WebHost");

        public void StopWebHost() => this._logger.LogInformation("Analytics: Stop WebHost");

        public void ClientConnectedToHub()
        {
            this._logger.LogInformation("Analytics: client connected to hub");
        }

        public void ClientDisconnectedFromHub()
        {
            this._logger.LogInformation("Analytics: client disconnected from hub");
        }

        public void ServiceStarted() => this._logger.LogInformation("Analytics: service started");

        public void ServiceStarting() => this._logger.LogInformation("Analytics: service starting");

        public void ServiceStopped() => this._logger.LogInformation("Analytics: service stopped");

        public void ServiceStopping() => this._logger.LogInformation("Analytics: service stopping");
    }
}
