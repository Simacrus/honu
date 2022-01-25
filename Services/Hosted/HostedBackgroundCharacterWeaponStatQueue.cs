﻿using DaybreakGames.Census.Exceptions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using watchtower.Models.Census;
using watchtower.Models.CharacterViewer.WeaponStats;
using watchtower.Models.Db;
using watchtower.Models.Queues;
using watchtower.Services.Census;
using watchtower.Services.Db;
using watchtower.Services.Queues;
using watchtower.Services.Repositories;

namespace watchtower.Services.Hosted {

    public class HostedBackgroundCharacterWeaponStatQueue : BackgroundService {

        private const string SERVICE_NAME = "background_character_cache";

        private readonly ILogger<HostedBackgroundCharacterWeaponStatQueue> _Logger;
        private readonly BackgroundCharacterWeaponStatQueue _Queue;

        private readonly CharacterMetadataDbStore _MetadataDb;

        private readonly CharacterCollection _CharacterCensus;
        private readonly CharacterDbStore _CharacterDb;
        private readonly CharacterWeaponStatCollection _WeaponCensus;
        private readonly ICharacterWeaponStatDbStore _WeaponStatDb;
        private readonly CharacterHistoryStatCollection _HistoryCensus;
        private readonly ICharacterHistoryStatDbStore _HistoryDb;
        private readonly CharacterItemCollection _ItemCensus;
        private readonly ICharacterItemDbStore _ItemDb;
        private readonly CharacterStatCollection _StatCensus;
        private readonly ICharacterStatDbStore _StatDb;
        private readonly CharacterFriendCollection _FriendCensus;
        private readonly CharacterFriendDbStore _FriendDb;
        private readonly CharacterDirectiveCollection _CharacterDirectiveCensus;
        private readonly CharacterDirectiveDbStore _CharacterDirectiveDb;
        private readonly CharacterDirectiveTreeCollection _CharacterDirectiveTreeCensus;
        private readonly CharacterDirectiveTreeDbStore _CharacterDirectiveTreeDb;
        private readonly CharacterDirectiveTierCollection _CharacterDirectiveTierCensus;
        private readonly CharacterDirectiveTierDbStore _CharacterDirectiveTierDb;
        private readonly CharacterDirectiveObjectiveCollection _CharacterDirectiveObjectiveCensus;
        private readonly CharacterDirectiveObjectiveDbStore _CharacterDirectiveObjectiveDb;

        private static int _Count = 0;

        private List<string> _Peepers = new List<string>() {
            "5429119940672421393", "5428345446430485649"
        };

        public HostedBackgroundCharacterWeaponStatQueue(ILogger<HostedBackgroundCharacterWeaponStatQueue> logger,
            BackgroundCharacterWeaponStatQueue queue,
            ICharacterWeaponStatDbStore db, CharacterWeaponStatCollection weaponColl,
            ICharacterHistoryStatDbStore hDb, CharacterHistoryStatCollection hColl,
            CharacterItemCollection itemCensus, ICharacterItemDbStore itemDb,
            CharacterStatCollection statCensus, ICharacterStatDbStore statDb,
            CharacterMetadataDbStore metadataDb, CharacterCollection charColl,
            CharacterDbStore charDb, CharacterFriendCollection friendCensus,
            CharacterDirectiveCollection charDirCensus, CharacterDirectiveDbStore charDirDb,
            CharacterDirectiveTreeCollection charDirTreeCensus, CharacterDirectiveTreeDbStore charDirTreeDb,
            CharacterDirectiveTierCollection charDirTierCensus, CharacterDirectiveTierDbStore charDirTierDb,
            CharacterDirectiveObjectiveCollection charDirObjectiveCensus, CharacterDirectiveObjectiveDbStore charDirObjectiveDb,
            CharacterFriendDbStore friendDb) {

            _Logger = logger;
            _Queue = queue ?? throw new ArgumentNullException(nameof(queue));

            _MetadataDb = metadataDb ?? throw new ArgumentNullException(nameof(metadataDb));

            _CharacterCensus = charColl;
            _CharacterDb = charDb;
            _WeaponStatDb = db ?? throw new ArgumentNullException(nameof(db));
            _WeaponCensus = weaponColl ?? throw new ArgumentNullException(nameof(weaponColl));
            _HistoryCensus = hColl;
            _HistoryDb = hDb;
            _ItemCensus = itemCensus;
            _ItemDb = itemDb;
            _StatCensus = statCensus;
            _StatDb = statDb;
            _FriendCensus = friendCensus;
            _FriendDb = friendDb;
            _CharacterDirectiveCensus = charDirCensus;
            _CharacterDirectiveDb = charDirDb;
            _CharacterDirectiveTreeCensus = charDirTreeCensus;
            _CharacterDirectiveTreeDb = charDirTreeDb;
            _CharacterDirectiveTierCensus = charDirTierCensus;
            _CharacterDirectiveTierDb = charDirTierDb;
            _CharacterDirectiveObjectiveCensus = charDirObjectiveCensus;
            _CharacterDirectiveObjectiveDb = charDirObjectiveDb;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            int errorCount = 0;
            _Logger.LogInformation($"Started {SERVICE_NAME}");

            Stopwatch timer = Stopwatch.StartNew();

            while (stoppingToken.IsCancellationRequested == false) {
                timer.Restart();
                CharacterUpdateQueueEntry entry = await _Queue.Dequeue(stoppingToken);

                try {
                    PsCharacter? censusChar = entry.CensusCharacter;
                    CharacterMetadata? metadata = await _MetadataDb.GetByCharacterID(entry.CharacterID);

                    if (censusChar == null) {
                        try {
                            censusChar = await _CharacterCensus.GetByID(entry.CharacterID);
                        } catch (CensusConnectionException) {
                            _Logger.LogWarning($"Got timeout when loading {entry.CharacterID} from census, requeueing and retrying");
                            await Task.Delay(30 * 1000, stoppingToken);
                            _Queue.Queue(entry);
                            continue;
                        }
                    }

                    if (metadata == null) {
                        metadata = new CharacterMetadata() {
                            ID = entry.CharacterID,
                            LastUpdated = DateTime.MinValue
                        };
                    }

                    stoppingToken.ThrowIfCancellationRequested();

                    // 3 conditions to check:
                    //      1. The character was not found in census. This could be from a deleted character, so increment the not found count
                    //      2. The character was found in census, but the metadata is AFTER the last time the character logged in,
                    //              meaning there is no way the character could have stats that need to be updated
                    //      3. The character was found in census, and the character is logged in since the last time stats were updated
                    if (censusChar == null) {
                        ++metadata.NotFoundCount;
                    } else if (censusChar.DateLastLogin < metadata.LastUpdated && entry.Force == false) {
                        if (_Peepers.Contains(entry.CharacterID)) {
                            _Logger.LogTrace($"{entry.CharacterID} last login: {censusChar.DateLastLogin:u}, last update: {metadata.LastUpdated:u} ({metadata.LastUpdated - censusChar.DateLastLogin}), skipping update");
                        }
                        metadata.NotFoundCount = 0;
                    } else if (censusChar.DateLastLogin >= metadata.LastUpdated || entry.Force == true) {
                        if (_Peepers.Contains(entry.CharacterID)) {
                            _Logger.LogTrace($"{entry.CharacterID} last login: {censusChar.DateLastLogin:u}, last update: {metadata.LastUpdated:u} ({metadata.LastUpdated - censusChar.DateLastLogin}), PERFORMING UPDATE");
                        }
                        metadata.NotFoundCount = 0;
                        metadata.LastUpdated = DateTime.UtcNow;

                        List<WeaponStatEntry> weaponStats = new();
                        List<PsCharacterHistoryStat> historyStats = new();
                        List<CharacterItem> itemStats = new();
                        List<PsCharacterStat> statEntries = new();
                        List<CharacterFriend> charFriends = new();
                        List<CharacterDirective> charDirs = new();
                        List<CharacterDirectiveTree> charTreeDirs = new();
                        List<CharacterDirectiveTier> charTierDirs = new();
                        List<CharacterDirectiveObjective> charObjDirs = new();

                        await _CharacterDb.Upsert(censusChar);

                        try {
                            await Task.WhenAll(
                                // Update the characters weapon stats
                                _WeaponCensus.GetByCharacterID(entry.CharacterID).ContinueWith(result => weaponStats = result.Result),

                                // Update the characters history stats
                                _HistoryCensus.GetByCharacterID(entry.CharacterID).ContinueWith(result => historyStats = result.Result),

                                // Update the items the character has
                                _ItemCensus.GetByID(entry.CharacterID).ContinueWith(result => itemStats = result.Result),

                                // Get the character stats (not the history ones)
                                _StatCensus.GetByID(entry.CharacterID).ContinueWith(result => statEntries = result.Result),

                                // Get the character's friends
                                _FriendCensus.GetByCharacterID(entry.CharacterID).ContinueWith(result => charFriends = result.Result),

                                // Get the character's directive data
                                _CharacterDirectiveCensus.GetByCharacterID(entry.CharacterID).ContinueWith(result => charDirs = result.Result),
                                _CharacterDirectiveTreeCensus.GetByCharacterID(entry.CharacterID).ContinueWith(result => charTreeDirs = result.Result),
                                _CharacterDirectiveTierCensus.GetByCharacterID(entry.CharacterID).ContinueWith(result => charTierDirs = result.Result),
                                _CharacterDirectiveObjectiveCensus.GetByCharacterID(entry.CharacterID).ContinueWith(result => charObjDirs = result.Result)
                            );
                        } catch (AggregateException ex) when (ex.InnerException is CensusConnectionException) {
                            _Logger.LogWarning($"Got timeout when getting data for {entry.CharacterID}, requeuing");
                            _Queue.Queue(entry);
                            await Task.Delay(1000 * 15, stoppingToken);
                            continue;
                        }

                        long censusTime = timer.ElapsedMilliseconds;

                        foreach (WeaponStatEntry iter in weaponStats) {
                            await _WeaponStatDb.Upsert(iter);
                        }
                        stoppingToken.ThrowIfCancellationRequested();

                        foreach (PsCharacterHistoryStat stat in historyStats) {
                            await _HistoryDb.Upsert(entry.CharacterID, stat.Type, stat);
                        }
                        stoppingToken.ThrowIfCancellationRequested();

                        if (itemStats.Count > 0) {
                            await _ItemDb.Set(entry.CharacterID, itemStats);
                        }
                        stoppingToken.ThrowIfCancellationRequested();

                        if (statEntries.Count > 0) {
                            await _StatDb.Set(entry.CharacterID, statEntries);
                        }
                        stoppingToken.ThrowIfCancellationRequested();

                        if (charFriends.Count > 0) {
                            await _FriendDb.Set(entry.CharacterID, charFriends);
                        }
                        stoppingToken.ThrowIfCancellationRequested();

                        foreach (CharacterDirective dir in charDirs) {
                            try {
                                await _CharacterDirectiveDb.Upsert(entry.CharacterID, dir);
                            } catch (Exception ex) {
                                _Logger.LogError(ex, $"Error upserting character directives for {entry.CharacterID}");
                            }
                        }
                        stoppingToken.ThrowIfCancellationRequested();

                        foreach (CharacterDirectiveTree tree in charTreeDirs) {
                            try {
                                await _CharacterDirectiveTreeDb.Upsert(entry.CharacterID, tree);
                            } catch (Exception ex) {
                                _Logger.LogError(ex, $"Error upserting character directive trees for {entry.CharacterID}");
                            }
                        }
                        stoppingToken.ThrowIfCancellationRequested();

                        foreach (CharacterDirectiveTier tier in charTierDirs) {
                            try {
                                await _CharacterDirectiveTierDb.Upsert(entry.CharacterID, tier);
                            } catch (Exception ex) {
                                _Logger.LogError(ex, $"Error upserting character directive tiers for {entry.CharacterID}");
                            }
                        }
                        stoppingToken.ThrowIfCancellationRequested();

                        foreach (CharacterDirectiveObjective obj in charObjDirs) {
                            try {
                                await _CharacterDirectiveObjectiveDb.Upsert(entry.CharacterID, obj);
                            } catch (Exception ex) {
                                _Logger.LogError(ex, $"Error upserting character directive objectives for {entry.CharacterID}");
                            }
                        }
                        stoppingToken.ThrowIfCancellationRequested();

                        long dbTime = timer.ElapsedMilliseconds;

                        //_Logger.LogTrace($"Took {censusTime}ms to get data from census, {dbTime}ms to update DB data");
                    }

                    await _MetadataDb.Upsert(entry.CharacterID, metadata);

                    ++_Count;

                    if (_Count % 500 == 0) {
                        _Logger.LogDebug($"Cached {_Count} characters");
                    }

                    errorCount = 0;
                } catch (Exception ex) when (stoppingToken.IsCancellationRequested == false) {
                    _Logger.LogError(ex, $"Failed in {nameof(HostedBackgroundCharacterWeaponStatQueue)}");
                    ++errorCount;

                    if (errorCount > 2) {
                        await Task.Delay(1000 * Math.Min(5, errorCount), stoppingToken);
                    }
                } catch (Exception) when (stoppingToken.IsCancellationRequested == true) {
                    _Logger.LogInformation($"Stopped {SERVICE_NAME} with {_Queue.Count()} left");
                }
            }
        }

    }
}
