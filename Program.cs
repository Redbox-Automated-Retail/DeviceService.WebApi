using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DeviceService.WebApi
{
    public class Program
    {
        private const string ASPNETCORE_ENVIRONMENT = "ASPNETCORE_ENVIRONMENT";

        public static void Main(string[] args)
        {
            bool flag = !Debugger.IsAttached && !((IEnumerable<string>)args).Contains<string>("--console");
            if (flag)
                Directory.SetCurrentDirectory(Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName));
            ApplicationControl.IsService = flag;
            IConfiguration configuration = Program.Configuration;
            Log.Logger = (ILogger)new LoggerConfiguration().ReadFrom.Configuration(configuration).Enrich.WithThreadId().Enrich.FromLogContext().WriteTo.Console().CreateLogger();
            try
            {
                Log.Information("------------------------------------ Starting web host -------------------------------------");
                Log.Information("ASPNETCORE_ENVIRONMENT: " + Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
                Log.Information(string.Format("DeviceService version: {0}", (object)DeviceService.Domain.DeviceService.AssemblyVersion));
                if (!flag)
                {
                    Log.Information("Program.Main - Starting as a console application");
                    WebHostExtensions.Run(Program.CreateWebHostBuilder(args, configuration).Build());
                }
                else
                {
                    Log.Information("Program.Main - Starting as a Windows Service");
                    Program.CreateWebHostBuilder(args, configuration).Build().RunAsDeviceServiceService();
                }
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Program.Main - Web host terminated with error");
            }
            finally
            {
                Log.Information("------------------------------------ Stopping web host -------------------------------------");
                Log.CloseAndFlush();
            }
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args, IConfiguration config)
        {
            Uri deviceServiceUri = Program.GetDeviceServiceUri(config);
            Log.Information(string.Format("CreateWebHostBuilder: url={0}  port={1}", (object)deviceServiceUri.AbsoluteUri, (object)deviceServiceUri.Port));
            return WebHostBuilderExtensions.UseStartup<Startup>(SerilogWebHostBuilderExtensions.UseSerilog(HostingAbstractionsWebHostBuilderExtensions.UseUrls(HostingAbstractionsWebHostBuilderExtensions.UseContentRoot(WebHost.CreateDefaultBuilder(args), Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName)), new string[1]
            {
        deviceServiceUri.AbsoluteUri
            }).UseSetting("Port", deviceServiceUri.Port.ToString()).ConfigureAppConfiguration((Action<WebHostBuilderContext, IConfigurationBuilder>)((builder, conf) => Program.GetConfiguration(conf))), (ILogger)null, false));
        }

        public static Uri GetDeviceServiceUri(IConfiguration config)
        {
            return new Uri(config.GetValue<string>("AppSettings:DeviceServiceUrl"));
        }

        public static IConfiguration Configuration { get; } = (IConfiguration)Program.GetConfiguration((IConfigurationBuilder)new ConfigurationBuilder()).Build();

        private static IConfigurationBuilder GetConfiguration(IConfigurationBuilder builder)
        {
            builder.SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json", false, true).AddJsonFile("Data\\DeviceServiceConfig.json", true, true);
            if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") != null)
                builder.AddJsonFile("appsettings." + Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") + ".json", true, true);
            return builder;
        }
    }
}
