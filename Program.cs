using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--analisar"]) return Analyze();
        using var instance = new Mutex(true, @"Local\CorretorLocal-66d45a78-5ac3-4f59-9887-710fbfd98850", out bool first);
        if (!first) return 0;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var app = new Tray();
        Application.Run(app);
        return 0;
    }

    private static int Analyze()
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
            using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            var packet = input.ReadLine();
            if (packet == null || packet.Length > Engine.MaxText * 12 + 200) return 1;
            var request = JsonSerializer.Deserialize<Request>(packet);
            if (request?.Text == null || request.Text.Length > Engine.MaxText || request.Context?.Length > Engine.MaxText) return 1;
            using var deadline = new CancellationTokenSource(2250);
            var correction = Engine.Correct(request, deadline.Token);
            output.WriteLine(JsonSerializer.Serialize(correction));
            return 0;
        }
        catch (Exception) { return 1; }
    }
}

internal sealed record Preferences(uint Mods = 3, uint Key = 32, bool Enabled = true,
    bool Preview = false, bool Notices = true, string Language = "auto")
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "preferencias.json");
    internal string Shortcut => ((Mods & 2) != 0 ? "Ctrl + " : "") + ((Mods & 1) != 0 ? "Alt + " : "") +
        ((Mods & 4) != 0 ? "Shift + " : "") + ((Keys)Key == Keys.Space ? "Espaço" : ((Keys)Key).ToString());

    internal static Preferences Load()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath));
            if (saved != null && saved.Mods is 3 or 5 or 6 or 7 && saved.Key is > 0 and <= 254 &&
                saved.Language is "auto" or "pt-BR" or "en-US")
                return saved;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    internal void Save()
    {
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, FilePath, true);
    }
}

internal sealed class Tray : ApplicationContext
{
    private const string Name = "Corretor Local";
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "CorretorLocal";
    private readonly NotifyIcon icon;
    private readonly Icon ready = MakeIcon(Color.FromArgb(14, 101, 91));
    private readonly Icon working = MakeIcon(Color.FromArgb(34, 95, 156));
    private readonly Icon disabled = MakeIcon(Color.FromArgb(97, 107, 120));
    private readonly HotkeyWindow listener;
    private readonly ToolStripMenuItem status = new() { Enabled = false };
    private readonly ToolStripMenuItem activation = new("Ativar corretor");
    private readonly ToolStripMenuItem cancel = new("Cancelar correção");
    private readonly ToolStripMenuItem preview = new("Sempre mostrar prévia");
    private readonly ToolStripMenuItem startup = new("Iniciar com o Windows");
    private readonly ToolStripMenuItem notices = new("Mostrar notificações");
    private readonly ToolStripMenuItem languages = new("Idioma");
    private Preferences prefs = Preferences.Load();
    private CancellationTokenSource? operation;
    private Task? pending;
    private Form? currentPreview;
    private long lastDone;
    private bool registered;
    private bool exiting;

    internal Tray()
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        listener = new HotkeyWindow(() => _ = OnHotkey());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[] { status, activation, cancel,
            new ToolStripSeparator(), preview, languages,
            new ToolStripMenuItem("Alterar atalho…", null, (_, _) => ConfigureShortcut()),
            notices, startup, new ToolStripSeparator(),
            new ToolStripMenuItem("Sair", null, (_, _) => ExitThread()) });
        languages.DropDownItems.AddRange(new ToolStripItem[]
        {
            Language("Automático", "auto"), Language("Português (Brasil)", "pt-BR"), Language("Inglês (EUA)", "en-US")
        });
        menu.Opening += (_, _) => RefreshMenu();
        activation.Click += (_, _) => SetEnabled(!prefs.Enabled || !registered);
        cancel.Click += (_, _) => { operation?.Cancel(); currentPreview?.Close(); };
        preview.Click += (_, _) => Save(prefs with { Preview = !prefs.Preview });
        notices.Click += (_, _) => Save(prefs with { Notices = !prefs.Notices });
        startup.Click += (_, _) => ToggleStartup();
        icon = new NotifyIcon { ContextMenuStrip = menu, Icon = ready, Text = Name, Visible = true };
        icon.DoubleClick += (_, _) => ConfigureShortcut();
        registered = prefs.Enabled && listener.Register(prefs);
        RefreshMenu();
        if (prefs.Enabled && !registered)
            Notify("Atalho ocupado. Use “Alterar atalho…” na bandeja.", true);
    }

    private ToolStripMenuItem Language(string caption, string tag)
    {
        var item = new ToolStripMenuItem(caption) { Tag = tag };
        item.Click += (_, _) => Save(prefs with { Language = tag });
        return item;
    }

    private void Save(Preferences next)
    {
        try { next.Save(); prefs = next; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notify("Não foi possível gravar as preferências.", true);
        }
        RefreshMenu();
    }

    private void SetEnabled(bool value)
    {
        if (value && !listener.Register(prefs))
        {
            Notify("Este atalho está em uso. Escolha outro na bandeja.", true);
            return;
        }
        if (!value)
        {
            operation?.Cancel();
            currentPreview?.Close();
            listener.Unregister();
        }
        registered = value;
        Save(prefs with { Enabled = value });
    }

    private void ConfigureShortcut()
    {
        operation?.Cancel();
        currentPreview?.Close();
        using var dialog = new Form
        {
            Text = "Alterar atalho", StartPosition = FormStartPosition.CenterScreen, TopMost = true,
            FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ClientSize = new Size(385, 124), Font = SystemFonts.MessageBoxFont
        };
        var label = new Label { Text = "Pressione duas teclas modificadoras e uma letra, número ou Espaço.", AutoSize = false, Dock = DockStyle.Top, Height = 48, Padding = new Padding(12) };
        var box = new TextBox { ReadOnly = true, ShortcutsEnabled = false, Dock = DockStyle.Top, Text = prefs.Shortcut };
        var hint = new Label { Dock = DockStyle.Fill, Padding = new Padding(12), Text = "Esc fecha sem mudar o atalho." };
        dialog.Controls.Add(hint);
        dialog.Controls.Add(box);
        dialog.Controls.Add(label);
        bool hadHotkey = registered;
        listener.Unregister();
        registered = false;
        box.PreviewKeyDown += (_, key) => { if (key.KeyCode == Keys.Escape) key.IsInputKey = true; };
        box.KeyDown += (_, key) =>
        {
            key.SuppressKeyPress = true;
            if (key.KeyCode == Keys.Escape) { dialog.Close(); return; }
            uint mods = (key.Control ? 2u : 0u) | (key.Alt ? 1u : 0u) | (key.Shift ? 4u : 0u);
            if (mods is not (3 or 5 or 6 or 7) ||
                key.KeyCode is not (Keys.Space or >= Keys.A and <= Keys.Z or >= Keys.D0 and <= Keys.D9 or >= Keys.F2 and <= Keys.F11))
                return;
            var next = prefs with { Mods = mods, Key = (uint)key.KeyCode, Enabled = true };
            if (!listener.Register(next))
            {
                hint.Text = "Este atalho está ocupado. Tente outra combinação.";
                return;
            }
            registered = true;
            Save(next);
            dialog.Close();
        };
        dialog.Shown += (_, _) =>
        {
            Native.ShowWindow(dialog.Handle, 5);
            dialog.Activate();
            box.Focus();
        };
        dialog.ShowDialog();
        if (!registered && hadHotkey) registered = listener.Register(prefs);
        RefreshMenu();
    }

    private void RefreshMenu()
    {
        status.Text = operation != null ? "Analisando…" : registered ? "Atalho " + prefs.Shortcut : "Corretor pausado";
        activation.Checked = registered;
        cancel.Enabled = operation != null;
        preview.Checked = prefs.Preview;
        notices.Checked = prefs.Notices;
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
        startup.Checked = key?.GetValue(StartupName) as string == StartupCommand();
        foreach (ToolStripMenuItem item in languages.DropDownItems)
            item.Checked = (string?)item.Tag == prefs.Language;
        icon.Icon = operation != null ? working : registered ? ready : disabled;
        icon.Text = operation != null ? Name + " · analisando" : registered ? Name + " · " + prefs.Shortcut : Name + " · pausado";
    }

    private static string StartupCommand() => "\""+ Environment.ProcessPath + "\"";

    private void ToggleStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
            if (startup.Checked) key.DeleteValue(StartupName, false);
            else key.SetValue(StartupName, StartupCommand());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Notify("O Windows recusou a configuração de inicialização.", true);
        }
        RefreshMenu();
    }

    private async Task OnHotkey()
    {
        if (!registered || exiting) return;
        if (operation != null)
        {
            operation.Cancel();
            currentPreview?.Close();
            return;
        }
        if (pending is { IsCompleted: false } || Environment.TickCount64 - lastDone < 550) return;
        var window = Native.GetForegroundWindow();
        var options = prefs;
        IntPtr notepadEditor = IntPtr.Zero;
        if ((options.Mods & 1) != 0 && Native.ClassOf(window) == "Notepad" &&
            Native.GetWindowThreadProcessId(window, out _) is uint thread && thread != 0)
        {
            var focus = Native.Focused(thread).Focus;
            if (Native.ClassOf(focus) == "RichEditD2DPT") notepadEditor = focus;
        }
        using var work = new CancellationTokenSource();
        operation = work;
        RefreshMenu();
        try
        {
            await WaitForRelease(window, options.Key, work.Token);
            var capture = await Bounded(Task.Run(() => ReadField(window, notepadEditor, work.Token)), work, 1400);
            var (source, lastInput) = capture;
            if (string.IsNullOrWhiteSpace(source.Subject))
            {
                Notify("O campo está vazio.");
                return;
            }
            var context = source.Selection && source.Subject.Length < source.Text.Length ? source.Text : null;
            var correction = await RunEngine(new(source.Subject, options.Language, source.Selection, context), work.Token);
            if (source.Subject == correction.Text)
            {
                Notify("Nenhuma correção segura encontrada.");
                return;
            }
            work.Token.ThrowIfCancellationRequested();
            if (options.Preview || correction.Review)
            {
                if (Native.GetForegroundWindow() != window || Native.LastInput() != lastInput)
                    throw new SafeStop("O foco ou a entrada mudou. Correção cancelada.");
                if (!Confirm(source.Subject, correction.Text, correction.Review)) return;
                if (Native.GetForegroundWindow() != window)
                    throw new SafeStop("Volte ao campo original e acione o atalho novamente.");
                lastInput = Native.LastInput();
            }
            using var guard = source.Pattern != null ? new Native.InputGuard() : null;
            await Bounded(Task.Run(() => WindowsEdit.Apply(source, correction.Text, lastInput, work.Token, guard)), work, 2300);
            Notify("Texto corrigido.");
        }
        catch (OperationCanceledException) { if (!exiting) Notify("Correção cancelada."); }
        catch (SafeStop e) { if (!exiting) Notify(e.Message); }
        catch (Exception) { if (!exiting) Notify("O campo ou o analisador não respondeu. Confira o texto."); }
        finally
        {
            lastDone = Environment.TickCount64;
            operation = null;
            if (!exiting) RefreshMenu();
        }
    }

    private static async Task WaitForRelease(IntPtr window, uint key, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (Native.GetForegroundWindow() != window)
                throw new SafeStop("O foco mudou. Correção cancelada.");
            if (!Native.ModifiersDown() && !Native.KeyDown((int)key)) return;
            if (watch.ElapsedMilliseconds >= 5000)
                throw new SafeStop("Solte as teclas do atalho e tente novamente.");
            await Task.Delay(20, token);
        }
    }

    private static (Snapshot, uint) ReadField(IntPtr window, IntPtr notepadEditor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var stamp = Native.LastInput();
        if (notepadEditor != IntPtr.Zero)
            WindowsEdit.RestoreNotepadFocus(window, notepadEditor, stamp, token);
        var source = WindowsEdit.Capture(window, requireGroupSelection: true);
        token.ThrowIfCancellationRequested();
        if (Native.LastInput() != stamp || Native.GetForegroundWindow() != window)
            throw new SafeStop("A entrada mudou. Correção cancelada.");
        return (source, stamp);
    }

    private async Task<T> Bounded<T>(Task<T> task, CancellationTokenSource work, int milliseconds)
    {
        pending = task;
        try { return await task.WaitAsync(TimeSpan.FromMilliseconds(milliseconds), work.Token); }
        catch (TimeoutException)
        {
            work.Cancel();
            throw new SafeStop("O aplicativo demorou a responder. Correção cancelada.");
        }
        finally
        {
            if (task.IsCompleted) pending = null;
            else _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
        }
    }

    private Task<bool> Bounded(Task task, CancellationTokenSource work, int milliseconds) =>
        Bounded(Completed(task), work, milliseconds);

    private static async Task<bool> Completed(Task task) { await task.ConfigureAwait(false); return true; }

    private static async Task<Correction> RunEngine(Request request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(2900);
        var stop = deadline.Token;
        var path = Environment.ProcessPath!;
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--analisar");
        using var worker = Process.Start(start) ?? throw new SafeStop("O analisador não iniciou.");
        using var cleanup = stop.Register(() =>
        {
            try { if (!worker.HasExited) worker.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        try
        {
            var read = worker.StandardOutput.ReadToEndAsync(stop);
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), stop);
            worker.StandardInput.Close();
            var result = await read;
            await worker.WaitForExitAsync(stop);
            stop.ThrowIfCancellationRequested();
            if (worker.ExitCode != 0) throw new SafeStop("A análise excedeu o prazo ou não pôde ser concluída.");
            return JsonSerializer.Deserialize<Correction>(result) ?? throw new SafeStop("O analisador não retornou uma correção.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new SafeStop("A análise excedeu o limite de tempo.");
        }
    }

    private bool Confirm(string before, string after, bool review)
    {
        var target = Native.GetForegroundWindow();
        using var dialog = new Form
        {
            Text = "Corretor Local · Prévia", TopMost = true, StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(630, 336), Font = SystemFonts.MessageBoxFont
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
        foreach (var style in new[] { new RowStyle(SizeType.Absolute, 30), new RowStyle(SizeType.Absolute, 20),
                     new RowStyle(SizeType.Percent, 50), new RowStyle(SizeType.Absolute, 20),
                     new RowStyle(SizeType.Percent, 50), new RowStyle(SizeType.Absolute, 42) }) layout.RowStyles.Add(style);
        layout.Controls.Add(new Label { Text = review ? "Confira a sugestão antes de aplicar." : "Revise o texto corrigido.", AutoSize = true });
        layout.Controls.Add(new Label { Text = "Original", AutoSize = true });
        layout.Controls.Add(PreviewText(before));
        layout.Controls.Add(new Label { Text = "Corrigido", AutoSize = true });
        layout.Controls.Add(PreviewText(after));
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var apply = new Button { Text = "Aplicar", DialogResult = DialogResult.OK, AutoSize = true };
        var skip = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(apply);
        buttons.Controls.Add(skip);
        layout.Controls.Add(buttons);
        dialog.Controls.Add(layout);
        dialog.CancelButton = skip;
        dialog.Shown += (_, _) =>
        {
            Native.ShowWindow(dialog.Handle, 5);
            dialog.Activate();
            skip.Focus();
        };
        bool accepted = false;
        apply.Click += (_, _) =>
        {
            if (Native.GetForegroundWindow() != dialog.Handle) return;
            dialog.DialogResult = DialogResult.OK;
            accepted = Native.GetForegroundWindow() == target || Native.SetForegroundWindow(target);
        };
        currentPreview = dialog;
        try { return dialog.ShowDialog() == DialogResult.OK && accepted; }
        finally { currentPreview = null; }
    }

    private static TextBox PreviewText(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
        Font = new Font("Segoe UI", 11)
    };

    private void Notify(string text, bool important = false)
    {
        if (!prefs.Notices && !important) return;
        icon.BalloonTipTitle = Name;
        icon.BalloonTipText = text;
        icon.BalloonTipIcon = ToolTipIcon.None;
        icon.ShowBalloonTip(2200);
    }

    protected override void ExitThreadCore()
    {
        exiting = true;
        operation?.Cancel();
        currentPreview?.Close();
        listener.Dispose();
        icon.Visible = false;
        icon.ContextMenuStrip?.Dispose();
        icon.Dispose();
        ready.Dispose();
        working.Dispose();
        disabled.Dispose();
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private static Icon MakeIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(color);
        graphics.FillEllipse(fill, 1, 1, 30, 30);
        using var pen = new Pen(Color.White, 3.3f) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
        graphics.DrawLines(pen, new[] { new Point(8, 16), new Point(13, 21), new Point(23, 11) });
        var handle = bitmap.GetHicon();
        using var borrowed = Icon.FromHandle(handle);
        var owned = (Icon)borrowed.Clone();
        DestroyIcon(handle);
        return owned;
    }
}

internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int Id = 0x2645;
    private readonly Action fired;

    internal HotkeyWindow(Action fired)
    {
        this.fired = fired;
        CreateHandle(new CreateParams { Caption = "Corretor Local", Parent = new IntPtr(-3) });
    }

    internal bool Register(Preferences prefs) => Native.RegisterHotKey(Handle, Id, prefs.Mods | 0x4000, prefs.Key);
    internal void Unregister() => Native.UnregisterHotKey(Handle, Id);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == Id) fired();
        base.WndProc(ref m);
    }

    public void Dispose() { Unregister(); DestroyHandle(); }
}
