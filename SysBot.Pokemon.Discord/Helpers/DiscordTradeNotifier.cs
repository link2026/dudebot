using Discord;
using Discord.WebSocket;
using PKHeX.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Color = Discord.Color;

namespace SysBot.Pokemon.Discord;

public class DiscordTradeNotifier<T> : IPokeTradeNotifier<T>
    where T : PKM, new()
{
    private T Data { get; }

    private PokeTradeTrainerInfo Info { get; }

    private int Code { get; }

    private List<Pictocodes> LGCode { get; }

    private SocketUser Trader { get; }

    private int BatchTradeNumber { get; }

    private int TotalBatchTrades { get; }

    private bool IsMysteryEgg { get; }

    public DiscordTradeNotifier(T data, PokeTradeTrainerInfo info, int code, SocketUser trader, int batchTradeNumber, int totalBatchTrades, bool isMysteryEgg, List<Pictocodes> lgcode)
    {
        Data = data;
        Info = info;
        Code = code;
        Trader = trader;
        BatchTradeNumber = batchTradeNumber;
        TotalBatchTrades = totalBatchTrades;
        IsMysteryEgg = isMysteryEgg;
        LGCode = lgcode;
    }

    public Action<PokeRoutineExecutor<T>>? OnFinish { private get; set; }

    public readonly PokeTradeHub<T> Hub = SysCord<T>.Runner.Hub;

    public void TradeInitialize(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info)
    {
        const int language = 2;
        var speciesName = SpeciesName.GetSpeciesName(Data.Species, language);
        var batchInfo = TotalBatchTrades > 1 ? $" (Trade {BatchTradeNumber} of {TotalBatchTrades})" : "";
        var receive = Data.Species == 0 ? string.Empty : $" ({Data.Nickname})";

        if (Data is PK9)
        {
            var message = $"Initializing trade{receive}{batchInfo}. Please be ready.";

            if (TotalBatchTrades > 1 && BatchTradeNumber == 1)
            {
                message += "\n**Please stay in the trade until all batch trades are completed.**";
            }

            EmbedHelper.SendTradeInitializingEmbedAsync(Trader, speciesName, Code, IsMysteryEgg, message).ConfigureAwait(false);
        }
        else if (Data is PB7)
        {
            var (thefile, lgcodeembed) = CreateLGLinkCodeSpriteEmbed(LGCode);
            Trader.SendFileAsync(thefile, $"Initializing trade{receive}. Please be ready. Your code is", embed: lgcodeembed).ConfigureAwait(false);
        }
        else
        {
            EmbedHelper.SendTradeInitializingEmbedAsync(Trader, speciesName, Code, IsMysteryEgg).ConfigureAwait(false);
        }
    }

    public void TradeSearching(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info)
    {
        var batchInfo = TotalBatchTrades > 1 ? $" for batch trade (Trade {BatchTradeNumber} of {TotalBatchTrades})" : "";
        var name = Info.TrainerName;
        var trainer = string.IsNullOrEmpty(name) ? string.Empty : $" {name}";

        if (Data is PB7 && LGCode != null && LGCode.Count != 0)
        {
            var message = $"I'm waiting for you{trainer}{batchInfo}! My IGN is **{routine.InGameName}**.";
            Trader.SendMessageAsync(message).ConfigureAwait(false);
        }
        else
        {
            string? additionalMessage = null;
            if (TotalBatchTrades > 1 && BatchTradeNumber > 1)
            {
                var receive = Data.Species == 0 ? string.Empty : $" ({Data.Nickname})";
                additionalMessage = $"Now trading{receive} (Trade {BatchTradeNumber} of {TotalBatchTrades}). **Select the Pokémon you wish to trade!**";
            }

            EmbedHelper.SendTradeSearchingEmbedAsync(Trader, trainer, routine.InGameName, additionalMessage).ConfigureAwait(false);
        }
    }

    public void TradeCanceled(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info, PokeTradeResult msg)
    {
        OnFinish?.Invoke(routine);
        EmbedHelper.SendTradeCanceledEmbedAsync(Trader, msg.ToString()).ConfigureAwait(false);
    }

    public void TradeFinished(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info, T result)
    {
        OnFinish?.Invoke(routine);
        var tradedToUser = Data.Species;
        var message = tradedToUser != 0 ? (info.IsMysteryEgg ? "Enjoy your **Mystery Egg**!" : $"Enjoy your **{(Species)tradedToUser}**!") : "Trade finished!";
        EmbedHelper.SendTradeFinishedEmbedAsync(Trader, message, Data, info.IsMysteryEgg).ConfigureAwait(false);
        if (result.Species != 0 && Hub.Config.Discord.ReturnPKMs)
            Trader.SendPKMAsync(result, "Here's what you traded me!").ConfigureAwait(false);
    }

    public void SendNotification(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info, string message)
    {
        EmbedHelper.SendNotificationEmbedAsync(Trader, message).ConfigureAwait(false);
    }

    public void SendNotification(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info, PokeTradeSummary message)
    {
        if (message.ExtraInfo is SeedSearchResult r)
        {
            SendNotificationZ3(r);
            return;
        }

        var msg = message.Summary;
        if (message.Details.Count > 0)
            msg += ", " + string.Join(", ", message.Details.Select(z => $"{z.Heading}: {z.Detail}"));
        Trader.SendMessageAsync(msg).ConfigureAwait(false);
    }

    public void SendNotification(PokeRoutineExecutor<T> routine, PokeTradeDetail<T> info, T result, string message)
    {
        if (result.Species != 0 && (Hub.Config.Discord.ReturnPKMs || info.Type == PokeTradeType.Dump))
            Trader.SendPKMAsync(result, message).ConfigureAwait(false);
    }

    private void SendNotificationZ3(SeedSearchResult r)
    {
        var lines = r.ToString();

        var embed = new EmbedBuilder { Color = Color.LighterGrey };
        embed.AddField(x =>
        {
            x.Name = $"Seed: {r.Seed:X16}";
            x.Value = lines;
            x.IsInline = false;
        });
        var msg = $"Here are the details for `{r.Seed:X16}`:";
        Trader.SendMessageAsync(msg, embed: embed.Build()).ConfigureAwait(false);
    }

    public static (string, Embed) CreateLGLinkCodeSpriteEmbed(List<Pictocodes> lgcode)
    {
#if WINDOWS
    List<System.Drawing.Image> spritearray = new List<System.Drawing.Image>();

    foreach (Pictocodes cd in lgcode)
    {
        var showdown = new ShowdownSet(cd.ToString());
        var sav = SaveUtil.GetBlankSAV(EntityContext.Gen7b, "pip");
        PKM pk = sav.GetLegalFromSet(showdown).Created;

        System.Drawing.Image png = pk.Sprite();
        var destRect = new Rectangle(-40, -65, 137, 130);
        var destImage = new Bitmap(137, 130);
        destImage.SetResolution(png.HorizontalResolution, png.VerticalResolution);

        using (var graphics = Graphics.FromImage(destImage))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(png, destRect, 0, 0, png.Width, png.Height, GraphicsUnit.Pixel);
        }
        png = destImage;
        spritearray.Add(png);
    }

    if (spritearray.Count == 0)
        throw new InvalidOperationException("No sprites available.");

    int outputImageWidth = spritearray[0].Width + 20;
    int outputImageHeight = spritearray[0].Height - 65;
    Bitmap outputImage = new Bitmap(outputImageWidth, outputImageHeight, PixelFormat.Format32bppArgb);

    using (Graphics graphics = Graphics.FromImage(outputImage))
    {
        graphics.DrawImage(spritearray[0], new Rectangle(0, 0, spritearray[0].Width, spritearray[0].Height),
            new Rectangle(new Point(), spritearray[0].Size), GraphicsUnit.Pixel);
        if (spritearray.Count > 1)
            graphics.DrawImage(spritearray[1], new Rectangle(50, 0, spritearray[1].Width, spritearray[1].Height),
                new Rectangle(new Point(), spritearray[1].Size), GraphicsUnit.Pixel);
        if (spritearray.Count > 2)
            graphics.DrawImage(spritearray[2], new Rectangle(100, 0, spritearray[2].Width, spritearray[2].Height),
                new Rectangle(new Point(), spritearray[2].Size), GraphicsUnit.Pixel);
    }
    System.Drawing.Image finalembedpic = outputImage;
    var filename = $"{Directory.GetCurrentDirectory()}//finalcode.png";
    finalembedpic.Save(filename);
    filename = Path.GetFileName($"{Directory.GetCurrentDirectory()}//finalcode.png");
    Embed returnembed = new EmbedBuilder().WithTitle($"{lgcode[0]}, {lgcode[1]}, {lgcode[2]}").WithImageUrl($"attachment://{filename}").Build();
    return (filename, returnembed);
#else
        throw new PlatformNotSupportedException("This code requires a Windows platform.");
#endif
    }
}
