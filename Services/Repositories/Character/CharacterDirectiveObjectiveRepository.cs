using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using watchtower.Models.Census;
using watchtower.Models.Db;
using watchtower.Services.Census;
using watchtower.Services.Db;

namespace watchtower.Services.Repositories {

    public class CharacterDirectiveObjectiveRepository {

        private readonly ILogger<CharacterDirectiveObjectiveRepository> _Logger;
        private readonly IMemoryCache _Cache;

        private readonly CharacterMetadataDbStore _Metadata;
        private readonly CharacterDbStore _CharacterDb;
        private readonly CharacterDirectiveObjectiveCollection _Census;
        private readonly CharacterDirectiveObjectiveDbStore _Db;

        private const string CACHE_KEY = "CharacterDirectiveObjective.{0}"; // {0} => Character ID

        public CharacterDirectiveObjectiveRepository(ILogger<CharacterDirectiveObjectiveRepository> logger,
            IMemoryCache cache, CharacterDirectiveObjectiveCollection census,
            CharacterDirectiveObjectiveDbStore db, CharacterMetadataDbStore metadata,
            CharacterDbStore charDb) {

            _Logger = logger;
            _Cache = cache;

            _Metadata = metadata;
            _CharacterDb = charDb;
            _Census = census;
            _Db = db;
        }

        public async Task<List<CharacterDirectiveObjective>> GetByCharacterID(string charID) {
            string cacheKey = string.Format(CACHE_KEY, charID);

            if (_Cache.TryGetValue(cacheKey, out List<CharacterDirectiveObjective> dirs) == false) {
                CharacterMetadata? metadata = await _Metadata.GetByCharacterID(charID);
                PsCharacter? dbChar = await _CharacterDb.GetByID(charID);

                // Metadata doesn't exist, character doesn't exist in db, or it hasn't been updated
                bool getCensus = metadata == null || dbChar == null || (metadata.LastUpdated < dbChar.DateLastLogin);

                // If we're gonna go to Census anyways, can just skip this bit, saving a db call
                if (getCensus == false) {
                    dirs = await _Db.GetByCharacterID(charID);
                    getCensus = dirs.Count == 0; // If there is no character directives, might not have gotten yet
                }

                if (getCensus == true) {
                    dirs = await _Census.GetByCharacterID(charID);

                    foreach (CharacterDirectiveObjective dir in dirs) {
                        await _Db.Upsert(charID, dir);
                    }
                }

                _Cache.Set(cacheKey, dirs, new MemoryCacheEntryOptions() {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(20)
                });
            }

            return dirs;
        }

        /// <summary>
        ///     Upsert a <see cref="CharacterDirectiveObjective"/>
        /// </summary>
        /// <param name="charID">ID of the character</param>
        /// <param name="obj">Parameters used to upsert</param>
        public async Task Upsert(string charID, CharacterDirectiveObjective obj) {
            string cacheKey = string.Format(CACHE_KEY, charID);
            _Cache.Remove(cacheKey);

            await _Db.Upsert(charID, obj);
        }

    }

}