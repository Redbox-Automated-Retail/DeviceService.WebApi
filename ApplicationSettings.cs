using DeviceService.ComponentModel;

namespace DeviceService.WebApi
{
    public class ApplicationSettings : IApplicationSettings
    {
        public bool UseSimulator { get; set; }

        public string DataFilePath { get; set; }

        public string DeviceServiceUrl { get; set; }

        public string DeviceServiceClientPath { get; set; }

        public bool ActivationFailureRetry { get; set; }
    }
}
