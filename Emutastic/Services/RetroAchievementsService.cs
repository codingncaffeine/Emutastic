using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    public class RetroAchievementsService
    {
        private static readonly HttpClient _http = new();
        private const string BaseUrl = "https://retroachievements.org/API/";

        /// <summary>
        /// Validates credentials by fetching the user profile from the RA Web API.
        /// Returns null on success, or an error message on failure.
        /// </summary>
        public async Task<string?> TestLoginAsync(string username, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(username))
                return "Username is required.";
            if (string.IsNullOrWhiteSpace(apiKey))
                return "Web API Key is required.";

            try
            {
                var url = $"{BaseUrl}API_GetUserProfile.php?u={Uri.EscapeDataString(username)}&y={Uri.EscapeDataString(apiKey)}";
                var response = await _http.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return $"HTTP {(int)response.StatusCode}";

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("User", out var userProp) &&
                    userProp.GetString() is string user && user.Length > 0)
                {
                    return null; // success
                }

                return "Invalid username or API key.";
            }
            catch (HttpRequestException ex)
            {
                return $"Connection failed: {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"Error: {ex.Message}";
            }
        }
    }
}
