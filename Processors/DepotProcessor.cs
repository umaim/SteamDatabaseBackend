/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class DepotProcessor
    {
        public class ManifestJob
        {
            public uint ChangeNumber;
            public uint ParentAppID;
            public uint DepotID;
            public ulong ManifestID;
            public ulong PreviousManifestID;
            public string DepotName;
            public string CDNToken;
            public string Server;
            public byte[] DepotKey;
            public int Tries;
        }

        private readonly CDNClient CDNClient;
        private readonly List<string> CDNServers;
        private readonly ConcurrentDictionary<uint, byte> DepotLocks;

        public DepotProcessor(SteamClient client, CallbackManager manager)
        {
            DepotLocks = new ConcurrentDictionary<uint, byte>();

            CDNClient = new CDNClient(client);

            FileDownloader.SetCDNClient(CDNClient);

            CDNServers = new List<string>
            {
                //"content1.steampowered.com", // Used to be Limelight
                "content2.steampowered.com", // Level3
                "content3.steampowered.com", // Highwinds
                //"content4.steampowered.com", // EdgeCast, seems to be missing content
                "content5.steampowered.com", // CloudFront
                //"content6.steampowered.com", // Comcast, non optimal
                //"content7.steampowered.com", //
                "content8.steampowered.com" // Akamai
            };

            manager.Register(new Callback<SteamApps.CDNAuthTokenCallback>(OnCDNAuthTokenCallback));
            manager.Register(new Callback<SteamApps.DepotKeyCallback>(OnDepotKeyCallback));
        }

        public void Process(uint appID, uint changeNumber, KeyValue depots)
        {
            var buildID = depots["branches"]["public"]["buildid"].AsInteger();

            foreach (KeyValue depot in depots.Children)
            {
                // Ignore these for now, parent app should be updated too anyway
                if (depot["depotfromapp"].Value != null)
                {
                    ////Log.WriteDebug("Depot Processor", "Ignoring depot {0} with depotfromapp value {1} (parent {2})", depot.Name, depot["depotfromapp"].AsString(), AppID);

                    continue;
                }

                uint depotID;

                if (!uint.TryParse(depot.Name, out depotID))
                {
                    // Ignore keys that aren't integers, for example "branches"
                    continue;
                }

                // TODO: instead of locking we could wait for current process to finish
                if (DepotLocks.ContainsKey(depotID))
                {
                    continue;
                }
                    
                var depotName = depot["name"].AsString();

                var request = new ManifestJob
                {
                    ChangeNumber = changeNumber,
                    ParentAppID  = appID,
                    DepotID      = depotID,
                    DepotName    = depotName
                };

                using (var db = Database.GetConnection())
                {
                    if (depot["manifests"]["public"].Value == null || !ulong.TryParse(depot["manifests"]["public"].Value, out request.ManifestID))
                    {
                        // If there is no public manifest for this depot, it still could have some sort of open beta

                        var branch = depot["manifests"].Children.FirstOrDefault(x => x.Name != "local");

                        if (branch == null || !ulong.TryParse(branch.Value, out request.ManifestID))
                        {
                            db.Execute("INSERT INTO `Depots` (`DepotID`, `Name`) VALUES (@DepotID, @Name) ON DUPLICATE KEY UPDATE `LastUpdated` = CURRENT_TIMESTAMP(), `Name` = @Name", new { DepotID = depotID, Name = depotName });

                            continue;
                        }

                        Log.WriteInfo("Depot Processor", "Failed to find public branch for depot {0} (parent {1}) - but found another branch: {2} (manifest: {3})", depotID, appID, branch.Name, branch.AsString());
                    }

                    var dbDepot = db.Query<Depot>("SELECT `DepotID`, `Name`, `ManifestID`, `BuildID` FROM `Depots` WHERE `DepotID` = @DepotID LIMIT 1", new { DepotID = depotID }).SingleOrDefault();

                    if (dbDepot.DepotID > 0)
                    {
                        request.PreviousManifestID = dbDepot.ManifestID;

                        if (dbDepot.ManifestID == request.ManifestID)
                        {
                            if (Settings.Current.FullRun < 2)
                            {
                                // Update depot name if changed
                                if (!depotName.Equals(request.DepotName))
                                {
                                    db.Execute("UPDATE `Depots` SET `Name` = @DepotName WHERE `DepotID` = @DepotID", request);
                                }

                                continue;
                            }
                        }
                        else if (dbDepot.BuildID > buildID)
                        {
                            Log.WriteDebug("Depot Processor", "Skipping depot {0} due to old buildid: {1} > {2}", depotID, dbDepot.BuildID, buildID);
                            continue;
                        }
                    }

                    DepotLocks.TryAdd(depotID, 1);

                    // Update/insert depot information straight away
                    if (dbDepot.BuildID != buildID || request.PreviousManifestID != request.ManifestID || !depotName.Equals(depot.Name))
                    {
                        db.Execute("INSERT INTO `Depots` (`DepotID`, `Name`, `BuildID`, `ManifestID`) VALUES (@DepotID, @Name, @BuildID, @ManifestID) ON DUPLICATE KEY UPDATE `LastUpdated` = CURRENT_TIMESTAMP(), `Name` = @Name, `BuildID` = @BuildID, `ManifestID` = @ManifestID",
                            new {
                                DepotID = request.DepotID,
                                BuildID = buildID,
                                ManifestID = request.ManifestID,
                                Name = depotName
                            }
                        );

                        if (request.PreviousManifestID != request.ManifestID)
                        {
                            MakeHistory(db, request, string.Empty, "manifest_change", request.PreviousManifestID, request.ManifestID);
                        }
                    }
                }

                JobManager.AddJob(() => Steam.Instance.Apps.GetDepotDecryptionKey(request.DepotID, request.ParentAppID), request);
            }
        }

        private void OnDepotKeyCallback(SteamApps.DepotKeyCallback callback)
        {
            JobAction job;

            if (!JobManager.TryRemoveJob(callback.JobID, out job))
            {
                return;
            }

            var request = job.ManifestJob;

            if (callback.Result != EResult.OK)
            {
                if (callback.Result != EResult.AccessDenied || FileDownloader.IsImportantDepot(request.DepotID))
                {
                    Log.WriteError("Depot Processor", "Failed to get depot key for depot {0} (parent {1}) - {2}", callback.DepotID, request.ParentAppID, callback.Result);
                }

                RemoveLock(request.DepotID);

                return;
            }

            request.DepotKey = callback.DepotKey;
            request.Tries = CDNServers.Count;
            request.Server = GetContentServer();

            JobManager.AddJob(() => Steam.Instance.Apps.GetCDNAuthToken(request.DepotID, request.Server), request);
        }

        private void OnCDNAuthTokenCallback(SteamApps.CDNAuthTokenCallback callback)
        {
            JobAction job;

            if (!JobManager.TryRemoveJob(callback.JobID, out job))
            {
                return;
            }

            var request = job.ManifestJob;

            if (callback.Result != EResult.OK)
            {
                if (FileDownloader.IsImportantDepot(request.DepotID))
                {
                    Log.WriteError("Depot Processor", "Failed to get CDN auth token for depot {0} (parent {1} - server {2}) - {3} (#{4})",
                        request.DepotID, request.ParentAppID, request.Server, callback.Result, request.Tries);
                }

                if (--request.Tries > 0)
                {
                    request.Server = GetContentServer(request.Tries);

                    JobManager.AddJob(() => Steam.Instance.Apps.GetCDNAuthToken(request.DepotID, request.Server), request);

                    return;
                }

                RemoveLock(request.DepotID);

                return;
            }

            request.CDNToken = callback.Token;

            // TODO: Using tasks makes every manifest download timeout
            // TODO: which seems to be bug with mono's threadpool implementation
            /*TaskManager.Run(() => DownloadManifest(request)).ContinueWith(task =>
            {
                RemoveLock(request.DepotID);

                Log.WriteDebug("Depot Processor", "Processed depot {0} ({1} depot locks left)", request.DepotID, DepotLocks.Count);
            });*/

            try
            {
                DownloadManifest(request);
            }
            catch (Exception)
            {
                RemoveLock(request.DepotID);
            }
        }

        private void DownloadManifest(ManifestJob request)
        {
            Log.WriteInfo("Depot Processor", "DepotID: {0}", request.DepotID);

            DepotManifest depotManifest = null;
            string lastError = string.Empty;

            // CDN is very random, just keep trying
            for (var i = 0; i <= 5; i++)
            {
                try
                {
                    depotManifest = CDNClient.DownloadManifest(request.DepotID, request.ManifestID, request.Server, request.CDNToken, request.DepotKey);

                    break;
                }
                catch (Exception e)
                {
                    lastError = e.Message;
                }
            }

            if (depotManifest == null)
            {
                Log.WriteError("Depot Processor", "Failed to download depot manifest for depot {0} ({1}: {2})", request.DepotID, request.Server, lastError);

                return;
            }

            if (FileDownloader.IsImportantDepot(request.DepotID))
            {
                IRC.Instance.AnnounceImportantAppUpdate(request.ParentAppID, "Depot update: {0}{1}{2} -{3} {4}", Colors.BLUE, request.DepotName, Colors.NORMAL, Colors.DARKBLUE, SteamDB.GetDepotURL(request.DepotID, "history"));

                TaskManager.Run(() => FileDownloader.DownloadFilesFromDepot(request, depotManifest));
            }

            // TODO: Task here instead of in OnCDNAuthTokenCallback due to mono's silly threadpool
            TaskManager.Run(() =>
            {
                using(var db = Database.GetConnection())
                {
                    ProcessDepotAfterDownload(db, request, depotManifest);
                }
            }).ContinueWith(task =>
            {
                RemoveLock(request.DepotID);

                Log.WriteDebug("Depot Processor", "Processed depot {0} ({1} depot locks left)", request.DepotID, DepotLocks.Count);
            });
        }

        private static void ProcessDepotAfterDownload(IDbConnection db, ManifestJob request, DepotManifest depotManifest)
        {
            var filesOld = db.Query<DepotFile>("SELECT `ID`, `File`, `Hash`, `Size`, `Flags` FROM `DepotsFiles` WHERE `DepotID` = @DepotID", new { request.DepotID }).ToDictionary(x => x.File, x => x);
            var filesNew = new List<DepotFile>();
            var filesAdded = new List<DepotFile>();
            var shouldHistorize = filesOld.Any(); // Don't historize file additions if we didn't have any data before

            foreach (var file in depotManifest.Files)
            {
                var depotFile = new DepotFile
                {
                    DepotID = request.DepotID,
                    File    = file.FileName.Replace('\\', '/'),
                    Size    = file.TotalSize,
                    Flags   = file.Flags
                };

                if (file.FileHash.Length > 0 && !file.Flags.HasFlag(EDepotFileFlag.Directory))
                {
                    depotFile.Hash = string.Concat(Array.ConvertAll(file.FileHash, x => x.ToString("X2")));
                }
                else
                {
                    depotFile.Hash = "0000000000000000000000000000000000000000";
                }

                filesNew.Add(depotFile);
            }

            foreach (var file in filesNew)
            {
                if (filesOld.ContainsKey(file.File))
                {
                    var oldFile = filesOld[file.File];
                    var updateFile = false;

                    if (oldFile.Size != file.Size || !file.Hash.Equals(oldFile.Hash))
                    {
                        MakeHistory(db, request, file.File, "modified", oldFile.Size, file.Size);

                        updateFile = true;
                    }

                    if (oldFile.Flags != file.Flags)
                    {
                        MakeHistory(db, request, file.File, "modified_flags", (ulong)oldFile.Flags, (ulong)file.Flags);

                        updateFile = true;
                    }

                    if (updateFile)
                    {
                        db.Execute("UPDATE `DepotsFiles` SET `Hash` = @Hash, `Size` = @Size, `Flags` = @Flags WHERE `DepotID` = @DepotID AND `ID` = @ID", file);
                    }

                    filesOld.Remove(file.File);
                }
                else
                {
                    // We want to historize modifications first, and only then deletions and additions
                    filesAdded.Add(file);
                }
            }

            if (filesOld.Any())
            {
                db.Execute("DELETE FROM `DepotsFiles` WHERE `DepotID` = @DepotID AND `ID` IN @Files", new { request.DepotID, Files = filesOld.Select(x => x.Value.ID) });

                db.Execute(GetHistoryQuery(), filesOld.Select(x => new DepotHistory
                {
                    DepotID  = request.DepotID,
                    ChangeID = request.ChangeNumber,
                    Action   = "removed",
                    File     = x.Value.File
                }));
            }

            if (filesAdded.Any())
            {
                db.Execute("INSERT INTO `DepotsFiles` (`DepotID`, `File`, `Hash`, `Size`, `Flags`) VALUES (@DepotID, @File, @Hash, @Size, @Flags)", filesAdded);

                if (shouldHistorize)
                {
                    db.Execute(GetHistoryQuery(), filesAdded.Select(x => new DepotHistory
                    {
                        DepotID  = request.DepotID,
                        ChangeID = request.ChangeNumber,
                        Action   = "added",
                        File     = x.File
                    }));
                }
            }
        }

        public static string GetHistoryQuery()
        {
            return "INSERT INTO `DepotsHistory` (`ChangeID`, `DepotID`, `File`, `Action`, `OldValue`, `NewValue`) VALUES (@ChangeID, @DepotID, @File, @Action, @OldValue, @NewValue)";
        }

        private static void MakeHistory(IDbConnection db, ManifestJob request, string file, string action, ulong oldValue = 0, ulong newValue = 0)
        {
            db.Execute(GetHistoryQuery(),
                new DepotHistory
                {
                    DepotID  = request.DepotID,
                    ChangeID = request.ChangeNumber,
                    Action   = action,
                    File     = file,
                    OldValue = oldValue,
                    NewValue = newValue
                }
            );
        }

        private void RemoveLock(uint depotID)
        {
            byte microsoftWhyIsThereNoRemoveMethodWithoutSecondParam;

            DepotLocks.TryRemove(depotID, out microsoftWhyIsThereNoRemoveMethodWithoutSecondParam);
        }

        private string GetContentServer()
        {
            var i = new Random().Next(CDNServers.Count);

            return CDNServers[i];
        }

        private string GetContentServer(int i)
        {
            i %= CDNServers.Count;

            return CDNServers[i];
        }
    }
}
