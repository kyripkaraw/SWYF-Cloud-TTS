using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwyfCloudTts.Plugin
{
    internal sealed class CloudTtsSpeaker
    {
        public string Id = "";
        public string Label = "";
        public string Gender = "unknown";
        public double PitchHz;
        public string FirstSeen = "";
        public bool KeepClone;
        public readonly Dictionary<string, string> Voices = new Dictionary<string, string>();

        public string ShortId => Id.Length > 6 ? Id.Substring(0, 6) : Id;

        public string Voice(string provider)
        {
            string voice;
            return Voices.TryGetValue(provider, out voice) ? voice : "";
        }
    }

    /// <summary>
    /// Reads and edits voices.json, the speaker book the worker fills in as characters speak.
    /// Edits go through a JObject so fields the worker adds survive the round trip; the worker
    /// re-reads the file when it changes, so a new choice applies from the next line.
    /// </summary>
    internal static class CloudTtsVoices
    {
        public static string BookPath => Path.Combine(CloudTtsPaths.StateDirectory, "voices.json");

        public static string ReferencePath(string id)
            => Path.Combine(Path.Combine(CloudTtsPaths.StateDirectory, "voices"), id + ".wav");

        /// <summary>Raw file text, used to notice changes without relying on file timestamps.</summary>
        public static string ReadText()
        {
            try
            {
                return File.Exists(BookPath) ? File.ReadAllText(BookPath) : "";
            }
            catch (Exception ex)
            {
                PluginLog.Debug("cannot read voices.json: " + ex.Message);
                return "";
            }
        }

        public static List<CloudTtsSpeaker> Parse(string text)
        {
            var speakers = new List<CloudTtsSpeaker>();
            JObject root = ParseRoot(text);
            JObject book = root["speakers"] as JObject;
            if (book == null)
            {
                return speakers;
            }

            foreach (JProperty property in book.Properties())
            {
                JObject entry = property.Value as JObject;
                if (entry == null)
                {
                    continue;
                }

                var speaker = new CloudTtsSpeaker
                {
                    Id = property.Name,
                    Label = CloudTtsConfig.String(entry, null, "label", ""),
                    Gender = CloudTtsConfig.String(entry, null, "gender", "unknown"),
                    PitchHz = CloudTtsConfig.Num(entry, null, "pitchHz", 0),
                    FirstSeen = CloudTtsConfig.String(entry, null, "firstSeen", ""),
                    KeepClone = CloudTtsConfig.Bool(entry, null, "keepClone", false),
                };

                if (entry["voices"] is JObject voices)
                {
                    foreach (JProperty voice in voices.Properties())
                    {
                        string value = voice.Value.Type == JTokenType.String ? (string)voice.Value : "";
                        if (!string.IsNullOrEmpty(value))
                        {
                            speaker.Voices[voice.Name] = value;
                        }
                    }
                }

                Insert(speakers, speaker);
            }

            return speakers;
        }

        public static void SetVoice(string id, string provider, string voice)
        {
            Update(root =>
            {
                JObject entry = Entry(root, id);
                if (entry == null)
                {
                    return;
                }

                JObject voices = entry["voices"] as JObject;
                if (voices == null)
                {
                    voices = new JObject();
                    entry["voices"] = voices;
                }

                if (string.IsNullOrWhiteSpace(voice))
                {
                    voices.Remove(provider);
                }
                else
                {
                    voices[provider] = voice.Trim();
                }
            });
        }

        public static void SetLabel(string id, string label)
        {
            Update(root =>
            {
                JObject entry = Entry(root, id);
                if (entry != null)
                {
                    entry["label"] = string.IsNullOrWhiteSpace(label) ? JValue.CreateNull() : new JValue(label.Trim());
                }
            });
        }

        /// <summary>A kept character's ElevenLabs clone is never deleted to free a voice slot.</summary>
        public static void SetKeepClone(string id, bool keep)
        {
            Update(root =>
            {
                JObject entry = Entry(root, id);
                if (entry != null)
                {
                    entry["keepClone"] = keep ? new JValue(true) : JValue.CreateNull();
                }
            });
        }

        public static int ResetProvider(string provider)
        {
            int changed = 0;
            Update(root =>
            {
                JObject book = root["speakers"] as JObject;
                if (book == null)
                {
                    return;
                }

                foreach (JProperty property in book.Properties())
                {
                    if (property.Value is JObject entry && entry["voices"] is JObject voices && voices.Remove(provider))
                    {
                        changed++;
                    }
                }
            });

            return changed;
        }

        public static byte[] ReadReference(string id)
        {
            string path = ReferencePath(id);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }

        private static void Update(Action<JObject> mutate)
        {
            string text = ReadText();
            JObject root = string.IsNullOrWhiteSpace(text) ? EmptyBook() : LoadBook(text);
            mutate(root);

            Directory.CreateDirectory(CloudTtsPaths.StateDirectory);
            string temp = BookPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, root.ToString(Formatting.Indented), new UTF8Encoding(false));
            try
            {
                if (File.Exists(BookPath))
                {
                    File.Replace(temp, BookPath, null);
                }
                else
                {
                    File.Move(temp, BookPath);
                }
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }

        private static JObject Entry(JObject root, string id)
            => root["speakers"] is JObject book ? book[id] as JObject : null;

        private static JObject ParseRoot(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return EmptyBook();
            }

            try
            {
                return LoadBook(text);
            }
            catch (Exception ex)
            {
                PluginLog.Warning("voices.json could not be parsed: " + ex.Message);
                return EmptyBook();
            }
        }

        /// <summary>Dates stay strings so the worker reads back exactly what it wrote.</summary>
        private static JObject LoadBook(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None })
            {
                return JObject.Load(reader, null);
            }
        }

        private static JObject EmptyBook() => new JObject { ["speakers"] = new JObject() };

        /// <summary>Keeps the list in the order characters were first heard (ISO dates sort as text).</summary>
        private static void Insert(List<CloudTtsSpeaker> list, CloudTtsSpeaker speaker)
        {
            int index = list.Count;
            while (index > 0 && string.CompareOrdinal(list[index - 1].FirstSeen, speaker.FirstSeen) > 0)
            {
                index--;
            }

            list.Insert(index, speaker);
        }
    }
}
