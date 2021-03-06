using System;
using System.Collections;
using System.Threading;
using Gadgeteer.Modules.GHIElectronics;
using Microsoft.SPOT;
using GTM = Gadgeteer.Modules;
using GHI.Processor;
using DateTimeExtension;
using ArrayListExtension;

namespace SmartLock
{
    internal static class DataHelper
    {
        // Data source
        public const int DataSourceUnknown = 0;
        public const int DataSourceError = 1;
        public const int DataSourceCache = 2;
        public const int DataSourceRemote = 3;
        public const int DataSourceRefresh = 4;

        private static bool initialized = false;

        // Event handling
        public delegate void DsChangedEventHandler(int dataSource);
        public static event DsChangedEventHandler DataSourceChanged;

        // Ethernet object
        private static EthernetJ11D ethernetJ11D;

        // Main data object
        private static readonly ArrayList logList = new ArrayList();
        private static readonly ArrayList userList = new ArrayList();

        // Server + JSON stuff
        private const string DataRequest = "data";
        private const string TimeRequest = "time";
        private const string UserHeader = "AllowedUsers";
        private const string LogHeader = "Log";

        // Thread
        private static bool threadRunning;
        private static Thread threadRoutine;
        private static readonly ManualResetEvent threadWaitForStop = new ManualResetEvent(false);

        private static int dataSource;
        private static bool timeChecked = false;

        public static void Init(EthernetJ11D _ethernetJ11D)
        {
            // Load ip from settings
            String gadgeteerIp = SettingsManager.Get(SettingsManager.LockIp);

            ethernetJ11D = _ethernetJ11D;
            ethernetJ11D.UseThisNetworkInterface();
            ethernetJ11D.UseStaticIP(gadgeteerIp, "255.255.255.0", "192.168.100.1");
            ethernetJ11D.NetworkUp += NetworkUp;
            ethernetJ11D.NetworkDown += NetworkDown;

            // Data is not yet loaded, data source is unknown
            ChangeDataSource(DataSourceUnknown);

            // Load users from cache
            if (CacheManager.Load(userList, CacheManager.UsersCacheFile))
            {
                DebugOnly.Print(userList.Count + " users loaded from cache!");

                // Data source is now cache
                ChangeDataSource(DataSourceCache);
            }
            else
            {
                // Empty data cache is assumed as an error!
                if (DataSourceChanged != null) DataSourceChanged(DataSourceError);

                // Clear user list
                userList.Clear();
            }

            // Load logs from cache if any
            if (CacheManager.Load(logList, CacheManager.LogsCacheFile))
            {
                DebugOnly.Print(logList.Count + " logs loaded from cache!");
            }
            else
            {
                // Clear log list
                logList.Clear();
            }

            initialized = true;
        }

        // Check if the class is initialized
        public static bool IsInitialized()
        {
            return initialized;
        }

        // Access Management
        public static bool CheckCardId(string cardId)
        {
            if (initialized)
            {
                foreach (User user in userList)
                    if (string.Compare(cardId, user.CardID) == 0)
                        return true;

                return false;
            }
            else
            {
                return false;
            }
        }

        public static bool CheckPin(string pin)
        {
            if (initialized)
            {
                foreach (User user in userList)
                    if (string.Compare(pin, user.Pin) == 0)
                        return true;

                return false;
            }
            else
            {
                return false;
            }
        }

        public static bool PinHasNullCardId(string pin)
        {
            if (initialized)
            {
                foreach (User user in userList)
                    if (string.Compare(pin, user.Pin) == 0)
                        if (user.CardID != null)
                            if (string.Compare(string.Empty, user.CardID) == 0)
                                return true;
                            else
                                return false;
                        else
                            return true;

                return false;
            }
            else
            {
                return false;
            }
        }

        public static void AddCardId(string pin, string cardId)
        {
            if (initialized)
            {
                foreach (User user in userList)
                    if (user.Pin == pin)
                    {
                        user.CardID = cardId;
                        break;
                    }

                // Update cache copy
                CacheManager.Store(userList, CacheManager.UsersCacheFile);
            }
        }

        public static void AddLog(Log log)
        {
            if (initialized)
            {
                AddLog(log, log.Type == Log.TypeError);
            }
        }

        public static void AddLog(Log log, bool urgent)
        {
            if (initialized) { 
                logList.Add(log);

                // Update cache copy
                CacheManager.Store(logList, CacheManager.LogsCacheFile);

                // If log is urgent start routine immediately
                if (urgent)
                {
                    startRoutine();
                }
            }
        }

        // Network is online event
        private static void NetworkUp(GTM.Module.NetworkModule sender, GTM.Module.NetworkModule.NetworkState state)
        {
            DebugOnly.Print("Network is up!");

            // Start ServerRoutine
            startRoutine();
        }

        // Network is offline event
        private static void NetworkDown(GTM.Module.NetworkModule sender, GTM.Module.NetworkModule.NetworkState state)
        {
            DebugOnly.Print("Network is down!");

            // Data source is now cache
            if (userList.Count > 0)
                ChangeDataSource(DataSourceCache);
            else
                ChangeDataSource(DataSourceError);

            // Stop ServerRoutine
            stopRoutine();
        }

        /*
         * SERVER ROUTINE:
         * ServerRoutine is the only thread of this class. It periodically updates the current user data, and
         * if logs are stored into logList, it sends the logs to the server.
         */

        private static void ServerRoutine()
        {
            while (threadRunning)
            {
                bool success = true;

                if (ethernetJ11D.IsNetworkUp)
                {
                    ChangeDataSource(DataSourceRefresh);

                    DebugOnly.Print("Beginning server routine...");

                    // Check if ip is valid
                    if (String.Compare(ethernetJ11D.NetworkSettings.IPAddress, "0.0.0.0") != 0)
                    {
                        DebugOnly.Print("My IP is: " + ethernetJ11D.NetworkSettings.IPAddress);
                    }
                    else
                    {
                        DebugOnly.Print("ERROR: Current IP appears to be null!");
                        success = false;
                    }

                    // Request current time
                    if (success && !timeChecked)
                    {
                        success = requestTime();
                    }

                    // Send logs
                    if (success && logList.Count > 0)
                    {
                        DebugOnly.Print(logList.Count + " stored logs must be sent to server!");
                        success = sendLogs();
                    }
                    
                    // Request users
                    if (success)
                    {
                        success = requestUsers();
                    }
                }
                else
                {
                    success = false;
                    DebugOnly.Print("ERROR: No connection, skipping scheduled server polling routine.");
                }

                // Plan next routine
                string periodString;
                int period;
                if (success)
                {
                    periodString = SettingsManager.Get(SettingsManager.RoutinePeriod);
                    period = Int32.Parse(periodString);
                    DebugOnly.Print("Server routine completed! Next event in " + periodString);

                    ChangeDataSource(DataSourceRemote);
                }
                else
                {
                    periodString = SettingsManager.Get(SettingsManager.RetryPeriod);
                    period = Int32.Parse(periodString);
                    DebugOnly.Print("Server routine failed! Next event in " + periodString);

                    // Data source is now cache
                    if (userList.Count > 0)
                        ChangeDataSource(DataSourceCache);
                    else
                        ChangeDataSource(DataSourceError);
                }

                threadWaitForStop.WaitOne(period, true);
                threadWaitForStop.Reset();
            }
        }

        private static void startRoutine()
        {
            threadRunning = true;

            if (threadRoutine != null)
            {
                if (!threadRoutine.IsAlive)
                {
                    threadRoutine = new Thread(ServerRoutine);
                    threadRoutine.Start();
                }
                else
                {
                    threadWaitForStop.Set();
                }
            }
            else
            {
                threadRoutine = new Thread(ServerRoutine);
                threadRoutine.Start();
            }
        }

        private static void stopRoutine()
        {
            threadRunning = false;
        }

        /*
         * DATA SOURCE:
         * The attribute dataSorce stores the source of the user data currently being used.
         * dataSource is determinred according to the following rules:
         * - If the data is not being loaded, dataSource is DATA_SOURCE_UNKNOWN
         * - If the network is down, dataSource is DATA_SOURCE_CACHE
         * - If the network id up and last ServerRoutine was succesfull, dataSource is DATA_SOURCE_REMOTE
         * - In any other case, dataSource is DATA_SOURCE_CACHE
         */

        // Changes the current data source and throws event DataSourceChanged
        private static void ChangeDataSource(int _dataSource)
        {
            if (dataSource != _dataSource)
            {
                dataSource = _dataSource;

                if (DataSourceChanged != null)
                    DataSourceChanged(dataSource);
            }
        }

        // Returns the current data source
        public static int GetDataSource()
        {
            return dataSource;
        }


        /*
         * Server Access
         */
        private static string buildUrlFromSettings(string field)
        {
            string ServerIp = SettingsManager.Get(SettingsManager.ServerIp);
            string ServerPort = SettingsManager.Get(SettingsManager.ServerPort);
            string LockId = SettingsManager.Get(SettingsManager.LockId);
            return "http://" + ServerIp + ":" + ServerPort + "/SmartLockRESTService/" + field + "/?id=" + LockId;
        }


        // Loads userlist from server
        private static bool requestUsers()
        {
            // Create URL
            string url = buildUrlFromSettings(DataRequest);

            // Send request
            DebugOnly.Print("Requesting user list to server...");
            Remote.Result result = Remote.Get(url);

            // Parse response
            if (result.Success)
            {
                ArrayList tempUserList = new ArrayList();

                if (Json.ParseNamedArray(UserHeader, result.Content, tempUserList, typeof(User)))
                {
                    // Copy content to main list
                    // NOTE: CopyFrom clears list automatically
                    userList.CopyFrom(tempUserList);

                    // Store cache copy
                    CacheManager.Store(userList, CacheManager.UsersCacheFile);

                    DebugOnly.Print(userList.Count + " users received from server");
                }
                else
                {
                    DebugOnly.Print("ERROR: User list request failed!");
                }
            }

            // Return result of the operation
            return result.Success;
        }

        // Get current time from server
        private static bool requestTime()
        {
            string url = buildUrlFromSettings(TimeRequest);

            Remote.Result result = Remote.Get(url);

            if (result.Success)
            {
                // Request current time
                DebugOnly.Print("Requesting current time to server...");
                DateTime serverDt = result.Content.ToDateTime();

                DateTime rtcDt = Utils.SafeRtc();

                if (!serverDt.WeakCompare(rtcDt))
                {
                    // Found time mismatch
                    DebugOnly.Print("ERROR: RTC/Server time mismatch! Server: " + serverDt.ToMyString() + ", RTC: " + rtcDt.ToMyString());
                    DebugOnly.Print("Setting RTC...");
                    Log log = new Log(Log.TypeInfo, "RTC/Server time mismatch! Server: " + serverDt.ToMyString() + ", RTC: " + rtcDt.ToMyString());
                    AddLog(log);

                    RealTimeClock.SetDateTime(serverDt);
                }
                else
                {
                    DebugOnly.Print("RTC already synced with server time!");
                }

                // RTC time is now valid
                timeChecked = true;

                return true;
            }

            return false;
        }

        // Sends log to server
        private static bool sendLogs()
        {
            // Create JSON String
            var jsonString = Json.BuildNamedArray(LogHeader, logList);

            // Create URL
            string url = buildUrlFromSettings(DataRequest);

            // Send request
            DebugOnly.Print("Sending logs to server...");
            Remote.Result result = Remote.Post(url, jsonString);

            if (result.Success)
            {
                DebugOnly.Print("Logs sent to server");

                // Log list sent to server successfully: delete loglist
                logList.Clear();
                CacheManager.Store(logList, CacheManager.LogsCacheFile);
            }
            else
            {
                DebugOnly.Print("ERROR: Log sending failed!");
            }

            // Return result of the operation
            return result.Success;
        }
    }
}