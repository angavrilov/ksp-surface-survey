using System;
using System.ComponentModel;
using System.Collections.Generic;
using UnityEngine;

namespace SurfaceSurvey
{
    // A string-to-scalar dictionary with default value that can be auto-loaded via KSPField.
    public class ConfigDictionary<ItemType> : IConfigNode
    {
        private Dictionary<string, ItemType> data = new Dictionary<string, ItemType>();

        private string default_key;
        private ItemType default_value_field;
        private ConfigNode backup_config;

        private static TypeConverter cvt = TypeDescriptor.GetConverter(typeof(ItemType));

        private ConfigDictionary() {}

        // Due to broken copying, it requires an adjacent ConfigNode field to back up the data
        public static void AwakeInit(ref ConfigDictionary<ItemType> dict, ref ConfigNode backup, ItemType defval, string key = null)
        {
            if (dict == null)
            {
                //Debug.Log("CDICT INIT "+(backup != null ? backup.ToString() : "null"));

                dict = new ConfigDictionary<ItemType>();
                dict.default_key = key ?? "default";
                dict.default_value_field = defval;

                if (backup == null)
                    backup = new ConfigNode();
                else
                    dict.Load(backup);

                dict.backup_config = backup;
            }
        }

        public ItemType default_value
        {
            get {
                return default_value_field;
            }
            private set {
                default_value_field = value;

                if (backup_config != null)
                    backup_config.AddValue(default_key, FormatValue(value));
            }
        }

        public ItemType this[string key]
        {
            get {
                return GetWithDefault(key, default_value_field);
            }
            private set {
                //Debug.Log("CDICT SET "+key+" = "+value);

                if (key == default_key)
                    default_value = value;
                else
                {
                    data[key] = value;

                    if (backup_config != null)
                        backup_config.AddValue(key, FormatValue(value));
                }
            }
        }

        public ItemType GetWithDefault(string key, ItemType defval)
        {
            ItemType val;
            if (!data.TryGetValue(key, out val))
                val = defval;
            return val;
        }

        private ItemType ParseValue(string key, string value)
        {
            try
            {
                return (ItemType)cvt.ConvertFromInvariantString(value);
            }
            catch
            {
                Debug.LogError("Could not parse value: "+key+" = "+value);
                return default_value;
            }
        }

        private string FormatValue(ItemType value)
        {
            return cvt.ConvertToInvariantString(value);
        }

        public void LoadDefault(string value)
        {
            if (value != null)
                default_value = ParseValue(default_key, value);
        }

        public void Load(ConfigNode node)
        {
            foreach (ConfigNode.Value val in node.values)
                this[val.name] = ParseValue(val.name, val.value);
        }

        public void Save(ConfigNode node)
        {
            node.AddValue(default_key, FormatValue(default_value));

            foreach (var key in data.Keys)
                node.AddValue(key, FormatValue(data[key]));
        }
    }
}
