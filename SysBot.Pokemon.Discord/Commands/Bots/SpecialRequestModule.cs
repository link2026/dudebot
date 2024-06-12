using Discord;
using Discord.Commands;
using Discord.Net;
using Discord.WebSocket;
using PKHeX.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DiscordColor = Discord.Color;

namespace SysBot.Pokemon.Discord
{
    /// <summary>
    /// Provides functionality for listing and requesting Pokémon wondercard events via Discord commands.
    /// Users can interact with the system in multiple ways:
    ///
    /// 1. Listing Events:
    ///    - Users can list events from a specified generation or game. Optionally, users can filter this list by specifying a Pokémon species name.
    ///    - Command format: .srp {generationOrGame} [speciesName] [pageX]
    ///    - Example: .srp gen9 Mew page2
    ///      This command lists the second page of events for the 'gen9' dataset, specifically filtering for events related to 'Mew'.
    ///
    /// 2. Requesting Specific Events:
    ///    - Users can request a specific event to be processed by providing an event index number.
    ///    - Command format: .srp {generationOrGame} {eventIndex}
    ///    - Example: .srp gen9 26
    ///      This command requests the processing of the event at index 26 within the 'gen9' dataset.
    ///
    /// 3. Pagination:
    ///    - Users can navigate through pages of event listings by specifying the page number after the generation/game and optionally the species.
    ///    - Command format: .srp {generationOrGame} [speciesName] pageX
    ///    - Example: .srp gen9 page3
    ///      This command lists the third page of events for the 'gen9' dataset.
    ///
    /// This module ensures that user inputs are properly validated for the specific commands to manage event data effectively, adjusting listings or processing requests based on user interactions.
    /// </summary>
    public class SpecialRequestModule<T> : ModuleBase<SocketCommandContext> where T : PKM, new()
    {
        private const int itemsPerPage = 25;

        private static TradeQueueInfo<T> Info => SysCord<T>.Runner.Hub.Queues.Info;

        private static T? GetRequest(Download<PKM> dl)
        {
            if (!dl.Success)
                return null;
            return dl.Data switch
            {
                null => null,
                T pk => pk,
                _ => EntityConverter.ConvertToType(dl.Data, typeof(T), out _) as T,
            };
        }

        [Command("specialrequestpokemon")]
        [Alias("srp")]
        [Summary("Lists available wondercard events from the specified generation or game or requests a specific event if a number is provided.")]
        public async Task ListSpecialEventsAsync(string generationOrGame, [Remainder] string args = "")
        {
            var botPrefix = SysCord<T>.Runner.Config.Discord.CommandPrefix;
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 1 && int.TryParse(parts[0], out int index))
            {
                await SpecialEventRequestAsync(generationOrGame, index.ToString()).ConfigureAwait(false);
                return;
            }

            int page = 1;
            string speciesName = "";

            foreach (string part in parts)
            {
                if (part.StartsWith("page", StringComparison.OrdinalIgnoreCase) && int.TryParse(part.AsSpan(4), out int pageNumber))
                {
                    page = pageNumber;
                    continue;
                }
                speciesName = part;
            }

            var eventData = GetEventData(generationOrGame);
            if (eventData == null)
            {
                await ReplyAsync($"Invalid generation or game: {generationOrGame}").ConfigureAwait(false);
                return;
            }

            var allEvents = GetFilteredEvents(eventData, speciesName);
            if (!allEvents.Any())
            {
                await ReplyAsync($"No events found for {generationOrGame} with the specified filter.").ConfigureAwait(false);
                return;
            }

            var pageCount = (int)Math.Ceiling((double)allEvents.Count() / itemsPerPage);
            page = Math.Clamp(page, 1, pageCount);
            var embed = BuildEventListEmbed(generationOrGame, allEvents, page, pageCount, botPrefix);
            await SendEventListAsync(embed).ConfigureAwait(false);
            await CleanupMessagesAsync().ConfigureAwait(false);
        }

        [Command("specialrequestpokemon")]
        [Alias("srp")]
        [Summary("Downloads wondercard event attachments from the specified generation and adds to trade queue.")]
        [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
        public async Task SpecialEventRequestAsync(string generationOrGame, [Remainder] string args = "")
        {
            if (!int.TryParse(args, out int index))
            {
                await ReplyAsync("Invalid event index. Please provide a valid event number.").ConfigureAwait(false);
                return;
            }

            var userID = Context.User.Id;
            if (Info.IsUserInQueue(userID))
            {
                await ReplyAsync("You already have an existing trade in the queue. Please wait until it is processed.").ConfigureAwait(false);
                return;
            }

            try
            {
                var eventData = GetEventData(generationOrGame);
                if (eventData == null)
                {
                    await ReplyAsync($"Invalid generation or game: {generationOrGame}").ConfigureAwait(false);
                    return;
                }

                var entityEvents = eventData.Where(gift => gift.IsEntity && !gift.IsItem).ToArray();
                if (index < 1 || index > entityEvents.Length)
                {
                    await ReplyAsync($"Invalid event index. Please use a valid event number from the `{SysCord<T>.Runner.Config.Discord.CommandPrefix}srp {generationOrGame}` command.").ConfigureAwait(false);
                    return;
                }

                var selectedEvent = entityEvents[index - 1];
                var pk = ConvertEventToPKM(selectedEvent);
                if (pk == null)
                {
                    await ReplyAsync("Wondercard data provided is not compatible with this module!").ConfigureAwait(false);
                    return;
                }

                var code = Info.GetRandomTradeCode(userID);
                var lgcode = Info.GetRandomLGTradeCode();
                var sig = Context.User.GetFavor();

                await AddTradeToQueueAsync(code, Context.User.Username, pk as T, sig, Context.User, lgcode: lgcode).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ReplyAsync($"An error occurred: {ex.Message}").ConfigureAwait(false);
            }
            finally
            {
                await CleanupUserMessageAsync().ConfigureAwait(false);
            }
        }

        private static MysteryGift[]? GetEventData(string generationOrGame)
        {
            return generationOrGame.ToLowerInvariant() switch
            {
                "3" or "gen3" => EncounterEvent.MGDB_G3,
                "4" or "gen4" => EncounterEvent.MGDB_G4,
                "5" or "gen5" => EncounterEvent.MGDB_G5,
                "6" or "gen6" => EncounterEvent.MGDB_G6,
                "7" or "gen7" => EncounterEvent.MGDB_G7,
                "gg" or "lgpe" => EncounterEvent.MGDB_G7GG,
                "swsh" => EncounterEvent.MGDB_G8,
                "pla" or "la" => EncounterEvent.MGDB_G8A,
                "bdsp" => EncounterEvent.MGDB_G8B,
                "9" or "gen9" => EncounterEvent.MGDB_G9,
                _ => null,
            };
        }

        private static IOrderedEnumerable<(int Index, string EventInfo)> GetFilteredEvents(MysteryGift[] eventData, string speciesName = "")
        {
            return eventData
                .Where(gift =>
                    gift.IsEntity &&
                    !gift.IsItem &&
                    (string.IsNullOrWhiteSpace(speciesName) || GameInfo.Strings.Species[gift.Species].Equals(speciesName, StringComparison.OrdinalIgnoreCase))
                )
                .Select((gift, index) =>
                {
                    string species = GameInfo.Strings.Species[gift.Species];
                    string levelInfo = $"{gift.Level}";
                    string formName = ShowdownParsing.GetStringFromForm(gift.Form, GameInfo.Strings, gift.Species, gift.Context);
                    formName = !string.IsNullOrEmpty(formName) ? $"-{formName}" : "";
                    string trainerName = gift.OriginalTrainerName;

                    string eventDetails = $"{gift.CardHeader} - {species}{formName} | Lvl.{levelInfo} | OT: {trainerName}";

                    return (Index: index + 1, EventInfo: eventDetails);
                })
                .OrderBy(x => x.Index);
        }

        private static EmbedBuilder BuildEventListEmbed(string generationOrGame, IOrderedEnumerable<(int Index, string EventInfo)> allEvents, int page, int pageCount, string botPrefix)
        {
            var embed = new EmbedBuilder()
                .WithTitle($"Available Events - {generationOrGame.ToUpperInvariant()}")
                .WithDescription($"Page {page} of {pageCount}")
                .WithColor(DiscordColor.Blue);

            foreach (var item in allEvents.Skip((page - 1) * itemsPerPage).Take(itemsPerPage))
            {
                embed.AddField($"{item.Index}. {item.EventInfo}", $"Use `{botPrefix}srp {generationOrGame} {item.Index}` to request this event.");
            }

            return embed;
        }

        private async Task SendEventListAsync(EmbedBuilder embed)
        {
            if (Context.User is not IUser user)
            {
                await ReplyAsync("**Error**: Unable to send a DM. Please check your **Server Privacy Settings**.");
                return;
            }

            try
            {
                var dmChannel = await user.CreateDMChannelAsync();
                await dmChannel.SendMessageAsync(embed: embed.Build());
                await ReplyAsync($"{Context.User.Mention}, I've sent you a DM with the list of events.");
            }
            catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.Forbidden)
            {
                await ReplyAsync($"{Context.User.Mention}, I'm unable to send you a DM. Please check your **Server Privacy Settings**.");
            }
        }

        private async Task CleanupMessagesAsync()
        {
            await Task.Delay(10_000).ConfigureAwait(false);
            await CleanupUserMessageAsync().ConfigureAwait(false);
        }

        private async Task CleanupUserMessageAsync()
        {
            if (Context.Message is IUserMessage userMessage)
                await userMessage.DeleteAsync().ConfigureAwait(false);
        }

        private static PKM? ConvertEventToPKM(MysteryGift selectedEvent)
        {
            var download = new Download<PKM>
            {
                Data = selectedEvent.ConvertToPKM(new SimpleTrainerInfo(), EncounterCriteria.Unrestricted),
                Success = true
            };

            if (download.Data is null)
                return null;

            var pk = GetRequest(download);
            if (pk is null)
                return null;
            if (selectedEvent is IEncounterServerDate)
            {
                var (start, end) = GetEncounterDateRange(selectedEvent);
                if (start.HasValue && end.HasValue)
                {
                    pk.MetDate = GenerateRandomDateInRange(start.Value, end.Value);
                }
                else
                {
                    // Date not found, using current date
                    pk.MetDate = DateOnly.FromDateTime(DateTime.Now);
                }
            }
            else
            {
                // Date not found, using current date
                pk.MetDate = DateOnly.FromDateTime(DateTime.Now);
            }

            return pk;
        }

        private static (DateOnly? Start, DateOnly? End) GetEncounterDateRange(MysteryGift selectedEvent)
        {
            if (selectedEvent is WC8 wc8)
            {
                if (EncounterServerDate.WC8Gifts.TryGetValue(wc8.CardID, out var wc8Range))
                    return wc8Range;
                else if (EncounterServerDate.WC8GiftsChk.TryGetValue(wc8.Checksum, out var wc8ChkRange))
                    return wc8ChkRange;
            }
            else if (selectedEvent is WA8 wa8 && EncounterServerDate.WA8Gifts.TryGetValue(wa8.CardID, out var wa8Range))
            {
                return wa8Range;
            }
            else if (selectedEvent is WB8 wb8 && EncounterServerDate.WB8Gifts.TryGetValue(wb8.CardID, out var wb8Range))
            {
                return wb8Range;
            }
            else if (selectedEvent is WC9 wc9)
            {
                if (EncounterServerDate.WC9Gifts.TryGetValue(wc9.CardID, out var wc9Range))
                    return wc9Range;
                else if (EncounterServerDate.WC9GiftsChk.TryGetValue(wc9.Checksum, out var wc9ChkRange))
                    return wc9ChkRange;
            }

            return (null, null);
        }

        private static DateOnly GenerateRandomDateInRange(DateOnly startDate, DateOnly endDate)
        {
            var random = new Random();
            var totalDays = (endDate.DayNumber - startDate.DayNumber) + 1;
            var randomDays = random.Next(totalDays);
            return startDate.AddDays(randomDays);
        }

        private async Task AddTradeToQueueAsync(int code, string trainerName, T? pk, RequestSignificance sig, SocketUser usr, bool isBatchTrade = false, int batchTradeNumber = 1, int totalBatchTrades = 1, bool isMysteryEgg = false, List<Pictocodes>? lgcode = null, PokeTradeType tradeType = PokeTradeType.Specific, bool ignoreAutoOT = false, bool isHiddenTrade = false)
        {
            lgcode ??= TradeModule<T>.GenerateRandomPictocodes(3);
#pragma warning disable CS8604 // Possible null reference argument.
            var la = new LegalityAnalysis(pk);
#pragma warning restore CS8604 // Possible null reference argument.
            if (!la.Valid)
            {
                string responseMessage = pk.IsEgg ? "Invalid Showdown Set for this Egg. Please review your information and try again." :
                    $"{typeof(T).Name} attachment is not legal, and cannot be traded!";
                var reply = await ReplyAsync(responseMessage).ConfigureAwait(false);
                await Task.Delay(6000);
                await reply.DeleteAsync().ConfigureAwait(false);
                return;
            }
            if (!la.Valid && la.Results.Any(m => m.Identifier is CheckIdentifier.Memory))
            {
                var clone = (T)pk.Clone();

                clone.HandlingTrainerName = pk.OriginalTrainerName;
                clone.HandlingTrainerGender = pk.OriginalTrainerGender;

                if (clone is PK8 or PA8 or PB8 or PK9)
                    ((dynamic)clone).HandlingTrainerLanguage = (byte)pk.Language;

                clone.CurrentHandler = 1;

                la = new LegalityAnalysis(clone);

                if (la.Valid) pk = clone;
            }

            await QueueHelper<T>.AddToQueueAsync(Context, code, trainerName, sig, pk, PokeRoutineType.LinkTrade, tradeType, usr, isBatchTrade, batchTradeNumber, totalBatchTrades, isHiddenTrade, isMysteryEgg, lgcode, ignoreAutoOT).ConfigureAwait(false);
        }
    }
}
