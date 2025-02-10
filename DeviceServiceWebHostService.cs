using DeviceService.ComponentModel.Analytics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace DeviceService.WebApi
{
    public class DeviceServiceWebHostService : WebHostService
    {
        private ILogger _logger;
        private IAnalyticsService _analytics;

        public DeviceServiceWebHostService(
          IWebHost host,
          ILogger<DeviceServiceWebHostService> logger,
          IAnalyticsService analytics)
          : base(host)
        {
            this._logger = (ILogger)logger;
            this._analytics = analytics;
        }

        protected override void OnStarting(string[] args)
        {
            ILogger logger = this._logger;
            if (logger != null)
                logger.LogInformation("WebHost: OnStarting");
            this._analytics?.ServiceStarting();
            base.OnStarting(args);
        }

        protected override void OnStarted()
        {
            ILogger logger = this._logger;
            if (logger != null)
                logger.LogInformation("WebHost: OnStarted");
            this._analytics?.ServiceStarted();
            base.OnStarted();
        }

        protected override void OnStopping()
        {
            ILogger logger = this._logger;
            if (logger != null)
                logger.LogInformation("WebHost: OnStopping");
            this._analytics?.ServiceStopping();
            base.OnStopping();
        }

        protected override void OnStopped()
        {
            ILogger logger = this._logger;
            if (logger != null)
                logger.LogInformation("WebHost: OnStopped");
            this._analytics?.ServiceStopped();
            base.OnStopped();
        }
    }
}
