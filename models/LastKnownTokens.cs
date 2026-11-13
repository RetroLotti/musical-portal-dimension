namespace MusicDimension.models
{
    using Newtonsoft.Json;

    public class LastKnownTokens
    {
        [JsonProperty("clientId")]
        public string? ClientId { get; set; }

        [JsonProperty("accessToken")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonProperty("refreshToken")]
        public string RefreshToken { get; set; } = string.Empty;

        [JsonProperty("expiresIn")]
        public int ExpiresIn { get; set; }

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        public bool IsExpired => CreatedAt.AddSeconds(ExpiresIn) <= DateTime.UtcNow;
    }
}
