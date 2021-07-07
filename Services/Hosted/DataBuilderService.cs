﻿using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using watchtower.Constants;
using watchtower.Hubs;
using watchtower.Models;
using watchtower.Models.Census;
using watchtower.Models.Db;
using watchtower.Models.Events;
using watchtower.Services.Db;
using watchtower.Services.Repositories;

namespace watchtower.Services {

    public class DataBuilderService : BackgroundService {

        private const int _RunDelay = 5;

        private readonly ILogger<DataBuilderService> _Logger;

        private readonly IHubContext<DataHub> _DataHub;

        private readonly IKillEventDbStore _KillEventDb;
        private readonly IExpEventDbStore _ExpEventDb;
        private readonly IWorldTotalDbStore _WorldTotalDb;

        private readonly ICharacterRepository _CharacterRepository;
        private readonly IOutfitRepository _OutfitRepository;

        private readonly IBackgroundCharacterCacheQueue _CharacterCacheQueue;

        private DateTime _LastUpdate = DateTime.UtcNow;

        public DataBuilderService(ILogger<DataBuilderService> logger,
            IHubContext<DataHub> hub, IBackgroundCharacterCacheQueue charQueue,
            IKillEventDbStore killDb, IExpEventDbStore expDb,
            ICharacterRepository charRepo, IOutfitRepository outfitRepo,
            IWorldTotalDbStore worldTotalDb) {

            _Logger = logger;

            _KillEventDb = killDb ?? throw new ArgumentNullException(nameof(killDb));
            _ExpEventDb = expDb ?? throw new ArgumentNullException(nameof(expDb));
            _WorldTotalDb = worldTotalDb ?? throw new ArgumentNullException(nameof(worldTotalDb));

            _CharacterRepository = charRepo ?? throw new ArgumentNullException(nameof(charRepo));
            _OutfitRepository = outfitRepo ?? throw new ArgumentNullException(nameof(outfitRepo));

            _CharacterCacheQueue = charQueue ?? throw new ArgumentNullException(nameof(charQueue));

            _DataHub = hub;
        }

        public override Task StartAsync(CancellationToken cancellationToken) {
            _LastUpdate = DateTime.UtcNow;
            return base.StartAsync(cancellationToken);
        }

        public override Task StopAsync(CancellationToken cancellationToken) {
            _Logger.LogError($"DataBuilder service stopped");
            return base.StopAsync(cancellationToken);
        }

        public async Task<List<KillData>> GetTopKillers(KillDbOptions options, Dictionary<string, TrackedPlayer> players) {
            List<KillData> data = new List<KillData>();

            List<KillDbEntry> topKillers = await _KillEventDb.GetTopKillers(options);

            foreach (KillDbEntry entry in topKillers) {
                PsCharacter? c = await _CharacterRepository.GetByID(entry.CharacterID);
                bool hasPlayer = players.TryGetValue(entry.CharacterID, out TrackedPlayer? p);

                if (hasPlayer == false && c != null) {
                    _Logger.LogWarning($"Missing {c?.Name}/{entry.CharacterID} in players passed, seconds online will be wrong");
                    _CharacterCacheQueue.Queue(entry.CharacterID);
                }

                KillData killDatum = new KillData() {
                    ID = entry.CharacterID,
                    Kills = entry.Kills,
                    Deaths = entry.Deaths,
                    Assists = 0,
                    Name = (c == null) ? $"Missing {entry.CharacterID}" : $"{(c.OutfitID != null ? $"[{c.OutfitTag}]" : $"[]")} {c.Name}",
                    Online = p?.Online ?? true,
                    SecondsOnline = (int)(p?.OnlineIntervals.Sum(iter => iter.End - iter.Start) ?? 1) / 1000
                };

                data.Add(killDatum);
            }

            return data;
        }

        public async Task<OutfitKillBlock> GetTopOutfitKillers(KillDbOptions options) {
            OutfitKillBlock block = new OutfitKillBlock();

            List<KillDbOutfitEntry> topOutfits = await _KillEventDb.GetTopOutfitKillers(options);
            foreach (KillDbOutfitEntry iter in topOutfits) {
                PsOutfit? outfit = await _OutfitRepository.GetByID(iter.OutfitID);

                TrackedOutfit tracked = new TrackedOutfit() {
                    ID = iter.OutfitID,
                    Kills = iter.Kills,
                    Deaths = iter.Deaths,
                    MembersOnline = iter.Members,
                    Members = iter.Members,
                    Name = outfit?.Name ?? $"Missing {iter.OutfitID}",
                    Tag = outfit?.Tag,
                };

                block.Entries.Add(tracked);
            }

            block.Entries = block.Entries.Where(iter => iter.Members > 4)
                .OrderByDescending(iter => iter.Kills / Math.Max(1, iter.MembersOnline))
                .Take(5).ToList();

            return block;
        }

        public async Task<List<BlockEntry>> GetExpBlock(ExpEntryOptions options) {
            List<BlockEntry> blockEntries = new List<BlockEntry>();

            List<ExpDbEntry> entries = await _ExpEventDb.GetEntries(options);
            foreach (ExpDbEntry entry in entries) {
                PsCharacter? c = await _CharacterRepository.GetByID(entry.ID);

                BlockEntry b = new BlockEntry() {
                    ID = entry.ID,
                    Name = (c == null) ? $"Missing {entry.ID}" : $"{(c.OutfitID != null ? $"[{c.OutfitTag}]" : $"[]")} {c.Name}",
                    Value = entry.Count
                };

                blockEntries.Add(b);
            }

            return blockEntries;
        }

        public async Task<List<BlockEntry>> GetOutfitExpBlock(ExpEntryOptions options) {
            List<BlockEntry> blockEntries = new List<BlockEntry>();

            List<ExpDbEntry> entries = await _ExpEventDb.GetTopOutfits(options);
            foreach (ExpDbEntry entry in entries) {
                PsOutfit? outfit = await _OutfitRepository.GetByID(entry.ID);

                BlockEntry b = new BlockEntry() {
                    ID = entry.ID,
                    Name = (entry.ID == "") ? "No outfit" : (outfit == null) ? $"Missing {entry.ID}" : $"[{outfit.Tag}] {outfit.Name}",
                    Value = entry.Count
                };

                blockEntries.Add(b);
            }

            return blockEntries.OrderByDescending(iter => iter.Value).Take(5).ToList();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            while (!stoppingToken.IsCancellationRequested) {
                try {
                    Stopwatch time = Stopwatch.StartNew();

                    long currentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                    WorldData data = new WorldData();

                    data.WorldID = 1;
                    data.WorldName = "Connery";
                    data.ContinentCount = new ContinentCount();

                    Dictionary<string, TrackedPlayer> players;
                    lock (CharacterStore.Get().Players) {
                        players = new Dictionary<string, TrackedPlayer>(CharacterStore.Get().Players);
                    }

                    long timeToCopyPlayers = time.ElapsedMilliseconds;

                    ExpEntryOptions expOptions = new ExpEntryOptions() {
                        Interval = 120,
                        WorldID = 1
                    };

                    expOptions.ExperienceIDs = new List<int>() { Experience.HEAL, Experience.SQUAD_HEAL };
                    expOptions.FactionID = Faction.VS;
                    data.VS.PlayerHeals.Entries = await GetExpBlock(expOptions);
                    data.VS.OutfitHeals.Entries = await GetOutfitExpBlock(expOptions);
                    expOptions.FactionID = Faction.NC;
                    data.NC.PlayerHeals.Entries = await GetExpBlock(expOptions);
                    data.NC.OutfitHeals.Entries = await GetOutfitExpBlock(expOptions);
                    expOptions.FactionID = Faction.TR;
                    data.TR.PlayerHeals.Entries = await GetExpBlock(expOptions);
                    data.TR.OutfitHeals.Entries = await GetOutfitExpBlock(expOptions);

                    long timeToGetHealEntries = time.ElapsedMilliseconds;

                    expOptions.ExperienceIDs = new List<int>() { Experience.REVIVE, Experience.SQUAD_REVIVE };
                    expOptions.FactionID = Faction.VS;
                    data.VS.PlayerRevives.Entries = await GetExpBlock(expOptions);
                    data.VS.OutfitRevives.Entries = await GetOutfitExpBlock(expOptions);
                    expOptions.FactionID = Faction.NC;
                    data.NC.PlayerRevives.Entries = await GetExpBlock(expOptions);
                    data.NC.OutfitRevives.Entries = await GetOutfitExpBlock(expOptions);
                    expOptions.FactionID = Faction.TR;
                    data.TR.PlayerRevives.Entries = await GetExpBlock(expOptions);
                    data.TR.OutfitRevives.Entries = await GetOutfitExpBlock(expOptions);

                    long timeToGetReviveEntries = time.ElapsedMilliseconds;

                    expOptions.ExperienceIDs = new List<int>() { Experience.RESUPPLY, Experience.SQUAD_RESUPPLY };
                    expOptions.FactionID = Faction.VS;
                    data.VS.PlayerResupplies.Entries = await GetExpBlock(expOptions);
                    data.VS.OutfitResupplies.Entries = await GetOutfitExpBlock(expOptions);
                    expOptions.FactionID = Faction.NC;
                    data.NC.PlayerResupplies.Entries = await GetExpBlock(expOptions);
                    data.NC.OutfitResupplies.Entries = await GetOutfitExpBlock(expOptions);
                    expOptions.FactionID = Faction.TR;
                    data.TR.PlayerResupplies.Entries = await GetExpBlock(expOptions);
                    data.TR.OutfitResupplies.Entries = await GetOutfitExpBlock(expOptions);

                    long timeToGetResupplyEntries = time.ElapsedMilliseconds;

                    expOptions.ExperienceIDs = new List<int>() {
                        Experience.SQUAD_SPAWN, Experience.GALAXY_SPAWN_BONUS, Experience.SUNDERER_SPAWN_BONUS,
                        Experience.SQUAD_VEHICLE_SPAWN_BONUS, Experience.GENERIC_NPC_SPAWN
                    };
                    expOptions.FactionID = Faction.VS;
                    data.VS.PlayerSpawns.Entries = await GetExpBlock(expOptions);
                    data.VS.OutfitSpawns.Entries = await GetOutfitExpBlock(expOptions);
                    expOptions.FactionID = Faction.NC;
                    data.NC.PlayerSpawns.Entries = await GetExpBlock(expOptions);
                    data.NC.OutfitSpawns.Entries = await GetOutfitExpBlock(expOptions);
                    expOptions.FactionID = Faction.TR;
                    data.TR.PlayerSpawns.Entries = await GetExpBlock(expOptions);
                    data.TR.OutfitSpawns.Entries = await GetOutfitExpBlock(expOptions);

                    long timeToGetSpawnEntries = time.ElapsedMilliseconds;

                    KillDbOptions killOptions = new KillDbOptions() {
                        Interval = 120,
                        WorldID = data.WorldID
                    };

                    killOptions.FactionID = Faction.VS;
                    data.VS.PlayerKills.Entries = await GetTopKillers(killOptions, players);
                    data.VS.OutfitKills = await GetTopOutfitKillers(killOptions);

                    long timeToGetVSKills = time.ElapsedMilliseconds;

                    killOptions.FactionID = Faction.NC;
                    data.NC.PlayerKills.Entries = await GetTopKillers(killOptions, players);
                    data.NC.OutfitKills = await GetTopOutfitKillers(killOptions);

                    long timeToGetNCKills = time.ElapsedMilliseconds;

                    killOptions.FactionID = Faction.TR;
                    data.TR.PlayerKills.Entries = await GetTopKillers(killOptions, players);
                    data.TR.OutfitKills = await GetTopOutfitKillers(killOptions);

                    long timeToGetTRKills = time.ElapsedMilliseconds;

                    data.TopSpawns = new SpawnEntries();

                    Dictionary<string, TrackedNpc> npcs;
                    lock (NpcStore.Get().Npcs) {
                        npcs = new Dictionary<string, TrackedNpc>(NpcStore.Get().Npcs);
                    }

                    long timeToCopyNpcStore = time.ElapsedMilliseconds;

                    data.TopSpawns.Entries = npcs.Values.OrderByDescending(iter => iter.SpawnCount).Take(8).Select(async iter => {
                        PsCharacter? c = await _CharacterRepository.GetByID(iter.OwnerID);

                        return new SpawnEntry() {
                            FirstSeenAt = iter.FirstSeenAt,
                            SecondsAlive = (int)(DateTime.UtcNow - iter.FirstSeenAt).TotalSeconds,
                            SpawnCount = iter.SpawnCount,
                            FactionID = c?.FactionID ?? Faction.UNKNOWN,
                            Owner = (c != null) ? $"{(c.OutfitTag != null ? $"[{c.OutfitTag}] " : "")}{c.Name}" : $"Missing {iter.OwnerID}"
                        };
                    }).Select(iter => iter.Result).ToList();

                    long timeToGetBiggestSpawns = time.ElapsedMilliseconds;

                    foreach (KeyValuePair<string, TrackedPlayer> entry in players) {
                        if (entry.Value.Online == true) {
                            ++data.OnlineCount;

                            // Add the current interval the character has been online for
                            entry.Value.OnlineIntervals.Add(new TimestampPair() {
                                Start = currentTime * 1000,
                                End = (currentTime + _RunDelay) * 1000 + time.ElapsedMilliseconds
                            });

                            if (entry.Value.FactionID == Faction.VS) {
                                data.ContinentCount.AddToVS(entry.Value.ZoneID);
                            } else if (entry.Value.FactionID == Faction.NC) {
                                data.ContinentCount.AddToNC(entry.Value.ZoneID);
                            } else if (entry.Value.FactionID == Faction.TR) {
                                data.ContinentCount.AddToTR(entry.Value.ZoneID);
                            } else if (entry.Value.FactionID == Faction.NS) {
                                data.ContinentCount.AddToNS(entry.Value.ZoneID);
                            }
                        }

                        long secondsOnline = 0;
                        foreach (TimestampPair pair in entry.Value.OnlineIntervals) {
                            secondsOnline += pair.End - pair.Start;
                        }
                    }

                    long timeToUpdateSecondsOnline = time.ElapsedMilliseconds;

                    WorldTotalOptions totalOptions = new WorldTotalOptions() {
                        Interval = 120,
                        WorldID = 1
                    };

                    WorldTotal worldTotal = await _WorldTotalDb.Get(totalOptions);

                    data.VS.TotalKills = worldTotal.GetValue(WorldTotal.TOTAL_VS_KILLS);
                    data.VS.TotalDeaths = worldTotal.GetValue(WorldTotal.TOTAL_VS_DEATHS);
                    data.VS.TotalAssists = worldTotal.GetValue(WorldTotal.TOTAL_VS_ASSISTS);
                    data.VS.PlayerHeals.Total = worldTotal.GetValue(WorldTotal.TOTAL_VS_HEALS);
                    data.VS.OutfitHeals.Total = worldTotal.GetValue(WorldTotal.TOTAL_VS_HEALS);
                    data.VS.PlayerRevives.Total = worldTotal.GetValue(WorldTotal.TOTAL_VS_REVIVES);
                    data.VS.OutfitRevives.Total = worldTotal.GetValue(WorldTotal.TOTAL_VS_REVIVES);
                    data.VS.PlayerResupplies.Total = worldTotal.GetValue(WorldTotal.TOTAL_VS_RESUPPLIES);
                    data.VS.OutfitResupplies.Total = worldTotal.GetValue(WorldTotal.TOTAL_VS_RESUPPLIES);
                    data.VS.PlayerSpawns.Total = worldTotal.GetValue(WorldTotal.TOTAL_VS_SPAWNS);
                    data.VS.OutfitSpawns.Total = worldTotal.GetValue(WorldTotal.TOTAL_VS_SPAWNS);

                    data.NC.TotalKills = worldTotal.GetValue(WorldTotal.TOTAL_NC_KILLS);
                    data.NC.TotalDeaths = worldTotal.GetValue(WorldTotal.TOTAL_NC_DEATHS);
                    data.NC.TotalAssists = worldTotal.GetValue(WorldTotal.TOTAL_NC_ASSISTS);
                    data.NC.PlayerHeals.Total = worldTotal.GetValue(WorldTotal.TOTAL_NC_HEALS);
                    data.NC.OutfitHeals.Total = worldTotal.GetValue(WorldTotal.TOTAL_NC_HEALS);
                    data.NC.PlayerRevives.Total = worldTotal.GetValue(WorldTotal.TOTAL_NC_REVIVES);
                    data.NC.OutfitRevives.Total = worldTotal.GetValue(WorldTotal.TOTAL_NC_REVIVES);
                    data.NC.PlayerResupplies.Total = worldTotal.GetValue(WorldTotal.TOTAL_NC_RESUPPLIES);
                    data.NC.OutfitResupplies.Total = worldTotal.GetValue(WorldTotal.TOTAL_NC_RESUPPLIES);
                    data.NC.PlayerSpawns.Total = worldTotal.GetValue(WorldTotal.TOTAL_NC_SPAWNS);
                    data.NC.OutfitSpawns.Total = worldTotal.GetValue(WorldTotal.TOTAL_NC_SPAWNS);

                    data.TR.TotalKills = worldTotal.GetValue(WorldTotal.TOTAL_TR_KILLS);
                    data.TR.TotalDeaths = worldTotal.GetValue(WorldTotal.TOTAL_TR_DEATHS);
                    data.TR.TotalAssists = worldTotal.GetValue(WorldTotal.TOTAL_TR_ASSISTS);
                    data.TR.PlayerHeals.Total = worldTotal.GetValue(WorldTotal.TOTAL_TR_HEALS);
                    data.TR.OutfitHeals.Total = worldTotal.GetValue(WorldTotal.TOTAL_TR_HEALS);
                    data.TR.PlayerRevives.Total = worldTotal.GetValue(WorldTotal.TOTAL_TR_REVIVES);
                    data.TR.OutfitRevives.Total = worldTotal.GetValue(WorldTotal.TOTAL_TR_REVIVES);
                    data.TR.PlayerResupplies.Total = worldTotal.GetValue(WorldTotal.TOTAL_TR_RESUPPLIES);
                    data.TR.OutfitResupplies.Total = worldTotal.GetValue(WorldTotal.TOTAL_TR_RESUPPLIES);
                    data.TR.PlayerSpawns.Total = worldTotal.GetValue(WorldTotal.TOTAL_TR_SPAWNS);
                    data.TR.OutfitSpawns.Total = worldTotal.GetValue(WorldTotal.TOTAL_TR_SPAWNS);

                    long timeToGetWorldTotals = time.ElapsedMilliseconds;

                    time.Stop();

                    _Logger.LogInformation(
                        $"{DateTime.UtcNow} took {time.ElapsedMilliseconds}ms to build world data\n"
                        + $"\ttime to copy players: {timeToCopyPlayers}ms\n"
                        + $"\ttime to get heal entries: {timeToGetHealEntries}ms\n"
                        + $"\ttime to get revive entries: {timeToGetReviveEntries}ms\n"
                        + $"\ttime to get resupply entries: {timeToGetResupplyEntries}ms\n"
                        + $"\ttime to get spawn entries: {timeToGetSpawnEntries}ms\n"
                        + $"\ttime to get vs kills: {timeToGetVSKills}ms\n"
                        + $"\ttime to get nc kills: {timeToGetNCKills}ms\n"
                        + $"\ttime to get tr kills: {timeToGetTRKills}ms\n"
                        + $"\ttime to copy npc store: {timeToCopyNpcStore}ms\n"
                        + $"\ttime to get biggest spawns: {timeToGetBiggestSpawns}ms\n"
                        + $"\ttime to update seconds online: {timeToUpdateSecondsOnline}ms\n"
                        + $"\ttime to get world totals: {timeToGetWorldTotals}ms\n"
                    );

                    string json = JsonConvert.SerializeObject(data, new JsonSerializerSettings() {
                        ContractResolver = new CamelCasePropertyNamesContractResolver()
                    });

                    _ = _DataHub.Clients.All.SendAsync("DataUpdate", json);

                    await Task.Delay(_RunDelay * 1000, stoppingToken);
                } catch (Exception) when (stoppingToken.IsCancellationRequested) {
                    _Logger.LogInformation($"Stopped data builder service");
                } catch (Exception ex) {
                    _Logger.LogError(ex, "Exception in DataBuilderService");
                }
            }
        }

    }
}
