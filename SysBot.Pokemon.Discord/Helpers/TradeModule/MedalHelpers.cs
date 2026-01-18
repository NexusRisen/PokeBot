using Discord;
using Discord.WebSocket;
using System.Collections.Generic;
using System.Linq;

namespace SysBot.Pokemon.Discord;

public static class MedalHelpers
{
    private const string BaseSpriteUrl = "https://raw.githubusercontent.com/NexusRisen/Nexus-Risen-Edition-Sprite-Images/main";

    private static readonly int[] Milestones = [700, 650, 600, 550, 500, 450, 400, 350, 300, 250, 200, 150, 100, 50, 1];

    private static readonly Dictionary<int, string> MilestoneStatuses = new()
    {
        { 1, "Newbie Trainer" },
        { 50, "Novice Trainer" },
        { 100, "Pokémon Professor" },
        { 150, "Pokémon Specialist" },
        { 200, "Pokémon Champion" },
        { 250, "Pokémon Hero" },
        { 300, "Pokémon Elite" },
        { 350, "Pokémon Trader" },
        { 400, "Pokémon Sage" },
        { 450, "Pokémon Legend" },
        { 500, "Region Master" },
        { 550, "Trade Master" },
        { 600, "World Famous" },
        { 650, "Pokémon Master" },
        { 700, "Pokémon God" }
    };

    public static int GetCurrentMilestone(int totalTrades)
    {
        return Milestones.FirstOrDefault(m => totalTrades >= m, 0);
    }

    public static Embed CreateMedalsEmbed(SocketUser user, int milestone, int totalTrades)
    {
        string status = MilestoneStatuses.TryGetValue(milestone, out var milestoneStatus)
            ? milestoneStatus
            : "New Trainer";

        string description = $"Total Trades: **{totalTrades}**\n**Current Status:** {status}";

        if (milestone > 0)
        {
            string imageUrl = $"{BaseSpriteUrl}/{milestone:D3}.png";
            return EmbedHelper.CreateBuilder($"{user.Username}'s Trading Status", description, EmbedHelper.ColorGold)
                .WithThumbnailUrl(imageUrl)
                .Build();
        }

        return EmbedHelper.CreateBuilder($"{user.Username}'s Trading Status", description, EmbedHelper.ColorGold)
            .Build();
    }
}
