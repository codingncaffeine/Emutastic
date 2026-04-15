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
        public Task<(string? error, string? token)> TestLoginAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username))
                return Task.FromResult<(string?, string?)>(("Username is required.", null));
            if (string.IsNullOrWhiteSpace(password))
                return Task.FromResult<(string?, string?)>(("Password is required.", null));

            return Task.Run<(string?, string?)>(() =>
            {
                RetroAchievementsClient? client = null;
                try
                {
                    client = new RetroAchievementsClient();
                    client.Initialize(null!, false);
                    var (ok, err, token) = client.LoginWithPassword(username, password);
                    return ok ? (null, token) : (err ?? "Login failed.", null);
                }
                catch (Exception ex)
                {
                    return ($"Error: {ex.Message}", null);
                }
                finally
                {
                    try { client?.Dispose(); } catch { }
                }
            });
        }
    }
}
