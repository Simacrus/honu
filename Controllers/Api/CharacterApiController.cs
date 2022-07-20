﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using watchtower.Models;
using watchtower.Models.Api;
using watchtower.Models.Census;
using watchtower.Models.CharacterViewer.CharacterStats;
using watchtower.Models.Db;
using watchtower.Services;
using watchtower.Services.CharacterViewer;
using watchtower.Services.Db;
using watchtower.Services.Queues;
using watchtower.Services.Repositories;

namespace watchtower.Controllers.Api {

    /// <summary>
    ///     Endpoints for getting <see cref="PsCharacter"/> information
    /// </summary>
    [ApiController]
    [Route("/api/")]
    public class CharacterApiController : ApiControllerBase {

        private readonly ILogger<CharacterApiController> _Logger;

        private readonly CharacterRepository _CharacterRepository;
        private readonly ICharacterStatGeneratorStore _GeneratorStore;
        private readonly CharacterHistoryStatRepository _HistoryRepository;
        private readonly SessionDbStore _SessionDb;
        private readonly CharacterItemRepository _CharacterItemRepository;
        private readonly ItemRepository _ItemRepository;
        private readonly ICharacterStatRepository _StatRepository;
        private readonly CharacterMetadataDbStore _MetadataDb;
        private readonly CharacterFriendRepository _CharacterFriendRepository;

        private readonly CharacterUpdateQueue _UpdateQueue;

        public CharacterApiController(ILogger<CharacterApiController> logger,
            CharacterRepository charRepo, ICharacterStatGeneratorStore genStore,
            CharacterHistoryStatRepository histRepo, SessionDbStore sessionDb,
            CharacterItemRepository charItemRepo, ItemRepository itemRepo,
            ICharacterStatRepository statRepo, CharacterMetadataDbStore metadataDb,
            CharacterFriendRepository charFriendRepo, CharacterUpdateQueue queue) {

            _Logger = logger;

            _CharacterRepository = charRepo;
            _GeneratorStore = genStore ?? throw new ArgumentNullException(nameof(genStore));
            _HistoryRepository = histRepo ?? throw new ArgumentNullException(nameof(histRepo));
            _SessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
            _CharacterItemRepository = charItemRepo ?? throw new ArgumentNullException(nameof(charItemRepo));
            _ItemRepository = itemRepo ?? throw new ArgumentNullException(nameof(itemRepo));
            _StatRepository = statRepo ?? throw new ArgumentNullException(nameof(statRepo));
            _MetadataDb = metadataDb ?? throw new ArgumentNullException(nameof(metadataDb));
            _CharacterFriendRepository = charFriendRepo;
            _UpdateQueue = queue;
        }

        /// <summary>
        ///     Get a specific character by the ID
        /// </summary>
        /// <remarks>
        ///     Even if the character does not exist in Census anymore, Honu will also check the local DB it stores
        /// </remarks>
        /// <param name="charID">ID of the character to get</param>
        /// <response code="200">
        ///     The response will contain the <see cref="PsCharacter"/> with <see cref="PsCharacter.ID"/>
        ///     of <paramref name="charID"/>
        /// </response>
        /// <response code="204">
        ///     There is no character with the ID given
        /// </response>
        /// <response code="400">
        ///     The parameter <paramref name="charID"/> was not all digits
        /// </response>
        [HttpGet("character/{charID}")]
        public async Task<ApiResponse<PsCharacter>> GetByID(string charID) {
            if (charID.All(char.IsDigit) == false) {
                return ApiBadRequest<PsCharacter>($"{nameof(charID)} was not all digits: '{charID}'");
            }

            PsCharacter? c = await _CharacterRepository.GetByID(charID);
            if (c == null) {
                return ApiNoContent<PsCharacter>();
            }

            return ApiOk(c);
        }

        [HttpGet("character/many")]
        public async Task<ApiResponse<List<PsCharacter>>> GetByIDs([FromQuery] List<string> IDs) {
            List<PsCharacter> chars = await _CharacterRepository.GetByIDs(IDs);

            return ApiOk(chars);
        }

        /// <summary>
        ///     Get the extra/fun stats generated by Honu
        /// </summary>
        /// <remarks>
        ///     How these stats are generated is done in a different library, and is not publicly available
        /// </remarks>
        /// <param name="charID">ID of the character to get the extra/fun stats of</param>
        /// <response code="200">
        ///     The response will contain a list of <see cref="CharacterStatBase"/>s generated
        ///     from the <see cref="PsCharacter"/> with <see cref="PsCharacter.ID"/> of <paramref name="charID"/>,
        ///     and the current time
        /// </response>
        /// <response code="404">
        ///     No <see cref="PsCharacter"/> with <see cref="PsCharacter.ID"/> of <paramref name="charID"/> exists
        /// </response>
        [HttpGet("character/{charID}/extra")]
        public async Task<ApiResponse<List<ExtraStatSet>>> GetExtraStats(string charID) {
            PsCharacter? c = await _CharacterRepository.GetByID(charID);
            if (c == null) {
                return ApiNotFound<List<ExtraStatSet>>($"{nameof(PsCharacter)} {charID}");
            }

            List<ExtraStatSet> stats = await _GeneratorStore.GenerateAll(charID);

            return ApiOk(stats);
        }

        /// <summary>
        ///     Get the history_stats of a character
        /// </summary>
        /// <remarks>
        ///     This is basically a wrapper to the Census endpoint /characters_history_stat, but if the data
        ///     is found in the local DB, it will use that instead
        /// </remarks>
        /// <param name="charID">ID of the character</param>
        /// <response code="200">
        ///     The response will contain a list of <see cref="PsCharacterHistoryStat"/>s for the
        ///     <see cref="PsCharacter"/> with <see cref="PsCharacter.ID"/> of <paramref name="charID"/>
        /// </response>
        /// <response code="404">
        ///     No <see cref="PsCharacter"/> with <see cref="PsCharacter.ID"/> of <paramref name="charID"/> exists
        /// </response>
        [HttpGet("character/{charID}/history_stats")]
        public async Task<ApiResponse<List<PsCharacterHistoryStat>>> GetHistoryStats(string charID) {
            PsCharacter? c = await _CharacterRepository.GetByID(charID);
            if (c == null) {
                return ApiNotFound<List<PsCharacterHistoryStat>>($"{nameof(PsCharacter)} {charID}");
            }

            List<PsCharacterHistoryStat> stats = await _HistoryRepository.GetByCharacterID(charID);

            return ApiOk(stats);
        }

        /// <summary>
        ///     Get a list of all sessions for a character
        /// </summary>
        /// <remarks>
        ///     Session tracking only started 2021-07-23
        /// </remarks>
        /// <param name="charID">ID of the character</param>
        /// <response code="200">
        ///     The response will contain a list of all <see cref="Session"/>s for
        ///     the <see cref="PsCharacter"/> with <see cref="PsCharacter.ID"/> of <paramref name="charID"/>
        /// </response>
        /// <response code="404">
        ///     No <see cref="PsCharacter"/> with <see cref="PsCharacter.ID"/> of <paramref name="charID"/> exists
        /// </response>
        [HttpGet("character/{charID}/sessions")]
        public async Task<ApiResponse<List<Session>>> GetSessions(string charID) {
            PsCharacter? c = await _CharacterRepository.GetByID(charID);
            if (c == null) {
                return ApiNotFound<List<Session>>($"{nameof(PsCharacter)} {charID}");
            }

            List<Session> sessions = await _SessionDb.GetAllByCharacterID(charID);

            return ApiOk(sessions);
        }

        /// <summary>
        ///     Get the items a character owns
        /// </summary>
        /// <remarks>
        ///     <see cref="ExpandedCharacterItem"/> includes the full <see cref="PsItem"/> information,
        ///     and an additional lookup to get the item is not needed
        /// </remarks>
        /// <param name="charID">ID of the character</param>
        /// <response code="200">
        ///     The response will contain a list of all <see cref="ExpandedCharacterItem"/>s
        ///     owned by the <see cref="PsCharacter"/> with <see cref="PsCharacter.ID"/> of <paramref name="charID"/>
        /// </response>
        /// <response code="404">
        ///     No <see cref="PsCharacter"/> with <see cref="PsCharacter.ID"/> of <paramref name="charID"/> exists
        /// </response>
        [HttpGet("character/{charID}/items")]
        public async Task<ApiResponse<List<ExpandedCharacterItem>>> GetCharacterItems(string charID) {
            PsCharacter? c = await _CharacterRepository.GetByID(charID);
            if (c == null) {
                return ApiNotFound<List<ExpandedCharacterItem>>($"{nameof(PsCharacter)} {charID}");
            }

            List<CharacterItem> items = await _CharacterItemRepository.GetByID(charID);

            List<ExpandedCharacterItem> expanded = new List<ExpandedCharacterItem>(items.Count);

            foreach (CharacterItem item in items) {
                ExpandedCharacterItem ex = new ExpandedCharacterItem();
                ex.Entry = item;
                ex.Item = await _ItemRepository.GetByID(int.Parse(item.ItemID));

                expanded.Add(ex);
            }

            return ApiOk(expanded);
        }

        /// <summary>
        ///     Get the stats of a character
        /// </summary>
        /// <remarks>
        ///     This is a Census wrapper around the /characters_stat endpoint, but with a DB lookup to save time
        /// </remarks>
        /// <param name="charID">ID of the character</param>
        /// <response code="200">
        ///     The response will contain the <see cref="PsCharacterStat"/>s for the
        ///     <see cref="PsCharacter"/> with <see cref="PsCharacter.ID"/> of <paramref name="charID"/>
        /// </response>
        /// <response code="404">
        ///     No <see cref="PsCharacter"/> with <see cref="PsCharacter.ID"/> of <paramref name="charID"/> exists
        /// </response>
        [HttpGet("character/{charID}/stats")]
        public async Task<ApiResponse<List<PsCharacterStat>>> GetCharacterStats(string charID) {
            PsCharacter? c = await _CharacterRepository.GetByID(charID);
            if (c == null) {
                return ApiNotFound<List<PsCharacterStat>>($"{nameof(PsCharacter)} {charID}");
            }

            List<PsCharacterStat> stats = await _StatRepository.GetByCharacterID(charID);
            return ApiOk(stats);
        }

        /// <summary>
        ///     Get the online status of a character
        /// </summary>
        /// <param name="charID">ID of the character</param>
        /// <response code="200">
        ///     The response will contain a boolean value indicating if the <see cref="TrackedPlayer"/>
        ///     with <see cref="TrackedPlayer.ID"/> of <paramref name="charID"/> is online or not.
        ///     If the player is not found, it is assumed they are offline as well
        /// </response>
        [HttpGet("character/{charID}/online")]
        public ApiResponse<bool> GetCurrentSession(string charID) {
            TrackedPlayer? player = null;

            lock (CharacterStore.Get().Players) {
                CharacterStore.Get().Players.TryGetValue(charID, out player);
            }

            if (player == null) {
                return ApiOk(false);
            }

            return ApiOk(player.Online);
        }

        /// <summary>
        ///     Get the characters that match the name (case insensitive)
        /// </summary>
        /// <remarks>
        ///     Because characters can be deleted and names reused, names are not unique
        /// </remarks>
        /// <param name="name">Name of the character to get</param>
        /// <response code="200">
        ///     The response will contain a list of all <see cref="PsCharacter"/>s with 
        ///     <see cref="PsCharacter.Name"/> of <paramref name="name"/>
        /// </response>
        [HttpGet("characters/name/{name}")]
        public async Task<ApiResponse<List<PsCharacter>>> GetByName(string name) {
            List<PsCharacter> chars = await _CharacterRepository.GetByName(name);

            return ApiOk(chars);
        }

        /// <summary>
        ///     Get the metadata of a character
        /// </summary>
        /// <param name="charID">ID of the character</param>
        /// <response code="200">
        ///     The response will contain the <see cref="CharacterMetadata"/> with <see cref="CharacterMetadata.ID"/> of <paramref name="charID"/>
        /// </response>
        /// <response code="204">
        ///     No <see cref="CharacterMetadata"/> with <see cref="CharacterMetadata.ID"/> of <paramref name="charID"/> exists
        /// </response>
        [HttpGet("character/{charID}/metadata")]
        public async Task<ApiResponse<CharacterMetadata>> GetMetadata(string charID) {
            CharacterMetadata? md = await _MetadataDb.GetByCharacterID(charID);
            if (md == null) {
                return ApiNoContent<CharacterMetadata>();
            }

            return ApiOk(md);
        }

        /// <summary>
        ///     Get the friends of a character
        /// </summary>
        /// <remarks>
        ///     If the character does not exist, no 404 will be returned
        ///     <br/><br/>
        ///     
        ///     The friends will be expanded, with the full PsCharacter information available
        /// </remarks>
        /// <param name="charID">ID of the character</param>
        /// <param name="fast">Will Census be hit if the repo determins the DB data is outta date?</param>
        /// <response code="200">
        ///     The response will contain a list of the character's friends
        /// </response>
        [HttpGet("character/{charID}/friends")]
        public async Task<ApiResponse<List<ExpandedCharacterFriend>>> GetFriends(string charID, [FromQuery] bool fast = false) {
            List<CharacterFriend> friends = await _CharacterFriendRepository.GetByCharacterID(charID, fast);

            List<ExpandedCharacterFriend> expanded = new List<ExpandedCharacterFriend>(friends.Count);

            List<PsCharacter> chars = await _CharacterRepository.GetByIDs(friends.Select(iter => iter.FriendID).ToList(), fast: true);

            foreach (CharacterFriend friend in friends) {
                ExpandedCharacterFriend ex = new ExpandedCharacterFriend() {
                    Entry = friend,
                    Friend = chars.FirstOrDefault(iter => iter.ID == friend.FriendID)
                };

                if (ex.Friend == null) {
                    _UpdateQueue.Queue(friend.FriendID);
                }

                expanded.Add(ex);
            }

            return ApiOk(expanded);
        }

        /// <summary>
        ///     Search for a character by it's name (case-insensitive)
        /// </summary>
        /// <remarks>
        ///     A minimum of 3 characters must be passed for a search to take place. 
        ///     <br/><br/>
        ///     The search that takes place is case-insensitive
        /// </remarks>
        /// <param name="name">Name of the character to search</param>
        /// <param name="censusTimeout">If a timeout will occur when doing a Census search</param>
        /// <response code="200">
        ///     The response will contain a list of <see cref="PsCharacter"/> that contain the string <paramref name="name"/>
        ///     within <see cref="PsCharacter.Name"/>
        /// </response>
        /// <response code="400">
        ///     The parameter <paramref name="name"/> was less than 3 characters long
        /// </response>
        [HttpGet("characters/search/{name}")]
        public async Task<ApiResponse<List<PsCharacter>>> SearchByName(string name, [FromQuery] bool censusTimeout = true) {
            if (name.Length < 3) {
                return ApiBadRequest<List<PsCharacter>>($"The parameter {nameof(name)} cannot have a length less than 3 (was {name.Length})");
            }

            List<PsCharacter> chars = await _CharacterRepository.SearchByName(name, censusTimeout);
            return ApiOk(chars);
        }

    }
}
