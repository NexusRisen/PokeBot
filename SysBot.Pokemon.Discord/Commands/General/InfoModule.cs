using Discord;
using Discord.Commands;
using SysBot.Pokemon.Helpers;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord;

// src: https://github.com/foxbot/patek/blob/master/src/Patek/Modules/InfoModule.cs
// ISC License (ISC)
// Copyright 2017, Christopher F. <foxbot@protonmail.com>
public class InfoModule : ModuleBase<SocketCommandContext>
{
    private const string detail = "I am a custom Pokémon Trading Bot, proudly part of the NexusRisen community.";
    private const string repo = "https://github.com/NexusRisen/PokeBot";

    [Command("info")]
    [Alias("about", "whoami", "owner")]
    public async Task InfoAsync()
    {
        var app = await Context.Client.GetApplicationInfoAsync().ConfigureAwait(false);

        var builder = EmbedHelper.CreateBuilder("🤖 NexusRisen PokeBot", detail, EmbedHelper.ColorInfo);

        builder.AddField("📝 Information",
            $"- **Source Code**: [GitHub Repository]({repo})\n" +
            $"- **Community**: NexusRisen\n" +
            $"- **Owner**: {app.Owner} ({app.Owner.Id})\n" +
            $"- **Library**: Discord.Net ({DiscordConfig.Version})\n",
            inline: false
        );

        builder.AddField("⚙️ System Status",
            $"- **Uptime**: {GetUptime()}\n" +
            $"- **System**: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})\n" +
            $"- **Framework**: {RuntimeInformation.FrameworkDescription}\n",
            inline: true
        );

        builder.AddField("📊 Bot Statistics",
            $"- **Heap Size**: {GetHeapSize()} MiB\n" +
            $"- **Guilds**: {Context.Client.Guilds.Count}\n" +
            $"- **Users**: {Context.Client.Guilds.Sum(g => g.MemberCount):N0}\n",
            inline: true
        );

        builder.AddField("📦 Versions",
            $"- **SysBot+**: {PokeBot.Version}\n" +
            $"- **Core**: {GetVersionInfo("PKHeX.Core")}\n" +
            $"- **AutoMod**: {GetVersionInfo("PKHeX.Core.AutoMod")}\n",
            inline: false
        );

        await ReplyAsync(embed: builder.Build()).ConfigureAwait(false);
    }

    private static string GetHeapSize() => Math.Round(GC.GetTotalMemory(true) / (1024.0 * 1024.0), 2).ToString(CultureInfo.CurrentCulture);

    private static string GetUptime() => (DateTime.Now - Process.GetCurrentProcess().StartTime).ToString(@"dd\.hh\:mm\:ss");

    private static string GetVersionInfo(string assemblyName, bool inclVersion = true)
    {
        const string _default = "Unknown";
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var assembly = Array.Find(assemblies, x => x.GetName().Name == assemblyName);

        var attribute = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (attribute is null)
            return _default;

        var info = attribute.InformationalVersion;
        var split = info.Split('+');
        if (split.Length >= 2)
        {
            var version = split[0];
            var revision = split[1];
            if (DateTime.TryParseExact(revision, "yyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var buildTime))
                return (inclVersion ? $"{version} " : "") + $@"{buildTime:yy-MM-dd\.hh\:mm}";
            return inclVersion ? version : _default;
        }
        return _default;
    }
}
