using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using ICities;

namespace RealisticRoads
{
    // Road builder configs on disk, one XML file per road, shared by every city:
    //   %LOCALAPPDATA%\Colossal Order\Cities_Skylines\RealisticRoads\Roads\<id>.xml
    // A copy also goes into each saved city (RoadConfigsData), so a shared save
    // can bring its roads along. Any thread; guarded by a lock.
    public static class RoadLibrary
    {
        public const string DataId = "RealisticRoads.RoadConfigs";
        private static readonly object sync = new object();
        private static List<RoadConfig> cache;

        public class ConfigList
        {
            public List<RoadConfig> Roads = new List<RoadConfig>();
        }

        public static string Folder
        {
            get
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(Path.Combine(Path.Combine(Path.Combine(local, "Colossal Order"), "Cities_Skylines"), "RealisticRoads"), "Roads");
            }
        }

        // A copy of the list (the configs themselves are shared; don't change them).
        public static List<RoadConfig> All()
        {
            lock (sync)
            {
                if (cache == null) cache = LoadFolder();
                return new List<RoadConfig>(cache);
            }
        }

        public static RoadConfig Find(string id)
        {
            foreach (var config in All()) if (config.Id == id) return config;
            return null;
        }

        public static void Save(RoadConfig config)
        {
            lock (sync)
            {
                if (cache == null) cache = LoadFolder();
                Directory.CreateDirectory(Folder);
                using (var stream = File.Create(PathOf(config)))
                    new XmlSerializer(typeof(RoadConfig)).Serialize(stream, config);
                cache.RemoveAll(c => c.Id == config.Id);
                cache.Add(config);
            }
            Mod.Log("Road builder: saved '" + config.Name + "' to " + PathOf(config));
        }

        public static void Delete(RoadConfig config)
        {
            lock (sync)
            {
                if (File.Exists(PathOf(config))) File.Delete(PathOf(config));
                if (cache != null) cache.RemoveAll(c => c.Id == config.Id);
            }
            RoadTypes.Forget(config);
            Mod.Log("Road builder: deleted '" + config.Name + "' from the library");
        }

        public static byte[] Serialize(List<RoadConfig> configs)
        {
            using (var stream = new MemoryStream())
            {
                new XmlSerializer(typeof(ConfigList)).Serialize(stream, new ConfigList { Roads = configs });
                return stream.ToArray();
            }
        }

        public static List<RoadConfig> Deserialize(byte[] data)
        {
            if (data == null || data.Length == 0) return new List<RoadConfig>();
            using (var stream = new MemoryStream(data))
            {
                var list = (ConfigList)new XmlSerializer(typeof(ConfigList)).Deserialize(stream);
                return list != null && list.Roads != null ? list.Roads : new List<RoadConfig>();
            }
        }

        private static string PathOf(RoadConfig config) => Path.Combine(Folder, config.Id + ".xml");

        private static List<RoadConfig> LoadFolder()
        {
            var list = new List<RoadConfig>();
            try
            {
                if (!Directory.Exists(Folder)) return list;
                var serializer = new XmlSerializer(typeof(RoadConfig));
                foreach (var file in Directory.GetFiles(Folder, "*.xml"))
                {
                    try
                    {
                        using (var stream = File.OpenRead(file))
                        {
                            var config = (RoadConfig)serializer.Deserialize(stream);
                            if (config != null && !string.IsNullOrEmpty(config.Id)) list.Add(config);
                        }
                    }
                    catch (Exception e)
                    {
                        Mod.LogError("Road builder: can't read " + file + ", skipped", e);
                    }
                }
                Mod.Log("Road builder: " + list.Count + " road(s) in " + Folder);
            }
            catch (Exception e)
            {
                Mod.LogError("Road builder: reading the library failed", e);
            }
            return list;
        }
    }

    // Keeps the configs of the city's road builder roads in the save.
    public class RoadConfigsData : SerializableDataExtensionBase
    {
        public override void OnLoadData()
        {
            try
            {
                var configs = RoadLibrary.Deserialize(serializableDataManager.LoadData(RoadLibrary.DataId));
                foreach (var config in configs)
                {
                    if (RoadLibrary.Find(config.Id) != null) continue;
                    RoadLibrary.Save(config);
                    Mod.Log("Road builder: '" + config.Name + "' came with this city and was added to your library. " +
                            "If its roads are missing, don't save: load the city again.");
                }
            }
            catch (Exception e)
            {
                Mod.LogError("Road builder: loading the city's roads failed", e);
            }
        }

        public override void OnSaveData()
        {
            try
            {
                serializableDataManager.SaveData(RoadLibrary.DataId, RoadLibrary.Serialize(RoadTypes.UsedConfigs()));
            }
            catch (Exception e)
            {
                Mod.LogError("Road builder: saving the city's roads failed", e);
            }
        }
    }
}
