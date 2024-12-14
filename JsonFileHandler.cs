using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MusicDimension
{
    using System;
    using System.IO;
    using System.Text.Json;

    public static class JsonFileHandler
    {
        // Save an object as JSON to a file
        public static void SaveObjectToJsonFile<T>(string filePath, T obj)
        {
            try
            {
                var jsonString = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
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
                var deserializedObject = JsonSerializer.Deserialize<T>(jsonString);
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
