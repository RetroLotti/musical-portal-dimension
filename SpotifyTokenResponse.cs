using Newtonsoft.Json;

namespace MusicDimension
{
    public class SpotifyTokenResponse
    {
        [JsonProperty("access_token")]
        public string AccessToken { get; set; }

        [JsonProperty("token_type")]
        public string TokenType { get; set; }

        [JsonProperty("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonProperty("scope")]
        public string Scope { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public bool IsExpired => this.CreatedAt.AddSeconds((double)this.ExpiresIn) <= DateTime.UtcNow;
    }
}
