using DeviceService.Client.Core;
using DeviceService.ComponentModel;
using DeviceService.ComponentModel.Analytics;
using DeviceService.ComponentModel.Bluefin;
using DeviceService.ComponentModel.FileUpdate;
using DeviceService.ComponentModel.KDS;
using DeviceService.Domain;
using DeviceService.Domain.FileUpdate;
using DeviceService.WebApi.Bluefin;
using DeviceService.WebApi.KDS;
using DeviceService.WebApi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RBA_SDK;
using RBA_SDK_ComponentModel;
using Serilog;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;
using Swashbuckle.AspNetCore.SwaggerUI;
using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace DeviceService.WebApi
{
    public class Startup
    {
        public Startup(IConfiguration configuration) => this.Configuration = configuration;

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            MvcCoreMvcBuilderExtensions.SetCompatibilityVersion(MvcServiceCollectionExtensions.AddMvc(services), (CompatibilityVersion)1);
            services.AddSwaggerGen((Action<SwaggerGenOptions>)(c => c.SwaggerDoc("v1", new Info()
            {
                Title = "My API",
                Version = "v1"
            })));
            services.Configure<ApplicationSettings>((IConfiguration)this.Configuration.GetSection("AppSettings"));
            services.Configure<DeviceServiceConfig>((IConfiguration)this.Configuration.GetSection("DeviceServiceConfig"));
            ServiceProvider provider1 = services.BuildServiceProvider();
            ApplicationSettings implementationInstance = provider1.GetRequiredService<IOptionsSnapshot<ApplicationSettings>>().Value;
            services.AddSingleton<IApplicationSettings>((IApplicationSettings)implementationInstance);
            ILogger<Startup> requiredService = provider1.GetRequiredService<ILogger<Startup>>();
            requiredService.LogInformation(string.Format("AppSettings - UseSimulator: {0}", (object)implementationInstance.UseSimulator));
            requiredService.LogInformation("AppSettings - DataFilePath: " + implementationInstance.DataFilePath);
            requiredService.LogInformation("AppSettings - DeviceServiceUrl: " + implementationInstance.DeviceServiceUrl);
            requiredService.LogInformation("AppSettings - DeviceServiceClientPath: " + implementationInstance.DeviceServiceClientPath);
            if (implementationInstance.UseSimulator)
            {
                if (File.Exists(".\\RBA_SDK_Simulator.dll"))
                {
                    Assembly assembly = Assembly.LoadFrom(".\\RBA_SDK_Simulator.dll");
                    if (assembly != null)
                    {
                        Log.Information("Using RBA_SDK_Simulator");
                        MvcCoreMvcBuilderExtensions.AddControllersAsServices(MvcCoreMvcBuilderExtensions.AddApplicationPart(MvcServiceCollectionExtensions.AddMvc(services), assembly));
                        Type type1 = assembly.GetType("RBA_SDK_Simulator.ISimulatorDataProvider");
                        Type type2 = assembly.GetType("RBA_SDK_Simulator.SimulatorDataProvider");
                        Type type3 = assembly.GetType("RBA_SDK_Simulator.ICommandQueue");
                        Type type4 = assembly.GetType("RBA_SDK_Simulator.CommandQueue");
                        ServiceCollectionServiceExtensions.AddSingleton(services, type1, type2);
                        ServiceCollectionServiceExtensions.AddSingleton(services, type3, type4);
                        ServiceCollectionServiceExtensions.AddSingleton(services, typeof(IRBA_API), assembly.GetType("RBA_SDK_Simulator.RBA_API_Simulator"));
                    }
                }
            }
            else
                services.AddSingleton<IRBA_API, RBA_API>();
            services.AddSingleton<IAnalyticsService>((Func<IServiceProvider, IAnalyticsService>)(provider => (IAnalyticsService)new AnalyticsService(provider.GetRequiredService<IApplicationSettings>(), provider.GetRequiredService<ILogger<AnalyticsService>>())));
            services.AddSingleton<IIUC285Notifier>((Func<IServiceProvider, IIUC285Notifier>)(provider => (IIUC285Notifier)new IUC285Notifier(provider.GetRequiredService<ILogger<IUC285Notifier>>(), provider.GetRequiredService<IHttpService>(), provider.GetRequiredService<IApplicationSettings>())));
            IIUC285Proxy singletonIUC285Proxy = (IIUC285Proxy)null;
            services.AddSingleton<IIUC285Proxy>((Func<IServiceProvider, IIUC285Proxy>)(provider =>
            {
                if (singletonIUC285Proxy == null)
                {
                    singletonIUC285Proxy = (IIUC285Proxy)new IUC285Proxy(provider.GetRequiredService<IRBA_API>(), provider.GetRequiredService<ILogger<IUC285Proxy>>(), provider.GetRequiredService<IIUC285Notifier>(), provider.GetRequiredService<IKioskDataServiceClient>(), provider.GetRequiredService<IApplicationSettings>(), provider.GetRequiredService<IOptionsMonitor<DeviceServiceConfig>>());
                    Log.Information("Begin Connect");
                    if (singletonIUC285Proxy is IUC285Proxy iuC285Proxy2)
                    {
                        iuC285Proxy2.Connect();
                    }
                }
                return singletonIUC285Proxy;
            }));
            services.AddSingleton<IFileUpdater>((Func<IServiceProvider, IFileUpdater>)(provider => singletonIUC285Proxy as IFileUpdater));
            services.AddSingleton<Serilog.ILogger>(Log.Logger);
            services.AddSignalR();
            services.AddSingleton<IBluefinServiceClient, BluefinServiceClient>();
            services.AddSingleton<IKioskDataServiceClient, KioskDataServiceClient>();
            services.AddSingleton<IDeviceServiceClientCore, DeviceServiceClient>();
            services.AddSingleton<IActivationService, ActivationService>();
            services.AddSingleton<IDeviceStatusService, DeviceStatusService>();
            services.AddSingleton<IHttpService, HttpService>();
            IApplicationControl singletonApplicationControl = (IApplicationControl)null;
            services.AddSingleton<IApplicationControl>((Func<IServiceProvider, IApplicationControl>)(provider =>
            {
                if (singletonApplicationControl == null)
                    singletonApplicationControl = (IApplicationControl)new ApplicationControl(provider.GetRequiredService<IApplicationLifetime>(), provider.GetRequiredService<ILogger<ApplicationControl>>(), provider.GetRequiredService<IIUC285Notifier>());
                return singletonApplicationControl;
            }));
            services.AddSingleton<IFileUpdateService, FileUpdateService>();
            new Task((Action)(() =>
            {
                ServiceProvider provider2 = services.BuildServiceProvider();
                provider2.GetRequiredService<IDeviceStatusService>().PostDeviceStatus();
                provider2.GetRequiredService<IFileUpdateService>();
            })).Start();
        }

        public void Configure(
          IApplicationBuilder app,
          IHostingEnvironment env,
          IApplicationLifetime applicationLifetime)
        {
            if (HostingEnvironmentExtensions.IsDevelopment(env))
                DeveloperExceptionPageExtensions.UseDeveloperExceptionPage(app);
            else
                HstsBuilderExtensions.UseHsts(app);
            HttpsPolicyBuilderExtensions.UseHttpsRedirection(app);
            SwaggerBuilderExtensions.UseSwagger(app, (Action<SwaggerOptions>)null);
            SwaggerUIBuilderExtensions.UseSwaggerUI(app, (Action<SwaggerUIOptions>)(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1")));
            MvcApplicationBuilderExtensions.UseMvc(app);
            SignalRAppBuilderExtensions.UseSignalR(app, (Action<HubRouteBuilder>)(route => route.MapHub<CardReaderHub>("/CardReaderEvents")));
        }
    }
}
