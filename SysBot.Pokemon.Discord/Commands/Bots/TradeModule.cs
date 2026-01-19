using Discord;
using Discord.Commands;
using Discord.WebSocket;
using PKHeX.Core;
using PKHeX.Core.AutoMod;
using SysBot.Base;
using SysBot.Pokemon.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord;

[Summary("Queues new Link Code trades")]
public class TradeModule<T> : ModuleBase<SocketCommandContext> where T : PKM, new()
{
    private static TradeQueueInfo<T> Info => SysCord<T>.Runner.Hub.Queues.Info;

    #region Medal Achievement Command

    [Command("medals")]
    [Alias("ml")]
    [Summary("Shows your current trade count and medal status")]
    public async Task ShowMedalsCommand()
    {
        var tradeCodeStorage = new TradeCodeStorage();
        int totalTrades = tradeCodeStorage.GetTradeCount(Context.User.Id);

        if (totalTrades == 0)
        {
            await ReplyAsync($"{Context.User.Username}, you haven't made any trades yet. Start trading to earn your first medal!");
            return;
        }

        int currentMilestone = MedalHelpers.GetCurrentMilestone(totalTrades);
        var embed = MedalHelpers.CreateMedalsEmbed(Context.User, currentMilestone, totalTrades);
        await Context.Channel.SendMessageAsync(embed: embed).ConfigureAwait(false);
    }

    #endregion

    #region Trade Commands

    [Command("trade")]
    [Alias("t")]
    [Summary("Makes the bot trade you a Pokémon converted from the provided Showdown Set.")]
    [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
    public Task TradeAsync([Summary("Showdown Set")][Remainder] string content)
    {
        var userID = Context.User.Id;
        var code = Info.GetRandomTradeCode(userID);
        return ProcessTradeAsync(code, content);
    }

    [Command("trade")]
    [Alias("t")]
    [Summary("Makes the bot trade you a Pokémon converted from the provided Showdown Set.")]
    [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
    public Task TradeAsync([Summary("Trade Code")] int code, [Summary("Showdown Set")][Remainder] string content)
        => ProcessTradeAsync(code, content);

    [Command("trade")]
    [Alias("t")]
    [Summary("Makes the bot trade you the provided Pokémon file.")]
    [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
    public Task TradeAsyncAttach([Summary("Trade Code")] int code, [Summary("Ignore AutoOT")] bool ignoreAutoOT = false)
    {
        var sig = Context.User.GetFavor();
        return ProcessTradeAttachmentAsync(code, sig, Context.User, ignoreAutoOT: ignoreAutoOT);
    }

    [Command("trade")]
    [Alias("t")]
    [Summary("Makes the bot trade you the attached file.")]
    [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
    public async Task TradeAsyncAttach([Summary("Ignore AutoOT")] bool ignoreAutoOT = false)
    {
        var userID = Context.User.Id;
        var code = Info.GetRandomTradeCode(userID);
        var sig = Context.User.GetFavor();

        await Task.Run(async () =>
        {
            await ProcessTradeAttachmentAsync(code, sig, Context.User, ignoreAutoOT: ignoreAutoOT).ConfigureAwait(false);
        }).ConfigureAwait(false);

        if (Context.Message is IUserMessage userMessage)
            _ = Helpers<T>.DeleteMessagesAfterDelayAsync(userMessage, null, 2);
    }

    [Command("hidetrade")]
    [Alias("ht")]
    [Summary("Makes the bot trade you a Pokémon without showing trade embed details.")]
    [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
    public Task HideTradeAsync([Summary("Showdown Set")][Remainder] string content)
    {
        var userID = Context.User.Id;
        var code = Info.GetRandomTradeCode(userID);
        return ProcessTradeAsync(code, content, isHiddenTrade: true);
    }

    [Command("hidetrade")]
    [Alias("ht")]
    [Summary("Makes the bot trade you a Pokémon without showing trade embed details.")]
    [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
    public Task HideTradeAsync([Summary("Trade Code")] int code, [Summary("Showdown Set")][Remainder] string content)
        => ProcessTradeAsync(code, content, isHiddenTrade: true);

    [Command("hidetrade")]
    [Alias("ht")]
    [Summary("Makes the bot trade you the provided file without showing trade embed details.")]
    [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
    public Task HideTradeAsyncAttach([Summary("Trade Code")] int code, [Summary("Ignore AutoOT")] bool ignoreAutoOT = false)
    {
        var sig = Context.User.GetFavor();
        return ProcessTradeAttachmentAsync(code, sig, Context.User, isHiddenTrade: true, ignoreAutoOT: ignoreAutoOT);
    }

    [Command("hidetrade")]
    [Alias("ht")]
    [Summary("Makes the bot trade you the attached file without showing trade embed details.")]
    [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
    public async Task HideTradeAsyncAttach([Summary("Ignore AutoOT")] bool ignoreAutoOT = false)
    {
        var userID = Context.User.Id;
        var code = Info.GetRandomTradeCode(userID);
        var sig = Context.User.GetFavor();

        await ProcessTradeAttachmentAsync(code, sig, Context.User, isHiddenTrade: true, ignoreAutoOT: ignoreAutoOT).ConfigureAwait(false);

        if (Context.Message is IUserMessage userMessage)
            _ = Helpers<T>.DeleteMessagesAfterDelayAsync(userMessage, null, 2);
    }

    [Command("tradeUser")]
    [Alias("tu", "tradeOther")]
    [Summary("Makes the bot trade the mentioned user the attached file.")]
    [RequireSudo]
    public async Task TradeAsyncAttachUser([Summary("Trade Code")] int code, [Remainder] string _)
    {
        if (Context.Message.MentionedUsers.Count > 1)
        {
            await ReplyAsync("Too many mentions. Queue one user at a time.").ConfigureAwait(false);
            return;
        }

        if (Context.Message.MentionedUsers.Count == 0)
        {
            await ReplyAsync("A user must be mentioned in order to do this.").ConfigureAwait(false);
            return;
        }

        var usr = Context.Message.MentionedUsers.ElementAt(0);
        var sig = usr.GetFavor();
        await ProcessTradeAttachmentAsync(code, sig, usr).ConfigureAwait(false);
    }

    [Command("tradeUser")]
    [Alias("tu", "tradeOther")]
    [Summary("Makes the bot trade the mentioned user the attached file.")]
    [RequireSudo]
    public Task TradeAsyncAttachUser([Remainder] string _)
    {
        var userID = Context.User.Id;
        var code = Info.GetRandomTradeCode(userID);
        return TradeAsyncAttachUser(code, _);
    }

    #endregion

    #region Special Trade Commands

    [Command("egg")]
    [Alias("Egg")]
    [Summary("Trades an egg generated from the provided Pokémon name.")]
    public async Task TradeEgg([Remainder] string egg)
    {
        var userID = Context.User.Id;
        var code = Info.GetRandomTradeCode(userID);
        await TradeEggAsync(code, egg).ConfigureAwait(false);
    }

    [Command("egg")]
    [Alias("Egg")]
    [Summary("Trades an egg generated from the provided Pokémon name.")]
    [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
    public async Task TradeEggAsync([Summary("Trade Code")] int code, [Summary("Showdown Set")][Remainder] string content)
    {
        var userID = Context.User.Id;
        if (!await Helpers<T>.EnsureUserNotInQueueAsync(userID))
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context,
                "You already have an existing trade in the queue that cannot be cleared. Please wait until it is processed.", 2);
            return;
        }

        content = ReusableActions.StripCodeBlock(content);
        var set = new ShowdownSet(content);
        var template = AutoLegalityWrapper.GetTemplate(set);

        _ = Task.Run(async () =>
        {
            try
            {
                var sav = AutoLegalityWrapper.GetTrainerInfo<T>();

                // Generate the egg using ALM's GenerateEgg method
                var pkm = sav.GenerateEgg(template, out var result);

                if (result != LegalizationResult.Regenerated)
                {
                    var reason = result == LegalizationResult.Timeout
                        ? "Egg generation took too long."
                        : "Failed to generate egg from the provided set.";
                    await Helpers<T>.ReplyAndDeleteAsync(Context, reason, 2);
                    return;
                }

                pkm = EntityConverter.ConvertToType(pkm, typeof(T), out _) ?? pkm;
                if (pkm is not T pk)
                {
                    await Helpers<T>.ReplyAndDeleteAsync(Context, "Oops! I wasn't able to create an egg for that.", 2);
                    return;
                }

                var sig = Context.User.GetFavor();
                await Helpers<T>.AddTradeToQueueAsync(Context, code, Context.User.Username, pk, sig, Context.User).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogUtil.LogSafe(ex, nameof(TradeModule<T>));
                await Helpers<T>.ReplyAndDeleteAsync(Context, "An error occurred while processing the request.", 2);
            }
        });

        if (Context.Message is IUserMessage userMessage)
            _ = Helpers<T>.DeleteMessagesAfterDelayAsync(userMessage, null, 2);
    }

    [Command("fixOT")]
    [Alias("fix", "f")]
    [Summary("Fixes OT and Nickname of a Pokémon you show via Link Trade if an advert is detected.")]
    [RequireQueueRole(nameof(DiscordManager.RolesFixOT))]
    public async Task FixAdOT()
    {
        var userID = Context.User.Id;
        if (!await Helpers<T>.EnsureUserNotInQueueAsync(userID))
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context,
                "You already have an existing trade in the queue that cannot be cleared. Please wait until it is processed.", 2);
            return;
        }

        var code = Info.GetRandomTradeCode(userID);
        await ProcessFixOTAsync(code);
    }

    [Command("fixOT")]
    [Alias("fix", "f")]
    [Summary("Fixes OT and Nickname of a Pokémon you show via Link Trade if an advert is detected.")]
    [RequireQueueRole(nameof(DiscordManager.RolesFixOT))]
    public async Task FixAdOT([Summary("Trade Code")] int code)
    {
        var userID = Context.User.Id;
        if (!await Helpers<T>.EnsureUserNotInQueueAsync(userID))
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context,
                "You already have an existing trade in the queue that cannot be cleared. Please wait until it is processed.", 2);
            return;
        }

        await ProcessFixOTAsync(code);
    }

    private async Task ProcessFixOTAsync(int code)
    {
        var trainerName = Context.User.Username;
        var sig = Context.User.GetFavor();
        var lgcode = Info.GetRandomLGTradeCode();

        await QueueHelper<T>.AddToQueueAsync(Context, code, trainerName, sig, new T(),
            PokeRoutineType.FixOT, PokeTradeType.FixOT, Context.User, 1, 1, false, false, lgcode: lgcode).ConfigureAwait(false);

        if (Context.Message is IUserMessage userMessage)
            _ = Helpers<T>.DeleteMessagesAfterDelayAsync(userMessage, null, 2);
    }

    [Command("dittoTrade")]
    [Alias("dt", "ditto")]
    [Summary("Makes the bot trade you a Ditto with a requested stat spread and language.")]
    public async Task DittoTrade([Summary("A combination of \"ATK/SPA/SPE\" or \"6IV\"")] string keyword,
        [Summary("Language")] string language, [Summary("Nature")] string nature)
    {
        var userID = Context.User.Id;
        if (!await Helpers<T>.EnsureUserNotInQueueAsync(userID))
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context,
                "You already have an existing trade in the queue that cannot be cleared. Please wait until it is processed.", 2);
            return;
        }

        var code = Info.GetRandomTradeCode(userID);
        await ProcessDittoTradeAsync(code, keyword, language, nature);
    }

    [Command("dittoTrade")]
    [Alias("dt", "ditto")]
    [Summary("Makes the bot trade you a Ditto with a requested stat spread and language.")]
    public async Task DittoTrade([Summary("Trade Code")] int code,
        [Summary("A combination of \"ATK/SPA/SPE\" or \"6IV\"")] string keyword,
        [Summary("Language")] string language, [Summary("Nature")] string nature)
    {
        var userID = Context.User.Id;
        if (!await Helpers<T>.EnsureUserNotInQueueAsync(userID))
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context,
                "You already have an existing trade in the queue that cannot be cleared. Please wait until it is processed.", 2);
            return;
        }

        await ProcessDittoTradeAsync(code, keyword, language, nature);
    }

    private async Task ProcessDittoTradeAsync(int code, string keyword, string language, string nature)
    {
        keyword = keyword.ToLower().Trim();

        if (!Enum.TryParse(language, true, out LanguageID lang))
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context, $"Couldn't recognize language: {language}.", 2);
            return;
        }

        nature = nature.Trim()[..1].ToUpper() + nature.Trim()[1..].ToLower();
        var set = new ShowdownSet($"{keyword}(Ditto)\nLanguage: {lang}\nNature: {nature}");
        var template = AutoLegalityWrapper.GetTemplate(set);
        var sav = AutoLegalityWrapper.GetTrainerInfo<T>();
        var pkm = sav.GetLegal(template, out var result);

        if (pkm == null)
        {
            await ReplyAsync("Set took too long to legalize.");
            return;
        }

        TradeExtensions<T>.DittoTrade((T)pkm);
        var la = new LegalityAnalysis(pkm);

        if (pkm is not T pk || !la.Valid)
        {
            var reason = result == "Timeout" ? "That set took too long to generate." : "I wasn't able to create something from that.";
            var imsg = $"Oops! {reason} Here's my best attempt for that Ditto!";
            await Context.Channel.SendPKMAsync(pkm, imsg).ConfigureAwait(false);
            return;
        }

        pk.ResetPartyStats();

        // Ad Name Check
        if (Info.Hub.Config.TradeSystem.Settings.TradeConfiguration.EnableSpamCheck)
        {
            if (TradeExtensions<T>.HasAdName(pk, out string ad))
            {
                await Helpers<T>.ReplyAndDeleteAsync(Context, "Detected Adname in the Pokémon's name or trainer name, which is not allowed.", 5);
                return;
            }
        }

        var sig = Context.User.GetFavor();
        await QueueHelper<T>.AddToQueueAsync(Context, code, Context.User.Username, sig, pk,
            PokeRoutineType.LinkTrade, PokeTradeType.Specific).ConfigureAwait(false);

        if (Context.Message is IUserMessage userMessage)
            _ = Helpers<T>.DeleteMessagesAfterDelayAsync(userMessage, null, 2);
    }

    [Command("itemTrade")]
    [Alias("it", "item")]
    [Summary("Makes the bot trade you a Pokémon holding the requested item.")]
    public async Task ItemTrade([Remainder] string item)
    {
        var userID = Context.User.Id;
        if (!await Helpers<T>.EnsureUserNotInQueueAsync(userID))
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context,
                "You already have an existing trade in the queue that cannot be cleared. Please wait until it is processed.", 2);
            return;
        }

        var code = Info.GetRandomTradeCode(userID);
        await ProcessItemTradeAsync(code, item);
    }

    [Command("itemTrade")]
    [Alias("it", "item")]
    [Summary("Makes the bot trade you a Pokémon holding the requested item.")]
    public async Task ItemTrade([Summary("Trade Code")] int code, [Remainder] string item)
    {
        var userID = Context.User.Id;
        if (!await Helpers<T>.EnsureUserNotInQueueAsync(userID))
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context,
                "You already have an existing trade in the queue that cannot be cleared. Please wait until it is processed.", 2);
            return;
        }

        await ProcessItemTradeAsync(code, item);
    }

    private async Task ProcessItemTradeAsync(int code, string item)
    {
        Species species = Info.Hub.Config.TradeSystem.Settings.TradeConfiguration.ItemTradeSpecies == Species.None
            ? Species.Diglett
            : Info.Hub.Config.TradeSystem.Settings.TradeConfiguration.ItemTradeSpecies;

        var set = new ShowdownSet($"{SpeciesName.GetSpeciesNameGeneration((ushort)species, 2, 8)} @ {item.Trim()}");
        var template = AutoLegalityWrapper.GetTemplate(set);
        var sav = AutoLegalityWrapper.GetTrainerInfo<T>();
        var pkm = sav.GetLegal(template, out var result);

        if (pkm == null)
        {
            await ReplyAsync("Set took too long to legalize.");
            return;
        }

        pkm = EntityConverter.ConvertToType(pkm, typeof(T), out _) ?? pkm;

        if (pkm.HeldItem == 0)
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context, $"{Context.User.Username}, the item you entered wasn't recognized.", 2);
            return;
        }

        var la = new LegalityAnalysis(pkm);
        if (pkm is not T pk || !la.Valid)
        {
            var reason = result == "Timeout" ? "That set took too long to generate." : "I wasn't able to create something from that.";
            var imsg = $"Oops! {reason} Here's my best attempt for that {species}!";
            await Context.Channel.SendPKMAsync(pkm, imsg).ConfigureAwait(false);
            return;
        }

        pk.ResetPartyStats();
        var sig = Context.User.GetFavor();
        await QueueHelper<T>.AddToQueueAsync(Context, code, Context.User.Username, sig, pk,
            PokeRoutineType.LinkTrade, PokeTradeType.Specific).ConfigureAwait(false);

        if (Context.Message is IUserMessage userMessage)
            _ = Helpers<T>.DeleteMessagesAfterDelayAsync(userMessage, null, 2);
    }

    #endregion

    #region List Commands

    [Command("tradeList")]
    [Alias("tl")]
    [Summary("Prints the users in the trade queues.")]
    [RequireSudo]
    public async Task GetTradeListAsync()
    {
        string msg = Info.GetTradeList(PokeRoutineType.LinkTrade);
        var embed = new EmbedBuilder();
        embed.AddField(x =>
        {
            x.Name = "Pending Trades";
            x.Value = msg;
            x.IsInline = false;
        });
        await ReplyAsync("These are the users who are currently waiting:", embed: embed.Build()).ConfigureAwait(false);
    }

    [Command("fixOTList")]
    [Alias("fl", "fq")]
    [Summary("Prints the users in the FixOT queue.")]
    [RequireSudo]
    public async Task GetFixListAsync()
    {
        string msg = Info.GetTradeList(PokeRoutineType.FixOT);
        var embed = new EmbedBuilder();
        embed.AddField(x =>
        {
            x.Name = "Pending Trades";
            x.Value = msg;
            x.IsInline = false;
        });
        await ReplyAsync("These are the users who are currently waiting:", embed: embed.Build()).ConfigureAwait(false);
    }

    [Command("listevents")]
    [Alias("le")]
    [Summary("Lists available event files, filtered by a specific letter or substring, and sends the list via DM.")]
    public Task ListEventsAsync([Remainder] string args = "")
        => ListHelpers<T>.HandleListCommandAsync(
            Context,
            SysCord<T>.Runner.Config.TradeSystem.Settings.RequestFolderSettings.EventsFolder,
            "events",
            "er",
            args
        );

    [Command("battlereadylist")]
    [Alias("brl")]
    [Summary("Lists available battle-ready files, filtered by a specific letter or substring, and sends the list via DM.")]
    public Task BattleReadyListAsync([Remainder] string args = "")
        => ListHelpers<T>.HandleListCommandAsync(
            Context,
            SysCord<T>.Runner.Config.TradeSystem.Settings.RequestFolderSettings.BattleReadyPKMFolder,
            "battle-ready files",
            "brr",
            args
        );

    #endregion

    #region Request Commands

    [Command("eventrequest")]
    [Alias("er")]
    [Summary("Downloads event attachments from the specified EventsFolder and adds to trade queue.")]
    [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
    public Task EventRequestAsync(int index)
        => ListHelpers<T>.HandleRequestCommandAsync(
            Context,
            SysCord<T>.Runner.Config.TradeSystem.Settings.RequestFolderSettings.EventsFolder,
            index,
            "event",
            "le"
        );

    [Command("battlereadyrequest")]
    [Alias("brr", "br")]
    [Summary("Downloads battle-ready attachments from the specified folder and adds to trade queue.")]
    [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
    public Task BattleReadyRequestAsync(int index)
        => ListHelpers<T>.HandleRequestCommandAsync(
            Context,
            SysCord<T>.Runner.Config.TradeSystem.Settings.RequestFolderSettings.BattleReadyPKMFolder,
            index,
            "battle-ready file",
            "brl"
        );

    #endregion

    #region Batch Trades

    [Command("batchTrade")]
    [Alias("bt")]
    [Summary("Makes the bot trade multiple Pokémon from the provided list or attachments, up to a maximum configured number.")]
    [RequireQueueRole(nameof(DiscordManager.RolesTrade))]
    public async Task BatchTradeAsync([Summary("List of Showdown Sets separated by '---'")][Remainder] string content = "")
    {
        // Batch trades are not supported for LGPE
        if (typeof(T) == typeof(PB7))
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context,
                "Batch trades are not supported for Let's Go Pikachu/Eevee.", 5);
            return;
        }

        var tradeConfig = SysCord<T>.Runner.Config.TradeSystem.Settings.TradeConfiguration;

        // Check if batch trades are allowed
        if (!tradeConfig.AllowBatchTrades)
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context,
                "Batch trades are currently disabled by the bot administrator.", 2);
            return;
        }

        var userID = Context.User.Id;
        if (!await Helpers<T>.EnsureUserNotInQueueAsync(userID))
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context,
                "You already have an existing trade in the queue that cannot be cleared. Please wait until it is processed.", 2);
            return;
        }

        content = ReusableActions.StripCodeBlock(content);
        var trades = BatchHelpers<T>.ParseBatchTradeContent(content);
        var attachments = Context.Message.Attachments;
        int totalTrades = trades.Count + attachments.Count;

        if (totalTrades == 0)
        {
             await Helpers<T>.ReplyAndDeleteAsync(Context, "Please provide Showdown sets or attach files for batch trading.", 5);
             return;
        }

        // Use configured max trades per batch, default to 4 if less than 1
        int maxTradesAllowed = tradeConfig.MaxPkmsPerTrade > 0 ? tradeConfig.MaxPkmsPerTrade : 4;

        if (totalTrades > maxTradesAllowed)
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context,
                $"You can only process up to {maxTradesAllowed} trades at a time. Please reduce the number of trades in your batch.", 5);
            return;
        }

        var processingMessage = await Context.Channel.SendMessageAsync($"{Context.User.Mention} Processing your batch trade with {totalTrades} Pokémon...");

        _ = Task.Run(async () =>
        {
            try
            {
                var batchPokemonList = new List<T>();
                var errors = new List<BatchTradeError>();

                // Process Attachments first
                foreach (var attachment in attachments)
                {
                    try
                    {
                        var dl = await NetUtil.DownloadPKMAsync(attachment).ConfigureAwait(false);
                        var pk = Helpers<T>.GetRequest(dl);
                        int currentTradeNumber = batchPokemonList.Count + errors.Count + 1;

                        if (pk != null)
                        {
                            // Basic validation for attachment
                            if (!pk.CanBeTraded())
                            {
                                errors.Add(new BatchTradeError 
                                { 
                                    TradeNumber = currentTradeNumber, 
                                    SpeciesName = SpeciesName.GetSpeciesName(pk.Species, 2),
                                    ErrorMessage = "Pokémon cannot be traded.", 
                                    ShowdownSet = attachment.Filename 
                                });
                                continue;
                            }

                            // Legality Check
                            var la = new LegalityAnalysis(pk);
                            if (!la.Valid)
                            {
                                errors.Add(new BatchTradeError 
                                { 
                                    TradeNumber = currentTradeNumber, 
                                    SpeciesName = SpeciesName.GetSpeciesName(pk.Species, 2),
                                    ErrorMessage = "Illegal Pokémon", 
                                    LegalizationHint = la.Report(), 
                                    ShowdownSet = attachment.Filename 
                                });
                                continue;
                            }

                            // Prepare the Pokemon (similar to Helpers.PrepareForTrade but for attachments we assume they are mostly ready)
                            // However, we might need to set Handler/Language if it was a file from elsewhere.
                            // Helpers<T>.AddTradeToQueueAsync handles some of this, but here we are bypassing it until the end.
                            // We should probably rely on standard preparation if needed, but for now assuming valid PKM file.
                            
                            batchPokemonList.Add(pk);
                        }
                        else
                        {
                            errors.Add(new BatchTradeError 
                            { 
                                TradeNumber = currentTradeNumber, 
                                SpeciesName = "File",
                                ErrorMessage = "Invalid attachment file.", 
                                ShowdownSet = attachment.Filename 
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add(new BatchTradeError 
                        { 
                            TradeNumber = batchPokemonList.Count + errors.Count + 1, 
                            SpeciesName = "File",
                            ErrorMessage = $"Error processing file: {ex.Message}", 
                            ShowdownSet = attachment.Filename 
                        });
                    }
                }

                // Process Text Trades
                for (int i = 0; i < trades.Count; i++)
                {
                    var (pk, error, set, legalizationHint) = await BatchHelpers<T>.ProcessSingleTradeForBatch(trades[i]);
                    if (pk != null)
                    {
                        batchPokemonList.Add(pk);
                    }
                    else
                    {
                        var speciesName = set != null && set.Species > 0
                            ? GameInfo.Strings.Species[set.Species]
                            : "Unknown";
                        errors.Add(new BatchTradeError
                        {
                            TradeNumber = batchPokemonList.Count + errors.Count + 1,
                            SpeciesName = speciesName,
                            ErrorMessage = error ?? "Unknown error",
                            LegalizationHint = legalizationHint,
                            ShowdownSet = set != null ? string.Join("\n", set.GetSetLines()) : trades[i]
                        });
                    }
                }

                await processingMessage.DeleteAsync();

                if (errors.Count > 0)
                {
                    static string Truncate(string value, int maxLen)
                    {
                        if (string.IsNullOrEmpty(value) || value.Length <= maxLen)
                            return value;
                        return value[..(maxLen - 3)] + "...";
                    }

                    var location = $"{Context.Guild?.Name ?? "DM"} #{Context.Channel.Name} ({Context.Channel.Id})";
                    var header = $"Batch trade errors | User: {Context.User.Username} ({Context.User.Id}) | Location: {location} | Trades: {totalTrades}";
                    var details = string.Join(
                        "\n\n",
                        errors.Take(5).Select(e =>
                            $"Trade {e.TradeNumber}: {e.SpeciesName}\nError: {e.ErrorMessage}\nHint: {e.LegalizationHint ?? "(none)"}\nShowdown/File: {Truncate(e.ShowdownSet, 500)}")
                    );
                    LogUtil.LogError($"{header}\n\n{Truncate(details, 3500)}", nameof(TradeModule<T>));

                    await BatchHelpers<T>.SendBatchErrorEmbedAsync(Context, errors, totalTrades);
                    return;
                }
                if (batchPokemonList.Count > 0)
                {
                    var batchTradeCode = Info.GetRandomTradeCode(userID);
                    await BatchHelpers<T>.ProcessBatchContainer(Context, batchPokemonList, batchTradeCode, totalTrades);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    await processingMessage.DeleteAsync();
                }
                catch { }

                await Context.Channel.SendMessageAsync($"{Context.User.Mention} An error occurred while processing your batch trade. Please try again.");
                Base.LogUtil.LogError($"Batch trade processing error: {ex.Message}", nameof(BatchTradeAsync));
            }
        });

        if (Context.Message is IUserMessage userMessage)
            _ = Helpers<T>.DeleteMessagesAfterDelayAsync(userMessage, null, 2);
    }

    #endregion

    #region Private Helper Methods

    private async Task ProcessTradeAsync(int code, string content, bool isHiddenTrade = false)
    {
        var userID = Context.User.Id;
        if (!await Helpers<T>.EnsureUserNotInQueueAsync(userID))
        {
            await Helpers<T>.ReplyAndDeleteAsync(Context,
                "You already have an existing trade in the queue that cannot be cleared. Please wait until it is processed.", 2);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                static string Truncate(string value, int maxLen)
                {
                    if (string.IsNullOrEmpty(value) || value.Length <= maxLen)
                        return value;
                    return value[..(maxLen - 3)] + "...";
                }

                // Detect custom trainer info BEFORE generating the Pokemon
                var ignoreAutoOT = content.Contains("OT:") || content.Contains("TID:") || content.Contains("SID:");

                var result = await Helpers<T>.ProcessShowdownSetAsync(content, ignoreAutoOT);

                if (result.Pokemon == null)
                {
                    var error = result.Error ?? "Unknown";
                    if (!error.StartsWith("Unable to parse Showdown", StringComparison.OrdinalIgnoreCase))
                    {
                        var location = $"{Context.Guild?.Name ?? "DM"} #{Context.Channel.Name} ({Context.Channel.Id})";
                        var request = $"User: {Context.User.Username} ({Context.User.Id}) | Code: {code:0000 0000} | Location: {location}";
                        var body = $"Reason: {error}\n\nShowdown:\n{Truncate(content, 1500)}";
                        LogUtil.LogError($"{request}\n{body}", nameof(TradeModule<T>));
                    }

                    await Helpers<T>.SendTradeErrorEmbedAsync(Context, result);
                    return;
                }

                var sig = Context.User.GetFavor();

                await Helpers<T>.AddTradeToQueueAsync(
                    Context, code, Context.User.Username, result.Pokemon, sig, Context.User,
                    isHiddenTrade: isHiddenTrade,
                    lgcode: result.LgCode,
                    ignoreAutoOT: ignoreAutoOT,
                    isNonNative: result.IsNonNative
                );
            }
            catch (Exception ex)
            {
                LogUtil.LogSafe(ex, nameof(TradeModule<T>));
                var msg = "Oops! An unexpected problem happened with this Showdown Set.";
                await Helpers<T>.ReplyAndDeleteAsync(Context, msg, 2);
            }
        });

        if (Context.Message is IUserMessage userMessage)
            _ = Helpers<T>.DeleteMessagesAfterDelayAsync(userMessage, null, isHiddenTrade ? 0 : 2);
    }

    private async Task ProcessTradeAttachmentAsync(int code, RequestSignificance sig, SocketUser user, bool isHiddenTrade = false, bool ignoreAutoOT = false)
    {
        var pk = await Helpers<T>.ProcessTradeAttachmentAsync(Context);
        if (pk == null)
            return;

        await Helpers<T>.AddTradeToQueueAsync(Context, code, user.Username, pk, sig, user,
            isHiddenTrade: isHiddenTrade, ignoreAutoOT: ignoreAutoOT);
    }

    #endregion
}


