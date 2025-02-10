using DeviceService.ComponentModel;
using DeviceService.ComponentModel.KDS;
using DeviceService.ComponentModel.Responses;
using DeviceService.Domain;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace DeviceService.WebApi.KDS
{
    public class KioskDataServiceClient : IKioskDataServiceClient
    {
        private const string AUTHHEADER = "Authorization";
        private const string KIOSKIDHEADER = "x-redbox-kioskid";
        private readonly IHttpService _httpService;
        private readonly string _apiUrl;
        private readonly List<Header> _headers = new List<Header>();
        private readonly ILogger _logger;

        public KioskDataServiceClient(
          IHttpService httpService,
          ILogger<KioskDataServiceClient> logger,
          IApplicationSettings settings)
        {
            this._httpService = httpService;
            this._logger = (ILogger)logger;
            KioskData fileData = DataFileHelper.GetFileData<KioskData>(settings.DataFilePath, "KioskData.json", new Action<string, Exception>(this.LogException));
            if (fileData == null)
                return;
            this._apiUrl = fileData.ApiUrl;
            this._headers.Add(new Header("Authorization", "Bearer " + fileData.ApiKey));
            this._headers.Add(new Header("x-redbox-kioskid", fileData.KioskId.ToString()));
        }

        public void LogException(string message, Exception e)
        {
            ILogger logger = this._logger;
            if (logger == null)
                return;
            logger.LogError(e, "Unhandled Exception in " + message);
        }

        public async Task<StandardResponse> PostDeviceStatus(DeviceStatus deviceStatus)
        {
            StandardResponse standardResponse;
            try
            {
                standardResponse = JsonConvert.DeserializeObject<StandardResponse>(await (await this._httpService.SendRequest(this._httpService.GenerateRequest("DeviceStatus", (object)deviceStatus, HttpMethod.Post, this._apiUrl + "/api/KioskData", this._headers), 5000)).Content?.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                standardResponse = new StandardResponse(ex);
            }
            this._logger.LogInformation("PostDeviceStatus Result: " + JsonConvert.SerializeObject((object)standardResponse));
            return standardResponse;
        }

        public async Task<StandardResponse> PostRebootStatus(RebootStatus rebootStatus)
        {
            StandardResponse standardResponse;
            try
            {
                standardResponse = JsonConvert.DeserializeObject<StandardResponse>(await (await this._httpService.SendRequest(this._httpService.GenerateRequest("RebootStatus", (object)rebootStatus, HttpMethod.Post, this._apiUrl + "/api/KioskData", this._headers), 5000)).Content?.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                standardResponse = new StandardResponse(ex);
            }
            this._logger.LogInformation("PostRebootStatus Result: " + JsonConvert.SerializeObject((object)standardResponse));
            return standardResponse;
        }

        public async Task<StandardResponse> PostCardStats(CardStats cardStats)
        {
            StandardResponse standardResponse;
            try
            {
                this._logger.LogInformation("Preparing to send PostCardStats to Kiosk Data Service");
                standardResponse = JsonConvert.DeserializeObject<StandardResponse>(await (await this._httpService.SendRequest(this._httpService.GenerateRequest("CardStats", (object)cardStats, HttpMethod.Post, this._apiUrl + "/api/KioskData", this._headers), 5000)).Content?.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                standardResponse = new StandardResponse(ex);
            }
            this._logger.LogInformation("PostCardStats Result: " + JsonConvert.SerializeObject((object)standardResponse));
            return standardResponse;
        }
    }
}
