namespace MusicDimension.models
{
    using Newtonsoft.Json;

    public class LastKnownTokens
    {
        [JsonProperty("accessToken")]
        public string AccessToken { get; set; }

        [JsonProperty("refreshToken")]
        public string RefreshToken { get; set; }

        [JsonProperty("expiresIn")]
        public int ExpiresIn { get; set; }

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; }

        public bool IsExpired => CreatedAt.AddSeconds(ExpiresIn) <= DateTime.UtcNow;
    }
}
