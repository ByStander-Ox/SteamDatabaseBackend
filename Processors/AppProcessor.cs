﻿/*
 * Copyright (c) 2013-present, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using SteamKit2;

namespace SteamDatabaseBackend
{
    internal class AppProcessor : BaseProcessor
    {
        public const string HistoryQuery = "INSERT INTO `AppsHistory` (`ChangeID`, `AppID`, `Action`, `Key`, `OldValue`, `NewValue`, `Diff`) VALUES (@ChangeID, @ID, @Action, @Key, @OldValue, @NewValue, @Diff)";

        private static readonly string[] Triggers =
        {
            "Valve",
            "Steam",
            "Half-Life",
            "Left 4 Dead",
            "Counter-Strike",
            "Dota",
            "Day of Defeat",
            "Team Fortress"
        };

        private Dictionary<string, PICSInfo> CurrentData;
        private uint ChangeNumber;
        private readonly uint AppID;

        public AppProcessor(uint appID, SteamApps.PICSProductInfoCallback.PICSProductInfo productInfo)
        {
            Id = appID;
            AppID = appID;
            ProductInfo = productInfo;
        }

        protected override AsyncJob RefreshSteam()
        {
            return Steam.Instance.Apps.PICSGetAccessTokens(AppID, null);
        }

        protected override async Task LoadData()
        {
            CurrentData = (await DbConnection.QueryAsync<PICSInfo>("SELECT `Name` as `KeyName`, `Value`, `Key` FROM `AppsInfo` INNER JOIN `KeyNames` ON `AppsInfo`.`Key` = `KeyNames`.`ID` WHERE `AppID` = @AppID", new { AppID })).ToDictionary(x => x.KeyName, x => x);
        }

        protected override async Task ProcessData()
        {
            ChangeNumber = ProductInfo.ChangeNumber;

            if (Settings.IsFullRun)
            {
                await DbConnection.ExecuteAsync("INSERT INTO `Changelists` (`ChangeID`) VALUES (@ChangeNumber) ON DUPLICATE KEY UPDATE `Date` = `Date`", new { ProductInfo.ChangeNumber });
                await DbConnection.ExecuteAsync("INSERT INTO `ChangelistsApps` (`ChangeID`, `AppID`) VALUES (@ChangeNumber, @AppID) ON DUPLICATE KEY UPDATE `AppID` = `AppID`", new { AppID, ProductInfo.ChangeNumber });
            }

            await ProcessKey("root_changenumber", "changenumber", ChangeNumber.ToString());

            var app = (await DbConnection.QueryAsync<App>("SELECT `Name`, `AppType` FROM `Apps` WHERE `AppID` = @AppID LIMIT 1", new { AppID })).SingleOrDefault();

            var newAppName = ProductInfo.KeyValues["common"]["name"].AsString();

            if (newAppName != null)
            {
                var currentType = ProductInfo.KeyValues["common"]["type"].AsString().ToLowerInvariant();

                var newAppType = await DbConnection.ExecuteScalarAsync<int?>("SELECT `AppType` FROM `AppsTypes` WHERE `Name` = @Type LIMIT 1", new { Type = currentType }) ?? -1;
                var modifiedNameOrType = false;

                if (newAppType == -1)
                {
                    await DbConnection.ExecuteAsync("INSERT INTO `AppsTypes` (`Name`, `DisplayName`) VALUES(@Name, @DisplayName)",
                        new { Name = currentType, DisplayName = ProductInfo.KeyValues["common"]["type"].AsString() }); // We don't need to lower display name

                    Log.WriteInfo("App Processor", "Creating new apptype \"{0}\" (AppID {1})", currentType, AppID);

                    IRC.Instance.SendOps("New app type: {0}{1}{2} - {3}", Colors.BLUE, currentType, Colors.NORMAL, SteamDB.GetAppURL(AppID, "history"));

                    newAppType = await DbConnection.ExecuteScalarAsync<int>("SELECT `AppType` FROM `AppsTypes` WHERE `Name` = @Type LIMIT 1", new { Type = currentType });
                }

                if (string.IsNullOrEmpty(app.Name) || app.Name.StartsWith(SteamDB.UNKNOWN_APP, StringComparison.Ordinal))
                {
                    await DbConnection.ExecuteAsync("INSERT INTO `Apps` (`AppID`, `AppType`, `Name`, `LastKnownName`) VALUES (@AppID, @Type, @AppName, @AppName) ON DUPLICATE KEY UPDATE `Name` = VALUES(`Name`), `LastKnownName` = VALUES(`LastKnownName`), `AppType` = VALUES(`AppType`)",
                        new { AppID, Type = newAppType, AppName = newAppName }
                    );

                    await MakeHistory("created_app");
                    await MakeHistory("created_info", SteamDB.DATABASE_NAME_TYPE, string.Empty, newAppName);

                    modifiedNameOrType = true;
                }
                else if (app.Name != newAppName)
                {
                    await DbConnection.ExecuteAsync("UPDATE `Apps` SET `Name` = @AppName, `LastKnownName` = @AppName WHERE `AppID` = @AppID", new { AppID, AppName = newAppName });
                    await MakeHistory("modified_info", SteamDB.DATABASE_NAME_TYPE, app.Name, newAppName);

                    modifiedNameOrType = true;
                }

                if (app.AppType == 0 || app.AppType != newAppType)
                {
                    await DbConnection.ExecuteAsync("UPDATE `Apps` SET `AppType` = @Type WHERE `AppID` = @AppID", new { AppID, Type = newAppType });

                    if (app.AppType == 0)
                    {
                        await MakeHistory("created_info", SteamDB.DATABASE_APPTYPE, string.Empty, newAppType.ToString());
                    }
                    else
                    {
                        await MakeHistory("modified_info", SteamDB.DATABASE_APPTYPE, app.AppType.ToString(), newAppType.ToString());
                    }

                    modifiedNameOrType = true;
                }

                if (modifiedNameOrType)
                {
                    if ((newAppType > 9 && newAppType != 13 && newAppType != 15 && newAppType != 17) || Triggers.Any(newAppName.Contains))
                    {
                        IRC.Instance.SendOps("New {0}: {1}{2}{3} -{4} {5}",
                            currentType,
                            Colors.BLUE, Utils.LimitStringLength(newAppName), Colors.NORMAL,
                            Colors.DARKBLUE, SteamDB.GetAppURL(AppID, "history"));
                    }
                }
            }

            foreach (var section in ProductInfo.KeyValues.Children)
            {
                var sectionName = section.Name.ToLowerInvariant();

                if (sectionName == "appid" || sectionName == "public_only" || sectionName == "change_number")
                {
                    continue;
                }

                if (sectionName == "common" || sectionName == "extended")
                {
                    foreach (var keyvalue in section.Children)
                    {
                        var keyName = string.Format("{0}_{1}", sectionName, keyvalue.Name);

                        if (keyName == "common_type" || keyName == "common_gameid" || keyName == "common_name" || keyName == "extended_order")
                        {
                            // Ignore common keys that are either duplicated or serve no real purpose
                            continue;
                        }

                        if (keyvalue.Children.Count > 0)
                        {
                            await ProcessKey(keyName, keyvalue.Name, Utils.JsonifyKeyValue(keyvalue), keyvalue);
                        }
                        else if (!string.IsNullOrEmpty(keyvalue.Value))
                        {
                            await ProcessKey(keyName, keyvalue.Name, keyvalue.Value);
                        }
                    }
                }
                else
                {
                    sectionName = string.Format("root_{0}", sectionName);

                    if (await ProcessKey(sectionName, sectionName, Utils.JsonifyKeyValue(section), section) && sectionName == "root_depots")
                    {
                        await DbConnection.ExecuteAsync("UPDATE `Apps` SET `LastDepotUpdate` = CURRENT_TIMESTAMP() WHERE `AppID` = @AppID", new { AppID });
                    }
                }
            }

            // If app gets hidden but we already have data, do not delete the already existing app info
            if (newAppName != null)
            {
                foreach (var data in CurrentData.Values.Where(data => !data.Processed && !data.KeyName.StartsWith("website", StringComparison.Ordinal)))
                {
                    await DbConnection.ExecuteAsync("DELETE FROM `AppsInfo` WHERE `AppID` = @AppID AND `Key` = @Key", new { AppID, data.Key });
                    await MakeHistory("removed_key", data.Key, data.Value);
                }
            }
            else
            {
                if (string.IsNullOrEmpty(app.Name)) // We don't have the app in our database yet
                {
                    await DbConnection.ExecuteAsync("INSERT INTO `Apps` (`AppID`, `Name`) VALUES (@AppID, @AppName) ON DUPLICATE KEY UPDATE `AppType` = `AppType`", new { AppID, AppName = string.Format("{0} {1}", SteamDB.UNKNOWN_APP, AppID) });
                }
                else if (!app.Name.StartsWith(SteamDB.UNKNOWN_APP, StringComparison.Ordinal)) // We do have the app, replace it with default name
                {
                    await DbConnection.ExecuteAsync("UPDATE `Apps` SET `Name` = @AppName, `AppType` = 0 WHERE `AppID` = @AppID", new { AppID, AppName = string.Format("{0} {1}", SteamDB.UNKNOWN_APP, AppID) });
                    await MakeHistory("deleted_app", 0, app.Name);
                }
            }

            // Close the connection as it's no longer needed going into depot processor
            DbConnection.Close();

            if (ProductInfo.KeyValues["depots"] != null)
            {
                await Steam.Instance.DepotProcessor.Process(AppID, ChangeNumber, ProductInfo.KeyValues["depots"]);
            }

            if (ProductInfo.MissingToken && PICSTokens.HasAppToken(AppID))
            {
                Log.WriteError(nameof(PICSTokens), $"Overriden token for appid {AppID} is invalid?");
                IRC.Instance.SendOps($"[Tokens] Looks like the overriden token for appid {AppID} ({newAppName}) is invalid");
            }
        }

        protected override async Task ProcessUnknown()
        {
            var name = await DbConnection.ExecuteScalarAsync<string>("SELECT `Name` FROM `Apps` WHERE `AppID` = @AppID LIMIT 1", new { AppID });

            var data = CurrentData.Values.Where(x => !x.KeyName.StartsWith("website", StringComparison.Ordinal)).ToList();

            if (data.Count > 0)
            {
                await DbConnection.ExecuteAsync(HistoryQuery, data.Select(x => new PICSHistory
                {
                    ID = AppID,
                    ChangeID = ChangeNumber,
                    Key = x.Key,
                    OldValue = x.Value,
                    Action = "removed_key"
                }));
            }

            if (!string.IsNullOrEmpty(name) && !name.StartsWith(SteamDB.UNKNOWN_APP, StringComparison.Ordinal))
            {
                await DbConnection.ExecuteAsync(HistoryQuery, new PICSHistory
                {
                    ID = AppID,
                    ChangeID = ChangeNumber,
                    OldValue = name,
                    Action = "deleted_app"
                });
            }

            await DbConnection.ExecuteAsync("DELETE FROM `Apps` WHERE `AppID` = @AppID", new { AppID });
            await DbConnection.ExecuteAsync("DELETE FROM `AppsInfo` WHERE `AppID` = @AppID", new { AppID });

            if (Settings.Current.CanQueryStore)
            {
                await DbConnection.ExecuteAsync("DELETE FROM `Store` WHERE `AppID` = @AppID", new { AppID });
            }
        }

        private async Task<bool> ProcessKey(string keyName, string displayName, string value, KeyValue newKv = null)
        {
            if (keyName.Length > 90)
            {
                Log.WriteError("App Processor", "Key {0} for AppID {1} is too long, not inserting info.", keyName, AppID);

                return false;
            }

            // All keys in PICS are supposed to be lower case
            keyName = keyName.ToLowerInvariant().Trim();

            if (!CurrentData.ContainsKey(keyName))
            {
                var key = KeyNameCache.GetAppKeyID(keyName);

                if (key == 0)
                {
                    var type = newKv != null ? 86 : 0; // 86 is a hardcoded const for the website

                    key = await KeyNameCache.CreateAppKey(keyName, displayName, type);

                    if (key == 0)
                    {
                        // We can't insert anything because key wasn't created
                        Log.WriteError("App Processor", "Failed to create key {0} for AppID {1}, not inserting info.", keyName, AppID);

                        return false;
                    }

                    IRC.Instance.SendOps("New app keyname: {0}{1} {2}(ID: {3}) ({4}) - {5}", Colors.BLUE, keyName, Colors.LIGHTGRAY, key, displayName, SteamDB.GetAppURL(AppID, "history"));
                }

                await DbConnection.ExecuteAsync("INSERT INTO `AppsInfo` (`AppID`, `Key`, `Value`) VALUES (@AppID, @Key, @Value)", new { AppID, Key = key, Value = value });
                await MakeHistory("created_key", key, string.Empty, value);

                if ((keyName == "extended_developer" || keyName == "extended_publisher") && value == "Valve")
                {
                    IRC.Instance.SendOps("New {0}=Valve app: {1}{2}{3} -{4} {5}",
                        displayName,
                        Colors.BLUE, Steam.GetAppName(AppID), Colors.NORMAL,
                        Colors.DARKBLUE, SteamDB.GetAppURL(AppID, "history"));
                }

                if (keyName == "common_oslist" && value.Contains("linux"))
                {
                    PrintLinux();
                }

                return true;
            }

            var data = CurrentData[keyName];

            if (data.Processed)
            {
                Log.WriteWarn("App Processor", "Duplicate key {0} in AppID {1}", keyName, AppID);

                return false;
            }

            data.Processed = true;

            CurrentData[keyName] = data;

            if (data.Value == value)
            {
                return false;
            }

            await DbConnection.ExecuteAsync("UPDATE `AppsInfo` SET `Value` = @Value WHERE `AppID` = @AppID AND `Key` = @Key", new { AppID, data.Key, Value = value });

            if (newKv != null)
            {
                await MakeHistoryForJson(data.Key, data.Value, newKv);
            }
            else
            {
                await MakeHistory("modified_key", data.Key, data.Value, value);
            }

            if (keyName == "common_oslist" && value.Contains("linux") && !data.Value.Contains("linux"))
            {
                PrintLinux();
            }

            return true;
        }

        private Task MakeHistoryForJson(uint keyNameId, string oldValue, KeyValue newKv)
        {
            var diff = JsonConvert.SerializeObject(DiffKeyValues.Diff(oldValue, newKv));

            return DbConnection.ExecuteAsync(HistoryQuery,
                new PICSHistory
                {
                    ID = AppID,
                    ChangeID = ChangeNumber,
                    Key = keyNameId,
                    Diff = diff,
                    Action = "modified_key"
                }
            );
        }

        private Task MakeHistory(string action, uint keyNameID = 0, string oldValue = "", string newValue = "")
        {
            return DbConnection.ExecuteAsync(HistoryQuery,
                new PICSHistory
                {
                    ID = AppID,
                    ChangeID = ChangeNumber,
                    Key = keyNameID,
                    OldValue = oldValue,
                    NewValue = newValue,
                    Action = action
                }
            );
        }

        private void PrintLinux()
        {
            var name = Steam.GetAppName(AppID, out var appType);

            if (appType != "Game" && appType != "Application")
            {
                return;
            }

            if (!Settings.Current.CanQueryStore)
            {
                return;
            }

            IRC.Instance.SendLinuxAnnouncement($"\U0001F427 {name} now lists Linux{LinkExpander.GetFormattedPrices(AppID)} - {SteamDB.GetAppURL(AppID, "history")} - https://store.steampowered.com/app/{AppID}/");
        }

        public override string ToString()
        {
            return $"{(ProductInfo == null ? "Unknown " : "")}App {AppID}";
        }
    }
}
