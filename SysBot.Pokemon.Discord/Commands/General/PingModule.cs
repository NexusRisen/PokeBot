using Discord;
using Discord.Commands;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord;

public class PingModule : ModuleBase<SocketCommandContext>
{
    [Command("ping")]
    [Summary("Makes the bot respond, indicating that it is running.")]
    public async Task PingAsync()
    {
        var embed = EmbedHelper.CreateBuilder("Ping Response", "Pong! The bot is running smoothly.", EmbedHelper.ColorSuccess)
            .WithImageUrl("https://i.gifer.com/QgxJ.gif")
            .Build();

        await ReplyAsync(embed: embed).ConfigureAwait(false);
    }
}
