using Discord;
using Discord.Commands;
using Discord.Net;
using Discord.WebSocket;
using PKHeX.Core;
using PKHeX.Core.AutoMod;
using PKHeX.Drawing.PokeSprite;
using SysBot.Pokemon.Discord.Commands.Bots;
using SysBot.Pokemon.Helpers;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Color = System.Drawing.Color;
using DiscordColor = Discord.Color;

namespace SysBot.Pokemon.Discord;

public static class QueueHelper<T> where T : PKM, new()
{
    private const uint MaxTradeCode = 9999_9999;

    private const string MedalSpriteBaseUrl = "https://raw.githubusercontent.com/NexusRisen/Nexus-Risen-Edition-Sprite-Images/main";

    private static string GetMilestoneDescription(int tradeCount)
    {
        return tradeCount switch
        {
            1 => "Congratulations on your first trade!\n**Status:** Newbie Trainer.",
            50 => "You've reached 50 trades!\n**Status:** Novice Trainer.",
            100 => "You've reached 100 trades!\n**Status:** Pokémon Professor.",
            150 => "You've reached 150 trades!\n**Status:** Pokémon Specialist.",
            200 => "You've reached 200 trades!\n**Status:** Pokémon Champion.",
            250 => "You've reached 250 trades!\n**Status:** Pokémon Hero.",
            300 => "You've reached 300 trades!\n**Status:** Pokémon Elite.",
            350 => "You've reached 350 trades!\n**Status:** Pokémon Trader.",
            400 => "You've reached 400 trades!\n**Status:** Pokémon Sage.",
            450 => "You've reached 450 trades!\n**Status:** Pokémon Legend.",
            500 => "You've reached 500 trades!\n**Status:** Region Master.",
            550 => "You've reached 550 trades!\n**Status:** Trade Master.",
            600 => "You've reached 600 trades!\n**Status:** World Famous.",
            650 => "You've reached 650 trades!\n**Status:** Pokémon Master.",
            700 => "You've reached 700 trades!\n**Status:** Pokémon God.",
            _ => $"Congratulations on reaching {tradeCount} trades! Keep it going!"
        };
    }

    private static string? GetMilestoneImageUrl(int tradeCount)
    {
        return tradeCount switch
        {
            1 or 50 or 100 or 150 or 200 or 250 or 300 or 350 or 400 or 450 or 500 or 550 or 600 or 650 or 700
                => $"{MedalSpriteBaseUrl}/{tradeCount:D3}.png",
            _ => null
        };
    }

        public static async Task AddToQueueAsync(SocketCommandContext context, int code, string trainer, RequestSignificance sig, T trade, PokeRoutineType routine, PokeTradeType type, SocketUser trader, int batchTradeNumber = 1, int totalBatchTrades = 1, bool isHiddenTrade = false, bool isMysteryEgg = false, List<Pictocodes>? lgcode = null, bool ignoreAutoOT = false, bool setEdited = false, bool isNonNative = false)
    {
        if ((uint)code > MaxTradeCode)
        {
            await context.Channel.SendMessageAsync("Trade code should be 00000000-99999999!").ConfigureAwait(false);
            return;
        }

        try
        {
            // Only send trade code for non-batch trades (batch container will handle its own)
            // if (!isBatchTrade) - Always true now since this method is for single trades
            {
                if (trade is PB7 && lgcode != null)
                {
                    var (thefile, lgcodeembed) = CreateLGLinkCodeSpriteEmbed(lgcode);
                    await trader.SendFileAsync(thefile, "Your trade code will be.", embed: lgcodeembed).ConfigureAwait(false);
                }
                else
                {
                    await EmbedHelper.SendTradeCodeEmbedAsync(trader, code).ConfigureAwait(false);
                }
            }

            var result = await AddToTradeQueue(context, trade, code, trainer, sig, routine, type, trader, batchTradeNumber, totalBatchTrades, isHiddenTrade, isMysteryEgg, lgcode, ignoreAutoOT, setEdited, isNonNative).ConfigureAwait(false);
        }
        catch (HttpException ex)
        {
            await HandleDiscordExceptionAsync(context, trader, ex).ConfigureAwait(false);
        }
    }

    public static Task AddToQueueAsync(SocketCommandContext context, int code, string trainer, RequestSignificance sig, T trade, PokeRoutineType routine, PokeTradeType type, bool ignoreAutoOT = false)
    {
        return AddToQueueAsync(context, code, trainer, sig, trade, routine, type, context.User, ignoreAutoOT: ignoreAutoOT);
    }

    private static async Task<TradeQueueResult> AddToTradeQueue(SocketCommandContext context, T pk, int code, string trainerName,
        RequestSignificance sig, PokeRoutineType type, PokeTradeType t, SocketUser trader,
        int batchTradeNumber, int totalBatchTrades, bool isHiddenTrade, bool isMysteryEgg = false,
        List<Pictocodes>? lgcode = null, bool ignoreAutoOT = false, bool setEdited = false, bool isNonNative = false)
    {
        var hub = SysCord<T>.Runner.Hub;
        var abuse = hub.Config.TradeSystem.Abuse;
        if (abuse.BannedRemoteUsers.Contains(trader.Id))
        {
            await context.Channel.SendMessageAsync($"{trader.Mention} - You are banned from trading.").ConfigureAwait(false);
            return new TradeQueueResult(false);
        }
        // Note: This method should only be called for individual trades now
        // Batch trades use AddBatchContainerToQueueAsync

        var user = trader;
        var userID = user.Id;
        var name = user.Username;
        var trainer = new PokeTradeTrainerInfo(trainerName, userID);
        var notifier = new DiscordTradeNotifier<T>(pk, trainer, code, trader, batchTradeNumber, totalBatchTrades,
            isMysteryEgg, lgcode: lgcode!);

        int uniqueTradeID = GenerateUniqueTradeID();

        var detail = new PokeTradeDetail<T>(pk, trainer, notifier, t, code, sig == RequestSignificance.Favored,
            lgcode, batchTradeNumber, totalBatchTrades, isMysteryEgg, uniqueTradeID, ignoreAutoOT, setEdited);

        var trade = new TradeEntry<T>(detail, userID, PokeRoutineType.LinkTrade, name, uniqueTradeID);
        var Info = hub.Queues.Info;
        var isSudo = sig == RequestSignificance.Owner;
        var added = Info.AddToTradeQueue(trade, userID, false, isSudo);

        // Start queue position updates for Discord notification
        if (added != QueueResultAdd.AlreadyInQueue && added != QueueResultAdd.NotAllowedItem && notifier is DiscordTradeNotifier<T> discordNotifier)
        {
            // IMPORTANT: Update the notifier's unique trade ID to match the one used in the queue
            // Otherwise the DM will check position with the wrong ID and return incorrect results
            discordNotifier.UpdateUniqueTradeID(uniqueTradeID);
            await discordNotifier.SendInitialQueueUpdate().ConfigureAwait(false);
        }

        int totalTradeCount = 0;
        TradeCodeStorage.TradeCodeDetails? tradeDetails = null;
        if (SysCord<T>.Runner.Config.TradeSystem.Settings.TradeConfiguration.StoreTradeCodes)
        {
            var tradeCodeStorage = new TradeCodeStorage();
            totalTradeCount = tradeCodeStorage.GetTradeCount(trader.Id);
            tradeDetails = tradeCodeStorage.GetTradeDetails(trader.Id);
        }

        if (added == QueueResultAdd.AlreadyInQueue)
        {
            await context.Channel.SendMessageAsync($"{trader.Mention} - You are already in the queue!").ConfigureAwait(false);
            return new TradeQueueResult(false);
        }

        if (added == QueueResultAdd.QueueFull)
        {
            var maxCount = SysCord<T>.Runner.Config.TradeSystem.Queues.MaxQueueCount;
            var embed = new EmbedBuilder()
                .WithColor(DiscordColor.Red)
                .WithTitle("🚫 Queue Full")
                .WithDescription($"The queue is currently full ({maxCount}/{maxCount}). Please try again later when space becomes available.")
                .WithFooter("Queue will open up as trades are completed")
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            await context.Channel.SendMessageAsync(embed: embed).ConfigureAwait(false);
            return new TradeQueueResult(false);
        }

        if (added == QueueResultAdd.NotAllowedItem)
        {
            var held = pk.HeldItem;
            var itemName = held > 0 ? PKHeX.Core.GameInfo.GetStrings("en").Item[held] : "(none)";
            await context.Channel.SendMessageAsync($"{trader.Mention} - Trade blocked: the held item '{itemName}' cannot be traded in PLZA.").ConfigureAwait(false);
            return new TradeQueueResult(false);
        }

        var embedData = DetailsExtractor<T>.ExtractPokemonDetails(
            pk, trader, isMysteryEgg, type == PokeRoutineType.Clone, type == PokeRoutineType.Dump,
            type == PokeRoutineType.FixOT, type == PokeRoutineType.SeedCheck, false, 1, 1
        );

        try
        {
            (string embedImageUrl, DiscordColor embedColor) = await PrepareEmbedDetails(pk);
            embedData.EmbedImageUrl = embedImageUrl;

            embedData.HeldItemUrl = string.Empty;
            if (!string.IsNullOrWhiteSpace(embedData.HeldItem))
            {
                string heldItemName = embedData.HeldItem.ToLower().Replace(" ", "");
                embedData.HeldItemUrl = $"https://serebii.net/itemdex/sprites/{heldItemName}.png";
            }

            embedData.IsLocalFile = File.Exists(embedData.EmbedImageUrl);

            var position = Info.CheckPosition(userID, uniqueTradeID, type);
            var botct = Info.Hub.Bots.Count;
            var baseEta = position.Position > botct ? Info.Hub.Config.TradeSystem.Queues.EstimateDelay(position.Position, botct) : 0;
            var etaMessage = $"Estimated: {baseEta:F1} min(s) for trade.";
            string footerText = $"Current Position: {(position.Position == -1 ? 1 : position.Position)}";

            string userDetailsText = DetailsExtractor<T>.GetUserDetails(totalTradeCount, tradeDetails);
            if (!string.IsNullOrEmpty(userDetailsText))
            {
                footerText += $"\n{userDetailsText}";
            }
            footerText += $"\n{etaMessage}";

            var embedBuilder = new EmbedBuilder()
                .WithColor(embedColor)
                .WithImageUrl(embedData.IsLocalFile ? $"attachment://{Path.GetFileName(embedData.EmbedImageUrl)}" : embedData.EmbedImageUrl)
                .WithFooter(footerText)
                .WithAuthor(new EmbedAuthorBuilder()
                    .WithName(embedData.AuthorName)
                    .WithIconUrl(trader.GetAvatarUrl() ?? trader.GetDefaultAvatarUrl()));

            DetailsExtractor<T>.AddAdditionalText(embedBuilder);

            if (!isMysteryEgg && type != PokeRoutineType.Clone && type != PokeRoutineType.Dump && type != PokeRoutineType.FixOT && type != PokeRoutineType.SeedCheck)
            {
                DetailsExtractor<T>.AddNormalTradeFields(embedBuilder, embedData, trader.Mention, pk);
            }
            else
            {
                DetailsExtractor<T>.AddSpecialTradeFields(embedBuilder, isMysteryEgg, type == PokeRoutineType.SeedCheck, type == PokeRoutineType.Clone, type == PokeRoutineType.FixOT, trader.Mention);
            }

            // Check if the Pokemon is Non-Native and/or has a Home Tracker
            if (pk is IHomeTrack homeTrack)
            {
                if (homeTrack.HasTracker && isNonNative)
                    embedBuilder.AddField("**__Notice__**: **This Pokemon is Non-Native & Has Home Tracker.**", "*AutoOT not applied.*");
                else if (homeTrack.HasTracker)
                    embedBuilder.AddField("**__Notice__**: **Home Tracker Detected.**", "*AutoOT not applied.*");
                else if (isNonNative)
                    embedBuilder.AddField("**__Notice__**: **This Pokemon is Non-Native.**", "*Cannot enter HOME & AutoOT not applied.*");
            }
            else if (isNonNative)
            {
                embedBuilder.AddField("**__Notice__**: **This Pokemon is Non-Native.**", "*Cannot enter HOME & AutoOT not applied.*");
            }

            DetailsExtractor<T>.AddThumbnails(embedBuilder, type == PokeRoutineType.Clone, type == PokeRoutineType.SeedCheck, embedData.HeldItemUrl);

            if (!isHiddenTrade && SysCord<T>.Runner.Config.TradeSystem.Settings.TradeEmbedSettings.UseEmbeds)
            {
                var embed = embedBuilder.Build();
                if (embed == null)
                {
                    Console.WriteLine("Error: Embed is null.");
                    await context.Channel.SendMessageAsync("An error occurred while preparing the trade details.");
                    return new TradeQueueResult(false);
                }

                await context.Channel.SendMessageAsync(embed: embed);
            }
            else
            {
                var message = $"{trader.Mention} - Added to the LinkTrade queue. Current Position: {position.Position}. Receiving: {embedData.SpeciesName}.\n{etaMessage}";
                await context.Channel.SendMessageAsync(message);
            }
        }
        catch (HttpException ex)
        {
            await HandleDiscordExceptionAsync(context, trader, ex);
            return new TradeQueueResult(false);
        }

        if (SysCord<T>.Runner.Hub.Config.TradeSystem.Settings.TradeConfiguration.StoreTradeCodes)
        {
            var tradeCodeStorage = new TradeCodeStorage();
            int tradeCount = tradeCodeStorage.GetTradeCount(trader.Id);
            _ = SendMilestoneEmbed(tradeCount, context.Channel, trader);
        }

        return new TradeQueueResult(true);
    }

    public static async Task AddBatchContainerToQueueAsync(SocketCommandContext context, int code, string trainer, T firstTrade, List<T> allTrades, RequestSignificance sig, SocketUser trader, int totalBatchTrades)
    {
        var userID = trader.Id;
        var name = trader.Username;
        var trainer_info = new PokeTradeTrainerInfo(trainer, userID);
        var notifier = new DiscordTradeNotifier<T>(firstTrade, trainer_info, code, trader, 1, totalBatchTrades, false, lgcode: []);

        int uniqueTradeID = GenerateUniqueTradeID();

        var detail = new PokeTradeDetail<T>(firstTrade, trainer_info, notifier, PokeTradeType.Batch, code,
            sig == RequestSignificance.Favored, null, 1, totalBatchTrades, false, uniqueTradeID)
        {
            BatchTrades = allTrades
        };

        var trade = new TradeEntry<T>(detail, userID, PokeRoutineType.Batch, name, uniqueTradeID);
        var hub = SysCord<T>.Runner.Hub;
        var Info = hub.Queues.Info;
        var added = Info.AddToTradeQueue(trade, userID, false, sig == RequestSignificance.Owner);

        // Send trade code once
        await EmbedHelper.SendTradeCodeEmbedAsync(trader, code).ConfigureAwait(false);

        // Start queue position updates for Discord notification
        if (added != QueueResultAdd.AlreadyInQueue && added != QueueResultAdd.NotAllowedItem && notifier is DiscordTradeNotifier<T> discordNotifier)
        {
            // IMPORTANT: Update the notifier's unique trade ID to match the one used in the queue
            // Otherwise the DM will check position with the wrong ID and return incorrect results
            discordNotifier.UpdateUniqueTradeID(uniqueTradeID);
            await discordNotifier.SendInitialQueueUpdate().ConfigureAwait(false);
        }

        // Handle the display
        if (added == QueueResultAdd.AlreadyInQueue)
        {
            await context.Channel.SendMessageAsync($"{trader.Mention} - You are already in the queue!").ConfigureAwait(false);
            return;
        }

        if (added == QueueResultAdd.QueueFull)
        {
            var maxCount = SysCord<T>.Runner.Config.TradeSystem.Queues.MaxQueueCount;
            var embed = new EmbedBuilder()
                .WithColor(DiscordColor.Red)
                .WithTitle("🚫 Queue Full")
                .WithDescription($"The queue is currently full ({maxCount}/{maxCount}). Please try again later when space becomes available.")
                .WithFooter("Queue will open up as trades are completed")
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            await context.Channel.SendMessageAsync(embed: embed).ConfigureAwait(false);
            return;
        }

        if (added == QueueResultAdd.NotAllowedItem)
        {
            var held = firstTrade.HeldItem;
            var itemName = held > 0 ? PKHeX.Core.GameInfo.GetStrings("en").Item[held] : "(none)";
            await context.Channel.SendMessageAsync($"{trader.Mention} - Trade blocked: the held item '{itemName}' cannot be traded in PLZA.").ConfigureAwait(false);
            return;
        }

        var position = Info.CheckPosition(userID, uniqueTradeID, PokeRoutineType.Batch);
        var botct = Info.Hub.Bots.Count;
        var baseEta = position.Position > botct ? Info.Hub.Config.TradeSystem.Queues.EstimateDelay(position.Position, botct) : 0;

        // Get user trade details for footer
        int totalTradeCount = 0;
        TradeCodeStorage.TradeCodeDetails? tradeDetails = null;
        if (SysCord<T>.Runner.Config.TradeSystem.Settings.TradeConfiguration.StoreTradeCodes)
        {
            var tradeCodeStorage = new TradeCodeStorage();
            totalTradeCount = tradeCodeStorage.GetTradeCount(trader.Id);
            tradeDetails = tradeCodeStorage.GetTradeDetails(trader.Id);
        }

        // Send initial batch summary message
        await context.Channel.SendMessageAsync($"{trader.Mention} - Added batch trade with {totalBatchTrades} Pokémon to the queue! Position: {position.Position}. Estimated: {baseEta:F1} min(s).").ConfigureAwait(false);

        // Create and send embeds for each Pokémon in the batch
        if (SysCord<T>.Runner.Config.TradeSystem.Settings.TradeEmbedSettings.UseEmbeds)
        {
            for (int i = 0; i < allTrades.Count; i++)
            {
                var pk = allTrades[i];
                var batchTradeNumber = i + 1;

                // Extract details for this Pokémon
                var embedData = DetailsExtractor<T>.ExtractPokemonDetails(
                    pk, trader, false, false, false, false, false, true, batchTradeNumber, totalBatchTrades
                );

                try
                {
                    // Prepare embed details
                    (string embedImageUrl, DiscordColor embedColor) = await PrepareEmbedDetails(pk);

                    embedData.EmbedImageUrl = embedImageUrl;
                    embedData.HeldItemUrl = string.Empty;
                    if (!string.IsNullOrWhiteSpace(embedData.HeldItem))
                    {
                        string heldItemName = embedData.HeldItem.ToLower().Replace(" ", "");
                        embedData.HeldItemUrl = $"https://serebii.net/itemdex/sprites/{heldItemName}.png";
                    }

                    embedData.IsLocalFile = File.Exists(embedData.EmbedImageUrl);

                    // Build footer text with batch info
                    string footerText = $"Batch Trade {batchTradeNumber} of {totalBatchTrades}";
                    if (i == 0) // Only show position and ETA on first embed
                    {
                        footerText += $" | Position: {position.Position}";
                        string userDetailsText = DetailsExtractor<T>.GetUserDetails(totalTradeCount, tradeDetails);
                        if (!string.IsNullOrEmpty(userDetailsText))
                        {
                            footerText += $"\n{userDetailsText}";
                        }
                        footerText += $"\nEstimated: {baseEta:F1} min(s) for batch";
                    }

                    // Create embed
                    var embedBuilder = new EmbedBuilder()
                        .WithColor(embedColor)
                        .WithImageUrl(embedData.IsLocalFile ? $"attachment://{Path.GetFileName(embedData.EmbedImageUrl)}" : embedData.EmbedImageUrl)
                        .WithFooter(footerText)
                        .WithAuthor(new EmbedAuthorBuilder()
                            .WithName(embedData.AuthorName)
                            .WithIconUrl(trader.GetAvatarUrl() ?? trader.GetDefaultAvatarUrl()));

                    DetailsExtractor<T>.AddAdditionalText(embedBuilder);
                    DetailsExtractor<T>.AddNormalTradeFields(embedBuilder, embedData, trader.Mention, pk);

                    // Check for Non-Native and Home Tracker
                    if (pk is IHomeTrack homeTrack)
                    {
                        if (homeTrack.HasTracker)
                            embedBuilder.AddField("**__Notice__**: **Home Tracker Detected.**", "*AutoOT not applied.*");
                    }

                    DetailsExtractor<T>.AddThumbnails(embedBuilder, false, false, embedData.HeldItemUrl);

                    var embed = embedBuilder.Build();

                    // Send embed
                    if (embedData.IsLocalFile)
                    {
                        await context.Channel.SendFileAsync(embedData.EmbedImageUrl, embed: embed);
                        await ScheduleFileDeletion(embedData.EmbedImageUrl, 0);
                    }
                    else
                    {
                        await context.Channel.SendMessageAsync(embed: embed);
                    }

                    // Small delay between embeds to avoid rate limiting
                    if (i < allTrades.Count - 1)
                    {
                        await Task.Delay(500);
                    }
                }
                catch (HttpException ex)
                {
                    await HandleDiscordExceptionAsync(context, trader, ex);
                }
            }
        }

        // Send milestone embed if applicable
        if (SysCord<T>.Runner.Hub.Config.TradeSystem.Settings.TradeConfiguration.StoreTradeCodes)
        {
            var tradeCodeStorage = new TradeCodeStorage();
            int tradeCount = tradeCodeStorage.GetTradeCount(trader.Id);
            _ = SendMilestoneEmbed(tradeCount, context.Channel, trader);
        }
    }

    private static int GenerateUniqueTradeID()
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int randomValue = Random.Shared.Next(1000);
        return (int)((timestamp % int.MaxValue) * 1000 + randomValue);
    }

    private static async Task<(string, DiscordColor)> PrepareEmbedDetails(T pk)
    {
        string embedImageUrl;
        if (pk.IsEgg)
        {
            var strings = new[]
            {
                "Normal","Fighting","Flying","Poison","Ground","Rock","Bug","Ghost",
                "Steel","Fire","Water","Grass","Electric","Psychic","Ice","Dragon",
                "Dark","Fairy"
            };
            var typeIndex = pk.PersonalInfo.Type1;
            var typeName = (typeIndex >= 0 && typeIndex < strings.Length) ? strings[typeIndex] : "Normal";
            embedImageUrl = $"https://raw.githubusercontent.com/NexusRisen/HomeImages/ebd562941ff77b1889a297ee50eacfa8cb3589de/128x128/Egg_{typeName}.png";
        }
        else
        {
            bool canGmax = pk is PK8 pk8 && pk8.CanGigantamax;
            embedImageUrl = TradeExtensions<T>.PokeImg(pk, canGmax, false, SysCord<T>.Runner.Config.TradeSystem.Settings.TradeEmbedSettings.PreferredImageSize);
        }

        (int R, int G, int B) = await GetDominantColorAsync(embedImageUrl);
        return (embedImageUrl, new DiscordColor(R, G, B));
    }

    private static async Task ScheduleFileDeletion(string filePath, int delayInMilliseconds)
    {
        await Task.Delay(delayInMilliseconds);
        DeleteFile(filePath);
    }

    private static void DeleteFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Error deleting file: {ex.Message}");
            }
        }
    }

    private static async Task SendMilestoneEmbed(int tradeCount, ISocketMessageChannel channel, SocketUser user)
    {
        string? imageUrl = GetMilestoneImageUrl(tradeCount);
        if (imageUrl == null)
            return;

        var embed = EmbedHelper.CreateBuilder($"{user.Username}'s Milestone Medal", GetMilestoneDescription(tradeCount), EmbedHelper.ColorGold)
            .WithThumbnailUrl(imageUrl)
            .Build();

        await channel.SendMessageAsync(embed: embed).ConfigureAwait(false);
    }

    public static async Task<(int R, int G, int B)> GetDominantColorAsync(string imagePath)
    {
        try
        {
            Bitmap image = await LoadImageAsync(imagePath);

            var colorCount = new Dictionary<Color, int>();
#pragma warning disable CA1416 // Validate platform compatibility
            await Task.Run(() =>
            {
                for (int y = 0; y < image.Height; y++)
                {
                    for (int x = 0; x < image.Width; x++)
                    {
                        var pixelColor = image.GetPixel(x, y);

                        if (pixelColor.A < 128 || pixelColor.GetBrightness() > 0.9) continue;

                        var brightnessFactor = (int)(pixelColor.GetBrightness() * 100);
                        var saturationFactor = (int)(pixelColor.GetSaturation() * 100);
                        var combinedFactor = brightnessFactor + saturationFactor;

                        var quantizedColor = Color.FromArgb(
                            pixelColor.R / 10 * 10,
                            pixelColor.G / 10 * 10,
                            pixelColor.B / 10 * 10
                        );

                        if (colorCount.ContainsKey(quantizedColor))
                        {
                            colorCount[quantizedColor] += combinedFactor;
                        }
                        else
                        {
                            colorCount[quantizedColor] = combinedFactor;
                        }
                    }
                }
            });

            image.Dispose();
#pragma warning restore CA1416 // Validate platform compatibility

            if (colorCount.Count == 0)
                return (255, 255, 255);

            var dominantColor = colorCount.Aggregate((a, b) => a.Value > b.Value ? a : b).Key;
            return (dominantColor.R, dominantColor.G, dominantColor.B);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing image from {imagePath}. Error: {ex.Message}");
            return (255, 255, 255);
        }
    }

    private static async Task<Bitmap> LoadImageAsync(string imagePath)
    {
        if (imagePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var httpClient = new HttpClient();
            using var response = await httpClient.GetAsync(imagePath);
            await using var stream = await response.Content.ReadAsStreamAsync();
#pragma warning disable CA1416 // Validate platform compatibility
            return new Bitmap(stream);
#pragma warning restore CA1416 // Validate platform compatibility
        }
        else
        {
#pragma warning disable CA1416 // Validate platform compatibility
            return new Bitmap(imagePath);
#pragma warning restore CA1416 // Validate platform compatibility
        }
    }

    private static async Task HandleDiscordExceptionAsync(SocketCommandContext context, SocketUser trader, HttpException ex)
    {
        string message = string.Empty;
        switch (ex.DiscordCode)
        {
            case DiscordErrorCode.InsufficientPermissions or DiscordErrorCode.MissingPermissions:
                {
                    var permissions = context.Guild.CurrentUser.GetPermissions(context.Channel as IGuildChannel);
                    if (!permissions.SendMessages)
                    {
                        message = "You must grant me \"Send Messages\" permissions!";
                        Base.LogUtil.LogError("QueueHelper", message);
                        return;
                    }
                    if (!permissions.ManageMessages)
                    {
                        var app = await context.Client.GetApplicationInfoAsync().ConfigureAwait(false);
                        var owner = app.Owner.Id;
                        message = $"<@{owner}> You must grant me \"Manage Messages\" permissions!";
                    }
                }
                break;

            case DiscordErrorCode.CannotSendMessageToUser:
                {
                    message = context.User == trader ? "You must enable private messages in order to be queued!" : "The mentioned user must enable private messages in order for them to be queued!";
                }
                break;

            default:
                {
                    message = ex.DiscordCode != null ? $"Discord error {(int)ex.DiscordCode}: {ex.Reason}" : $"Http error {(int)ex.HttpCode}: {ex.Message}";
                }
                break;
        }
        await context.Channel.SendMessageAsync(message).ConfigureAwait(false);
    }

    // Removed external egg image dependency

    public static (string, Embed) CreateLGLinkCodeSpriteEmbed(List<Pictocodes> lgcode)
    {
        int codecount = 0;
        List<System.Drawing.Image> spritearray = [];
        foreach (Pictocodes cd in lgcode)
        {
            var showdown = new ShowdownSet(cd.ToString());
            var sav = BlankSaveFile.Get(EntityContext.Gen7b, "pip");
            PKM pk = sav.GetLegalFromSet(showdown).Created;
#pragma warning disable CA1416 // Validate platform compatibility
            System.Drawing.Image png = pk.Sprite();
            var destRect = new Rectangle(-40, -65, 137, 130);
            var destImage = new Bitmap(137, 130);

            destImage.SetResolution(png.HorizontalResolution, png.VerticalResolution);

            using (var graphics = Graphics.FromImage(destImage))
            {
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(png, destRect, 0, 0, png.Width, png.Height, GraphicsUnit.Pixel);
            }
            png = destImage;
            spritearray.Add(png);
#pragma warning restore CA1416 // Validate platform compatibility
            codecount++;
        }

#pragma warning disable CA1416 // Validate platform compatibility
        int outputImageWidth = spritearray[0].Width + 20;
        int outputImageHeight = spritearray[0].Height - 65;

        Bitmap outputImage = new(outputImageWidth, outputImageHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using (Graphics graphics = Graphics.FromImage(outputImage))
        {
            graphics.DrawImage(spritearray[0], new Rectangle(0, 0, spritearray[0].Width, spritearray[0].Height),
                new Rectangle(new Point(), spritearray[0].Size), GraphicsUnit.Pixel);
            graphics.DrawImage(spritearray[1], new Rectangle(50, 0, spritearray[1].Width, spritearray[1].Height),
                new Rectangle(new Point(), spritearray[1].Size), GraphicsUnit.Pixel);
            graphics.DrawImage(spritearray[2], new Rectangle(100, 0, spritearray[2].Width, spritearray[2].Height),
                new Rectangle(new Point(), spritearray[2].Size), GraphicsUnit.Pixel);
        }

        System.Drawing.Image finalembedpic = outputImage;
        var filename = $"{Directory.GetCurrentDirectory()}//finalcode.png";
        finalembedpic.Save(filename);
#pragma warning restore CA1416 // Validate platform compatibility

        filename = Path.GetFileName($"{Directory.GetCurrentDirectory()}//finalcode.png");
        Embed returnembed = new EmbedBuilder().WithTitle($"{lgcode[0]}, {lgcode[1]}, {lgcode[2]}").WithImageUrl($"attachment://{filename}").Build();
        return (filename, returnembed);
    }
}
