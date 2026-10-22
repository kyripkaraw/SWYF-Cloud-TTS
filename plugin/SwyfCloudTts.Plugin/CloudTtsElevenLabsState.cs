using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SwyfCloudTts.Plugin
{
    /// What the worker last learned about the ElevenLabs account: clones and limits from
    /// elevenlabs-clones.json, the player's own voices from elevenlabs-voices.json. The card
    /// never calls ElevenLabs itself.
    internal sealed class CloudTtsElevenLabsState
    {
        public readonly HashSet<string> Clones = new HashSet<string>();

        /// The player's own voices (designed, cloned by hand, saved from the library): id and name.
        public readonly List<KeyValuePair<string, string>> MyVoices = new List<KeyValuePair<string, string>>();

        public bool HasAccount;
        public int SlotsUsed;
        public int SlotLimit;
        public int AddEdits;
        public int MaxAddEdits = -1;
        public string Checked = "";
        public string BlockedUntil = "";
        public string BlockedCode = "";
        public string BlockedReason = "";

        public static string StatePath => Path.Combine(CloudTtsPaths.StateDirectory, "elevenlabs-clones.json");

        public static string VoicesPath => Path.Combine(CloudTtsPaths.StateDirectory, "elevenlabs-voices.json");

        public static CloudTtsElevenLabsState Load()
        {
            var state = new CloudTtsElevenLabsState();
            try
            {
                JObject root = Read(StatePath);
                if (root != null)
                {
                    if (root["clones"] is JObject clones)
                    {
                        foreach (JProperty clone in clones.Properties())
                        {
                            state.Clones.Add(clone.Name);
                        }
                    }

                    if (root["account"] is JObject account)
                    {
                        state.HasAccount = true;
                        state.SlotsUsed = CloudTtsConfig.Int(account, null, "slotsUsed", 0);
                        state.SlotLimit = CloudTtsConfig.Int(account, null, "slotLimit", 0);
                        state.AddEdits = CloudTtsConfig.Int(account, null, "addEdits", 0);
                        state.MaxAddEdits = CloudTtsConfig.Int(account, null, "maxAddEdits", -1);
                        state.Checked = CloudTtsConfig.String(account, null, "checked", "");
                    }

                    state.BlockedUntil = CloudTtsConfig.String(root, null, "blockedUntil", "");
                    state.BlockedCode = CloudTtsConfig.String(root, null, "blockedCode", "");
                    state.BlockedReason = CloudTtsConfig.String(root, null, "blockedReason", "");
                }

                JObject voices = Read(VoicesPath);
                if (voices != null && voices["voices"] is JArray list)
                {
                    foreach (JToken token in list)
                    {
                        if (token is JObject voice)
                        {
                            string id = CloudTtsConfig.String(voice, null, "id", "");
                            string name = CloudTtsConfig.String(voice, null, "name", "");
                            if (id.Length > 0)
                            {
                                state.MyVoices.Add(new KeyValuePair<string, string>(id, name.Length > 0 ? name : id));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Debug("cannot read the ElevenLabs state: " + ex.Message);
            }

            return state;
        }

        public string MyVoiceName(string id)
        {
            foreach (KeyValuePair<string, string> voice in MyVoices)
            {
                if (voice.Key == id)
                {
                    return voice.Value;
                }
            }

            return null;
        }

        /// One line for the card, or empty before the worker has looked at the account.
        public string Describe()
        {
            string text = "";
            if (HasAccount)
            {
                text = MaxAddEdits >= 0
                    ? CloudTtsText.T("eleven.accountBudget", Short(Checked), SlotsUsed, SlotLimit, AddEdits, MaxAddEdits)
                    : CloudTtsText.T("eleven.account", Short(Checked), SlotsUsed, SlotLimit);
            }

            if (BlockedUntil.Length > 0 &&
                string.CompareOrdinal(BlockedUntil, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)) > 0)
            {
                string reason = BlockedCode.Length > 0 ? CloudTtsText.T("block." + BlockedCode) : BlockedReason;
                text += (text.Length > 0 ? " " : "") + CloudTtsText.T("eleven.paused", Short(BlockedUntil), reason);
            }

            return text;
        }

        private static JObject Read(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None })
            {
                return JObject.Load(reader, null);
            }
        }

        /// "2026-10-22T13:51:58.123Z" becomes "2026-10-22 13:51 UTC".
        private static string Short(string iso)
            => iso.Length >= 16 ? iso.Substring(0, 10) + " " + iso.Substring(11, 5) + " UTC" : iso;
    }
}
