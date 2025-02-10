using DeviceService.ComponentModel;
using DeviceService.ComponentModel.Responses;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace DeviceService.WebApi.Services
{
    public class HttpService : IHttpService
    {
        public HttpRequestMessage GenerateRequest(
          string endpoint,
          object requestObject,
          HttpMethod method,
          string baseUrl,
          List<Header> headers = null)
        {
            Uri requestUri = new Uri(baseUrl + "/" + endpoint);
            HttpRequestMessage httpRequest = new HttpRequestMessage(method, requestUri);
            headers?.ForEach((Action<Header>)(header => httpRequest.Headers.Add(header.Key, header.Value)));
            httpRequest.Content = (HttpContent)new StringContent(JsonConvert.SerializeObject(requestObject, Formatting.None, new JsonSerializerSettings()
            {
                NullValueHandling = NullValueHandling.Ignore
            }), Encoding.UTF8, "application/json");
            return httpRequest;
        }

        public async Task<HttpResponseMessage> SendRequest(HttpRequestMessage request, int timeout)
        {
            using (HttpClient httpClient = new HttpClient()
            {
                Timeout = TimeSpan.FromMilliseconds((double)timeout)
            })
            {
                try
                {
                    return await httpClient.SendAsync(request);
                }
                catch (Exception ex)
                {
                    string content = JsonConvert.SerializeObject((object)new StandardResponse(ex));
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = (HttpContent)new StringContent(content)
                    };
                }
            }
        }
    }
}
