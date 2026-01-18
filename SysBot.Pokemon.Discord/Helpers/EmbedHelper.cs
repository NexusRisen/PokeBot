using Discord;
using PKHeX.Core;
using SysBot.Pokemon.Helpers;
using System;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord;

public static class EmbedHelper
{
    // NexusRisen Theme Colors
    public static readonly Color ColorPrimary = new Color(43, 45, 49);     // Dark Gray (Discord-like)
    public static readonly Color ColorAccent = new Color(88, 101, 242);    // Blurple
    public static readonly Color ColorSuccess = new Color(87, 242, 135);   // Green
    public static readonly Color ColorDanger = new Color(237, 66, 69);     // Red
    public static readonly Color ColorWarning = new Color(254, 231, 92);   // Yellow
    public static readonly Color ColorInfo = new Color(88, 101, 242);      // Blue/Blurple
    public static readonly Color ColorGold = new Color(255, 215, 0);       // Gold

    // Common Footer
    public static readonly EmbedFooterBuilder Footer = new EmbedFooterBuilder()
        .WithText("Powered by SysBots.NET - Nexus Risen Edition")
        .WithIconUrl("https://raw.githubusercontent.com/NexusRisen/sprites/main/pokeball.png");

    /// <summary>
    /// Creates a standard EmbedBuilder with the default theme.
    /// </summary>
    public static EmbedBuilder CreateBuilder(string? title = null, string? description = null, Color? color = null)
    {
        var builder = new EmbedBuilder()
            .WithTimestamp(DateTimeOffset.Now)
            .WithFooter(Footer)
            .WithColor(color ?? ColorPrimary);

        if (!string.IsNullOrEmpty(title))
            builder.WithTitle(title);

        if (!string.IsNullOrEmpty(description))
            builder.WithDescription(description);

        return builder;
    }

    public static async Task SendNotificationEmbedAsync(IUser user, string message)
    {
        var embed = CreateBuilder("📢 Notification", message, ColorInfo).Build();
        await user.SendMessageAsync(embed: embed).ConfigureAwait(false);
    }

    public static async Task SendTradeCodeEmbedAsync(IUser user, int code)
    {
        var embed = CreateBuilder("🔄 Ready to Trade!",
                $"Please enter the following Link Code:\n# {code:0000 0000}\n\n**Enter this code in your game, but DO NOT search yet.**",
                ColorAccent)
            .WithThumbnailUrl("https://raw.githubusercontent.com/NexusRisen/sprites/main/tradecode.gif")
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

        var embed = CreateBuilder(title, message, ColorSuccess)
            .WithThumbnailUrl(thumbUrl)
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

        var embed = CreateBuilder("🚀 Trade Initializing...", description, ColorAccent)
            .AddField("Pokémon", speciesName, true)
            .WithThumbnailUrl(imageUrl ?? "https://raw.githubusercontent.com/NexusRisen/sprites/main/initializing.gif")
            .Build();

        await user.SendMessageAsync(embed: embed).ConfigureAwait(false);
    }

    public static async Task SendTradeSearchingEmbedAsync(IUser user, string trainerName, string inGameName, string? message = null)
    {
        var embed = CreateBuilder("🔍 Searching For You", message ?? "Please begin searching code in game.", ColorWarning)
            .AddField("Trainer", trainerName, true)
            .AddField("Bot IGN", inGameName, true)
            .WithThumbnailUrl("https://raw.githubusercontent.com/NexusRisen/sprites/main/searching.gif")
            .Build();

        await user.SendMessageAsync(embed: embed).ConfigureAwait(false);
    }

    public static async Task SendTradeCanceledEmbedAsync(IUser user, string reason)
    {
        var embed = CreateBuilder("⛔ Trade Canceled", reason, ColorDanger)
            .Build();

        await user.SendMessageAsync(embed: embed).ConfigureAwait(false);
    }
}
