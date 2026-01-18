using PKHeX.Core;
using PKHeX.Core.AutoMod;
using PKHeX.Core.Searching;
using SysBot.Base;
using SysBot.Pokemon.Helpers;
using System;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.PokeDataOffsetsSV;
using static SysBot.Pokemon.TradeHub.SpecialRequests;

namespace SysBot.Pokemon;

public class PokeTradeBotSV(PokeTradeHub<PK9> Hub, PokeBotState Config) : PokeRoutineExecutor9(Config), ICountBot, ITradeBot
{
    public PokeTradeHub<PK9> Hub { get; } = Hub;

    private readonly TradeSettings TradeSettings = Hub.Config.TradeSystem.Settings;

    private readonly TradeAbuseSettings AbuseSettings = Hub.Config.TradeSystem.Abuse;

    public event EventHandler<Exception>? ConnectionError;

    public event EventHandler? ConnectionSuccess;

    private void OnConnectionError(Exception ex)
    {
        ConnectionError?.Invoke(this, ex);
    }

    private void OnConnectionSuccess()
    {
        ConnectionSuccess?.Invoke(this, EventArgs.Empty);
    }

    public ICountSettings Counts => TradeSettings;

    private readonly FolderSettings DumpSetting = Hub.Config.Global.Folder;

    public bool ShouldWaitAtBarrier { get; private set; }

    public int FailedBarrier { get; private set; }

    public override async Task MainLoop(CancellationToken token)
    {
        try
        {
            await InitializeHardware(Hub.Config.TradeSystem.Settings, token).ConfigureAwait(false);

            Log("Identifying trainer data of the host console.");
            var sav = await IdentifyTrainer(token).ConfigureAwait(false);
            RecentTrainerCache.SetRecentTrainer(sav);
            await InitializeSessionOffsets(token).ConfigureAwait(false);
            OnConnectionSuccess();
            Log($"Starting main {nameof(PokeTradeBotSV)} loop.");
            await InnerLoop(sav, token).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            OnConnectionError(e);
            throw;
        }

        Log($"Ending {nameof(PokeTradeBotSV)} loop.");
        await HardStop().ConfigureAwait(false);
    }

    public override async Task HardStop()
    {
        UpdateBarrier(false);
        await CleanExit(TradeSettings, CancellationToken.None).ConfigureAwait(false);
    }

    public override async Task RebootAndStop(CancellationToken t)
    {
        Hub.Queues.Info.CleanStuckTrades();
        await Task.Delay(2_000, t).ConfigureAwait(false);
            await ReOpenGame(Hub.Config, t).ConfigureAwait(false);
        await HardStop().ConfigureAwait(false);
        await Task.Delay(2_000, t).ConfigureAwait(false);
        if (!t.IsCancellationRequested)
        {
            Log("Restarting the main loop.");
            await MainLoop(t).ConfigureAwait(false);
        }
    }

        private async Task<bool> ReturnToOverworld(CancellationToken token)
        {
            int tries = 15;
            while (!await CanPlayerMove(token).ConfigureAwait(false))
            {
                await Click(B, 1_000, token).ConfigureAwait(false);
                await Click(B, 1_000, token).ConfigureAwait(false);
                await Click(B, 0_500, token).ConfigureAwait(false);
                await Click(B, 0_500, token).ConfigureAwait(false);
                await Click(A, 1_000, token).ConfigureAwait(false);
                if (tries-- < 1)
                {
                    return false;
                }
            }

            await EstablishOverworldPokePortalMinimum(token).ConfigureAwait(false);

            return true;
        }

        private async Task InitializeSessionOffsets(CancellationToken token)
        {
            Log("Caching session offsets...");
            await Task.Delay(0, token).ConfigureAwait(false);
        }


        private async Task<bool> ConnectIfNotConnected(bool aPressFirst, CancellationToken token)
        {
            if (!await IsConnected(token).ConfigureAwait(false))
            {
                if (aPressFirst)
                    for (int i = 0; i < 3; i++)
                        await Click(A, 0_350, token).ConfigureAwait(false);
                if (!await ReturnToOverworld(token).ConfigureAwait(false))
                    return false;

                await Task.Delay(2_000, token).ConfigureAwait(false);
                await Click(X, 0_700, token).ConfigureAwait(false);
                await Click(L, 8_000, token).ConfigureAwait(false);

                int tries = 11;
                while (!await IsConnected(token).ConfigureAwait(false))
                {
                    if (tries-- < 1)
                        return false;
                    await Task.Delay(0_800).ConfigureAwait(false);
                    await Click(B, 0_350, token).ConfigureAwait(false);
                }

                await Task.Delay(0_800).ConfigureAwait(false);
                for (int i = 0; i < 3; i++)
                    await Click(B, 0_350, token).ConfigureAwait(false);
            }

            return true;
        }

        public async Task<bool> RestartGameIfCantTrade(bool skipInitialChecks, int? code, CancellationToken token, bool verboseLogging = false)
        {
            if (verboseLogging)
                Log("Something has failed so we will now be verbose.");

            if (!await IsGameRunning(token).ConfigureAwait(false))
                await StartGame(Hub.Config, token).ConfigureAwait(false);

            if (await IsGameRunning(token).ConfigureAwait(false) && !await IsInGame(token).ConfigureAwait(false))
            {
                int tries = 30;
                while (!await IsInGame(token).ConfigureAwait(false) && tries-- > 0)
                {
                    if (await IsKeyboardOpen(token).ConfigureAwait(false))
                        break;
                    await Click(A, 0_800, token).ConfigureAwait(false);
                }
            }

            if (await IsGameRunning(token).ConfigureAwait(false) && !await CanPlayerMove(token).ConfigureAwait(false) && !await IsPokePortalLoaded(token, verboseLogging).ConfigureAwait(false))
            {
                int tries = 30;
                while (!await CanPlayerMove(token).ConfigureAwait(false) && tries-- > 0)
                {
                    if (await IsKeyboardOpen(token).ConfigureAwait(false))
                        break;
                    await Click(A, 0_800, token).ConfigureAwait(false);
                }
            }

            // Allow the game to settle in the overworld before attempting inputs
            await Task.Delay(1_500, token).ConfigureAwait(false);

            if (!await ConnectIfNotConnected(verboseLogging, token).ConfigureAwait(false))
                return false;

            if (await IsKeyboardOpen(token).ConfigureAwait(false))
                return true;

            await ClearKeyboardBuffer(code, token).ConfigureAwait(false);

            if (verboseLogging)
                Log("At the IsSearching point.");

            // check if we are still searching
            if (await IsSearching(token).ConfigureAwait(false))
            {
                await Click(B, 0_800, token).ConfigureAwait(false);
                await Click(A, 1_200, token).ConfigureAwait(false);
                await Click(A, 0_350, token).ConfigureAwait(false);
                await Click(PLUS, 1_000, token).ConfigureAwait(false);

                if (await IsKeyboardOpen(token).ConfigureAwait(false))
                    return true;
            }

            if (!skipInitialChecks)
            {
                if (!await CanPlayerMove(token).ConfigureAwait(false) && await IsPokePortalLoaded(token, verboseLogging).ConfigureAwait(false))
                {
                    await Click(A, 1_000, token).ConfigureAwait(false);
                    await Click(PLUS, 1_000, token).ConfigureAwait(false);

                    if (await IsKeyboardOpen(token).ConfigureAwait(false))
                        return true;
                }

                // Go all the way back to overworld
                if (!await ReturnToOverworld(token).ConfigureAwait(false))
                {
                    if (verboseLogging)
                        Log("Could not return to overworld, restarting...");

                    await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                    await RestartGameIfCantTrade(true, code, token).ConfigureAwait(false);
                }
                else
                    await EstablishOverworldPokePortalMinimum(token).ConfigureAwait(false);
            }

            // Ensure we wait a bit before opening the menu
            await Task.Delay(0_800, token).ConfigureAwait(false);
            await Click(X, 0_800, token).ConfigureAwait(false);

            if (!skipInitialChecks)
            {
                // hold dpad up
                await PressAndHold(DUP, 2_000, 0_300, token).ConfigureAwait(false);
            }

            // Assuming we've unlocked picnic
            await Click(DRIGHT, 0_350, token).ConfigureAwait(false);
            await Click(DUP, 0_350, token).ConfigureAwait(false);
            await Click(DUP, 0_350, token).ConfigureAwait(false);
            await Click(DUP, 0_650, token).ConfigureAwait(false);
            await Click(DUP, 0_650, token).ConfigureAwait(false);
            await Click(A, 0_350, token).ConfigureAwait(false);

            int checks = 20;
            while (!await IsPokePortalLoaded(token, verboseLogging).ConfigureAwait(false))
            {
                await Task.Delay(0_800, token).ConfigureAwait(false);
                if (checks-- < 1)
                {
                    Log("Couldn't get to PokePortal, restarting...");
                    await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
                    return false;
                }
            }

            if (!await IsConnected(token).ConfigureAwait(false))
            {
                Log("Not connected, trying again...");
                await ConnectIfNotConnected(false, token).ConfigureAwait(false);
                await RestartGameIfCantTrade(true, code, token).ConfigureAwait(false);
            }

            await Task.Delay(4_000 + Hub.Config.Timings.ExtraTimeLoadPortal, token).ConfigureAwait(false);

            await Click(DDOWN, 0_700, token).ConfigureAwait(false);
            await Click(DDOWN, 0_700, token).ConfigureAwait(false);
            await Click(A, 0_700, token).ConfigureAwait(false);
            await Click(PLUS, 1_500, token).ConfigureAwait(false);

            if (!await IsKeyboardOpen(token).ConfigureAwait(false))
            {
                if (verboseLogging)
                {
                    var connectState = await IsConnected(token).ConfigureAwait(false);
                    var pokePortalState = await IsPokePortalLoaded(token, true).ConfigureAwait(false);
                    Log($"At final keyboard check. Connected: {connectState}. PokePortal: {pokePortalState}.");
                }
                return false;
            }

            return true;
        }

        private async Task AttemptGetBackToPokePortal(CancellationToken token)
        {
            if (await CanPlayerMove(token).ConfigureAwait(false) || await IsKeyboardOpen(token).ConfigureAwait(false))
                return;

            int tries = 12;
            while (!await IsPokePortalLoaded(token).ConfigureAwait(false) && !await CanPlayerMove(token).ConfigureAwait(false) && tries-- > 0)
            {
                await Click(B, 0_500, token).ConfigureAwait(false);
                await Click(B, 0_500, token).ConfigureAwait(false);
                if (!await IsPokePortalLoaded(token).ConfigureAwait(false))
                    await Click(A, 1_000, token).ConfigureAwait(false);
            }

            if (await IsPokePortalLoaded(token).ConfigureAwait(false))
                await Task.Delay(1_500 + Hub.Config.Timings.ExtraTimeLoadPortal, token).ConfigureAwait(false);
        }

        private async Task InnerLoop(SAV9SV sav, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Config.IterateNextRoutine();
                var task = Config.CurrentRoutineType switch
                {
                    PokeRoutineType.Idle => DoNothing(token),
                    _ => DoTrades(sav, token),
                };
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (SocketException e)
                {
                    Log(e.Message);
                    Connection.Reset();
                }
            }
        }

        private async Task DoNothing(CancellationToken token)
        {
            int waitCounter = 0;
            while (!token.IsCancellationRequested && Config.NextRoutineType == PokeRoutineType.Idle)
            {
                if (waitCounter == 0)
                    Log("No task assigned. Waiting for new task assignment.");
                waitCounter++;
                if (waitCounter % 10 == 0 && Hub.Config.Global.AntiIdle)
                    await Click(B, 1_000, token).ConfigureAwait(false);
                else
                    await Task.Delay(1_000, token).ConfigureAwait(false);
            }
        }

        private async Task DoTrades(SAV9SV sav, CancellationToken token)
        {
            var type = Config.CurrentRoutineType;
            int waitCounter = 0;
            while (!token.IsCancellationRequested && Config.NextRoutineType == type)
            {
                await AttemptClearTradePartnerPointer(token).ConfigureAwait(false);
                var (detail, priority) = GetTradeData(type);
                if (detail is null)
                {
                    await WaitForQueueStep(waitCounter++, token).ConfigureAwait(false);
                    continue;
                }
                waitCounter = 0;

                detail.IsProcessing = true;
                if (!await RestartGameIfCantTrade(false, detail.Code, token).ConfigureAwait(false))
                    await RestartGameIfCantTrade(false, detail.Code, token, true).ConfigureAwait(false);
                string tradetype = $" ({detail.Type})";
                Log($"Starting next {type}{tradetype} Bot Trade. Getting data...");
                Hub.Config.Integration.Stream.StartTrade(this, detail, Hub);
                Hub.Queues.StartTrade(this, detail);

                await PerformTrade(sav, detail, type, priority, token).ConfigureAwait(false);

                // return to original position if required
                await RestartGameIfCantTrade(false, null, token).ConfigureAwait(false);
            }
        }

        private async Task WaitForQueueStep(int waitCounter, CancellationToken token)
        {
            if (waitCounter == 0)
            {
                // Updates the assets.
                Hub.Config.Integration.Stream.IdleAssets(this);
                Log("Nothing to check, waiting for new users...");
            }

            const int interval = 10;
            if (waitCounter % interval == interval - 1 && Hub.Config.Global.AntiIdle)
                await Click(B, 0_800, token).ConfigureAwait(false);
            else
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }

        protected virtual (PokeTradeDetail<PK9>? detail, uint priority) GetTradeData(PokeRoutineType type)
        {
            var botName = Connection.Name;
            if (Hub.Queues.TryDequeue(type, out var detail, out var priority, botName))
                return (detail, priority);
            if (Hub.Queues.TryDequeueLedy(out detail))
                return (detail, PokeTradePriorities.TierFree);
            return (null, PokeTradePriorities.TierFree);
        }

        private async Task AttemptClearTradePartnerPointer(CancellationToken token)
        {
            (var valid, var offs) = await ValidatePointerAll(LinkTradePartnerNIDPointer, token).ConfigureAwait(false);
            if (valid)
                await SwitchConnection.WriteBytesAbsoluteAsync(new byte[8], offs, token).ConfigureAwait(false);

            (valid, offs) = await ValidatePointerAll(LinkTradePartnerNameSlot1Pointer, token).ConfigureAwait(false);
            if (valid)
                await SwitchConnection.WriteBytesAbsoluteAsync(new byte[4], offs, token).ConfigureAwait(false);

            (valid, offs) = await ValidatePointerAll(LinkTradePartnerNameSlot2Pointer, token).ConfigureAwait(false);
            if (valid)
                await SwitchConnection.WriteBytesAbsoluteAsync(new byte[4], offs, token).ConfigureAwait(false);
        }

        private async Task PerformTrade(SAV9SV sav, PokeTradeDetail<PK9> detail, PokeRoutineType type, uint priority, CancellationToken token)
        {
            PokeTradeResult result;
            try
            {
                if (detail.Type == PokeTradeType.Batch)
                    result = await PerformBatchTrade(sav, detail, token).ConfigureAwait(false);
                else
                    result = await PerformLinkCodeTrade(sav, detail, token).ConfigureAwait(false);

                if (result == PokeTradeResult.Success)
                    return;
            }
            catch (SocketException socket)
            {
                Log(socket.Message);
                result = PokeTradeResult.ExceptionConnection;
                if (detail.Type == PokeTradeType.Batch)
                    await HandleAbortedBatchTrade(detail, type, priority, result, token).ConfigureAwait(false);
                else
                    HandleAbortedTrade(detail, type, priority, result);
                throw; // let this interrupt the trade loop. re-entering the trade loop will recheck the connection.
            }
            catch (Exception e)
            {
                Log(e.Message);
                result = PokeTradeResult.ExceptionInternal;
                if (detail.Type == PokeTradeType.Batch)
                    await HandleAbortedBatchTrade(detail, type, priority, result, token).ConfigureAwait(false);
                else
                    HandleAbortedTrade(detail, type, priority, result);
                return;
            }

            if (detail.Type == PokeTradeType.Batch)
                await HandleAbortedBatchTrade(detail, type, priority, result, token).ConfigureAwait(false);
            else
                HandleAbortedTrade(detail, type, priority, result);
        }

        private async Task<PokeTradeResult> PerformLinkCodeTrade(SAV9SV sav, PokeTradeDetail<PK9> poke, CancellationToken token)
        {
            if (poke.Type == PokeTradeType.Random)
                SetText(sav, $"Trade code: {poke.Code:0000 0000}\r\nSending: {(Species)poke.TradeData.Species}{(poke.TradeData.IsEgg ? " (egg)" : string.Empty)}");
            else
                SetText(sav, "Running a\nSpecific trade.");

            UpdateBarrier(poke.IsSynchronized);
            poke.TradeInitialize(this);
            Hub.Config.Integration.Stream.EndEnterCode(this);

            if (poke.Type != PokeTradeType.Random)
                Hub.Config.Integration.Stream.StartEnterCode(this);

            var toSend = poke.TradeData;
            if (toSend.Species != 0)
                await SetBoxPokemon(toSend, token, sav).ConfigureAwait(false);

            if (!await IsKeyboardOpen(token).ConfigureAwait(false))
            {
                await Click(A, 0_500, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverStart;
            }

            if (!await BeginTradeViaCode(poke, poke.Code, token).ConfigureAwait(false))
            {
                for (int i = 0; i < 5; ++i)
                    await Click(B, 0_500, token).ConfigureAwait(false);
                await RestartGameIfCantTrade(false, null, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverOpenBox;
            }

            poke.TradeSearching(this);

            // Wait to hit the bot or quit if no trade partner found
            int inBoxChecks = Hub.Config.Trade.TradeConfiguration.TradeWaitTime;
            while (await IsPokePortalLoaded(token).ConfigureAwait(false))
            {
                if (inBoxChecks-- < 0)
                {
                    await Click(B, 1_500, token).ConfigureAwait(false);
                    if (await IsPokePortalLoaded(token).ConfigureAwait(false))
                    {
                        await Click(A, 1_500, token).ConfigureAwait(false);
                        await ClearKeyboardBuffer(null, token).ConfigureAwait(false);
                        await Click(PLUS, 0_800, token).ConfigureAwait(false);
                        return PokeTradeResult.NoTrainerFound;
                    }
                }

                await Task.Delay(1_000, token).ConfigureAwait(false);
            }

            // Still going through dialog and extremely laggy box opening.
            await Task.Delay(2_000, token).ConfigureAwait(false);

            Hub.Config.Integration.Stream.EndEnterCode(this);

            if (poke.Type == PokeTradeType.Random)
                await ClearKeyboardBuffer(null, token).ConfigureAwait(false);

            var tradePartnerNID = await GetTradePartnerNID(token).ConfigureAwait(false);
            var tradePartner = await FetchIDFromTradeOffset(token).ConfigureAwait(false);
            tradePartner.NSAID = tradePartnerNID;

            Log($"Found trading partner: {tradePartner.TrainerName}-{tradePartner.TID}-{tradePartner.SID} ({poke.Trainer.TrainerName}) (NID: {tradePartnerNID}) [CODE:{poke.Code:00000000}]");

            poke.SendNotification(this, $"Found Trading Partner: {tradePartner.TrainerName}. TID: {tradePartner.TID} SID: {tradePartner.SID} Waiting for a Pokémon...");

            if (poke.Type == PokeTradeType.Dump)
                return await ProcessDumpTradeAsync(poke, token).ConfigureAwait(false);

            if (poke.Type == PokeTradeType.Random)
                if (CheckPartnerReputation(poke, tradePartnerNID, tradePartner.TrainerName, token) != PokeTradeResult.Success)
                    return PokeTradeResult.SuspiciousActivity;

            // Confirm Box 1 Slot 1
            if (poke.Type == PokeTradeType.Specific)
            {
                for (int i = 0; i < 10; i++)
                    await Click(A, 0_500, token).ConfigureAwait(false);
            }

            var offered = await ReadUntilPresentPointer(LinkTradePartnerPokemonPointer, 25_000, 1_000, TradeFormatSlotSize, token).ConfigureAwait(false);
            Log("Pointer is present with a pokemon.");

            var offset = await SwitchConnection.PointerAll(LinkTradePartnerPokemonPointer, token).ConfigureAwait(false);
            var oldEC = await SwitchConnection.ReadBytesAbsoluteAsync(offset, 4, token).ConfigureAwait(false);
            if (offered is null)
            {
                Log("Offered is NULL");
                await AttemptGetBackToPokePortal(token).ConfigureAwait(false);
                return PokeTradeResult.NoPokemonDetected;
            }

            SpecialTradeType itemReq = SpecialTradeType.None;
            if (poke.Type == PokeTradeType.Seed)
                itemReq = CheckItemRequest(ref offered, this, poke, tradePartner.TrainerName, sav);
            if (itemReq == SpecialTradeType.FailReturn)
                return PokeTradeResult.IllegalTrade;

            if (poke.Type == PokeTradeType.Seed && itemReq == SpecialTradeType.None)
            {
                // Immediately exit, we aren't trading anything.
                poke.SendNotification(this, "SSRNo held item or valid request!");
                return await EndQuickTradeAsync(poke, offered, token).ConfigureAwait(false);
            }

            PokeTradeResult update;
            (toSend, update) = await GetEntityToSend(sav, poke, offered, oldEC, toSend, tradePartner, poke.Type == PokeTradeType.Seed ? itemReq : null, token).ConfigureAwait(false);
            if (update != PokeTradeResult.Success)
            {
                if (itemReq != SpecialTradeType.None)
                    poke.SendNotification(this, "SSRYour request isn't legal. Please try a different Pokémon or request.");
                return update;
            }

            if (itemReq == SpecialTradeType.WonderCard)
                poke.SendNotification(this, "SSRDistribution success!");
            else if (itemReq != SpecialTradeType.None && itemReq != SpecialTradeType.Shinify)
                poke.SendNotification(this, "SSRSpecial request successful!");
            else if (itemReq == SpecialTradeType.Shinify)
                poke.SendNotification(this, "SSRShinify success! Thanks for being part of the community!");

            Log("Confirming trade...");

            var tradeResult = await ConfirmAndStartTrading(poke, token).ConfigureAwait(false);
            if (tradeResult == PokeTradeResult.Hiccup_Server || tradeResult == PokeTradeResult.TrainerHasBadConnection)
            {
                Log("Connection hiccup detected! Waiting it out...");
                await Click(A, 0_100, token).ConfigureAwait(false);
                await Task.Delay(2_900, token).ConfigureAwait(false);
            }
            else if (tradeResult != PokeTradeResult.Success)
                return tradeResult;

            if (token.IsCancellationRequested)
                return PokeTradeResult.RoutineCancel;

            // Trade was Successful!
            var received = await ReadBoxPokemon(1, 1, token).ConfigureAwait(false);
            // Pokémon in b1s1 is same as the one they were supposed to receive (was never sent).
            if (SearchUtil.HashByDetails(received) == SearchUtil.HashByDetails(toSend))
            {
                Log($"User did not complete the trade. Sent:");
                if (tradeResult == PokeTradeResult.TrainerHasBadConnection)
                    return PokeTradeResult.TrainerHasBadConnection;
                return PokeTradeResult.NoPokemonDetected;
            }

            // As long as we got rid of our inject in b1s1, assume the trade went through.
            Log("User completed the trade.");
            poke.TradeFinished(this, received);

            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                DumpPokemon(DumpSetting.DumpFolder, "trade", received);

            await AttemptGetBackToPokePortal(token).ConfigureAwait(false);
            return PokeTradeResult.Success;
        }

        private async Task<PokeTradeResult> ConfirmAndStartTrading(PokeTradeDetail<PK9> detail, CancellationToken token)
        {
            var oldPKData = await SwitchConnection.PointerPeek(BoxFormatSlotSize, BoxStartPokemonPointer, token).ConfigureAwait(false);

            await Click(A, 2_000, token).ConfigureAwait(false);
            for (int i = 0; i < 14; i++)
            {
                await Click(A, 1_000, token).ConfigureAwait(false);
            }

            await Click(A, 2_000, token).ConfigureAwait(false);
            var tradeCounter = 0;
            while (await IsPokePortalLoaded(token).ConfigureAwait(false))
            {
                await Click(A, 0_800, token).ConfigureAwait(false);
                tradeCounter++;

                var v1 = await SwitchConnection.PointerPeek(BoxFormatSlotSize, BoxStartPokemonPointer, token).ConfigureAwait(false);
                if (!v1.SequenceEqual(oldPKData))
                {
                    await Task.Delay(16_000 + Hub.Config.Timings.ExtraTimeTradeAnimation, token).ConfigureAwait(false);
                    return PokeTradeResult.Success;
                }
                if (tradeCounter >= Hub.Config.Trade.TradeConfiguration.TradeAnimationMaxDelaySeconds)
                    break;
            }

            if (detail.Type == PokeTradeType.Specific && !await IsPokePortalLoaded(token).ConfigureAwait(false)) // One last chance to force them to take the pokemon
                for (int i = 0; i < 8; i++)
                    await Click(A, 0_300, token).ConfigureAwait(false);
            
            // If we don't detect a B1S1 change, the trade didn't go through in that time.
            return PokeTradeResult.TrainerHasBadConnection;
        }

        static readonly byte[] EmptyByteArray = new byte[16];
        private async Task<bool> BeginTradeViaCode(PokeTradeDetail<PK9> poke, int tradeCode, CancellationToken token)
        {
            if (!await IsKeyboardOpen(token).ConfigureAwait(false))
            {
                Log($"Starting new trade, but keyboard was not open!");
                return false;
            }

            Log($"Starting new trade, keyboard is open! Entering Link Trade code: {tradeCode:0000 0000}...");
            poke.SendNotification(this, $"Entering Link Trade Code: {tradeCode:0000 0000}...");

            // Just inject the code instead
            var offs = await SwitchConnection.PointerAll(KeyboardBufferPointer, token).ConfigureAwait(false);
            var keyboardbytes = await SwitchConnection.ReadBytesAbsoluteAsync(offs, 16, token).ConfigureAwait(false);
            if (keyboardbytes.SequenceEqual(EmptyByteArray))
            {
                // get out of keyboard
                    await Click(PLUS, 0_800, token).ConfigureAwait(false);

                // as we inject the code, a wait should be placed here to give the other trainer time to setup
                if (poke.Type == PokeTradeType.Specific)
                    await Task.Delay(Hub.Config.Timings.ExtraTimeOpenCodeEntry, token).ConfigureAwait(false);

                // inject
                var codeText = $"{tradeCode:00000000}";
                var codeBytes = Encoding.Unicode.GetBytes(codeText);
                await SwitchConnection.WriteBytesAbsoluteAsync(codeBytes, offs, token).ConfigureAwait(false);

                // get back in (cycle)
                await Click(PLUS, 0_800, token).ConfigureAwait(false); 
            }

            // Wait for Barrier to trigger all bots simultaneously.
            WaitAtBarrierIfApplicable(token);

            await Task.Delay(0_400, token).ConfigureAwait(false);
            await Click(PLUS, 0_800, token).ConfigureAwait(false);
            for (int i = 0; i < 5; ++i)
                await Click(A, 0_350, token).ConfigureAwait(false);

            int checks = 3;
            while (!await IsSearching(token).ConfigureAwait(false))
            {
                await Task.Delay(0_800).ConfigureAwait(false);
                if (checks-- < 0)
                    return false;
            }    

            return true;
        }

        private async Task<PokeTradeResult> ProcessDumpTradeAsync(PokeTradeDetail<PK9> detail, CancellationToken token)
        {
            int ctr = 0;
            var time = TimeSpan.FromSeconds(Hub.Config.Trade.TradeConfiguration.MaxDumpTradeTime);
            var start = DateTime.Now;
            var pkprev = new PK9();
            while (ctr < Hub.Config.Trade.TradeConfiguration.MaxDumpsPerTrade && DateTime.Now - start < time)
            {
                var pk = await ReadUntilPresentPointer(LinkTradePartnerPokemonPointer, 3_000, 1_000, TradeFormatSlotSize, token).ConfigureAwait(false);
                if (pk == null || pk.Species < 1 || !pk.ChecksumValid || SearchUtil.HashByDetails(pk) == SearchUtil.HashByDetails(pkprev))
                    continue;

                // Save the new Pokémon for comparison next round.
                pkprev = pk;

                // Send results from separate thread; the bot doesn't need to wait for things to be calculated.
            if (DumpSetting.Dump)
            {
                var subfolder = detail.Type.ToString().ToLower();
                DumpPokemon(DumpSetting.DumpFolder, subfolder, pk); // received
            }

                var la = new LegalityAnalysis(pk);
                var verbose = la.Report(true);
                Log($"Shown Pokémon is: {(la.Valid ? "Valid" : "Invalid")}.");

                detail.SendNotification(this, pk, verbose);
                ctr++;
            }

            Log($"Ended Dump loop after processing {ctr} Pokémon.");
            if (ctr == 0)
                return PokeTradeResult.NoPokemonDetected;

            detail.Notifier.SendNotification(this, detail, $"Dumped {ctr} Pokémon.");
            detail.Notifier.TradeFinished(this, detail, new PK9()); // blank
            return PokeTradeResult.Success;
        }

        protected virtual async Task<(PK9 toSend, PokeTradeResult check)> GetEntityToSend(SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, byte[] oldEC, PK9 toSend, TrainerIDBlock partnerID, SpecialTradeType? stt, CancellationToken token)
        {
            return poke.Type switch
            {
                PokeTradeType.Random => await HandleRandomLedy(sav, poke, offered, toSend, partnerID, token).ConfigureAwait(false),
                PokeTradeType.Clone => await HandleClone(sav, poke, offered, oldEC, token).ConfigureAwait(false),
                PokeTradeType.Seed when stt is not SpecialTradeType.WonderCard => await HandleClone(sav, poke, offered, oldEC, token).ConfigureAwait(false),
                PokeTradeType.Seed when stt is SpecialTradeType.WonderCard => await JustInject(sav, offered, token).ConfigureAwait(false),
                _ => (await ApplyAutoOT(toSend, poke, partnerID, sav, token).ConfigureAwait(false), PokeTradeResult.Success),
            };
        }

        private async Task<PK9> ApplyAutoOT(PK9 toSend, PokeTradeDetail<PK9> poke, TrainerIDBlock partner, SAV9SV sav, CancellationToken token)
        {
            if (!Hub.Config.Global.Legality.UseTradePartnerInfo || poke.IgnoreAutoOT)
                return toSend;

            // Special handling for Pokémon GO
            if (toSend.Version == GameVersion.GO)
            {
                var goClone = (PK9)toSend.Clone();

                // Check if GO Pokemon has a home tracker
                if (toSend is IHomeTrack { HasTracker: true })
                {
                    // Can only change OT name if it has a home tracker
                    goClone.OriginalTrainerName = partner.TrainerName;
                    ClearOTTrash(goClone, partner.TrainerName);

                    if (!toSend.ChecksumValid)
                        goClone.RefreshChecksum();

                    Log("Applied only OT name to Pokémon from GO (has HOME tracker).");
                    await SetBoxPokemon(goClone, token, sav).ConfigureAwait(false);
                    return goClone;
                }
                else
                {
                    // No home tracker: can apply OT, TID, and SID
                    goClone.OriginalTrainerName = partner.TrainerName;
                    goClone.OriginalTrainerGender = partner.Gender;
                    goClone.TrainerTID7 = (uint)partner.TID7;
                    goClone.TrainerSID7 = (uint)partner.SID7;

                    ClearOTTrash(goClone, partner.TrainerName);

                    if (toSend.IsShiny)
                        goClone.PID = (uint)((goClone.TID16 ^ goClone.SID16 ^ (goClone.PID & 0xFFFF) ^ toSend.ShinyXor) << 16) | (goClone.PID & 0xFFFF);

                    if (!toSend.ChecksumValid)
                        goClone.RefreshChecksum();

                    Log("Applied OT, TID, and SID to Pokémon from GO (no HOME tracker).");
                    await SetBoxPokemon(goClone, token, sav).ConfigureAwait(false);
                    return goClone;
                }
            }

            // Check for Home Tracker (Non-GO)
            if (toSend is IHomeTrack pk && pk.HasTracker)
            {
                Log("Home tracker detected. Can't apply AutoOT.");
                return toSend;
            }

            var cln = (PK9)toSend.Clone();

            // Check if the Pokémon is from a Mystery Gift
            bool isMysteryGift = toSend.FatefulEncounter;

            if (isMysteryGift)
            {
                Log("Mystery Gift detected. Only applying OT info, preserving language.");
                // Only set OT-related info for Mystery Gifts without preset OT/TID/SID
                cln.OriginalTrainerGender = partner.Gender;
                cln.TrainerTID7 = (uint)partner.TID7;
                cln.TrainerSID7 = (uint)partner.SID7;
                cln.OriginalTrainerName = partner.TrainerName;
            }
            else
            {
                cln.OriginalTrainerName = partner.TrainerName;
                cln.OriginalTrainerGender = partner.Gender;
                cln.TrainerTID7 = (uint)partner.TID7;
                cln.TrainerSID7 = (uint)partner.SID7;
                cln.Language = partner.Language;
            }

            ClearOTTrash(cln, partner.TrainerName);

            if (cln.IsShiny)
                cln.PID = (uint)((cln.TID16 ^ cln.SID16 ^ (cln.PID & 0xFFFF) ^ toSend.ShinyXor) << 16) | (cln.PID & 0xFFFF);

            if (!cln.IsNicknamed)
                cln.ClearNickname();

            cln.RefreshChecksum();

            var la = new LegalityAnalysis(cln);
            if (la.Valid)
            {
                Log("Pokemon is valid with Trade Partner Info applied. Swapping details.");
                await SetBoxPokemon(cln, token, sav).ConfigureAwait(false);
                return cln;
            }
            else
            {
                Log("Pokemon not valid after using Trade Partner Info.");
                return toSend;
            }
        }

        private static void ClearOTTrash(PK9 pokemon, string trainerName)
        {
            Span<byte> trash = pokemon.OriginalTrainerTrash;
            trash.Clear();
            int maxLength = trash.Length / 2;
            int actualLength = Math.Min(trainerName.Length, maxLength);
            for (int i = 0; i < actualLength; i++)
            {
                char value = trainerName[i];
                trash[i * 2] = (byte)value;
                trash[(i * 2) + 1] = (byte)(value >> 8);
            }
            if (actualLength < maxLength)
            {
                trash[actualLength * 2] = 0x00;
                trash[(actualLength * 2) + 1] = 0x00;
            }
        }

        private async Task<(PK9 toSend, PokeTradeResult check)> HandleClone(SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, byte[] oldEC, CancellationToken token)
        {
            if (Hub.Config.Integration.Discord.ReturnPKMs)
                poke.SendNotification(this, offered, "Here's what you showed me!");

            var la = new LegalityAnalysis(offered);
            if (!la.Valid)
            {
                Log($"Clone request (from {poke.Trainer.TrainerName}) has detected an invalid Pokémon: {(Species)offered.Species}.");
                if (DumpSetting.Dump)
                    DumpPokemon(DumpSetting.DumpFolder, "hacked", offered);

                var report = la.Report();
                Log(report);
                poke.SendNotification(this, "This Pokémon is not legal per PKHeX's legality checks. I am forbidden from cloning this. Exiting trade.");
                poke.SendNotification(this, report);

                return (offered, PokeTradeResult.IllegalTrade);
            }

            // Inject the shown Pokémon.
            var clone = (PK9)offered.Clone();

            poke.SendNotification(this, $"**Cloned your {(Species)clone.Species}!**\nNow press B to cancel your offer and trade me a Pokémon you don't want.");
            Log($"Cloned a {(Species)clone.Species}. Waiting for user to change their Pokémon...");

            // Separate this out from WaitForPokemonChanged since we compare to old EC from original read.
            var valid = false;
            var offset = 0ul;
            while (!valid)
            {
                await Task.Delay(0_500, token).ConfigureAwait(false);
                (valid, offset) = await ValidatePointerAll(LinkTradePartnerPokemonPointer, token).ConfigureAwait(false);
            }

            var pkmChanged = await ReadUntilChanged(offset, oldEC, 15_000, 0_200, false, true, token).ConfigureAwait(false);

            if (!pkmChanged)
            {
                poke.SendNotification(this, "**HEY CHANGE IT NOW OR I AM LEAVING!!!**");
                // They get one more chance.
                pkmChanged = await ReadUntilChanged(offset, oldEC, 15_000, 0_200, false, true, token).ConfigureAwait(false);
            }

            // Dump if required
            if (DumpSetting.Dump)
                DumpPokemon(DumpSetting.DumpFolder, "clone", offered);

            // resolve pointer for any shifts
            offset = await SwitchConnection.PointerAll(LinkTradePartnerPokemonPointer, token).ConfigureAwait(false);
            var pk2 = await ReadUntilPresent(offset, 3_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
            if (!pkmChanged || pk2 == null || SearchUtil.HashByDetails(pk2) == SearchUtil.HashByDetails(offered))
            {
                Log("Trade partner did not change their Pokémon.");
                return (offered, PokeTradeResult.NoPokemonDetected);
            }

            await SetBoxPokemon(clone, token, sav).ConfigureAwait(false);
            await Click(A, 0_600, token).ConfigureAwait(false);

            for (int i = 0; i < 5; i++)
                await Click(A, 0_350, token).ConfigureAwait(false);

            return (clone, PokeTradeResult.Success);
        }

        private async Task<(PK9 toSend, PokeTradeResult check)> HandleRandomLedy(SAV9SV sav, PokeTradeDetail<PK9> poke, PK9 offered, PK9 toSend, TrainerIDBlock partner, CancellationToken token)
        {
            // Allow the trade partner to do a Ledy swap.
            var config = Hub.Config.TradeSystem.Distribution;
            var trade = Hub.Ledy.GetLedyTrade(offered, partner.NSAID, config.LedySpecies);
            if (trade != null)
            {
                if (trade.Type == LedyResponseType.AbuseDetected)
                {
                    var msg = $"Found {partner.TrainerName} has been detected for abusing Ledy trades.";
                    EchoUtil.Echo(msg);

                    return (toSend, PokeTradeResult.SuspiciousActivity);
                }

                toSend = trade.Receive;
                poke.TradeData = toSend;

                toSend = await ApplyAutoOT(toSend, poke, partner, sav, token).ConfigureAwait(false);

                poke.SendNotification(this, "Injecting the requested Pokémon.");
                await Click(A, 0_800, token).ConfigureAwait(false);
                await SetBoxPokemon(toSend, token, sav).ConfigureAwait(false);
                await Task.Delay(1_000, token).ConfigureAwait(false);
            }
            else if (config.LedyQuitIfNoMatch)
            {
                return (toSend, PokeTradeResult.TrainerRequestBad);
            }

            for (int i = 0; i < 5; i++)
            {
                await Click(A, 0_500, token).ConfigureAwait(false);
            }

            return (toSend, PokeTradeResult.Success);
        }

        private async Task<(PK9 toSend, PokeTradeResult check)> JustInject(SAV9SV sav, PK9 offered, CancellationToken token)
        {
            await Click(A, 0_800, token).ConfigureAwait(false);
            await SetBoxPokemon(offered, token, sav).ConfigureAwait(false);

            for (int i = 0; i < 5; i++)
                await Click(A, 0_500, token).ConfigureAwait(false);

            return (offered, PokeTradeResult.Success);
        }

        private async Task<PokeTradeResult> EndQuickTradeAsync(PokeTradeDetail<PK9> detail, PK9 pk, CancellationToken token)
        {
            int attempts = 20;
            while (!await IsPokePortalLoaded(token).ConfigureAwait(false))
            {
                await Click(B, 0_600, token).ConfigureAwait(false);
                await Click(B, 0_600, token).ConfigureAwait(false);
                await Click(A, 0_900, token).ConfigureAwait(false);
                if (attempts-- < 1)
                    return PokeTradeResult.RecoverReturnOverworld;
            }

            detail.TradeFinished(this, pk);

            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                DumpPokemon(DumpSetting.DumpFolder, "quick", pk);

            return PokeTradeResult.Success;
        }

        private void HandleAbortedTrade(PokeTradeDetail<PK9> detail, PokeRoutineType type, uint priority, PokeTradeResult result)
        {
            detail.IsProcessing = false;
            if (result.ShouldAttemptRetry() && detail.Type != PokeTradeType.Random && !detail.IsRetry)
            {
                detail.IsRetry = true;
                Hub.Queues.Enqueue(type, detail, Math.Min(priority, PokeTradePriorities.Tier2));
                detail.SendNotification(this, "Oops! Something happened. I'm going to requeue you for another attempt, give me a moment.");
            }
            else
            {
                detail.SendNotification(this, $"Oops! Something happened. Canceling the trade due to reason: {result}.");
                detail.TradeCanceled(this, result);
            }
        }

        private void WaitAtBarrierIfApplicable(CancellationToken token)
        {
            if (!ShouldWaitAtBarrier)
                return;
            var opt = Hub.Config.TradeSystem.Distribution.SynchronizeBots;
            if (opt == BotSyncOption.NoSync)
                return;

            var timeoutAfter = Hub.Config.TradeSystem.Distribution.SynchronizeTimeout;
            if (FailedBarrier == 1) // failed last iteration
                timeoutAfter *= 2; // try to re-sync in the event things are too slow.

            var result = Hub.BotSync.Barrier.SignalAndWait(TimeSpan.FromSeconds(timeoutAfter), token);

            if (result)
            {
                FailedBarrier = 0;
                return;
            }

            FailedBarrier++;
            Log($"Barrier sync timed out after {timeoutAfter} seconds. Continuing.");
        }


        /// <summary>
        /// Checks if the barrier needs to get updated to consider this bot.
        /// If it should be considered, it adds it to the barrier if it is not already added.
        /// If it should not be considered, it removes it from the barrier if not already removed.
        /// </summary>
        private void UpdateBarrier(bool shouldWait)
        {
            if (ShouldWaitAtBarrier == shouldWait)
                return; // no change required

            ShouldWaitAtBarrier = shouldWait;
            if (shouldWait)
            {
                Hub.BotSync.Barrier.AddParticipant();
                Log($"Joined the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
            else
            {
                Hub.BotSync.Barrier.RemoveParticipant();
                Log($"Left the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
        }

        private void SetText(SAV9SV sav, string text)
        {
            System.IO.File.WriteAllText($"code{sav.OT}-{sav.DisplayTID}.txt", text);
        }

        private async Task ClearKeyboardBuffer(int? code, CancellationToken token)
        {
            (var valid, var offs) = await ValidatePointerAll(KeyboardBufferPointer, token).ConfigureAwait(false);
            if (!valid)
                return;

            if (code.HasValue)
            {
                var codeText = $"{code:00000000}";
                var codeBytes = Encoding.Unicode.GetBytes(codeText);
                await SwitchConnection.WriteBytesAbsoluteAsync(codeBytes, offs, token).ConfigureAwait(false);
            }
            else
                await SwitchConnection.WriteBytesAbsoluteAsync(new byte[0x10], offs, token).ConfigureAwait(false);
        }

        private PokeTradeResult CheckPartnerReputation(PokeTradeDetail<PK9> poke, ulong TrainerNID, string TrainerName, CancellationToken token)
        {
            var user = poke.Trainer;
            var result = CheckPartnerReputation(poke, TrainerNID, TrainerName, AbuseSettings);

            if (result != PokeTradeResult.Success)
            {
                var isDistribution = poke.Type == PokeTradeType.Random;
                var useridmsg = isDistribution ? "" : $" ({user.ID})";
                var msg = $"{user.TrainerName}{useridmsg} has been flagged for suspicious activity while trading with OT: {TrainerName}.";
                EchoUtil.Echo(msg);
                return result;
            }

            return PokeTradeResult.Success;
        }

        private async Task<PokeTradeResult> PerformBatchTrade(SAV9SV sav, PokeTradeDetail<PK9> poke, CancellationToken token)
        {
            var startingDetail = poke;
            var tradesToProcess = poke.BatchTrades ?? [poke.TradeData];
            var totalBatchTrades = tradesToProcess.Count;
            TrainerIDBlock? cachedPartner = null;
            var originalTrainerID = startingDetail.Trainer.ID;

            // Helper to clean up on abort
            void SendCollectedPokemonAndCleanup()
            {
                var allReceived = BatchTracker.GetReceivedPokemon(originalTrainerID);
                if (allReceived.Count > 0)
                {
                    poke.SendNotification(this, $"Sending you the {allReceived.Count} Pokémon you traded to me before the interruption.");
                    for (int j = 0; j < allReceived.Count; j++)
                    {
                        var pokemon = allReceived[j];
                        var speciesName = SpeciesName.GetSpeciesName(pokemon.Species, 2);
                        poke.SendNotification(this, pokemon, $"Pokémon you traded to me: {speciesName}");
                        Thread.Sleep(500);
                    }
                }
                BatchTracker.ClearReceivedPokemon(originalTrainerID);
                BatchTracker.ReleaseBatch(originalTrainerID, startingDetail.UniqueTradeID);
                poke.IsProcessing = false;
                Hub.Queues.Info.Remove(new TradeEntry<PK9>(poke, originalTrainerID, PokeRoutineType.Batch, poke.Trainer.TrainerName, poke.UniqueTradeID));
            }

            for (int i = 0; i < totalBatchTrades; i++)
            {
                var currentTradeIndex = i;
                var toSend = tradesToProcess[currentTradeIndex];
                poke.TradeData = toSend;
                poke.Notifier.UpdateBatchProgress(currentTradeIndex + 1, toSend, poke.UniqueTradeID);

                // Apply AutoOT if we have cached partner info (Optimization)
                if (cachedPartner != null && Hub.Config.Global.Legality.UseTradePartnerInfo && !poke.IgnoreAutoOT)
                {
                    toSend = await ApplyAutoOT(toSend, poke, cachedPartner, sav, token).ConfigureAwait(false);
                    poke.TradeData = toSend;
                    tradesToProcess[currentTradeIndex] = toSend;
                }

                if (poke.Type == PokeTradeType.Random)
                    SetText(sav, $"Trade code: {poke.Code:0000 0000}\r\nSending: {(Species)poke.TradeData.Species}{(poke.TradeData.IsEgg ? " (egg)" : string.Empty)}");
                else
                    SetText(sav, $"Batch Trade {currentTradeIndex + 1}/{totalBatchTrades}");

                UpdateBarrier(poke.IsSynchronized);
                poke.TradeInitialize(this);
                Hub.Config.Integration.Stream.EndEnterCode(this);

                if (poke.Type != PokeTradeType.Random)
                    Hub.Config.Integration.Stream.StartEnterCode(this);

                // Set box pokemon (First inject)
                if (toSend.Species != 0)
                    await SetBoxPokemon(toSend, token, sav).ConfigureAwait(false);

                if (!await IsKeyboardOpen(token).ConfigureAwait(false))
                {
                    await Click(A, 0_500, token).ConfigureAwait(false);
                    // If failing to start, we should probably abort or retry the whole batch
                     SendCollectedPokemonAndCleanup();
                     return PokeTradeResult.RecoverStart;
                }

                if (!await BeginTradeViaCode(poke, poke.Code, token).ConfigureAwait(false))
                {
                    for (int retry = 0; retry < 5; ++retry)
                        await Click(B, 0_500, token).ConfigureAwait(false);
                    await RestartGameIfCantTrade(false, null, token).ConfigureAwait(false);
                    SendCollectedPokemonAndCleanup();
                    return PokeTradeResult.RecoverOpenBox;
                }

                poke.TradeSearching(this);
                if (currentTradeIndex > 0)
                     poke.SendNotification(this, $"**Ready!** You can now offer your Pokémon for trade {currentTradeIndex + 1}/{totalBatchTrades}.");

                // Wait for partner
                int inBoxChecks = Hub.Config.Trade.TradeConfiguration.TradeWaitTime;
                bool partnerFound = true;
                while (await IsPokePortalLoaded(token).ConfigureAwait(false))
                {
                    if (inBoxChecks-- < 0)
                    {
                        await Click(B, 1_500, token).ConfigureAwait(false);
                        if (await IsPokePortalLoaded(token).ConfigureAwait(false))
                        {
                            await Click(A, 1_500, token).ConfigureAwait(false);
                            await ClearKeyboardBuffer(null, token).ConfigureAwait(false);
                            await Click(PLUS, 0_800, token).ConfigureAwait(false);
                            partnerFound = false;
                            break;
                        }
                    }
                    await Task.Delay(1_000, token).ConfigureAwait(false);
                }

                if (!partnerFound)
                {
                    SendCollectedPokemonAndCleanup();
                    return PokeTradeResult.NoTrainerFound;
                }

                // Partner found
                await Task.Delay(2_000, token).ConfigureAwait(false);
                Hub.Config.Integration.Stream.EndEnterCode(this);

                if (poke.Type == PokeTradeType.Random)
                    await ClearKeyboardBuffer(null, token).ConfigureAwait(false);

                var tradePartnerNID = await GetTradePartnerNID(token).ConfigureAwait(false);
                var tradePartner = await FetchIDFromTradeOffset(token).ConfigureAwait(false);
                tradePartner.NSAID = tradePartnerNID;
                cachedPartner = tradePartner; // Cache for next trades

                Log($"Found trading partner: {tradePartner.TrainerName}-{tradePartner.TID}-{tradePartner.SID} ({poke.Trainer.TrainerName}) (NID: {tradePartnerNID})");
                poke.SendNotification(this, $"Found Trading Partner: {tradePartner.TrainerName}. Trade {currentTradeIndex + 1}/{totalBatchTrades}.");

                // Reputation check
                if (poke.Type == PokeTradeType.Random)
                    if (CheckPartnerReputation(poke, tradePartnerNID, tradePartner.TrainerName, token) != PokeTradeResult.Success)
                    {
                        SendCollectedPokemonAndCleanup();
                        return PokeTradeResult.SuspiciousActivity;
                    }

                // Wait for offer
                var offered = await ReadUntilPresentPointer(LinkTradePartnerPokemonPointer, 25_000, 1_000, TradeFormatSlotSize, token).ConfigureAwait(false);
                var offset = await SwitchConnection.PointerAll(LinkTradePartnerPokemonPointer, token).ConfigureAwait(false);
                var oldEC = await SwitchConnection.ReadBytesAbsoluteAsync(offset, 4, token).ConfigureAwait(false);

                if (offered is null)
                {
                    await AttemptGetBackToPokePortal(token).ConfigureAwait(false);
                    SendCollectedPokemonAndCleanup();
                    return PokeTradeResult.NoPokemonDetected;
                }

                // Prepare entity to send (Apply AutoOT again/verify)
                PokeTradeResult update;
                (toSend, update) = await GetEntityToSend(sav, poke, offered, oldEC, toSend, tradePartner, null, token).ConfigureAwait(false);
                
                if (update != PokeTradeResult.Success)
                {
                    SendCollectedPokemonAndCleanup();
                    return update;
                }

                // Confirm
                var tradeResult = await ConfirmAndStartTrading(poke, token).ConfigureAwait(false);
                if (tradeResult != PokeTradeResult.Success)
                {
                     SendCollectedPokemonAndCleanup();
                     return tradeResult;
                }

                if (token.IsCancellationRequested)
                {
                     SendCollectedPokemonAndCleanup();
                     return PokeTradeResult.RoutineCancel;
                }

                // Verify success
                var received = await ReadBoxPokemon(1, 1, token).ConfigureAwait(false);
                if (SearchUtil.HashByDetails(received) == SearchUtil.HashByDetails(toSend))
                {
                    Log($"User did not complete the trade.");
                    SendCollectedPokemonAndCleanup();
                    return PokeTradeResult.NoPokemonDetected;
                }

                // Success
                Log($"Batch Trade {currentTradeIndex + 1}/{totalBatchTrades} completed.");
                BatchTracker.AddReceivedPokemon(originalTrainerID, received);
                
                // Dump
                if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                    DumpPokemon(DumpSetting.DumpFolder, "trade", received);

                await AttemptGetBackToPokePortal(token).ConfigureAwait(false);
            }

            // All trades done
            var finalAllReceived = BatchTracker.GetReceivedPokemon(originalTrainerID);
            poke.SendNotification(this, "All batch trades completed! Thank you for trading!");
            
            if (Hub.Config.Integration.Discord.ReturnPKMs && finalAllReceived.Count > 0)
            {
                poke.SendNotification(this, $"Here are the {finalAllReceived.Count} Pokémon you traded to me:");
                for (int j = 0; j < finalAllReceived.Count; j++)
                {
                    var pokemon = finalAllReceived[j];
                    var speciesName = SpeciesName.GetSpeciesName(pokemon.Species, 2);
                    poke.SendNotification(this, pokemon, $"Pokémon you traded to me: {speciesName}");
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }

            if (finalAllReceived.Count > 0)
                poke.TradeFinished(this, finalAllReceived[^1]);
            else
                poke.TradeFinished(this, new PK9());

            // Remove from queue
            Hub.Queues.CompleteTrade(startingDetail); 
            
            BatchTracker.ClearReceivedPokemon(originalTrainerID);
            return PokeTradeResult.Success;
        }

        private async Task HandleAbortedBatchTrade(PokeTradeDetail<PK9> detail, PokeRoutineType type, uint priority, PokeTradeResult result, CancellationToken token)
        {
            detail.IsProcessing = false;
            Hub.Queues.Info.Remove(new TradeEntry<PK9>(detail, detail.Trainer.ID, type, detail.Trainer.TrainerName, detail.UniqueTradeID));

            if ((detail.BatchTrades?.Count ?? 0) > 1)
            {
                BatchTracker.ReleaseBatch(detail.Trainer.ID, detail.UniqueTradeID);

                if (result.ShouldAttemptRetry() && detail.Type != PokeTradeType.Random && !detail.IsRetry)
                {
                    detail.IsRetry = true;
                    Hub.Queues.Enqueue(type, detail, Math.Min(priority, PokeTradePriorities.Tier2));
                    detail.SendNotification(this, "Oops! Something happened during your batch trade. I'll requeue you for another attempt.");
                }
                else
                {
                    detail.SendNotification(this, $"Batch trade failed: {result}");
                    detail.TradeCanceled(this, result);
                }
            }
            else
            {
                HandleAbortedTrade(detail, type, priority, result);
            }
        }
}
