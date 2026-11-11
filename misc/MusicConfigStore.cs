namespace MusicDimension.misc
{
    using MusicDimension.models;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Converters;
    using Newtonsoft.Json.Serialization;

    public sealed class MusicConfigStore
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = new CamelCaseNamingStrategy() },
            Converters = { new StringEnumConverter(new CamelCaseNamingStrategy()) }
        };

        private readonly object _sync = new();
        private readonly string _filePath;
        private MusicConfig _config;

        public MusicConfigStore(string filePath)
        {
            _filePath = Path.GetFullPath(filePath);
            _config = File.Exists(_filePath)
                ? JsonConvert.DeserializeObject<MusicConfig>(File.ReadAllText(_filePath), Settings) ?? new MusicConfig()
                : new MusicConfig();
        }

        public MusicConfig Snapshot()
        {
            lock (_sync)
            {
                var json = JsonConvert.SerializeObject(_config, Settings);
                return JsonConvert.DeserializeObject<MusicConfig>(json, Settings) ?? new MusicConfig();
            }
        }

        public List<Song> GetSongs(string uid)
        {
            lock (_sync)
            {
                var tag = _config.RegisteredTags.FirstOrDefault(item => string.Equals(item.Uid, uid, StringComparison.OrdinalIgnoreCase));
                return tag?.Songs.ToList() ?? [];
            }
        }

        public bool TryAddTag(Tag tag)
        {
            lock (_sync)
            {
                if (_config.RegisteredTags.Any(item => string.Equals(item.Uid, tag.Uid, StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                _config.RegisteredTags.Add(tag);
                try
                {
                    Save();
                    return true;
                }
                catch
                {
                    _config.RegisteredTags.Remove(tag);
                    throw;
                }
            }
        }

        private void Save()
        {
            var temporaryPath = _filePath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonConvert.SerializeObject(_config, Formatting.Indented, Settings));
                File.Move(temporaryPath, _filePath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
}