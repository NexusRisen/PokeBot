using PKHeX.Core;
using SysBot.Base;
using SysBot.Pokemon.Helpers;
using SysBot.Pokemon.WinForms.Properties;
using SysBot.Pokemon.WinForms.Helpers;
using SysBot.Pokemon.Z3;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Windows.Forms;

namespace SysBot.Pokemon.WinForms
{
    public sealed partial class Main : Form
    {
        // Windows API for forcing window frame update
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, 
            int X, int Y, int cx, int cy, uint uFlags);
        
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;
        
        // Performance optimization flags
        private bool _suspendLayout = false;
        private readonly List<PokeBotState> Bots = [];

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal ProgramConfig Config { get; set; } = null!;

        private IPokeBotRunner RunningEnvironment { get; set; } = null!;

        public readonly ISwitchConnectionAsync? SwitchConnection;
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public static volatile bool IsUpdating = false;
        private System.Windows.Forms.Timer? _autoSaveTimer;
        private System.Windows.Forms.Timer? _logCleanupTimer;
        private bool _isFormLoading = true;

        private SearchManager _searchManager = null!;
        private TextBoxForwarder _textBoxForwarder = null!;

        internal bool hasUpdate = false;
        private bool _isRestoringFromTray = false;
        private LinearGradientBrush? _logoBrush;
        private Image? _currentModeImage = null;

        public Main()
        {
            // Enable DPI awareness
            this.AutoScaleMode = AutoScaleMode.Dpi;
            
            InitializeComponent();
            
            // Enable double buffering for PropertyGrid to reduce flickering/glitches
            EnableDoubleBuffering(PG_Hub);

            // Performance optimizations
            SetStyle(ControlStyles.AllPaintingInWmPaint | 
                    ControlStyles.UserPaint | 
                    ControlStyles.DoubleBuffer | 
                    ControlStyles.ResizeRedraw |
                    ControlStyles.OptimizedDoubleBuffer, true);
            UpdateStyles();
            
            // Apply dark mode to the main window
            DarkModeHelper.SetDarkMode(this.Handle);
            
            // Apply Alienware Theme
            WinFormsTheme.Apply(this);

            Text = $"PokéBot Control Center {PokeBot.Version}";

            Load += async (sender, e) => await InitializeAsync();

            TC_Main = new TabControl { Visible = false };
            // Setup TabControl for Alienware styling even if hidden
            TC_Main.DrawMode = TabDrawMode.OwnerDrawFixed;
            TC_Main.DrawItem += WinFormsTheme.DrawTabControl;

            Tab_Bots = new TabPage();
            Tab_Hub = new TabPage();
            Tab_Logs = new TabPage();
            TC_Main.TabPages.AddRange([Tab_Bots, Tab_Hub, Tab_Logs]);
            TC_Main.SendToBack();

            _searchManager = new SearchManager(RTB_Logs, searchStatusLabel);
            ConfigureSearchEventHandlers();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Use our custom theme background painter
            WinFormsTheme.PaintBackground(e.Graphics, this.ClientRectangle);
        }

        private void ConfigureSearchEventHandlers()
        {
            btnCaseSensitive.CheckedChanged += (s, e) => _searchManager.ToggleCaseSensitive();
            btnRegex.CheckedChanged += (s, e) => _searchManager.ToggleRegex();
            btnWholeWord.CheckedChanged += (s, e) => _searchManager.ToggleWholeWord();
        }

        private void CreateNewConfig()
        {
            Config = new ProgramConfig();
            RunningEnvironment = GetRunner(Config);
            Config.Hub.Global.Folder.CreateDefaults(Program.WorkingDirectory);
            UpdateChecker.SetRepository("NexusRisen", "PokeBot");
        }

        private async Task InitializeAsync()
        {
            if (IsUpdating)
                return;
            string discordName = string.Empty;

            PokeTradeBotSWSH.SeedChecker = new Z3SeedSearchHandler<PK8>();

            if (File.Exists(Program.ConfigPath))
            {
                try
                {
                    var lines = File.ReadAllText(Program.ConfigPath);

                    // Check for corrupted file (null bytes)
                    if (string.IsNullOrWhiteSpace(lines) || lines.Contains('\0'))
                    {
                        throw new JsonException("Config file contains null bytes or is empty");
                    }

                    Config = JsonSerializer.Deserialize(lines, ProgramConfigContext.Default.ProgramConfig) ?? new ProgramConfig();
                    
                    // Use static repository for update checks

                    LogConfig.MaxArchiveFiles = Config.Hub.Global.MaxArchiveFiles;
                    LogConfig.LoggingEnabled = Config.Hub.Global.LoggingEnabled;
                    Config.Hub.TradeSystem.Distribution.CurrentMode = Config.Mode;
                    comboBox1.SelectedValue = (int)Config.Mode;

                    RunningEnvironment = GetRunner(Config);
                    UpdateChecker.SetRepository("NexusRisen", "PokeBot");
                    foreach (var bot in Config.Bots)
                    {
                        bot.Initialize();
                        AddBot(bot);
                    }
                }
                catch (Exception ex) when (ex is JsonException || ex is NotSupportedException)
                {
                    LogUtil.LogError($"Config file is corrupted: {ex.Message}. Attempting to recover from backup.", "Config");

                    // Try to recover from backup
                    var backupPath = Program.ConfigPath + ".bak";
                    if (File.Exists(backupPath))
                    {
                        try
                        {
                            var backupLines = File.ReadAllText(backupPath);
                            Config = JsonSerializer.Deserialize(backupLines, ProgramConfigContext.Default.ProgramConfig) ?? new ProgramConfig();

                            // Restore the main config from backup
                            File.Copy(backupPath, Program.ConfigPath, true);
                            LogUtil.LogInfo("Config", "Successfully recovered configuration from backup.");

                            LogConfig.MaxArchiveFiles = Config.Hub.Global.MaxArchiveFiles;
                            LogConfig.LoggingEnabled = Config.Hub.Global.LoggingEnabled;
                            Config.Hub.TradeSystem.Distribution.CurrentMode = Config.Mode;
                            comboBox1.SelectedValue = (int)Config.Mode;

                            RunningEnvironment = GetRunner(Config);
                            UpdateChecker.SetRepository("NexusRisen", "PokeBot");
                            foreach (var bot in Config.Bots)
                            {
                                bot.Initialize();
                                AddBot(bot);
                            }
                        }
                        catch (Exception backupEx)
                        {
                            LogUtil.LogError("Config", $"Failed to recover from backup: {backupEx.Message}. Creating new configuration.");
                            CreateNewConfig();
                        }
                    }
                    else
                    {
                        LogUtil.LogError("Config", "No backup file found. Creating new configuration.");
                        CreateNewConfig();
                    }
                }
            }
            else
            {
                CreateNewConfig();
            }

            try
            {
                var (updateAvailable, _, _) = await UpdateChecker.CheckForUpdatesAsync();
                hasUpdate = updateAvailable;
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Update check failed: {ex.Message}", "Update");
            }

            RTB_Logs.MaxLength = 2_000_000; // Limit to 2MB of text to prevent memory issues
            LoadControls();
            Text = $"{(string.IsNullOrEmpty(Config.Hub.Global.BotName) ? "NexusRisen PokeBot" : Config.Hub.Global.BotName)} {PokeBot.Version} ({Config.Mode})";
            trayIcon.Text = Text;
            _ = Task.Run(BotMonitor);
            InitUtil.InitializeStubs(Config.Mode);
            _isFormLoading = false;
            UpdateBackgroundImage(Config.Mode);
            UpdateStatusIndicatorColor();
            
            this.ActiveControl = null;
            LogUtil.LogInfo("System", $"Bot initialization complete");
            _ = Task.Run(() =>
            {
                try
                {
                    this.InitWebServer();
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Failed to initialize web server: {ex.Message}", "System");
                }
            });
        }

        #region Enhanced Search Implementation

        private void LogSearchBox_TextChanged(object sender, EventArgs e)
        {
            _searchManager.UpdateSearch(logSearchBox.Text);
        }

        private void LogSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Enter:
                    e.SuppressKeyPress = true;
                    if (e.Shift)
                        _searchManager.FindPrevious();
                    else
                        _searchManager.FindNext();
                    break;

                case Keys.Escape:
                    e.SuppressKeyPress = true;
                    _searchManager.ClearSearch();
                    logSearchBox.Clear();
                    break;

                case Keys.F when e.Control:
                    e.SuppressKeyPress = true;
                    logSearchBox.Focus();
                    logSearchBox.SelectAll();
                    break;
            }
        }

        private void RTB_Logs_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.F when e.Control:
                    e.SuppressKeyPress = true;
                    logSearchBox.Focus();
                    logSearchBox.SelectAll();
                    break;

                case Keys.F3:
                    e.SuppressKeyPress = true;
                    if (e.Shift)
                        _searchManager.FindPrevious();
                    else
                        _searchManager.FindNext();
                    break;
            }
        }

        #endregion

        private static IPokeBotRunner GetRunner(ProgramConfig cfg) => cfg.Mode switch
        {
            ProgramMode.SWSH => new PokeBotRunnerImpl<PK8>(cfg.Hub, new BotFactory8SWSH(), cfg),
            ProgramMode.BDSP => new PokeBotRunnerImpl<PB8>(cfg.Hub, new BotFactory8BS(), cfg),
            ProgramMode.LA => new PokeBotRunnerImpl<PA8>(cfg.Hub, new BotFactory8LA(), cfg),
            ProgramMode.SV => new PokeBotRunnerImpl<PK9>(cfg.Hub, new BotFactory9SV(), cfg),
            ProgramMode.LGPE => new PokeBotRunnerImpl<PB7>(cfg.Hub, new BotFactory7LGPE(), cfg),
            ProgramMode.PLZA => new PokeBotRunnerImpl<PA9>(cfg.Hub, new BotFactory9PLZA(), cfg),
            _ => throw new IndexOutOfRangeException("Unsupported mode."),
        };

        private async Task BotMonitor()
        {
            while (!Disposing)
            {
                try
                {
                    // Only update UI if form is visible and not suspended
                    if (WindowState != FormWindowState.Minimized && !_suspendLayout)
                    {
                        // Batch updates to reduce UI thread blocking
                        var controllers = FLP_Bots.Controls.OfType<BotController>().ToList();
                        if (controllers.Count > 0)
                        {
                            BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
                            {
                                SuspendLayout();
                                foreach (var c in controllers)
                                    c.ReadState();
                                ResumeLayout(false);
                            }));
                        }

                        UpdateControlButtonStates();
                    }

                    if (trayIcon != null && trayIcon.Visible && Config != null)
                    {
                        // Get bot counts in a thread-safe manner
                        int runningBots = 0;
                        int totalBots = 0;

                        if (InvokeRequired)
                        {
                            // Use BeginInvoke to avoid blocking the monitoring thread
                            BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
                            {
                                runningBots = FLP_Bots.Controls.OfType<BotController>().Count(c => c.GetBot()?.IsRunning ?? false);
                                totalBots = FLP_Bots.Controls.OfType<BotController>().Count();
                                
                                // Update tray icon text from UI thread
                                string botTitle = string.IsNullOrWhiteSpace(Config.Hub.Global.BotName) ? "PokéBot" : Config.Hub.Global.BotName;
                                trayIcon.Text = totalBots == 0
                                    ? $"{botTitle} - No bots configured"
                                    : $"{botTitle} - {runningBots}/{totalBots} bots running";
                            }));
                        }
                        else
                        {
                            runningBots = FLP_Bots.Controls.OfType<BotController>().Count(c => c.GetBot()?.IsRunning ?? false);
                            totalBots = FLP_Bots.Controls.OfType<BotController>().Count();
                            
                            string botTitle = string.IsNullOrWhiteSpace(Config.Hub.Global.BotName) ? "PokéBot" : Config.Hub.Global.BotName;
                            trayIcon.Text = totalBots == 0
                                ? $"{botTitle} - No bots configured"
                                : $"{botTitle} - {runningBots}/{totalBots} bots running";
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"BotMonitor error: {ex.Message}", "Monitor");
                }
                await Task.Delay(3_000).ConfigureAwait(false); // Reduced frequency for better performance
            }
        }

        private void UpdateControlButtonStates()
        {
            if (InvokeRequired)
            {
                BeginInvoke(() => UpdateControlButtonStates());
                return;
            }

            var botControllers = FLP_Bots.Controls.OfType<BotController>().ToList(); // Cache the collection
            var runningBots = botControllers.Count(c => c.GetBot()?.IsRunning ?? false);
            var totalBots = botControllers.Count;
            var anyRunning = runningBots > 0;

            if (btnStart?.Tag is EnhancedButtonAnimationState startState)
            {
                startState.IsActive = !anyRunning && totalBots > 0;
            }

            if (btnStop?.Tag is EnhancedButtonAnimationState stopState)
            {
                stopState.IsActive = anyRunning;
            }

            if (btnReboot?.Tag is EnhancedButtonAnimationState rebootState)
            {
                rebootState.IsActive = anyRunning;
            }
        }

        private void LoadControls()
        {
            PG_Hub.SelectedObject = RunningEnvironment.Config;
            _autoSaveTimer = new System.Windows.Forms.Timer
            {
                Interval = 10_000,
                Enabled = true
            };
            _autoSaveTimer.Tick += (s, e) =>
            {
                // Run auto-save on background thread to avoid blocking UI
                Task.Run(() =>
                {
                    try
                    {
                        SaveCurrentConfig();
                    }
                    catch (Exception ex)
                    {
                        LogUtil.LogError($"Auto-save failed: {ex.Message}", "Config");
                    }
                });
            };
            var routines = ((PokeRoutineType[])Enum.GetValues(typeof(PokeRoutineType))).Where(z => RunningEnvironment.SupportsRoutine(z));
            var list = routines.Select(z => new ComboItem(z.ToString(), (int)z)).ToArray();
            CB_Routine.DisplayMember = nameof(ComboItem.Text);
            CB_Routine.ValueMember = nameof(ComboItem.Value);
            CB_Routine.DataSource = list;
            CB_Routine.SelectedValue = (int)PokeRoutineType.FlexTrade;

            var protocols = (SwitchProtocol[])Enum.GetValues(typeof(SwitchProtocol));
            var listP = protocols.Select(z => new ComboItem(z.ToString(), (int)z)).ToArray();
            CB_Protocol.DisplayMember = nameof(ComboItem.Text);
            CB_Protocol.ValueMember = nameof(ComboItem.Value);
            CB_Protocol.DataSource = listP;
            CB_Protocol.SelectedIndex = (int)SwitchProtocol.WiFi;

            var gameModes = Enum.GetValues(typeof(ProgramMode))
                .Cast<ProgramMode>()
                .Where(m => m != ProgramMode.None)
                .Select(mode => new { Text = mode.ToString(), Value = (int)mode })
                .ToList();
            comboBox1.DisplayMember = "Text";
            comboBox1.ValueMember = "Value";
            comboBox1.DataSource = gameModes;
            comboBox1.SelectedValue = (int)Config.Mode;
            
            // Apply enhanced styling to the game selector
            ConfigureGameSelector();

            _textBoxForwarder = new TextBoxForwarder(RTB_Logs);
            LogUtil.Forwarders.Add(_textBoxForwarder);

            // Initialize log cleanup timer - runs every 30 minutes
            _logCleanupTimer = new System.Windows.Forms.Timer
            {
                Interval = 30 * 60 * 1000, // 30 minutes
                Enabled = true
            };
            _logCleanupTimer.Tick += (s, e) =>
            {
                try
                {
                    // Clean up logs if they're getting too large
                    if (RTB_Logs.TextLength > RTB_Logs.MaxLength * 0.8)
                    {
                        LogUtil.LogInfo("Performing automatic log cleanup to maintain performance", "System");

                        // Keep only the last 25% of logs
                        BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
                        {
                            var lines = RTB_Logs.Lines;
                            var linesToKeep = lines.Length / 4;
                            RTB_Logs.Lines = lines[^linesToKeep..];
                            _searchManager.ClearSearch(); // Clear search after cleanup
                        }));
                    }

                    // Also clean up old log files on disk
                    CleanupOldLogFiles();
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Log cleanup failed: {ex.Message}", "System");
                }
            };

            // Developer Tools Wiring
            if (btnDevConnect != null) btnDevConnect.Click += BtnDevConnect_Click;
            if (btnMonitorToggle != null) btnMonitorToggle.Click += BtnMonitorToggle_Click;
            if (monitorTimer != null) monitorTimer.Tick += MonitorTimer_Tick;
            if (txtMonitorValue != null) txtMonitorValue.KeyDown += TxtMonitorValue_KeyDown;
            if (btnCopyAddress != null) btnCopyAddress.Click += BtnCopyAddress_Click;
            if (btnScan != null) btnScan.Click += BtnScan_Click;
            
            // Wire up AutoScan
            if (btnAutoScan != null) btnAutoScan.Click += BtnAutoScan_Click;
            if (btnDumpMain != null) btnDumpMain.Click += BtnDumpMain_Click;
            if (btnFindSig != null) btnFindSig.Click += BtnFindSig_Click;
            if (btnAutoUpdate != null) btnAutoUpdate.Click += BtnAutoUpdate_Click;
            if (btnFindChain != null) btnFindChain.Click += BtnFindChain_Click;
            if (btnVerify != null) btnVerify.Click += BtnVerify_Click;
        }

        private async void BtnVerify_Click(object? sender, EventArgs e)
        {
            if (_devConnection?.Connected != true)
            {
                MessageBox.Show("Not connected to Switch!");
                return;
            }

            var cbGame = cbGameVersion;
            var selectedGame = cbGame?.SelectedItem?.ToString() ?? "PLZA";

            var btnVerify = sender as Button;
            if (btnVerify != null) btnVerify.Enabled = false;
            
            lblScanStatus.Text = "Verifying...";
            lblScanStatus.ForeColor = Color.Yellow;
            rtbResults.Clear();

            var progress = new Progress<string>(s => 
            {
                rtbResults.AppendText(s + Environment.NewLine);
                rtbResults.ScrollToCaret();
            });

            try
            {
                string result = await PointerScanner.VerifyLoadedOffsetsAsync(_devConnection, selectedGame, progress, CancellationToken.None);
                rtbResults.AppendText("----------------" + Environment.NewLine);
                rtbResults.AppendText(result + Environment.NewLine);
                
                lblScanStatus.Text = "Done";
                lblScanStatus.ForeColor = Color.Green;
            }
            catch (Exception ex)
            {
                rtbResults.AppendText($"Error: {ex.Message}{Environment.NewLine}");
                lblScanStatus.Text = "Error";
                lblScanStatus.ForeColor = Color.Red;
            }
            finally
            {
                if (btnVerify != null) btnVerify.Enabled = true;
            }
        }

        private ProgramConfig GetCurrentConfiguration()
        {
            if (Config == null)
            {
                throw new InvalidOperationException("Config has not been initialized because a valid license was not entered.");
            }
            Config.Bots = [.. Bots];
            return Config;
        }

        private void Main_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (IsUpdating) return;

            // Remove log forwarders to prevent memory leaks
            LogUtil.Forwarders.Remove(_textBoxForwarder);

            // Let the form close normally when X button is clicked
            // No longer minimizing to tray on close
            this.StopWebServer();

            try
            {
                string? exePath = Application.ExecutablePath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    string? dirPath = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(dirPath))
                    {
                        string portInfoPath = Path.Combine(dirPath, $"MergeBot_{Environment.ProcessId}.port");
                        if (File.Exists(portInfoPath))
                            File.Delete(portInfoPath);
                    }
                }
            }
            catch { }

            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Stop();
                _autoSaveTimer.Dispose();
            }

            if (_logCleanupTimer != null)
            {
                _logCleanupTimer.Stop();
                _logCleanupTimer.Dispose();
            }

            // Animation timer removed

            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }

            if (_logoBrush != null)
            {
                _logoBrush.Dispose();
                _logoBrush = null;
            }

            SaveCurrentConfig();
            var bots = RunningEnvironment;
            if (bots == null || !bots.IsRunning)
                return;

            async Task WaitUntilNotRunning()
            {
                while (bots != null && bots.IsRunning)
                    await Task.Delay(10).ConfigureAwait(false);
            }

            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            bots?.StopAll();
            Task.WhenAny(WaitUntilNotRunning(), Task.Delay(5_000)).ConfigureAwait(true).GetAwaiter().GetResult();
        }

        private void SaveCurrentConfig()
        {
            try
            {
                var cfg = GetCurrentConfiguration();
                var json = JsonSerializer.Serialize(cfg, ProgramConfigContext.Default.ProgramConfig);

                // Use atomic write operation to prevent corruption
                var tempPath = Program.ConfigPath + ".tmp";
                var backupPath = Program.ConfigPath + ".bak";

                // Write to temporary file first
                File.WriteAllText(tempPath, json);

                // Create backup of existing config if it exists
                if (File.Exists(Program.ConfigPath))
                {
                    File.Copy(Program.ConfigPath, backupPath, true);
                }

                // Atomic rename operation
                File.Move(tempPath, Program.ConfigPath, true);

                // Delete backup after successful save
                if (File.Exists(backupPath))
                {
                    try { File.Delete(backupPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Failed to save config: {ex.Message}", "Config");
            }
        }


        private void B_Start_Click(object sender, EventArgs e)
        {
            SaveCurrentConfig();

            LogUtil.LogInfo("Form", "Starting all bots...");
            RunningEnvironment.InitializeStart();
            SendAll(BotControlCommand.Start);

            SetButtonActiveState(btnStart, true);
            SetButtonActiveState(btnStop, false);
            SetButtonActiveState(btnReboot, false);

            // Keep the Bots tab selected when starting
            foreach (Button navBtn in navButtonsPanel.Controls.OfType<Button>())
            {
                if (navBtn.Tag is NavButtonState state)
                {
                    // Keep Bots tab (index 0) selected, deselect others
                    state.IsSelected = (state.Index == 0);
                    navBtn.Invalidate();
                }
            }

            // Stay on Bots tab instead of switching to Logs
            TransitionPanels(0);
            titleLabel.Text = "Bot Management";

            if (Bots.Count == 0)
                WinFormsUtil.Alert("No bots configured, but all supporting services have been started.");
        }

        private void B_RebootStop_Click(object sender, EventArgs e)
        {
            SetButtonActiveState(btnReboot, true);
            SetButtonActiveState(btnStart, false);
            SetButtonActiveState(btnStop, false);

            Task.Run(async () =>
            {
                try
                {
                    LogUtil.LogInfo("Form", "Starting reset process...");
                    SaveCurrentConfig();

                    // Phase 1: Stop all bots gracefully
                    LogUtil.LogInfo("Form", "Phase 1: Stopping all bots...");
                    SendAll(BotControlCommand.Stop);

                    // Phase 2: Wait for all bots to fully stop
                    var stopTimeout = DateTime.Now.AddSeconds(30);
                    while (DateTime.Now < stopTimeout)
                    {
                        if (AreAllBotsStopped())
                        {
                            LogUtil.LogInfo("Form", "All bots stopped successfully");
                            break;
                        }
                        await Task.Delay(500).ConfigureAwait(false);
                    }

                    if (!AreAllBotsStopped())
                    {
                        LogUtil.LogInfo("Form", "Some bots did not stop in time, forcing stop...");
                        SendAll(BotControlCommand.Stop);
                        await Task.Delay(2000).ConfigureAwait(false);
                    }

                    // Phase 3: Stop all services
                    LogUtil.LogInfo("Form", "Phase 3: Stopping all services...");
                    await Task.Delay(2000).ConfigureAwait(false); // Give services time to fully stop

                    // Phase 4: Reinitialize environment
                    LogUtil.LogInfo("Form", "Phase 4: Reinitializing environment...");
                    RunningEnvironment.InitializeStart();
                    await Task.Delay(1000).ConfigureAwait(false);

                    // Phase 5: Reboot consoles
                    LogUtil.LogInfo("Form", "Phase 5: Rebooting all consoles...");
                    SendAll(BotControlCommand.RebootAndStop);
                    await Task.Delay(8000).ConfigureAwait(false); // Give consoles time to reboot

                    // Phase 6: Restart all bots with staggered timing
                    LogUtil.LogInfo("Form", "Phase 6: Starting all bots...");
                    await StartBotsStaggeredAsync();

                    BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
                    {
                        SetButtonActiveState(btnReboot, false);
                        SetButtonActiveState(btnStop, true);
                        SetButtonActiveState(btnStart, false);

                        foreach (Button navBtn in navButtonsPanel.Controls.OfType<Button>())
                        {
                            if (navBtn.Tag is NavButtonState state)
                            {
                                state.IsSelected = false;
                                navBtn.Invalidate();
                            }
                        }

                        if (btnNavLogs.Tag is NavButtonState logsState)
                        {
                            logsState.IsSelected = true;
                            btnNavLogs.Invalidate();
                        }

                        TransitionPanels(2);
                        titleLabel.Text = "System Logs";
                    }));

                    LogUtil.LogInfo("Reset process completed successfully", "Form");

                    if (Bots.Count == 0)
                        WinFormsUtil.Alert("No bots configured, but all supporting services have been issued the reboot command.");
                }
                catch (Exception ex)
                {
                    LogUtil.LogError($"Reset process failed: {ex.Message}", "Form");
                    BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
                    {
                        SetButtonActiveState(btnReboot, false);
                        SetButtonActiveState(btnStop, false);
                        SetButtonActiveState(btnStart, false);
                        WinFormsUtil.Error($"Reset failed: {ex.Message}");
                    }));
                }
            });
        }

        private async void Updater_Click(object sender, EventArgs e)
        {
            var (updateAvailable, updateRequired, newVersion) = await UpdateChecker.CheckForUpdatesAsync();
            hasUpdate = updateAvailable;

            if (!updateAvailable)
            {
                var result = MessageBox.Show(
                    "You are on the latest version. Would you like to re-download the current version?",
                    "Update Check",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    UpdateForm updateForm = new(updateRequired, newVersion, updateAvailable: false);
                    updateForm.ShowDialog();
                }
            }
            else
            {
                UpdateForm updateForm = new(updateRequired, newVersion, updateAvailable: true);
                updateForm.ShowDialog();
            }
        }

        private static void SetButtonActiveState(Button button, bool isActive)
        {
            if (button?.Tag is EnhancedButtonAnimationState state)
            {
                state.IsActive = isActive;
                button.Invalidate();
            }
        }

        private void SendAll(BotControlCommand cmd)
        {
            foreach (var c in FLP_Bots.Controls.OfType<BotController>())
                c.SendCommand(cmd);

            LogUtil.LogText($"All bots have been issued a command to {cmd}.");
        }

        private void BtnTray_Click(object sender, EventArgs e)
        {
            // Send to Tray - minimizes to system tray
            MinimizeToTray();
        }

        private void UpdateAddButtonPosition()
        {
            if (B_New != null && CB_Routine != null)
            {
                B_New.Location = new Point(CB_Routine.Right + 10, 16);
            }
        }

        private void AddBotPanel_Layout(object sender, EventArgs e)
        {
            UpdateAddButtonPosition();
        }

        private void CB_Routine_SizeChanged(object sender, EventArgs e)
        {
            UpdateAddButtonPosition();
        }

        private void CB_Routine_LocationChanged(object sender, EventArgs e)
        {
            UpdateAddButtonPosition();
        }

        private void FLP_Bots_Scroll(object sender, ScrollEventArgs e)
        {
            FLP_Bots.Invalidate();
        }

        private void FLP_Bots_ControlAdded(object sender, ControlEventArgs e)
        {
            FLP_Bots.Invalidate();
        }

        private void FLP_Bots_ControlRemoved(object sender, ControlEventArgs e)
        {
            FLP_Bots.Invalidate();
        }

        private void BtnClearLogs_Click(object sender, EventArgs e)
        {
            RTB_Logs.Clear();
            _searchManager.ClearSearch();
        }


        private void B_Stop_Click(object sender, EventArgs e)
        {
            var env = RunningEnvironment;
            if (!env.IsRunning && (ModifierKeys & Keys.Alt) == 0)
            {
                WinFormsUtil.Alert("Nothing is currently running.");
                return;
            }

            var cmd = BotControlCommand.Stop;

            if ((ModifierKeys & Keys.Control) != 0 || (ModifierKeys & Keys.Shift) != 0)
            {
                if (env.IsRunning)
                {
                    WinFormsUtil.Alert("Commanding all bots to Idle.", "Press Stop (without a modifier key) to hard-stop and unlock control, or press Stop with the modifier key again to resume.");
                    cmd = BotControlCommand.Idle;
                    SetButtonActiveState(btnStop, true);
                }
                else
                {
                    WinFormsUtil.Alert("Commanding all bots to resume their original task.", "Press Stop (without a modifier key) to hard-stop and unlock control.");
                    cmd = BotControlCommand.Resume;
                    SetButtonActiveState(btnStop, false);
                }
            }
            else
            {
                env.StopAll();
                SetButtonActiveState(btnStart, false);
                SetButtonActiveState(btnStop, false);
                SetButtonActiveState(btnReboot, false);
            }
            SendAll(cmd);
        }

        private void B_New_Click(object sender, EventArgs e)
        {
            var cfg = CreateNewBotConfig();
            if (!AddBot(cfg))
            {
                WinFormsUtil.Alert("Unable to add bot; ensure details are valid and not duplicate with an already existing bot.");
                return;
            }
            System.Media.SystemSounds.Asterisk.Play();
        }

        private bool AddBot(PokeBotState cfg)
        {
            if (!cfg.IsValid())
                return false;

            if (Bots.Any(z => z.Connection.Equals(cfg.Connection)))
                return false;

            PokeRoutineExecutorBase newBot;
            try
            {
                LogUtil.LogError("Bot", $"Current Mode ({Config.Mode}) does not support this type of bot ({cfg.CurrentRoutineType}).");
                newBot = RunningEnvironment.CreateBotFromConfig(cfg);
            }
            catch
            {
                return false;
            }

            try
            {
                RunningEnvironment.Add(newBot);
            }
            catch (ArgumentException ex)
            {
                WinFormsUtil.Error(ex.Message);
                return false;
            }

            AddBotControl(cfg);
            Bots.Add(cfg);
            return true;
        }

        private void AddBotControl(PokeBotState cfg)
        {
            int scrollBarWidth = SystemInformation.VerticalScrollBarWidth;
            int availableWidth = FLP_Bots.ClientSize.Width;

            if (FLP_Bots.VerticalScroll.Visible)
            {
                availableWidth -= scrollBarWidth;
            }

            int botWidth = Math.Max(400, availableWidth - 10);

            var row = new BotController { Width = botWidth };
            row.Initialize(RunningEnvironment, cfg);
            FLP_Bots.Controls.Add(row);
            FLP_Bots.SetFlowBreak(row, true);

            row.Click += (s, e) =>
            {
                var details = cfg.Connection;
                TB_IP.Text = details.IP;
                NUD_Port.Value = details.Port;
                CB_Protocol.SelectedIndex = (int)details.Protocol;
                CB_Routine.SelectedValue = (int)cfg.InitialRoutine;
            };

            EventHandler removeHandler = null!;
            removeHandler = (s, e) =>
            {
                row.Remove -= removeHandler; // Unsubscribe to prevent memory leak
                Bots.Remove(row.State);
                RunningEnvironment.Remove(row.State, !RunningEnvironment.Config.Global.SkipConsoleBotCreation);
                FLP_Bots.Controls.Remove(row);
                row.Dispose(); // Ensure proper disposal
            };
            row.Remove += removeHandler;
        }

        private PokeBotState CreateNewBotConfig()
        {
            var ip = TB_IP.Text;
            var port = (int)NUD_Port.Value;
            var cfg = BotConfigUtil.GetConfig<SwitchConnectionConfig>(ip, port);
            cfg.Protocol = (SwitchProtocol)WinFormsUtil.GetIndex(CB_Protocol);

            var pk = new PokeBotState { Connection = cfg };
            var type = (PokeRoutineType)WinFormsUtil.GetIndex(CB_Routine);
            pk.Initialize(type);
            return pk;
        }

        private void FLP_Bots_Resize(object sender, EventArgs e)
        {
            FLP_Bots.SuspendLayout();
            int scrollBarWidth = SystemInformation.VerticalScrollBarWidth;
            int availableWidth = FLP_Bots.ClientSize.Width;

            if (FLP_Bots.VerticalScroll.Visible)
            {
                availableWidth -= scrollBarWidth;
            }

            int botWidth = Math.Max(400, availableWidth - 10);

            foreach (var c in FLP_Bots.Controls.OfType<BotController>())
            {
                c.Width = botWidth;
            }
            FLP_Bots.ResumeLayout(true);
        }

        private void CB_Protocol_SelectedIndexChanged(object sender, EventArgs e)
        {
            TB_IP.Visible = CB_Protocol.SelectedIndex == 0;
        }

        private void ComboBox1_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_isFormLoading) return;
            if (comboBox1.SelectedValue is int selectedValue)
            {
                ProgramMode newMode = (ProgramMode)selectedValue;
                Config.Mode = newMode;
                Config.Hub.TradeSystem.Distribution.CurrentMode = newMode;
                SaveCurrentConfig();
                UpdateRunnerAndUI();
                UpdateBackgroundImage(newMode);

                // Refresh PropertyGrid to update visibility of mode-specific settings
                if (PG_Hub != null)
                {
                    var currentConfig = PG_Hub.SelectedObject;
                    PG_Hub.SelectedObject = null;
                    PG_Hub.SelectedObject = currentConfig;
                    PG_Hub.Refresh();
                }
            }
        }

        private void ConfigureGameSelector()
        {
            // Enhanced styling for the game selector
            comboBox1.DrawMode = DrawMode.OwnerDrawFixed;
            comboBox1.ItemHeight = 24;
            comboBox1.DrawItem += (sender, e) =>
            {
                if (e.Index < 0) return;

                // Custom background colors
                var backgroundColor = (e.State & DrawItemState.Selected) == DrawItemState.Selected
                    ? Color.FromArgb(45, 125, 200)
                    : Color.FromArgb(32, 38, 48);
                    
                using (var bgBrush = new SolidBrush(backgroundColor))
                {
                    e.Graphics.FillRectangle(bgBrush, e.Bounds);
                }

                // Get the item text properly
                string text = "";
                var item = comboBox1.Items[e.Index];
                
                if (item != null)
                {
                    // Handle anonymous type from DataSource
                    var textProp = item.GetType().GetProperty("Text");
                    if (textProp != null)
                    {
                        text = textProp.GetValue(item)?.ToString() ?? "";
                    }
                    else
                    {
                        text = item.ToString() ?? "";
                    }
                }

                // Draw text with proper colors
                var textColor = (e.State & DrawItemState.Selected) == DrawItemState.Selected
                    ? Color.White
                    : Color.FromArgb(239, 239, 239);
                    
                using (var textBrush = new SolidBrush(textColor))
                {
                    var textRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
                    var format = new StringFormat
                    {
                        LineAlignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.NoWrap
                    };
                    e.Graphics.DrawString(text, e.Font ?? comboBox1.Font, textBrush, textRect, format);
                }
                
                if ((e.State & DrawItemState.Focus) == DrawItemState.Focus)
                {
                    e.DrawFocusRectangle();
                }
            };
        }

        private void UpdateRunnerAndUI()
        {
            RunningEnvironment = GetRunner(Config);
            Text = $"{(string.IsNullOrEmpty(Config.Hub.Global.BotName) ? "NexusRisen PokeBot" : Config.Hub.Global.BotName)} {PokeBot.Version} ({Config.Mode})";
        }



        private void UpdateStatusIndicatorColor()
        {
            if (statusIndicator == null) return;

            // Simple static color - no animation
            Color newColor = hasUpdate ? Color.FromArgb(255, 102, 192, 244) : Color.FromArgb(100, 100, 100);
            statusIndicator.BackColor = newColor;
        }

        private void UpdateBackgroundImage(ProgramMode mode)
        {
            try
            {
                _currentModeImage = mode switch
                {
                    ProgramMode.SV => Resources.sv_mode_image,
                    ProgramMode.SWSH => Resources.swsh_mode_image,
                    ProgramMode.BDSP => Resources.bdsp_mode_image,
                    ProgramMode.LA => Resources.pla_mode_image,
                    ProgramMode.LGPE => Resources.lgpe_mode_image,
                    //Todo: Add Resources.plza_mode_image when asset is available
                    ProgramMode.PLZA => null,
                    _ => null,
                };
                FLP_Bots.Invalidate();
            }
            catch
            {
                _currentModeImage = null;
            }
        }

        #region Tray Icon Methods

        private void TrayIcon_DoubleClick(object sender, EventArgs e)
        {
            ShowFromTray();
        }

        private void TrayMenuShow_Click(object sender, EventArgs e)
        {
            ShowFromTray();
        }

        private void TrayMenuExit_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void ShowFromTray()
        {
            // Set flag to prevent re-minimizing
            _isRestoringFromTray = true;
            
            // Make visible in taskbar first
            ShowInTaskbar = true;
            trayIcon.Visible = false;
            
            // Show the form without suspending layout
            Show();
            
            // Force normal window state
            WindowState = FormWindowState.Normal;

            // Ensure window is properly restored and focused
            BringToFront();
            Activate();
            Focus();

            // Apply dark mode after the window is fully shown
            // Use BeginInvoke to ensure it happens after the UI thread processes the show event
            BeginInvoke((MethodInvoker)(() =>
            {
                DarkModeHelper.SetDarkMode(this.Handle);

                // Force a repaint of the non-client area
                SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

                // Force proper panel layout after tray restore
                EnsurePanelLayout();
            }));

            // Clear the flag after a delay
            Task.Run(async () =>
            {
                await Task.Delay(500);
                _isRestoringFromTray = false;
                _suspendLayout = false;
            });

            // Update bots asynchronously without blocking UI
            if (TC_Main.SelectedTab == Tab_Bots && FLP_Bots.Controls.Count > 0)
            {
                // Use BeginInvoke to update bots after UI has settled
                BeginInvoke((MethodInvoker)(() =>
                {
                    // Resume animations for all bots
                    foreach (var bot in FLP_Bots.Controls.OfType<BotController>())
                    {
                        bot.ResumeAnimations();
                    }
                    
                    // Schedule bot state updates asynchronously
                    Task.Run(async () =>
                    {
                        // Small delay to let UI fully restore
                        await Task.Delay(200);
                        
                        BeginInvoke((MethodInvoker)(() =>
                        {
                            // Only update visible bots in viewport
                            var scrollPos = FLP_Bots.VerticalScroll.Value;
                            var viewportHeight = FLP_Bots.ClientSize.Height;
                            
                            foreach (var bot in FLP_Bots.Controls.OfType<BotController>())
                            {
                                // Check if bot is in visible viewport
                                if (bot.Top >= scrollPos - bot.Height && 
                                    bot.Top <= scrollPos + viewportHeight)
                                {
                                    bot.ReadState();
                                }
                            }
                        }));
                    });
                }));
            }
        }

        private void MinimizeToTray()
        {
            _suspendLayout = true;

            // Pause animations on all bot controllers before hiding
            foreach (var bot in FLP_Bots.Controls.OfType<BotController>())
            {
                bot.PauseAnimations();
            }

            Hide();
            ShowInTaskbar = false;
            trayIcon.Visible = true;

            var runningBots = FLP_Bots.Controls.OfType<BotController>().Count(c => c.GetBot()?.IsRunning ?? false);
            var totalBots = FLP_Bots.Controls.OfType<BotController>().Count();

            string message = totalBots == 0
                ? "No bots configured"
                : $"{runningBots} of {totalBots} bots running";

            trayIcon.ShowBalloonTip(2000, "PokéBot Minimized", message, ToolTipIcon.Info);
        }

        private void Main_Resize(object sender, EventArgs e)
        {
            // Don't minimize to tray on minimize button - only on close (X) button
            // The minimize button should just minimize normally to taskbar

            // Handle window state changes to manage animations
            if (WindowState == FormWindowState.Minimized)
            {
                // Pause animations when minimized
                foreach (var bot in FLP_Bots.Controls.OfType<BotController>())
                {
                    bot.PauseAnimations();
                }
            }
            else if (WindowState == FormWindowState.Normal || WindowState == FormWindowState.Maximized)
            {
                // Resume animations when restored
                foreach (var bot in FLP_Bots.Controls.OfType<BotController>())
                {
                    bot.ResumeAnimations();
                }
            }
        }

        #endregion

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // Reapply dark mode when form is shown (helps with tray restore)
            DarkModeHelper.SetDarkMode(this.Handle);

            // Ensure panels are properly positioned
            EnsurePanelLayout();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);

            // Apply dark mode when window is activated
            if (_isRestoringFromTray)
            {
                DarkModeHelper.SetDarkMode(this.Handle);
                // Also ensure proper panel layout when restoring from tray
                EnsurePanelLayout();
            }
        }
        
        private void EnsurePanelLayout()
        {
            // Skip if controls aren't ready
            if (contentPanel == null || headerPanel == null)
                return;
                
            // Force proper layout recalculation
            contentPanel.SuspendLayout();
            
            // Fix z-order: headerPanel must be last (on top) for DockStyle.Top to work correctly
            // The order matters: panels docked with Fill should be added first, then Top-docked panels
            contentPanel.Controls.SetChildIndex(botsPanel, 0);
            contentPanel.Controls.SetChildIndex(hubPanel, 0);
            contentPanel.Controls.SetChildIndex(logsPanel, 0);
            contentPanel.Controls.SetChildIndex(headerPanel, contentPanel.Controls.Count - 1);
            
            // Reset docking to force recalculation
            headerPanel.Dock = DockStyle.None;
            botsPanel.Dock = DockStyle.None;
            hubPanel.Dock = DockStyle.None;
            logsPanel.Dock = DockStyle.None;
            
            // Force layout update
            contentPanel.PerformLayout();
            
            // Reapply docking in correct order
            headerPanel.Dock = DockStyle.Top;
            headerPanel.Height = 60;
            
            botsPanel.Dock = DockStyle.Fill;
            hubPanel.Dock = DockStyle.Fill;
            logsPanel.Dock = DockStyle.Fill;
            
            contentPanel.ResumeLayout(true);
            contentPanel.PerformLayout();
        }
        
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            
            // Update all controls for new DPI
            DpiHelper.UpdateDpiForControl(this);
            
            // Update specific UI elements
            if (statusIndicator != null)
            {
                var scaledSize = DpiHelper.Scale(20);
                statusIndicator.Size = new Size(scaledSize, scaledSize);
                statusIndicator.Location = DpiHelper.Scale(new Point(10, 6));
            }
            
            // Update bot controllers
            foreach (var controller in FLP_Bots.Controls.OfType<BotController>())
            {
                controller.PerformLayout();
            }
            
            // Force layout update
            PerformLayout();
        }

        #region Performance Optimization Methods

        protected override void WndProc(ref Message m)
        {
            const int WM_NCPAINT = 0x0085;
            
            // Skip non-client area painting for performance
            if (m.Msg == WM_NCPAINT && WindowState != FormWindowState.Normal)
            {
                return;
            }
            
            base.WndProc(ref m);
        }

        #endregion

        #region Log Management

        private void CleanupOldLogFiles()
        {
            // Skip cleanup if disabled - rely on NLog's built-in MaxArchiveFiles setting
            if (!LogConfig.EnableLogFileCleanup)
                return;

            try
            {
                var workingDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
                var logDirectory = Path.Combine(workingDirectory, "logs");

                if (!Directory.Exists(logDirectory))
                    return;

                var logFiles = Directory.GetFiles(logDirectory, "SysBotLog.*.txt")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTime)
                    .ToList();

                // Use configured retention days
                var cutoffDate = DateTime.Now.AddDays(-LogConfig.LogFileRetentionDays);
                foreach (var file in logFiles.Where(f => f.LastWriteTime < cutoffDate))
                {
                    try
                    {
                        file.Delete();
                        LogUtil.LogInfo($"Deleted old log file: {file.Name}", "System");
                    }
                    catch
                    {
                        // File might be in use, ignore
                    }
                }

                // Also check current log file size
                var currentLogFile = Path.Combine(logDirectory, "SysBotLog.txt");
                if (File.Exists(currentLogFile))
                {
                    var fileInfo = new FileInfo(currentLogFile);
                    // If current log file exceeds configured max size, log a notice (NLog handles rotation)
                    if (fileInfo.Length > LogConfig.MaxLogFileSize)
                    {
                        LogUtil.LogInfo("Current log file exceeds max size, NLog will rotate on next write", "System");
                    }
                }
            }
            catch (Exception ex)
            {
                LogUtil.LogError($"Failed to cleanup old log files: {ex.Message}", "System");
            }
        }

        #endregion

        #region Reset Helper Methods

        private bool AreAllBotsStopped()
        {
            foreach (var controller in FLP_Bots.Controls.OfType<BotController>())
            {
                var state = controller.ReadBotState();
                if (state != "STOPPED" && state != "IDLE")
                    return false;
            }
            return true;
        }

        private async Task StartBotsStaggeredAsync()
        {
            var controllers = FLP_Bots.Controls.OfType<BotController>().ToList();

            if (controllers.Count == 0)
            {
                SendAll(BotControlCommand.Start);
                return;
            }

            // Start bots in groups with delays to prevent overwhelming the system
            const int batchSize = 3;
            const int delayBetweenBatches = 2000; // 2 seconds between batches

            for (int i = 0; i < controllers.Count; i += batchSize)
            {
                var batch = controllers.Skip(i).Take(batchSize);
                foreach (var controller in batch)
                {
                    controller.SendCommand(BotControlCommand.Start);
                }

                if (i + batchSize < controllers.Count)
                {
                    await Task.Delay(delayBetweenBatches).ConfigureAwait(false);
                }
            }

            LogUtil.LogText($"Started {controllers.Count} bots in batches");
        }

        #endregion

        

        private void PaintAlienTechPanel(object sender, PaintEventArgs e)
        {
            if (sender is not Panel p) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = p.ClientRectangle;
            
            var drawRect = new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1);

            // Full Chamfered Path
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            int chamfer = 20;
            
            // Complex Alienware Shape
            path.AddLine(drawRect.Left + chamfer, drawRect.Top, drawRect.Right - chamfer, drawRect.Top);
            path.AddLine(drawRect.Right, drawRect.Top + chamfer, drawRect.Right, drawRect.Bottom - chamfer);
            path.AddLine(drawRect.Right - chamfer, drawRect.Bottom, drawRect.Left + chamfer, drawRect.Bottom);
            path.AddLine(drawRect.Left, drawRect.Bottom - chamfer, drawRect.Left, drawRect.Top + chamfer);
            path.CloseFigure();

            // 1. Background - Dark Glass
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(drawRect, 
                Color.FromArgb(240, 10, 10, 10), 
                Color.FromArgb(220, 5, 5, 5), 
                45f))
            {
                g.FillPath(brush, path);
            }

            // 2. Tech Grid Background (Subtle)
            using (var gridPen = new Pen(Color.FromArgb(15, 255, 255, 255), 1))
            {
                // Horizontal lines only for "server rack" look
                for (int y = drawRect.Top + 40; y < drawRect.Bottom; y += 40)
                {
                    g.DrawLine(gridPen, drawRect.Left + 5, y, drawRect.Right - 5, y);
                }
            }

            // 3. Border - Neutral Grey/White
            using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1))
            {
                g.DrawPath(pen, path);
            }

            // 4. Tech Accents (Corner Brackets) - Subtle Blue/Cyan but not overwhelming
            using (var accentPen = new Pen(Color.FromArgb(100, 0, 200, 255), 2))
            {
                int cornerSize = 15;
                // Top Left
                g.DrawLine(accentPen, drawRect.Left, drawRect.Top + cornerSize, drawRect.Left, drawRect.Top);
                g.DrawLine(accentPen, drawRect.Left, drawRect.Top, drawRect.Left + cornerSize, drawRect.Top);

                // Top Right
                g.DrawLine(accentPen, drawRect.Right, drawRect.Top + cornerSize, drawRect.Right, drawRect.Top);
                g.DrawLine(accentPen, drawRect.Right, drawRect.Top, drawRect.Right - cornerSize, drawRect.Top);

                // Bottom Left
                g.DrawLine(accentPen, drawRect.Left, drawRect.Bottom - cornerSize, drawRect.Left, drawRect.Bottom);
                g.DrawLine(accentPen, drawRect.Left, drawRect.Bottom, drawRect.Left + cornerSize, drawRect.Bottom);

                // Bottom Right
                g.DrawLine(accentPen, drawRect.Right, drawRect.Bottom - cornerSize, drawRect.Right, drawRect.Bottom);
                g.DrawLine(accentPen, drawRect.Right, drawRect.Bottom, drawRect.Right - cornerSize, drawRect.Bottom);
            }
        }

        private void PaintAlienInputPanel(object sender, PaintEventArgs e)
        {
            PaintGlassPanel(sender, e, 15);
        }

        private void PaintAlienInputPanelSmall(object sender, PaintEventArgs e)
        {
            PaintGlassPanel(sender, e, 8);
        }

        private void PaintGlassPanel(object sender, PaintEventArgs e, int chamfer)
        {
            if (sender is not Panel p) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = p.ClientRectangle;
            
            var drawRect = new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1);

            // Full Chamfered Path (Glass Style)
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            
            // Top Line
            path.AddLine(drawRect.Left + chamfer, drawRect.Top, drawRect.Right - chamfer, drawRect.Top);
            // Right Line
            path.AddLine(drawRect.Right, drawRect.Top + chamfer, drawRect.Right, drawRect.Bottom - chamfer);
            // Bottom Line
            path.AddLine(drawRect.Right - chamfer, drawRect.Bottom, drawRect.Left + chamfer, drawRect.Bottom);
            // Left Line
            path.AddLine(drawRect.Left, drawRect.Bottom - chamfer, drawRect.Left, drawRect.Top + chamfer);
            path.CloseFigure();

            // 1. Background - Transparent Glass Effect
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(drawRect, 
                Color.FromArgb(10, 200, 200, 200),  // Faint white tint top
                Color.FromArgb(5, 100, 100, 100),   // Almost clear bottom
                90f))
            {
                g.FillPath(brush, path);
            }

            // 2. Subtle Border (White/Grey)
            using (var pen = new Pen(Color.FromArgb(30, 255, 255, 255), 1))
            {
                g.DrawPath(pen, path);
            }
        }

        private void PaintAlienTechButton(object sender, PaintEventArgs e)
        {
            if (sender is not Button btn) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = btn.ClientRectangle;
            
            // Create chamfered path matching the region
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            int chamfer = 10;
            var drawRect = new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            
            path.AddLine(drawRect.Left + chamfer, drawRect.Top, drawRect.Right - chamfer, drawRect.Top);
            path.AddLine(drawRect.Right, drawRect.Top + chamfer, drawRect.Right, drawRect.Bottom - chamfer);
            path.AddLine(drawRect.Right - chamfer, drawRect.Bottom, drawRect.Left + chamfer, drawRect.Bottom);
            path.AddLine(drawRect.Left, drawRect.Bottom - chamfer, drawRect.Left, drawRect.Top + chamfer);
            path.CloseFigure();

            // Background - Darker than add button
            using (var bg = new System.Drawing.Drawing2D.LinearGradientBrush(rect, 
                Color.FromArgb(30, 30, 30), 
                Color.FromArgb(10, 10, 10), 
                System.Drawing.Drawing2D.LinearGradientMode.Vertical))
            {
                g.FillPath(bg, path);
            }

            // Border - Use ForeColor (Cyan/Red/Green)
            using (var pen = new Pen(btn.ForeColor, 1))
            {
                g.DrawPath(pen, path);
            }
            
            // Text
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using (var textBrush = new SolidBrush(btn.ForeColor))
            {
                g.DrawString(btn.Text, btn.Font, textBrush, rect, sf);
            }
            
            // Tech Accents (Corner Brackets)
            using (var accentPen = new Pen(Color.FromArgb(100, btn.ForeColor), 2))
            {
                int cornerSize = 5;
                g.DrawLine(accentPen, drawRect.Left, drawRect.Top + cornerSize, drawRect.Left, drawRect.Top);
                g.DrawLine(accentPen, drawRect.Left, drawRect.Top, drawRect.Left + cornerSize, drawRect.Top);
                
                g.DrawLine(accentPen, drawRect.Right, drawRect.Bottom - cornerSize, drawRect.Right, drawRect.Bottom);
                g.DrawLine(accentPen, drawRect.Right, drawRect.Bottom, drawRect.Right - cornerSize, drawRect.Bottom);
            }
        }

        private void PaintAlienAddButton(object sender, PaintEventArgs e)
        {
            if (sender is not Button btn) return;
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = btn.ClientRectangle;
            
            // Create chamfered path matching the region
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            int chamfer = 15;
            var drawRect = new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            
            path.AddLine(drawRect.Left + chamfer, drawRect.Top, drawRect.Right - chamfer, drawRect.Top);
            path.AddLine(drawRect.Right, drawRect.Top + chamfer, drawRect.Right, drawRect.Bottom - chamfer);
            path.AddLine(drawRect.Right - chamfer, drawRect.Bottom, drawRect.Left + chamfer, drawRect.Bottom);
            path.AddLine(drawRect.Left, drawRect.Bottom - chamfer, drawRect.Left, drawRect.Top + chamfer);
            path.CloseFigure();

            using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(rect, Color.FromArgb(20, 20, 20), Color.FromArgb(5, 5, 5), System.Drawing.Drawing2D.LinearGradientMode.Vertical);
            using var pen = new Pen(Color.White, 1.5f);
            g.FillPath(bg, path);
            g.DrawPath(pen, path);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var textBrush = new SolidBrush(Color.White);
            g.DrawString(btn.Text, btn.Font, textBrush, rect, sf);
        }

        private void BtnAutoScroll_Click(object sender, EventArgs e)
        {
            // Toggle no-op placeholder
            var btn = sender as Button;
            if (btn?.Tag is EnhancedButtonAnimationState state)
            {
                state.IsActive = !state.IsActive;
                btn.Invalidate();
            }
        }

        private void BtnExportLogs_Click(object sender, EventArgs e)
        {
            using var sfd = new SaveFileDialog { Filter = "Text Files (*.txt)|*.txt", FileName = "logs.txt" };
            if (sfd.ShowDialog(this) == DialogResult.OK)
            {
                File.WriteAllText(sfd.FileName, RTB_Logs.Text ?? string.Empty);
            }
        }

        #region Developer Tools

        private ISwitchConnectionAsync? _devConnection;
        private string? _devCachedPointerInput;
        private ulong _devCachedPointerAddress;

        private async void BtnDevConnect_Click(object? sender, EventArgs e)
        {
            if (_devConnection?.Connected == true)
            {
                _devConnection.Disconnect();
                _devConnection = null;
                btnDevConnect.Text = "Connect";
                lblConnStatus.Text = "Disconnected";
                lblConnStatus.ForeColor = Color.Red;
                return;
            }

            try
            {
                var ip = txtIP.Text;
                if (string.IsNullOrWhiteSpace(ip))
                {
                    MessageBox.Show("Please enter a valid IP address.");
                    return;
                }

                if (!int.TryParse(txtPort.Text, out int port))
                {
                    MessageBox.Show("Please enter a valid port number.");
                    return;
                }

                btnDevConnect.Enabled = false;
                btnDevConnect.Text = "Connecting...";

                var config = new SwitchConnectionConfig { IP = ip, Port = port, Protocol = SwitchProtocol.WiFi };
                _devConnection = SwitchSocketAsync.CreateInstance(config);
                
                // Connect is synchronous, so run on background thread
                await Task.Run(() => _devConnection.Connect());
                
                btnDevConnect.Text = "Disconnect";
                lblConnStatus.Text = "Connected";
                lblConnStatus.ForeColor = Color.Green;
                btnDevConnect.Enabled = true;

                // Validate Game
                var cbGame = cbGameVersion;
                var selectedGame = cbGame?.SelectedItem?.ToString() ?? "PLZA";
                
                var titleID = await _devConnection.GetTitleID(CancellationToken.None);
                bool valid = false;
                string expected = "";

                switch (selectedGame)
                {
                    case "PLZA":
                        valid = titleID == "0100F43008C44000";
                        expected = "0100F43008C44000 (PLZA)";
                        break;
                    case "SV":
                        valid = titleID == "010028D01402E000" || titleID == "01006F8002326000";
                        expected = "010028D01402E000 or 01006F8002326000 (SV)";
                        break;
                    case "LA":
                        valid = titleID == "01001F5010DFA000";
                        expected = "01001F5010DFA000 (LA)";
                        break;
                    case "SWSH":
                        valid = titleID == "01008DB008C2C000" || titleID == "0100ABF008968000";
                        expected = "01008DB008C2C000 or 0100ABF008968000 (SWSH)";
                        break;
                    case "BDSP":
                        valid = titleID == "0100000011D90000" || titleID == "010018E011D92000";
                        expected = "0100000011D90000 or 010018E011D92000 (BDSP)";
                        break;
                }

                if (!valid)
                {
                    MessageBox.Show($"Warning: Connected game TitleID ({titleID}) does not match selected game ({selectedGame}).\nExpected: {expected}", "Game Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Connection failed: {ex.Message}");
                _devConnection = null;
                btnDevConnect.Text = "Connect";
                btnDevConnect.Enabled = true;
            }
        }

        private async void BtnScan_Click(object? sender, EventArgs e)
        {
            if (_devConnection?.Connected != true)
            {
                MessageBox.Show("Not connected to Switch!");
                return;
            }

            var pattern = txtPattern.Text.Trim();
            if (string.IsNullOrWhiteSpace(pattern))
            {
                MessageBox.Show("Please enter a hex pattern.");
                return;
            }

            btnScan.Enabled = false;
            lblScanStatus.Text = "Scanning...";
            lblScanStatus.ForeColor = Color.Yellow;
            rtbResults.Clear();

            try
            {
                ulong startAddress = 0;
                ulong length = 0x04000000; // Default 64MB scan for now

                // Check if user specified length
                if (txtLength != null && !string.IsNullOrWhiteSpace(txtLength.Text))
                {
                    if (ulong.TryParse(txtLength.Text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), 
                        System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong userLen))
                    {
                        length = userLen;
                    }
                }

                var region = cbRegion.SelectedItem?.ToString();
                if (region == "Heap")
                {
                    startAddress = await _devConnection.GetHeapBaseAsync(CancellationToken.None);
                }
                else // Main
                {
                    startAddress = await _devConnection.GetMainNsoBaseAsync(CancellationToken.None);
                }
                
                // Add offset if specified
                if (txtStart != null && !string.IsNullOrWhiteSpace(txtStart.Text))
                {
                    if (ulong.TryParse(txtStart.Text.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase), 
                        System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong offset))
                    {
                        startAddress += offset;
                    }
                }

                var progress = new Progress<float>(p => 
                {
                    // Update on UI thread
                    if (InvokeRequired)
                    {
                        BeginInvoke((MethodInvoker)(() => lblScanStatus.Text = $"Scanning... {(p * 100):F1}%"));
                    }
                    else
                    {
                        lblScanStatus.Text = $"Scanning... {(p * 100):F1}%";
                    }
                });

                var results = await MemoryScanner.ScanPatternAsync(_devConnection, pattern, startAddress, length, CancellationToken.None, progress);

                lblScanStatus.Text = $"Found {results.Count} matches.";
                lblScanStatus.ForeColor = results.Count > 0 ? Color.Green : Color.White;
                
                foreach (var result in results)
                {
                    rtbResults.AppendText($"Found at: {result.Address:X16} (Base+{result.OffsetFromBase:X}){Environment.NewLine}");
                }
            }
            catch (Exception ex)
            {
                lblScanStatus.Text = "Error";
                lblScanStatus.ForeColor = Color.Red;
                MessageBox.Show($"Scan failed: {ex.Message}");
            }
            finally
            {
                btnScan.Enabled = true;
            }
        }

        private async void BtnAutoScan_Click(object? sender, EventArgs e)
        {
            if (_devConnection?.Connected != true)
            {
                MessageBox.Show("Not connected to Switch!");
                return;
            }

            var clickedButton = sender as Button;
            var cbGame = cbGameVersion;
            var gameVersion = cbGame?.SelectedItem?.ToString() ?? "PLZA";

            var signatures = PointerSignatures.GetSignaturesForGame(gameVersion);
            if (signatures.Count == 0)
            {
                MessageBox.Show($"No signatures defined for {gameVersion} yet.");
                return;
            }

            lblScanStatus.Text = "Auto-Scanning...";
            lblScanStatus.ForeColor = Color.Yellow;
            rtbResults.Clear();
            rtbResults.AppendText($"--- Auto-Scan Results for {gameVersion} ---{Environment.NewLine}");

            // Disable buttons
            if (clickedButton != null) clickedButton.Enabled = false;

            try
            {
                var progress = new Progress<string>(status => lblScanStatus.Text = status);
                string autoScanResult = await PointerChainScanner.AutoScanAndGenerateAsync(_devConnection, gameVersion, progress, CancellationToken.None);
                rtbResults.AppendText(autoScanResult);
                
                lblScanStatus.Text = "Auto-Scan Complete";
                lblScanStatus.ForeColor = Color.Green;
            }
            catch (Exception ex)
            {
                lblScanStatus.Text = "Error";
                lblScanStatus.ForeColor = Color.Red;
                rtbResults.AppendText($"Error: {ex.Message}{Environment.NewLine}");
            }
            finally
            {
                if (clickedButton != null) clickedButton.Enabled = true;
            }
        }



        private async void BtnFindChain_Click(object? sender, EventArgs e)
        {
            if (_devConnection?.Connected != true)
            {
                MessageBox.Show("Not connected to Switch!");
                return;
            }

            var clickedButton = sender as Button;
            var input = txtMonitorAddr?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                MessageBox.Show("Enter a target address in the monitor Address box.");
                return;
            }

            var cleaned = input.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            if (!ulong.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var targetAddress))
            {
                MessageBox.Show("Invalid address format.");
                return;
            }

            if (clickedButton != null) clickedButton.Enabled = false;

            lblScanStatus.Text = "Finding pointer chains...";
            lblScanStatus.ForeColor = Color.Yellow;
            rtbResults.Clear();

            try
            {
                var heapBase = await _devConnection.GetHeapBaseAsync(CancellationToken.None);
                var mainBase = await _devConnection.GetMainNsoBaseAsync(CancellationToken.None);

                lblScanStatus.Text = "Dumping Heap...";
                var heapDump = await PointerChainScanner.DumpMemoryAsync(_devConnection, heapBase, 0x10000000, "Heap", null, CancellationToken.None);

                lblScanStatus.Text = "Dumping Main...";
                var mainDump = await PointerChainScanner.DumpMemoryAsync(_devConnection, mainBase, 0x4000000, "Main", null, CancellationToken.None);

                lblScanStatus.Text = "Searching for chains...";
                var chains = PointerChainScanner.FindPointerChains(heapDump, mainDump, targetAddress, 2, 0);

                if (chains.Count == 0)
                {
                    lblScanStatus.Text = "No chains found";
                    lblScanStatus.ForeColor = Color.Orange;
                    rtbResults.AppendText("No pointer chains found in the scanned dumps.");
                    return;
                }

                lblScanStatus.Text = $"Found {chains.Count} chains";
                lblScanStatus.ForeColor = Color.Green;

                for (int i = 0; i < chains.Count; i++)
                {
                    var code = PointerChainScanner.FormatChainAsCode(chains[i], "TargetPointer");
                    rtbResults.AppendText(code + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                lblScanStatus.Text = "Error";
                lblScanStatus.ForeColor = Color.Red;
                rtbResults.AppendText($"Error: {ex.Message}{Environment.NewLine}");
            }
            finally
            {
                if (clickedButton != null) clickedButton.Enabled = true;
            }
        }

        private async void BtnDumpMain_Click(object? sender, EventArgs e)
        {
            if (_devConnection?.Connected != true)
            {
                MessageBox.Show("Not connected to Switch!");
                return;
            }

            using var sfd = new SaveFileDialog();
            sfd.FileName = "main.nso";
            sfd.Filter = "NSO Files|*.nso|All Files|*.*";
            if (sfd.ShowDialog() != DialogResult.OK) return;

            lblScanStatus.Text = "Dumping...";
            lblScanStatus.ForeColor = Color.Yellow;
            var clickedButton = sender as Button;
            if (clickedButton != null) clickedButton.Enabled = false;

            try
            {
                var progress = new Progress<float>(p => 
                {
                    if (InvokeRequired) BeginInvoke((MethodInvoker)(() => lblScanStatus.Text = $"Dumping... {(p * 100):F1}%"));
                    else lblScanStatus.Text = $"Dumping... {(p * 100):F1}%";
                });

                await PointerScanner.DumpMainToDiskAsync(_devConnection, sfd.FileName, progress, CancellationToken.None);
                
                lblScanStatus.Text = "Dump Complete";
                lblScanStatus.ForeColor = Color.Green;
                MessageBox.Show($"Dump saved to {sfd.FileName}");
            }
            catch (Exception ex)
            {
                lblScanStatus.Text = "Error";
                lblScanStatus.ForeColor = Color.Red;
                MessageBox.Show($"Dump failed: {ex.Message}");
            }
            finally
            {
                if (clickedButton != null) clickedButton.Enabled = true;
            }
        }

        private async void BtnFindSig_Click(object? sender, EventArgs e)
        {
            if (_devConnection?.Connected != true)
            {
                MessageBox.Show("Not connected to Switch!");
                return;
            }

            var offsetStr = txtSigOffset?.Text?.Trim() ?? "";
            
            if (string.IsNullOrWhiteSpace(offsetStr))
            {
                MessageBox.Show("Please enter an offset (hex).");
                return;
            }

            if (!ulong.TryParse(offsetStr.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 
                System.Globalization.NumberStyles.HexNumber, null, out ulong offset))
            {
                MessageBox.Show("Invalid offset format.");
                return;
            }

            lblScanStatus.Text = "Finding Sig...";
            lblScanStatus.ForeColor = Color.Yellow;
            rtbResults.Clear();
            var clickedButton = sender as Button;
            if (clickedButton != null) clickedButton.Enabled = false;

            try
            {
                var progress = new Progress<float>(p => 
                {
                     if (InvokeRequired) BeginInvoke((MethodInvoker)(() => lblScanStatus.Text = $"Scanning... {(p * 100):F1}%"));
                     else lblScanStatus.Text = $"Scanning... {(p * 100):F1}%";
                });

                var results = await PointerScanner.FindSignaturesForOffsetAsync(_devConnection, offset, progress, CancellationToken.None);
                
                if (results.Count > 0)
                {
                    lblScanStatus.Text = $"Found {results.Count} signatures";
                    lblScanStatus.ForeColor = Color.Green;
                    
                    foreach (var sig in results)
                    {
                        rtbResults.AppendText($"// {sig.Name} (Encoding: {sig.Encoding}){Environment.NewLine}");
                        rtbResults.AppendText($"Signature: \"{sig.Signature}\"{Environment.NewLine}");
                        rtbResults.AppendText($"// Add to PointerSignatures.cs{Environment.NewLine}{Environment.NewLine}");
                    }
                }
                else
                {
                    lblScanStatus.Text = "No references found";
                    lblScanStatus.ForeColor = Color.Orange;
                    rtbResults.AppendText("No code references found for this offset in the scanned region (first 64MB).");
                }
            }
            catch (Exception ex)
            {
                lblScanStatus.Text = "Error";
                lblScanStatus.ForeColor = Color.Red;
                MessageBox.Show($"Search failed: {ex.Message}");
            }
            finally
            {
                if (clickedButton != null) clickedButton.Enabled = true;
            }
        }

        private async void BtnAutoUpdate_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog();
            ofd.Filter = "NSO Files|*.nso|All Files|*.*";
            ofd.Title = "Select Main NSO to Analyze";
            if (ofd.ShowDialog() != DialogResult.OK) return;

            lblScanStatus.Text = "Analyzing NSO...";
            lblScanStatus.ForeColor = Color.Yellow;
            rtbResults.Clear();
            var clickedButton = sender as Button;
            if (clickedButton != null) clickedButton.Enabled = false;

            try
            {
                var progress = new Progress<float>(p => 
                {
                     if (InvokeRequired) BeginInvoke((MethodInvoker)(() => lblScanStatus.Text = $"Analyzing... {(p * 100):F1}%"));
                     else lblScanStatus.Text = $"Analyzing... {(p * 100):F1}%";
                });

                // Get signatures (in future, load from config/file)
                var signatures = Helpers.PointerSignatures.GetSignaturesForGame("PLZA");
                
                // Run scan
                var results = await PointerScanner.ScanFileForSignaturesAsync(ofd.FileName, signatures, progress, CancellationToken.None);
                
                if (results.Count > 0)
                {
                    lblScanStatus.Text = $"Resolved {results.Count} pointers";
                    lblScanStatus.ForeColor = Color.Green;
                    
                    var code = PointerScanner.GeneratePokeDataOffsetsCode(results);
                    rtbResults.Text = code;
                }
                else
                {
                    lblScanStatus.Text = "No pointers resolved";
                    lblScanStatus.ForeColor = Color.Orange;
                    rtbResults.AppendText("No known signatures matched in this NSO.");
                }
            }
            catch (Exception ex)
            {
                lblScanStatus.Text = "Error";
                lblScanStatus.ForeColor = Color.Red;
                MessageBox.Show($"Analysis failed: {ex.Message}");
            }
            finally
            {
                if (clickedButton != null) clickedButton.Enabled = true;
            }
        }

        private void BtnCopyAddress_Click(object? sender, EventArgs e)
        {
            var text = txtMonitorAddr?.Text;
            if (string.IsNullOrWhiteSpace(text))
                return;
            try
            {
                Clipboard.SetText(text.Trim());
            }
            catch
            {
            }
        }

        private void BtnMonitorToggle_Click(object? sender, EventArgs e)
        {
            if (monitorTimer.Enabled)
            {
                monitorTimer.Stop();
                btnMonitorToggle.Text = "Start Monitor";
            }
            else
            {
                if (_devConnection?.Connected != true)
                {
                    MessageBox.Show("Not connected to Switch!");
                    return;
                }
                
                monitorTimer.Start();
                btnMonitorToggle.Text = "Stop Monitor";
            }
        }

        private async void MonitorTimer_Tick(object? sender, EventArgs e)
        {
            if (_devConnection?.Connected != true)
            {
                monitorTimer.Stop();
                btnMonitorToggle.Text = "Start Monitor";
                return;
            }

            try
            {
                var readLen = numLength != null ? (int)numLength.Value : 4;
                if (readLen <= 0)
                    readLen = 4;

                var pointerInput = txtPointerInfo?.Text ?? string.Empty;
                var monitorAddrInput = txtMonitorAddr?.Text ?? string.Empty;

                var hasPointer = !string.IsNullOrWhiteSpace(pointerInput);
                var hasMonitorAddr = !string.IsNullOrWhiteSpace(monitorAddrInput);
                if (!hasPointer && !hasMonitorAddr)
                    return;

                ulong address;

                if (hasPointer)
                {
                    if (chkCachePointer?.Checked == true &&
                        _devCachedPointerAddress != 0 &&
                        string.Equals(_devCachedPointerInput, pointerInput, StringComparison.Ordinal))
                    {
                        address = _devCachedPointerAddress;
                    }
                    else
                    {
                        var (jumps, isHeap) = PointerParser.Parse(pointerInput);
                        if (!jumps.Any())
                            return;

                        address = isHeap
                            ? await _devConnection.PointerRelative(jumps, CancellationToken.None)
                            : await _devConnection.PointerAll(jumps, CancellationToken.None);

                        _devCachedPointerInput = pointerInput;
                        _devCachedPointerAddress = address;
                    }
                }
                else
                {
                    var cleaned = monitorAddrInput.Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);
                    if (!ulong.TryParse(cleaned, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out address))
                        return;
                }

                if (txtMonitorAddr != null)
                    txtMonitorAddr.Text = address.ToString("X16");

                var data = await _devConnection.ReadBytesAbsoluteAsync(address, readLen, CancellationToken.None);
                var hex = BitConverter.ToString(data).Replace("-", " ");
                if (txtMonitorValue != null && !txtMonitorValue.Focused)
                    txtMonitorValue.Text = hex;
            }
            catch
            {
            }
        }

        private async void TxtMonitorValue_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && e.Control)
            {
                e.SuppressKeyPress = true; // Prevent ding sound
                if (_devConnection?.Connected != true) return;

                try
                {
                    var pointerInput = txtPointerInfo?.Text ?? string.Empty;
                    var valStr = (txtMonitorValue?.Text ?? string.Empty)
                        .Replace("0x", "", StringComparison.OrdinalIgnoreCase)
                        .Replace(" ", "")
                        .Replace("-", "")
                        .Replace("\r", "")
                        .Replace("\n", "")
                        .Replace("\t", "");

                    if (string.IsNullOrWhiteSpace(pointerInput))
                        return;
                    
                    var (jumps, isHeap) = PointerParser.Parse(pointerInput);
                    if (!jumps.Any()) return;

                    // Resolve address
                    ulong address = isHeap
                        ? await _devConnection.PointerRelative(jumps, CancellationToken.None)
                        : await _devConnection.PointerAll(jumps, CancellationToken.None);

                    _devCachedPointerInput = pointerInput;
                    _devCachedPointerAddress = address;
                    if (txtMonitorAddr != null)
                        txtMonitorAddr.Text = address.ToString("X16");
                    
                    // Parse value
                    if ((valStr.Length % 2) != 0)
                        return;

                    byte[] data = new byte[valStr.Length / 2];
                    for (int i = 0; i < data.Length; i++)
                        data[i] = Convert.ToByte(valStr.Substring(i * 2, 2), 16);

                    await _devConnection.WriteBytesAbsoluteAsync(data, address, CancellationToken.None);
                    
                    // Visual feedback?
                    if (txtMonitorValue == null)
                        return;
                    var old = txtMonitorValue.BackColor;
                    txtMonitorValue.BackColor = Color.LightGreen;
                    await Task.Delay(200);
                    txtMonitorValue.BackColor = old;
                }
                catch
                {
                    if (txtMonitorValue == null)
                        return;
                    var old = txtMonitorValue.BackColor;
                    txtMonitorValue.BackColor = Color.Salmon;
                    await Task.Delay(200);
                    txtMonitorValue.BackColor = old;
                }
            }
        }

        #endregion
    }

    public sealed class SearchManager
    {
        private readonly RichTextBox _textBox;
        private readonly Label _statusLabel;
        private readonly List<SearchMatch> _matches = [];
        private int _currentIndex = -1;
        private string _lastSearchText = string.Empty;
        private bool _caseSensitive = false;
        private bool _useRegex = false;
        private bool _wholeWord = false;

        private readonly Color HighlightColor = Color.FromArgb(102, 192, 244);
        private readonly Color CurrentHighlightColor = Color.FromArgb(57, 255, 221);

        public SearchManager(RichTextBox textBox, Label statusLabel)
        {
            _textBox = textBox ?? throw new ArgumentNullException(nameof(textBox));
            _statusLabel = statusLabel ?? throw new ArgumentNullException(nameof(statusLabel));
        }

        public void UpdateSearch(string searchText)
        {
            if (string.IsNullOrEmpty(searchText))
            {
                ClearSearch();
                return;
            }

            if (searchText == _lastSearchText)
                return;

            _lastSearchText = searchText;
            PerformSearch(searchText);
        }

        public void FindNext()
        {
            if (_matches.Count == 0)
                return;

            _currentIndex = (_currentIndex + 1) % _matches.Count;
            HighlightCurrentMatch();
        }

        public void FindPrevious()
        {
            if (_matches.Count == 0)
                return;

            _currentIndex = _currentIndex == 0 ? _matches.Count - 1 : _currentIndex - 1;
            HighlightCurrentMatch();
        }

        public void ClearSearch()
        {
            ClearHighlights();
            _matches.Clear();
            _matches.TrimExcess(); // Free up memory
            _currentIndex = -1;
            _lastSearchText = string.Empty;
            _statusLabel.Text = string.Empty;
        }

        public void ToggleCaseSensitive()
        {
            _caseSensitive = !_caseSensitive;
            if (!string.IsNullOrEmpty(_lastSearchText))
                PerformSearch(_lastSearchText);
        }

        public void ToggleRegex()
        {
            _useRegex = !_useRegex;
            if (!string.IsNullOrEmpty(_lastSearchText))
                PerformSearch(_lastSearchText);
        }

        public void ToggleWholeWord()
        {
            _wholeWord = !_wholeWord;
            if (!string.IsNullOrEmpty(_lastSearchText))
                PerformSearch(_lastSearchText);
        }

        private void PerformSearch(string searchText)
        {
            ClearHighlights();
            _matches.Clear();
            _currentIndex = -1;

            if (string.IsNullOrEmpty(searchText))
            {
                _statusLabel.Text = string.Empty;
                return;
            }

            try
            {
                var text = _textBox.Text;
                var matches = _useRegex ? FindRegexMatches(text, searchText) : FindTextMatches(text, searchText);

                _matches.AddRange(matches);

                if (_matches.Count > 0)
                {
                    HighlightAllMatches();
                    _currentIndex = 0;
                    HighlightCurrentMatch();
                    _statusLabel.Text = $"1 of {_matches.Count}";
                }
                else
                {
                    _statusLabel.Text = "No matches found";
                }
            }
            catch (ArgumentException)
            {
                _statusLabel.Text = "Invalid regex pattern";
            }
        }

        private IEnumerable<SearchMatch> FindTextMatches(string text, string searchText)
        {
            var comparison = _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var searchPattern = _wholeWord ? $@"\b{Regex.Escape(searchText)}\b" : searchText;

            if (_wholeWord)
            {
                var regex = new Regex(searchPattern, _caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                return regex.Matches(text).Cast<Match>()
                    .Select(m => new SearchMatch(m.Index, m.Length));
            }

            var matches = new List<SearchMatch>();
            int index = 0;
            while ((index = text.IndexOf(searchText, index, comparison)) != -1)
            {
                matches.Add(new SearchMatch(index, searchText.Length));
                index += searchText.Length;
            }
            return matches;
        }

        private IEnumerable<SearchMatch> FindRegexMatches(string text, string pattern)
        {
            var options = _caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            var regex = new Regex(pattern, options);
            return regex.Matches(text).Cast<Match>()
                .Select(m => new SearchMatch(m.Index, m.Length));
        }

        private void HighlightAllMatches()
        {
            foreach (var match in _matches)
            {
                _textBox.Select(match.Start, match.Length);
                _textBox.SelectionBackColor = HighlightColor;
            }
        }

        private void HighlightCurrentMatch()
        {
            if (_currentIndex < 0 || _currentIndex >= _matches.Count)
                return;

            ClearCurrentHighlight();

            var currentMatch = _matches[_currentIndex];
            _textBox.Select(currentMatch.Start, currentMatch.Length);
            _textBox.SelectionBackColor = CurrentHighlightColor;
            _textBox.ScrollToCaret();

            _statusLabel.Text = $"{_currentIndex + 1} of {_matches.Count}";
        }

        private void ClearCurrentHighlight()
        {
            if (_currentIndex >= 0 && _currentIndex < _matches.Count)
            {
                var match = _matches[_currentIndex];
                _textBox.Select(match.Start, match.Length);
                _textBox.SelectionBackColor = HighlightColor;
            }
        }

        private void ClearHighlights()
        {
            _textBox.SelectAll();
            _textBox.SelectionBackColor = _textBox.BackColor;
            _textBox.DeselectAll();
        }
    }

    public readonly record struct SearchMatch(int Start, int Length);
}
