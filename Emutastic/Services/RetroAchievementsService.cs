using System;
using System.Threading.Tasks;

namespace Emutastic.Services
{
    public class RetroAchievementsService
    {
        /// <summary>
        /// Validates credentials by attempting a password login via rcheevos.
        /// Returns null on success, or an error message on failure.
        /// </summary>
        public Task<string?> TestLoginAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username))
                return Task.FromResult<string?>("Username is required.");
            if (string.IsNullOrWhiteSpace(password))
                return Task.FromResult<string?>("Password is required.");

            return Task.Run<string?>(() =>
            {
                RetroAchievementsClient? client = null;
                try
                {
                    client = new RetroAchievementsClient();
                    client.Initialize(null!, false);
                    var (ok, err, _) = client.LoginWithPassword(username, password);
                    return ok ? null : (err ?? "Login failed.");
                }
                catch (Exception ex)
                {
                    return $"Error: {ex.Message}";
                }
                finally
                {
                    try { client?.Dispose(); } catch { }
                }
            });
        }
    }
}
