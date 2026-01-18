using Discord;
using PKHeX.Core;
using SysBot.Pokemon.Helpers;
using System;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord;

public static class EmbedHelper
{
    // NexusRisen Theme Colors
    private static readonly Color ColorPrimary = new Color(43, 45, 49);     // Dark Gray (Discord-like)
    private static readonly Color ColorAccent = new Color(88, 101, 242);    // Blurple
    private static readonly Color ColorSuccess = new Color(87, 242, 135);   // Green
    private static readonly Color ColorDanger = new Color(237, 66, 69);     // Red
    private static readonly Color ColorWarning = new Color(254, 231, 92);   // Yellow
    private static readonly Color ColorInfo = new Color(88, 101, 242);      // Blue/Blurple

    // Common Footer
    private static readonly EmbedFooterBuilder Footer = new EmbedFooterBuilder()
        .WithText("Powered by SysBots.NET - Nexus Risen Edition")
        .WithIconUrl("https://raw.githubusercontent.com/NexusRisen/sprites/main/pokeball.png");

    public static async Task SendNotificationEmbedAsync(IUser user, string message)
    {
        var embed = new EmbedBuilder()
            .WithTitle("📢 Notification")
            .WithDescription(message)
            .WithTimestamp(DateTimeOffset.Now)
            .WithColor(ColorInfo)
            .WithFooter(Footer)
            .Build();

        await user.SendMessageAsync(embed: embed).ConfigureAwait(false);
    }

    public static async Task SendTradeCodeEmbedAsync(IUser user, int code)
    {
        var embed = new EmbedBuilder()
            .WithTitle("🔄 Ready to Trade!")
            .WithDescription($"Please enter the following Link Code:\n# {code:0000 0000}\n\n**Enter this code in your game, but DO NOT search yet.**")
            .WithTimestamp(DateTimeOffset.Now)
            .WithThumbnailUrl("https://raw.githubusercontent.com/NexusRisen/sprites/main/tradecode.gif")
            .WithColor(ColorAccent)
            .WithFooter(Footer)
            .Build();

        await user.SendMessageAsync(embed: embed).ConfigureAwait(false);
    }

    public static async Task SendTradeFinishedEmbedAsync<T>(IUser user, string message, T pk, bool isMysteryEgg)
        where T : PKM, new()
    {
        string title = "✅ Trade Completed";
        if (isMysteryEgg)
            title = "🥚 Mystery Egg Sent!";

        string? thumbUrl = null;
        if (isMysteryEgg)
        {
            thumbUrl = "https://raw.githubusercontent.com/NexusRisen/HomeImages/master/128x128/Egg_Normal.png";
        }
        else
        {
            bool canGmax = pk is PK8 pk8 && pk8.CanGigantamax;
            thumbUrl = TradeExtensions<T>.PokeImg(pk, canGmax, false, null);
        }

        var embed = new EmbedBuilder()
            .WithTitle(title)
            .WithDescription(message)
            .WithTimestamp(DateTimeOffset.Now)
            .WithColor(ColorSuccess)
            .WithThumbnailUrl(thumbUrl)
            .WithFooter(Footer)
            .Build();

        await user.SendMessageAsync(embed: embed).ConfigureAwait(false);
    }

    public static async Task SendTradeInitializingEmbedAsync(IUser user, string speciesName, int code, bool isMysteryEgg, string? imageUrl = null, string? message = null, PKM? pkm = null, bool showMoves = true)
    {
        if (isMysteryEgg)
        {
            speciesName = "**Mystery Egg**";
            imageUrl ??= "https://raw.githubusercontent.com/NexusRisen/sprites/main/mysteryegg3.png";
        }

        var description = $"# {code:0000 0000}\n";

        if (pkm != null && !isMysteryEgg)
        {
            var strings = GameInfo.GetStrings("en");
            description += $"\n**Level:** {pkm.CurrentLevel}";
            description += $"\n**Ball:** {strings.balllist[pkm.Ball]}";
            description += $"\n**Ability:** {strings.abilitylist[pkm.Ability]}";
            description += $"\n**{strings.natures[(int)pkm.Nature]}** Nature";

            if (showMoves)
            {
                description += "\n\n**Moves:**";
                ushort[] moves = new ushort[4];
                pkm.GetMoves(moves.AsSpan());
                int[] pps = [pkm.Move1_PP, pkm.Move2_PP, pkm.Move3_PP, pkm.Move4_PP];
                
                for (int i = 0; i < 4; i++)
                {
                    if (moves[i] != 0)
                    {
                         description += $"\n{strings.movelist[moves[i]]} ({pps[i]}pp)";
                    }
                }
            }
            description += "\n\n";
        }

        if (!string.IsNullOrEmpty(message))
        {
            description += message;
        }
        
        description += "\n\n**Please enter code in game but do not search yet.**";

        var embed = new EmbedBuilder()
            .WithTitle("🚀 Trade Initializing...")
            .AddField("Pokémon", speciesName, true)
            .WithDescription(description)
            .WithTimestamp(DateTimeOffset.Now)
            .WithThumbnailUrl(imageUrl ?? "https://raw.githubusercontent.com/NexusRisen/sprites/main/initializing.gif")
            .WithColor(ColorAccent) // Use accent color instead of orange
            .WithFooter(Footer);

        var builtEmbed = embed.Build();
        await user.SendMessageAsync(embed: builtEmbed).ConfigureAwait(false);
    }

    public static async Task SendTradeSearchingEmbedAsync(IUser user, string trainerName, string inGameName, string? message = null)
    {
        var embed = new EmbedBuilder()
            .WithTitle($"🔍 Searching For You")
            .AddField("Trainer", trainerName, true)
            .AddField("Bot IGN", inGameName, true)
            .WithTimestamp(DateTimeOffset.Now)
            .WithThumbnailUrl("https://raw.githubusercontent.com/NexusRisen/sprites/main/searching.gif")
            .WithColor(ColorWarning)
            .WithFooter(Footer);

        if (!string.IsNullOrEmpty(message))
        {
            embed.WithDescription(message);
        }
        else
        {
            embed.WithDescription("Please begin searching code in game.");
        }

        var builtEmbed = embed.Build();
        await user.SendMessageAsync(embed: builtEmbed).ConfigureAwait(false);
    }

    public static async Task SendTradeCanceledEmbedAsync(IUser user, string reason)
    {
        var embed = new EmbedBuilder()
            .WithTitle("⛔ Trade Canceled")
            .WithDescription(reason)
            .WithTimestamp(DateTimeOffset.Now)
            .WithColor(ColorDanger)
            .WithFooter(Footer)
            .Build();

        await user.SendMessageAsync(embed: embed).ConfigureAwait(false);
    }
}
