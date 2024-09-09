using System.Text;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ocr.Services
{
    public class VllmService
    {
        private readonly HttpClient _httpClient;

        public VllmService(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<string> GetApiResponseAsync(string prompt, int maxTokens)
        {
            var requestData = new
            {
                prompt = prompt,
                max_tokens = maxTokens
            };

            string jsonRequest = JsonConvert.SerializeObject(requestData);
            StringContent content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

            HttpResponseMessage response = await _httpClient.PostAsync("http://localhost:8000/generate", content);

            if (response.IsSuccessStatusCode)
            {
                string jsonResponse = await response.Content.ReadAsStringAsync();

                try
                {
                    var responseObject = JsonConvert.DeserializeObject<dynamic>(jsonResponse);
                    var textArray = responseObject?.text?.ToObject<List<string>>();
                    var result = textArray != null ? string.Join(" ", textArray) : "No text found in response";

                    return result;
                }
                catch (Exception ex)
                {
                    return $"Error parsing response: {ex.Message}";
                }
            }
            else
            {
                string errorResponse = await response.Content.ReadAsStringAsync();
                return $"Error: {response.StatusCode}, Details: {errorResponse}";
            }
        }
    }
}
