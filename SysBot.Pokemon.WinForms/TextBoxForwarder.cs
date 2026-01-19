using SysBot.Base;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Forms;

namespace SysBot.Pokemon.WinForms;

/// <summary>
/// Forward logs to a TextBox with enhanced coloring and scroll control.
/// Uses a background queue and batched UI updates to prevent blocking the main thread.
/// </summary>
public sealed class TextBoxForwarder : ILogForwarder, IDisposable
{
    private readonly TextBoxBase Box;
    private readonly ConcurrentQueue<LogEntry> _logQueue = new();
    private readonly System.Windows.Forms.Timer _updateTimer;
    private const int MaxBatchSize = 200; // Process up to 200 logs per tick

    public bool AutoScroll { get; set; } = true;
    public event EventHandler? LogCleanup;

    private readonly struct LogEntry
    {
        public readonly string Message;
        public readonly string Identity;
        public readonly DateTime Time;

        public LogEntry(string message, string identity, DateTime time)
        {
            Message = message;
            Identity = identity;
            Time = time;
        }
    }

    public TextBoxForwarder(TextBoxBase box)
    {
        Box = box;
        // Timer runs on the UI thread (if created here) to process the queue
        _updateTimer = new System.Windows.Forms.Timer { Interval = 100 }; // 100ms update rate
        _updateTimer.Tick += UpdateTimer_Tick;
        _updateTimer.Start();
    }

    public void Forward(string message, string identity)
    {
        if (Box.IsDisposed) return;
        _logQueue.Enqueue(new LogEntry(message, identity, DateTime.Now));
    }

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_logQueue.IsEmpty || Box.IsDisposed) return;

        // Ensure we are on the UI thread
        if (Box.InvokeRequired)
        {
            Box.BeginInvoke(new MethodInvoker(ProcessBatch));
        }
        else
        {
            ProcessBatch();
        }
    }

    private void ProcessBatch()
    {
        try
        {
            if (Box.IsDisposed) return;

            CheckCleanup();

            int processed = 0;
            bool isRichText = Box is RichTextBox;
            
            // Suspend layout to prevent flickering during batch update
            if (isRichText) Box.SuspendLayout();

            while (processed < MaxBatchSize && _logQueue.TryDequeue(out var entry))
            {
                if (isRichText)
                {
                    AppendRichText((RichTextBox)Box, entry);
                }
                else
                {
                    AppendStandardText(Box, entry);
                }
                processed++;
            }
            
            if (isRichText)
            {
                if (AutoScroll)
                {
                    Box.SelectionStart = Box.TextLength;
                    Box.ScrollToCaret();
                }
                Box.ResumeLayout();
            }
        }
        catch { }
    }

    private void AppendRichText(RichTextBox rtb, LogEntry entry)
    {
        // Timestamp
        rtb.SelectionStart = rtb.TextLength;
        rtb.SelectionLength = 0;
        rtb.SelectionColor = Color.Gray;
        rtb.AppendText($"[{entry.Time:HH:mm:ss}] ");

        // Identity
        Color idColor = Color.FromArgb(100, 180, 255); // Light Blue
        if (entry.Identity.Contains("Error", StringComparison.OrdinalIgnoreCase) || entry.Identity.Contains("Fail", StringComparison.OrdinalIgnoreCase)) 
            idColor = Color.FromArgb(255, 100, 100); // Red
        else if (entry.Identity.Contains("Warn", StringComparison.OrdinalIgnoreCase)) 
            idColor = Color.FromArgb(255, 200, 100); // Orange
        else if (entry.Identity.Contains("Success", StringComparison.OrdinalIgnoreCase) || entry.Identity.Contains("Trade", StringComparison.OrdinalIgnoreCase)) 
            idColor = Color.FromArgb(100, 255, 100); // Green
        
        rtb.SelectionColor = idColor;
        rtb.AppendText($"- {entry.Identity}: ");

        // Message
        rtb.SelectionColor = Color.FromArgb(220, 220, 220); // Off-white
        rtb.AppendText($"{entry.Message}{Environment.NewLine}");
    }

    private void AppendStandardText(TextBoxBase box, LogEntry entry)
    {
        var line = $"[{entry.Time:HH:mm:ss}] - {entry.Identity}: {entry.Message}{Environment.NewLine}";
        box.AppendText(line);
    }

    private void CheckCleanup()
    {
        // Check if text length exceeds 90% of max length
        if (Box.TextLength > Box.MaxLength * 0.9)
        {
            // Optimization: Instead of Box.Lines which creates a massive array,
            // simply remove the first half of the text
            int halfLength = Box.TextLength / 2;
            Box.Select(0, halfLength);
            Box.SelectedText = "";
            
            LogCleanup?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _updateTimer.Stop();
        _updateTimer.Dispose();
    }
}
