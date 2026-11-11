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

        public bool TryUpdateTag(Tag tag)
        {
            lock (_sync)
            {
                var index = _config.RegisteredTags.FindIndex(item => string.Equals(item.Uid, tag.Uid, StringComparison.OrdinalIgnoreCase));
                if (index < 0) return false;

                var previous = _config.RegisteredTags[index];
                _config.RegisteredTags[index] = tag;
                try
                {
                    Save();
                    return true;
                }
                catch
                {
                    _config.RegisteredTags[index] = previous;
                    throw;
                }
            }
        }

        public bool TryDeleteTag(string uid)
        {
            lock (_sync)
            {
                var index = _config.RegisteredTags.FindIndex(item => string.Equals(item.Uid, uid, StringComparison.OrdinalIgnoreCase));
                if (index < 0) return false;

                var removedTag = _config.RegisteredTags[index];
                _config.RegisteredTags.RemoveAt(index);
                try
                {
                    Save();
                    return true;
                }
                catch
                {
                    _config.RegisteredTags.Insert(index, removedTag);
                    throw;
                }
            }
        }

        public void SetTrackControlTags(string? forwardUid, string? rewindUid)
        {
            lock (_sync)
            {
                var previous = _config.ControlTags;
                var configuredUids = new HashSet<string>(
                    new[] { forwardUid, rewindUid }.Where(uid => uid != null)!,
                    StringComparer.OrdinalIgnoreCase);
                var updated = previous
                    .Where(pair => !string.Equals(pair.Value, "next", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(pair.Value, "previous", StringComparison.OrdinalIgnoreCase)
                        && !configuredUids.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

                if (forwardUid != null) updated[forwardUid] = "next";
                if (rewindUid != null) updated[rewindUid] = "previous";

                _config.ControlTags = updated;
                try
                {
                    Save();
                }
                catch
                {
                    _config.ControlTags = previous;
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