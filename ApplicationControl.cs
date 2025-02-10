using DeviceService.ComponentModel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace DeviceService.WebApi
{
    public class ApplicationControl : IApplicationControl
    {
        private IApplicationLifetime _applicationLifetime;
        private ILogger<ApplicationControl> _logger;
        private IIUC285Notifier _iuc285Notifier;
        private AutoResetEvent _canShutDownAutoResetEvent;
        private bool _canShutDownClientResponse;

        public ApplicationControl(
          IApplicationLifetime applicationLifetime,
          ILogger<ApplicationControl> logger,
          IIUC285Notifier iuc285Notifier)
        {
            this._applicationLifetime = applicationLifetime;
            this._logger = logger;
            this._iuc285Notifier = iuc285Notifier;
        }

        public static bool IsService { get; set; }

        public bool CanShutDown(ShutDownReason shutDownReason)
        {
            string str = (string)null;
            this._canShutDownClientResponse = true;
            if (this._canShutDownClientResponse)
            {
                this._iuc285Notifier?.SendDeviceServiceCanShutDownEvent();
                this._canShutDownAutoResetEvent = new AutoResetEvent(false);
                this._canShutDownAutoResetEvent.WaitOne(5000);
                if (!this._canShutDownClientResponse)
                    str = "Kiosk Engine prevented shutdown";
            }
            else
                str = "Device Service Command(s) are currently processing";
            if (!string.IsNullOrEmpty(str))
                str = "  Reason: " + str;
            ILogger<ApplicationControl> logger = this._logger;
            if (logger != null)
                logger.LogInformation(string.Format("ApplicationControl.CanShutDown: {0} {1}", (object)this._canShutDownClientResponse, (object)str));
            return this._canShutDownClientResponse;
        }

        public void SetCanShutDownClientResponse(bool clientAllowsShutDown)
        {
            this._canShutDownClientResponse &= clientAllowsShutDown;
            if (clientAllowsShutDown || this._canShutDownAutoResetEvent == null)
                return;
            ILogger<ApplicationControl> logger = this._logger;
            if (logger != null)
                logger.LogInformation(string.Format("ApplicationControl.SetCanShutDownClientResponse value: {0}", (object)clientAllowsShutDown));
            this._canShutDownAutoResetEvent.Set();
        }

        public bool ShutDown(bool forceShutdown, ShutDownReason shutDownReason)
        {
            ILogger<ApplicationControl> logger1 = this._logger;
            if (logger1 != null)
                logger1.LogInformation(string.Format("ApplicationControl.ShutDown: forceShutdown: {0}, ShutDown Reason: {1}", (object)forceShutdown, (object)shutDownReason));
            bool flag = false;
            if (forceShutdown || this.CanShutDown(shutDownReason))
            {
                try
                {
                    ILogger<ApplicationControl> logger2 = this._logger;
                    if (logger2 != null)
                        logger2.LogInformation("ApplicationControl.ShutDown: starting shut down");
                    Task.Run((Func<Task>)(async () =>
                    {
                        this._iuc285Notifier.SendDeviceServiceShutDownStartingEvent(shutDownReason);
                        await Task.Delay(3000);
                        if (ApplicationControl.IsService)
                        {
                            ServiceController serviceController = new ServiceController("device$service");
                            if (serviceController != null)
                            {
                                ILogger<ApplicationControl> logger3 = this._logger;
                                if (logger3 != null)
                                    logger3.LogInformation("Stopping service application.");
                                serviceController?.Stop();
                            }
                            else
                            {
                                ILogger<ApplicationControl> logger4 = this._logger;
                                if (logger4 == null)
                                    return;
                                logger4.LogError("Unable to find service device$service.  Unable to shut down windows service.");
                            }
                        }
                        else
                        {
                            ILogger<ApplicationControl> logger5 = this._logger;
                            if (logger5 != null)
                                logger5.LogInformation("Stopping non-service application.");
                            this._applicationLifetime?.StopApplication();
                        }
                    }));
                    flag = true;
                }
                catch (Exception ex)
                {
                    ILogger<ApplicationControl> logger6 = this._logger;
                    if (logger6 != null)
                        logger6.LogInformation(string.Format("ApplicationControl.ShutDown failed.  {0}", (object)ex));
                }
            }
            return flag;
        }
    }
}
