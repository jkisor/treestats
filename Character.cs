﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using WindowsTimer = System.Windows.Forms.Timer;
using System.Globalization;
using Decal.Adapter;
using Decal.Adapter.Wrappers;

namespace TreeStats
{
    public struct AllegianceInfoRecord 
    {
        public String name;
        public int rank;
        public int race;
        public int gender;

        public AllegianceInfoRecord(String _name, int _rank, int _race, int _gender)
        {
            name = _name;
            rank = _rank;
            race = _race;
            gender = _gender;
        }                   
    }                      

    public static class Character
    {
        public static Uri endpoint;

        public static CoreManager MyCore { get; set; }
        public static PluginHost MyHost { get; set; }

        // Updates
        public static DateTime lastSend;            // Throttle sending
        public static int minimumSendInterval = 5;  // Seconds
        public static WindowsTimer updateTimer;     // Automatically send updates every `updateTimerInterval`
        public static int updateTimerInterval = 1000 * 60 * 60; // One hour
        public static bool sentServerPopulation;    // Only send server pop the first time (after login)
        
        // Store latest message 
        public static string lastMessage = null;

        // Make an area to store information
        // These are stored for later because we get some of
        // this information before we login and are ready to 
        // send the character update.

        public static string character;
        public static string server;
        public static int currentTitle;
        public static List<Int32> titlesList;
        public static Int64 luminance_earned;
        public static Int64 luminance_total;

        // Allegiance information
        public static string allegianceName;
        public static int allegianceSize;
        public static int followers;
        public static AllegianceInfoRecord monarch;
        public static AllegianceInfoRecord patron;
        public static List<AllegianceInfoRecord> vassals;

        // Store character properties from GameEvent/Login Character message
        public static Dictionary<Int32, Int32> characterProperties;


        // DWORD Blacklist
        // We're going to grab all the Character Property DWORD values
        // becuase we haven't figured all of the ones we want.
        // But there are some we know we never want and we'll blacklist those.

        public static List<Int32> dwordBlacklist;

        // Resources
        // http://www.immortalbob.com/phpBB3/viewtopic.php?f=24&t=100&start=10
        // http://pastebin.com/X05rYnYU
        // http://www.virindi.net/repos/virindi_public/trunk/VirindiTankLootPlugins/VTClassic%20Shared/Constants.cs

        internal static void Init(CoreManager core, PluginHost host)
        {
            try
            {
                endpoint = new Uri(PluginCore.urlBase);

                MyCore = core;
                MyHost = host;

                // General character info
                currentTitle = -1;
                titlesList = new List<Int32>();
                allegianceName = null;
                luminance_earned = -1;
                luminance_total = -1;
                vassals = new List<AllegianceInfoRecord>();

                // Store all returned character properties from the Login Player event
                characterProperties = new Dictionary<Int32, Int32>();

                // A list of dwords we know we don't want to save
                dwordBlacklist = new List<Int32>()
                {
                     2,5,7,10,17,19,20,24,25,26,28,30,33,35,36,38,43,45,86,87,88,89,90,91,
                     92,98,105,106,107,108,109,110,111,113,114,115,117,125,129,131,134,158,
                     159,160,166,170,171,172,174,175,176,177,178,179,188,193,270,271,272,293
                };


                lastSend = DateTime.MinValue;

                // Set up timed updates
                updateTimer = new WindowsTimer();
                updateTimer.Interval = updateTimerInterval;
                updateTimer.Tick += new EventHandler(updateTimer_Tick);
                updateTimer.Start();

                sentServerPopulation = false;
            }
            catch (Exception ex)
            {
                Logging.LogError(ex);
            }
        }

        internal static void Destroy()
        {
            try
            {
                endpoint = null;

                lastMessage = null;

                characterProperties.Clear();
                characterProperties = null;
                
                if (vassals != null)
                {
                    vassals.Clear();
                }

                vassals = null;
                
                dwordBlacklist.Clear();
                dwordBlacklist = null;

                lastSend = DateTime.MinValue;

                if (updateTimer != null)
                {
                    updateTimer.Stop();
                    updateTimer.Tick -= updateTimer_Tick;
                    updateTimer.Dispose();
                    updateTimer = null;
                }
            }
            catch (Exception ex)
            {
                Logging.LogError(ex);
            }
        }

        internal static void updateTimer_Tick(object sender, EventArgs e)
        {
            Logging.LogMessage("updateTimer_Tick()");

            TryUpdate(false);
        }


        /* DoUpdate()
         * 
         * Sends an update without checking for the minimum send interval
         */

        internal static void DoUpdate()
        {
            try
            {
                Logging.LogMessage("DoUpdate()");

                GetCharacterInfo();
                SendCharacterInfo(lastMessage);
            }
            catch (Exception ex)
            {
                Logging.LogError(ex);
            }
        }


        /* TryUpdate()
         * 
         * Sends an update via DoUpdate() after checking for the minimum time interval
         */

        internal static void TryUpdate(bool isManual)
        {
            TryUpdate(isManual, false);
        }

        internal static void TryUpdate(bool isManual, bool isQuiet)
        {
            Logging.LogMessage("TryUpdate()");

            // Automatic 
            if (!isManual) 
            {
                if (Settings.ShouldSendCharacter(Character.server + "-" + Character.character)) 
                {
                    DoUpdate();
                } 

            // Manual update
            }
            else
            {
                if (lastSend != DateTime.MinValue) // Null check: Can't do null checks on DateTimes, so we do this
                {
                    TimeSpan diff = DateTime.Now - lastSend;

                    if (diff.Seconds < minimumSendInterval) 
                    {
                        if (!isQuiet)
                        {
                            Util.WriteToChat("Failed to send character: Please wait " + (minimumSendInterval - diff.Seconds).ToString() + "s before sending again.");
                        }

                        return;
                    }
                }

                DoUpdate();

            }
        }


        /* GetCharacterInfo()
         * 
         * Gets player information
         *  
         * This method builds a JSON request string which is later encrypted
         * It would be really nice to use a proper data structure for
         * this and using Json.NET to serialize that data structure.
         * 
         * Some of the stuff here is stored beforehand from other messages
         * Most of it is taken from CharacterFilter once login is completed
         * 
         * Saves the concatenated JSON string to class variable lastMessage
         */

        internal static void GetCharacterInfo()
        {
            try
            {
                // Declare the fileservice for later use
                Decal.Adapter.Wrappers.CharacterFilter cf = MyCore.CharacterFilter;
                Decal.Filters.FileService fs = CoreManager.Current.FileService as Decal.Filters.FileService;

                // Save character and server for later since we're going to use this alot
                server = cf.Server;

                // Prepare the en-US culture for string creation
                // I use this because I want my dates to all be formatted as if
                // the client using an en-US locale
                CultureInfo culture_en_us = new CultureInfo("en-US");

                // One long string stores the entire POST request body
                // And the string is generated with StringBuilder
                StringBuilder req = new StringBuilder();
                req.Append("{");

                // Add the TreeStats Account if it's valid (logged in)
                Logging.LogMessage("Checking whether player is logged in...");
                Logging.LogMessage("  Settings::isLoggedIn() : " + Settings.isLoggedIn.ToString());
                Logging.LogMessage("  Settings::useAccount() : " + Settings.useAccount.ToString());
                Logging.LogMessage("  Settings::accountName() : " + Settings.accountName);

                if (Settings.isLoggedIn && Settings.useAccount && Settings.accountName.Length > 0 && Settings.accountPassword.Length > 0)
                {
                    Logging.LogMessage("Appending account name " + Settings.accountName + " to this upload.");
                    req.AppendFormat("\"account_name\":\"{0}\",", Settings.accountName);
                }

                // General attributes
                req.AppendFormat("\"version\":\"{0}\",", 2);
                req.AppendFormat("\"name\":\"{0}\",", character);
                req.AppendFormat("\"race\":\"{0}\",", cf.Race);
                req.AppendFormat("\"gender\":\"{0}\",", cf.Gender);
                req.AppendFormat("\"level\":{0},", cf.Level);

                // Add allegiance name if we've gotten it in a message
                if (allegianceName != null)
                {
                    req.AppendFormat("\"allegiance_name\":\"{0}\",", allegianceName);
                }

                req.AppendFormat("\"rank\":{0},", cf.Rank);
                req.AppendFormat("\"followers\":{0},", followers);
                req.AppendFormat("\"server\":\"{0}\",", cf.Server);

                /* Only append server population if it hasn't been sent yet (we just logged in).
                / Character Filter only receives this value from the server and login, instead
                / of continuously. If we sent this each time we'd be reporting inaccurate server
                 * populations.
                */
                if (!sentServerPopulation)
                {
                    req.AppendFormat("\"server_population\":{0},", cf.ServerPopulation);
                    sentServerPopulation = true;
                }


                req.AppendFormat("\"deaths\":{0},", cf.Deaths);
                req.AppendFormat("\"birth\":\"{0}\",", cf.Birth.ToString(culture_en_us));
                req.AppendFormat("\"total_xp\":{0},", cf.TotalXP);
                req.AppendFormat("\"unassigned_xp\":{0},", cf.UnassignedXP);
                req.AppendFormat("\"skill_credits\":{0},", cf.SkillPoints);
                //req.AppendFormat("\"age\":{0},", cf.Age);

                // Luminance XP
                if (luminance_earned != -1)
                {
                    req.AppendFormat("\"luminance_earned\":{0},", luminance_earned);
                }

                if (luminance_total != -1)
                {

                    req.AppendFormat("\"luminance_total\":{0},", luminance_total);
                }

                // Attributes
                req.Append("\"attribs\":{");
                string attribs_format = "\"{0}\":{{\"name\":\"{1}\",\"base\":{2},\"creation\":{3}}},";

                foreach (var attr in MyCore.CharacterFilter.Attributes)
                {
                    req.AppendFormat(attribs_format, attr.Name.ToLower(), attr.Name, attr.Base, attr.Creation);
                }

                req.Remove(req.Length - 1, 1);
                req.Append("},");


                // Vitals
                req.Append("\"vitals\":{");
                string vitals_format = "\"{0}\":{{\"name\":\"{1}\",\"base\":{2}}},";

                foreach (var vital in MyCore.CharacterFilter.Vitals)
                {
                    req.AppendFormat(vitals_format, vital.Name.ToLower(), vital.Name, vital.Base);
                }

                req.Remove(req.Length - 1, 1);
                req.Append("},");


                // Skills
                Decal.Interop.Filters.SkillInfo skillinfo = null;

                req.Append("\"skills\":{");
                string skill_format = "\"{0}\":{{\"name\":\"{1}\",\"base\":{2},\"training\":\"{3}\"}},";

                string name;
                string training;

                for (int i = 0; i < fs.SkillTable.Length; ++i)
                {
                    try
                    {
                        skillinfo = MyCore.CharacterFilter.Underlying.get_Skill((Decal.Interop.Filters.eSkillID)fs.SkillTable[i].Id);

                        name = skillinfo.Name.ToLower().Replace(" ", "_");
                        training = skillinfo.Training.ToString().Substring(6);

                        req.AppendFormat(skill_format, name, name, skillinfo.Base, training);
                    }
                    finally
                    {
                        if (skillinfo != null)
                        {
                            System.Runtime.InteropServices.Marshal.ReleaseComObject(skillinfo);
                            skillinfo = null;
                        }

                        name = null;
                        training = null;
                    }
                }


                req.Remove(req.Length - 1, 1);
                req.Append("},");


                // Monarch & Patron Information
                // We wrap in try/catch because AllegianceInfoWrapper behaves oddly (Is not null when it should be? Not sure on this.)
                try
                {
                    if (monarch.name != null)
                    {
                        req.AppendFormat("\"monarch\":{{\"name\":\"{0}\",\"race\":{1},\"rank\":{2},\"gender\":{3},\"followers\":{4}}},", 
                            monarch.name, monarch.race, monarch.rank, monarch.gender, allegianceSize);
                    }
                }
                catch (Exception ex)
                {
                    Logging.LogError(ex);
                }

                try
                {
                    if (patron.name != null)
                    {
                        req.AppendFormat("\"patron\":{{\"name\":\"{0}\",\"race\":{1},\"rank\":{2},\"gender\":{3}}},", 
                            patron.name, patron.race, patron.rank, patron.gender);
                    }
                }
                catch (Exception ex)
                {
                    Logging.LogError(ex);
                }


                // Vassals
                try
                {
                    if (vassals.Count > 0)
                    {
                        req.Append("\"vassals\":[");
                        string vassal_format = "{{\"name\":\"{0}\",\"race\":{1},\"rank\":{2},\"gender\":{3}}},";

                        foreach (AllegianceInfoRecord vassal in vassals)
                        {
                            req.AppendFormat(vassal_format, vassal.name, vassal.race, vassal.rank, vassal.gender);
                        }

                        req.Remove(req.Length - 1, 1);
                        req.Append("],");
                    }
                }
                catch (Exception ex)
                {
                    Logging.LogError(ex);
                }

                // Titles
                // Add titles to message if we have them
                if (currentTitle != -1)
                {
                    req.AppendFormat("\"current_title\":{0},", currentTitle);
                    req.Append("\"titles\":[");

                    //foreach(int titleId in titlesList)
                    for (int i = 0; i < titlesList.Count; i++)
                    {
                        req.AppendFormat("{0},", titlesList[i]);
                    }

                    // Remove final trailing comma
                    req.Remove(req.Length - 1, 1);
                    req.Append("],");
                }


                // Character Properties
                if (characterProperties.Count > 0)
                {
                    req.Append("\"properties\":{");

                    string property_format = "\"{0}\":{1},";

                    foreach (var kvp in characterProperties)
                    {
                        req.AppendFormat(property_format, kvp.Key, kvp.Value);
                    }

                    req.Remove(req.Length - 1, 1);
                    req.Append("},");
                }

                req.Remove(req.Length - 1, 1); // Remove trailing comma
                req.Append("}");

                // Encrypt POST request
                
                lastMessage = Encryption.encrypt(req.ToString());

                Logging.LogMessage(req.ToString());
            }
            catch (Exception ex)
            {
                Logging.LogError(ex);
            }
        }

        internal static void SendCharacterInfo(string message)
        {
            Uri sendUrl;

            try
            {
                lastSend = DateTime.Now;

                if (message == null || message.Length < 1)
                {
                    return;
                }

                // Do the sending
                using (var client = new WebClient())
                {
                    client.UploadStringCompleted += (s, e) =>
                    {
                        if (e.Error != null)
                        {
                            Util.WriteToChat("Update failed: " + e.Error.Message);
                        }
                        else
                        {
                            Util.WriteToChat(e.Result);
                        }
                    };

                    // Decide whether to use the default URL or a custom one
                    if (Settings.useCustomURL) {
                        sendUrl = new Uri(Settings.customURL);
                        Logging.LogMessage("Using custom send URL of " + sendUrl.ToString());
                    } else {
                        sendUrl = endpoint;
                    }

                    client.Headers.Add("User-Agent", "TreeStats v" + typeof(PluginCore).Assembly.GetName().Version.ToString());
                    client.UploadStringAsync(sendUrl, "POST", message);
                }
            }
            catch (Exception ex)
            {
                Logging.LogError(ex);
            }
        }

        internal static void ProcessTitlesMessage(NetworkMessageEventArgs e)
        {
            try
            {

                // Save current title
                currentTitle = e.Message.Value<Int32>("current");

                MessageStruct titles = e.Message.Struct("titles");

                for (int i = 0; i < titles.Count; i++)
                {
                    // Add title to list

                    // Check if exists first so multiple firings of the event don't make 
                    // duplicate titles

                    Int32 titleId = titles.Struct(i).Value<Int32>("title");

                    if (!titlesList.Contains(titleId))
                    {
                        titlesList.Add(titleId);
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.LogError(ex);
            }
        }

        internal static void ProcessCharacterPropertyData(NetworkMessageEventArgs e)
        {
            try
            {
                MessageStruct props = e.Message.Struct("properties");
                MessageStruct dwords = props.Struct("dwords");
                MessageStruct qwords = props.Struct("qwords");
                MessageStruct strings = props.Struct("strings");

                MessageStruct tmpStruct;

                Int32 tmpKey;
                Int32 tmpValue;

                // Process strings to extract character name
                // This is a workaround for a bug in GDLE
                character = strings.Struct(0).Value<string>("value");

                // Process DWORDS
                for (int i = 0; i < dwords.Count; i++)
                {
                    tmpStruct = dwords.Struct(i);

                    tmpKey = tmpStruct.Value<Int32>("key");
                    tmpValue = tmpStruct.Value<Int32>("value");

                    if (!dwordBlacklist.Contains(tmpKey))
                    {
                        characterProperties.Add(tmpKey, tmpValue);
                    }
                }


                // Process QWORDS
                Int64 qwordKey;
                Int64 qwordValue;

                for (int i = 0; i < qwords.Count; i++)
                {
                    tmpStruct = qwords.Struct(i);

                    qwordKey = tmpStruct.Value<Int64>("key");
                    qwordValue = tmpStruct.Value<Int64>("value");

                    if (qwordKey == 6)
                    {
                        luminance_earned = qwordValue;
                    }
                    else if (qwordKey == 7)
                    {
                        luminance_total = qwordValue;
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.LogError(ex);
            }
        }

        internal static void ProcessAllegianceInfoMessage(NetworkMessageEventArgs e)
        {
            monarch = new AllegianceInfoRecord();
            patron = new AllegianceInfoRecord();
            vassals = new List<AllegianceInfoRecord>();
            Dictionary<int, AllegianceInfoRecord> recs = new Dictionary<int, AllegianceInfoRecord>();
            Dictionary<int, int> parents = new Dictionary<int, int>();

            try
            {
                // Take down general info
                allegianceName = e.Message.Value<string>("allegianceName");
                allegianceSize = e.Message.Value<Int32>("allegianceSize");
                followers = e.Message.Value<Int32>("followers");

                // Process the records struct, which has all the members of the
                // allegiance
                MessageStruct records = e.Message.Struct("records");
                MessageStruct record;

                /* Determine monarch, patron, and vassals from the records struct
                 * 
                 * I'm not sure if all of this is strictly necessary but it works. The main
                 * thing adding complexity is the logic for finding the patron. I could
                 * make this simpler if I knew that the records vector was in tree order.
                 */


                int currentId = MyCore.CharacterFilter.Id;

                for (int i = 0; i < records.Count; i++)
                {
                    record = records.Struct(i);

                    Logging.LogMessage("Setting treeparent in parents dict.");

                    // Build up the parents dict as we go
                    parents[record.Value<int>("character")] = record.Value<int>("treeParent");

                    // Save the record for later
                    recs.Add(
                        record.Value<int>("character"), 
                        new AllegianceInfoRecord(record.Value<string>("name"), 
                        record.Value<int>("rank"),
                        record.Value<int>("race"),
                        record.Value<int>("gender")));

                    // Add to vassals if appropriate
                    if (record.Value<int>("treeParent") == currentId)
                    {
                        vassals.Add(
                            new AllegianceInfoRecord(
                                record.Value<string>("name"), 
                                record.Value<int>("rank"), 
                                record.Value<int>("race"), 
                                record.Value<int>("gender")));
                    }

                    // Set as monarch if appropriate
                    else if (record.Value<int>("treeParent") <= 1)
                    {
                        monarch = new AllegianceInfoRecord(
                            record.Value<string>("name"), 
                            record.Value<int>("rank"),
                            record.Value<int>("race"), 
                            record.Value<int>("gender"));
                    }
                }

                // Should stop now if records is empty, because we aren't in an allegiance


                Logging.LogMessage("Printing records");

                foreach (KeyValuePair<int, AllegianceInfoRecord> rec in recs)
                {
                    Logging.LogMessage(rec.Key.ToString() + " : " + rec.Value.name);  
                }

                Logging.LogMessage("Printing parents");

                foreach (KeyValuePair<int, int> rec in parents)
                {
                    Logging.LogMessage(rec.Key.ToString() + ": " + rec.Value.ToString());
                }

                // Set patron
                if (parents.Count > 0 && recs.ContainsKey(parents[currentId]))
                {
                    patron = recs[parents[currentId]];
                }
            }
            catch (Exception ex)
            {
                Logging.LogError(ex);
            }
            finally
            {
                recs.Clear();
                recs = null;
                parents.Clear();
                parents = null;
            }
        }

        internal static void ProcessSetTitleMessage(NetworkMessageEventArgs e)
        {
            try
            {
                Int32 title = e.Message.Value<Int32>("title");
                bool active = e.Message.Value<bool>("active");

                if (active)
                {
                    currentTitle = title;
                }
            }
            catch (Exception ex)
            {
                Logging.LogError(ex);
            }
        }
    }
}
