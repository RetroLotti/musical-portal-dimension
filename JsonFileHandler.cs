namespace MusicDimension
{
    using Newtonsoft.Json;
    using System;
    using System.IO;

    public static class JsonFileHandler
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
            {
                NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy()
            }
        };

        // Save an object as JSON to a file
        public static void SaveObjectToJsonFile<T>(string filePath, T sourceObject)
        {
            try
            {
                var jsonString = JsonConvert.SerializeObject(sourceObject, Formatting.Indented, Settings);
                File.WriteAllTextAsync(filePath, jsonString);
                Console.WriteLine($"Object saved to {filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving object to file: {ex.Message}");
            }
        }

        // Read an object from a JSON file
        public static T? ReadObjectFromJsonFile<T>(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return default;

                var jsonString = File.ReadAllText(filePath);
                var deserializedObject = JsonConvert.DeserializeObject<T>(jsonString, Settings);
                Console.WriteLine($"Object read from {filePath}");
                return deserializedObject;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading object from file: {ex.Message}");
                return default;
            }
        }
    }

}
