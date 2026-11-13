using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;

internal sealed class SafeStop(string message) : Exception(message);

internal readonly record struct EditDelta(int Start, int End, string Insert)
{
    internal static EditDelta Between(string before, string after)
    {
        int prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        while (prefix > 0 && (!Boundary(before, prefix) || !Boundary(after, prefix))) prefix--;
        int end = before.Length, newEnd = after.Length;
        while (end > prefix && newEnd > prefix && before[end - 1] == after[newEnd - 1])
        {
            end--;
            newEnd--;
        }
        while (end < before.Length && newEnd < after.Length &&
               (!Boundary(before, end) || !Boundary(after, newEnd)))
        {
            end++;
            newEnd++;
        }
        return new(prefix, end, after[prefix..newEnd]);
    }

    private static bool Boundary(string text, int index) => index == text.Length ||
        (!char.IsLowSurrogate(text[index]) &&
         CharUnicodeInfo.GetUnicodeCategory(text, index) is not
             (UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark));

    internal int MoveCaret(int caret) =>
        caret <= Start ? caret : caret >= End ? caret + Insert.Length - (End - Start) :
        Math.Min(caret, Start + Insert.Length);
}

internal sealed record Snapshot(
    IntPtr Window, IntPtr Focus, string App, string Text, int Start, int End, bool Rich,
    AutomationElement? Element, TextPattern? Pattern)
{
    internal bool Selection => Start != End;
    internal string Subject => Selection ? Text[Start..End] : Text;
    internal bool SameAs(Snapshot other) => SameField(other) &&
        Text == other.Text && Start == other.Start && End == other.End;
    internal bool SameField(Snapshot other) =>
        Window == other.Window && Focus == other.Focus && App == other.App &&
        (Element == null ? other.Element == null : Element.Equals(other.Element));
}

internal static class WindowsEdit
{
    private const string Unsupported = "O campo em foco não oferece uma edição segura.";
    private const string Changed = "O foco, o texto ou a seleção mudou. Correção cancelada.";
    private static readonly Regex BlockedApp = new(
        @"^(?:code(?: - insiders)?|cursor|vscodium|devenv|sublime_text|notepad\+\+|vim|nvim|gvim|emacs|idea64|pycharm64|rider64|webstorm64|zed|obs64|obs32|windowsterminal|openconsole|conhost|cmd|pwsh|powershell|mintty|wezterm-gui|alacritty|putty|kitty|wt|keepass.*|1password.*|bitwarden.*|credentialuibroker|logonui|explorer|searchhost|startmenuexperiencehost|regedit|taskmgr|mmc|mstsc|consent)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ChromiumApp = new(
        @"^(?:chrome|msedge|brave|chromium|vivaldi|opera|thorium|discord(?:canary|ptb)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Sensitive = new(
        @"password|passwd|senha|passphrase|passcode|(?:\b|[_-])pin(?:\b|[_-])|\bcvv\b|\bcvc\b|one.?time|verification.?code|c[oó]digo de verifica|c[oó]digo de seguran|security.?code|auth.?token|secret|segredo|card.?number|n[uú]mero do cart[aã]o|\botp\b|code.?editor|terminal|monaco|codemirror|ace[_-]editor|cm-editor|xterm",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static Snapshot Capture(IntPtr expected, bool requireGroupSelection = false)
    {
        var window = Native.GetForegroundWindow();
        if (window == IntPtr.Zero || window != expected) throw new SafeStop(Changed);
        var thread = Native.GetWindowThreadProcessId(window, out uint pid);
        if (thread == 0 || pid == Environment.ProcessId) throw new SafeStop(Unsupported);
        using var process = Process.GetProcessById((int)pid);
        var app = process.ProcessName;
        if (BlockedApp.IsMatch(app)) throw new SafeStop(Unsupported);

        var gui = Native.Focused(thread);
        if ((gui.Flags & 0x1E) != 0 || gui.Focus == IntPtr.Zero ||
            Native.GetAncestor(gui.Focus, 2) != window || !Native.IsWindowEnabled(gui.Focus))
            throw new SafeStop(Unsupported);
        var @class = Native.ClassOf(gui.Focus);
        bool classic = @class.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
                       @class.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase);
        bool rich = app.Equals("notepad", StringComparison.OrdinalIgnoreCase) &&
                    @class.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
        if (classic || rich)
        {
            int style = Native.GetWindowLong(gui.Focus, -16);
            if ((style & (0x20 | 0x800 | 0x2000)) != 0 ||
                Native.GetWindowThreadProcessId(gui.Focus, out var editPid) == 0 || editPid != pid)
                throw new SafeStop(Unsupported);
            var element = AutomationElement.FromHandle(gui.Focus);
            if (element.Current.IsPassword || Sensitive.IsMatch(element.Current.AutomationId + " " + element.Current.Name))
                throw new SafeStop(Unsupported);
            int size = Native.Message(gui.Focus, 0x000E).ToInt32();
            if (size < 0 || size > Engine.MaxText) throw new SafeStop("O campo excede o limite seguro de 12 mil caracteres.");
            var value = Native.ReadText(gui.Focus, Engine.MaxText + 2);
            var (start, end) = Native.GetSelection(gui.Focus);
            if (rich)
            {
                start = FromRichOffset(value, start);
                end = FromRichOffset(value, end);
            }
            CheckBounds(value, start, end);
            return new(window, gui.Focus, app, value, start, end, rich, null, null);
        }

        if (!ChromiumApp.IsMatch(app) || !Native.ClassOf(window).StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal))
            throw new SafeStop(Unsupported);
        var focused = AutomationElement.FocusedElement;
        if (focused == null)
            throw new SafeStop("O navegador não informou qual elemento está em foco.");
        ValidateBrowserField(focused, pid);
        if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var provider) ||
            provider is not TextPattern pattern || pattern.SupportedTextSelection != SupportedTextSelection.Single)
            throw new SafeStop("O navegador não disponibilizou a seleção do editor.");
        var document = pattern.DocumentRange;
        if (!Equals(document.GetAttributeValue(TextPattern.IsReadOnlyAttribute), false))
            throw new SafeStop("O navegador não confirmou que este campo é editável.");
        var selections = pattern.GetSelection();
        if (selections.Length != 1)
            throw new SafeStop("O navegador não retornou uma seleção única.");
        var selection = selections[0];
        if (selection.CompareEndpoints(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start) < 0 ||
            selection.CompareEndpoints(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.End) > 0)
            throw new SafeStop("A seleção informada pelo navegador ultrapassa o campo.");
        // Chromium can report the final boundary of a selection past the editable text.
        // Keep the portion inside this field and verify the narrower range before typing.
        if (selection.CompareEndpoints(TextPatternRangeEndpoint.End, document, TextPatternRangeEndpoint.End) > 0)
        {
            selection = selection.Clone();
            selection.MoveEndpointByRange(TextPatternRangeEndpoint.End, document, TextPatternRangeEndpoint.End);
        }
        // A generic Group is eligible only with an explicit, nonempty selection.
        if (requireGroupSelection && focused.Current.ControlType == ControlType.Group &&
            selection.CompareEndpoints(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End) == 0)
            throw new SafeStop("Selecione o trecho que deseja corrigir neste campo.");
        var text = document.GetText(Engine.MaxText + 1);
        var prefix = document.Clone();
        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
        int offset = prefix.GetText(Engine.MaxText + 1).Length;
        var selected = selection.GetText(Engine.MaxText + 1);
        CheckBounds(text, offset, offset + selected.Length);
        if (!text.AsSpan(offset, selected.Length).SequenceEqual(selected))
            throw new SafeStop("O navegador retornou posições incompatíveis para a seleção.");
        return new(window, gui.Focus, app, text, offset, offset + selected.Length, false, focused, pattern);
    }

    private static void ValidateBrowserField(AutomationElement field, uint pid)
    {
        var current = field.Current;
        if (current.ProcessId != pid || !current.HasKeyboardFocus)
            throw new SafeStop("O navegador não confirmou o foco do editor.");
        if (!current.IsEnabled || current.IsPassword || Sensitive.IsMatch(current.Name + " " + current.HelpText))
            throw new SafeStop("O navegador sinalizou um campo protegido ou desativado.");
        if (current.ControlType != ControlType.Edit && current.ControlType != ControlType.Group)
            throw new SafeStop("O tipo deste campo do navegador ainda não é compatível.");
        bool inPage = false;
        AutomationElement? node = field;
        // Chromium pages can nest editors under many accessible groups.
        for (int i = 0; i < 64 && node != null; i++)
        {
            var info = node.Current;
            if (info.IsPassword || Sensitive.IsMatch(info.ClassName + " " + info.AutomationId))
                throw new SafeStop("O editor pertence a uma área protegida ou de código.");
            if (info.ControlType == ControlType.Document) inPage = true;
            if (info.ControlType == ControlType.Window) break;
            node = TreeWalker.RawViewWalker.GetParent(node);
        }
        if (!inPage) throw new SafeStop("O navegador não confirmou que o editor pertence à página.");
    }

    internal static void RestoreNotepadFocus(IntPtr window, IntPtr editor, uint lastInput, CancellationToken token)
    {
        if (Native.GetForegroundWindow() != window || Native.LastInput() != lastInput ||
            Native.GetAncestor(editor, 2) != window || !Native.IsWindowEnabled(editor))
            throw new SafeStop(Changed);
        var state = Native.Focused(Native.GetWindowThreadProcessId(window, out _));
        // The Alt release can focus the XAML menu host in the modern Notepad.
        if (state.Focus == editor || state.Flags != 0 || Native.ClassOf(state.Focus) != "InputSiteWindowClass") return;
        var element = AutomationElement.FromHandle(editor);
        if (element.Current.IsPassword || element.Current.ClassName != "RichEditD2DPT")
            throw new SafeStop(Unsupported);
        token.ThrowIfCancellationRequested();
        if (Native.GetForegroundWindow() != window || Native.LastInput() != lastInput) throw new SafeStop(Changed);
        Native.Refocus(window, editor, lastInput, token);
    }

    internal static void Apply(Snapshot original, string replacement, uint lastInput, CancellationToken token,
        Native.InputGuard? guard = null)
    {
        token.ThrowIfCancellationRequested();
        var latest = Capture(original.Window);
        if (!original.SameAs(latest)) throw new SafeStop(Changed);
        var expected = original.Selection ? original.Text[..original.Start] + replacement + original.Text[original.End..] : replacement;
        if (expected.Length > Engine.MaxText) throw new SafeStop("O resultado excede o limite seguro do campo.");
        var local = EditDelta.Between(original.Subject, replacement);
        int baseOffset = original.Selection ? original.Start : 0;
        var delta = new EditDelta(baseOffset + local.Start, baseOffset + local.End, local.Insert);
        int caret = original.Selection ? original.Start + replacement.Length :
            original.Start == original.Text.Length ? expected.Length : delta.MoveCaret(original.Start);
        EnsureStill(latest, lastInput, token);
        if (latest.Pattern == null)
        {
            int limit = Native.Message(latest.Focus, 0x00D5).ToInt32(); // EM_GETLIMITTEXT
            if (limit > 0 && expected.Length > limit) throw new SafeStop("O campo não comporta o resultado.");
            Native.Select(latest.Focus, ToNativeOffset(latest, delta.Start), ToNativeOffset(latest, delta.End));
            try
            {
                EnsureStill(latest, lastInput, token);
                if (Native.GetSelection(latest.Focus) !=
                    (ToNativeOffset(latest, delta.Start), ToNativeOffset(latest, delta.End)))
                    throw new SafeStop(Unsupported);
                Native.Replace(latest.Focus, delta.Insert);
            }
            catch
            {
                if (Native.GetForegroundWindow() == latest.Window && Native.LastInput() == lastInput &&
                    Native.ReadText(latest.Focus, Engine.MaxText + 2) == latest.Text)
                    Native.Select(latest.Focus, ToNativeOffset(latest, latest.Start), ToNativeOffset(latest, latest.End));
                throw;
            }
            var actual = Native.ReadText(latest.Focus, Engine.MaxText + 2);
            if (actual != expected)
            {
                if (actual != latest.Text && InsertedPrefix(latest.Text, actual, delta) >= 0 &&
                    Native.GetForegroundWindow() == latest.Window && Native.LastInput() == lastInput &&
                    Native.Message(latest.Focus, 0x00C6) != IntPtr.Zero) // EM_CANUNDO
                {
                    Native.Message(latest.Focus, 0x00C7); // EM_UNDO
                    if (Native.ReadText(latest.Focus, Engine.MaxText + 2) == latest.Text)
                        throw new SafeStop("O aplicativo recusou a correção. O texto original foi restaurado.");
                }
                throw new SafeStop("O aplicativo recusou ou alterou o resultado. Confira o campo.");
            }
            var updated = latest with { Text = expected };
            if (Native.GetForegroundWindow() == latest.Window && Native.LastInput() == lastInput)
                Native.Select(latest.Focus, ToNativeOffset(updated, caret), ToNativeOffset(updated, caret));
            return;
        }
        if (guard == null) throw new SafeStop(Unsupported);
        ApplyBrowser(latest, expected, delta, caret, lastInput, token, guard);
    }

    private static void ApplyBrowser(Snapshot latest, string expected, EditDelta delta, int caret,
        uint lastInput, CancellationToken token, Native.InputGuard guard)
    {
        // A literal input batch has no clipboard side effects and never contains Enter.
        if (delta.Insert.Length > 1600 || delta.Insert.Any(char.IsControl) ||
            latest.Text.AsSpan(delta.Start, delta.End - delta.Start).IndexOfAny('\r', '\n', '\t') >= 0)
            throw new SafeStop("Neste campo, selecione e corrija uma linha de cada vez.");
        var pattern = latest.Pattern!;
        var originalSelection = pattern.GetSelection().Single();
        var range = RangeAt(pattern, latest.Text, delta.Start, delta.End);
        if (range.GetChildren().Any(child => child.Current.ControlType != ControlType.Text) ||
            new[] { TextPattern.FontNameAttribute, TextPattern.FontWeightAttribute, TextPattern.IsItalicAttribute }
                .Any(attribute => Equals(range.GetAttributeValue(attribute), TextPattern.MixedAttributeValue)))
            throw new SafeStop("Selecione um trecho com formatação uniforme.");
        EnsureStill(latest, lastInput, token);
        range.Select();
        try
        {
            var verified = WaitFor(latest, s => s.Text == latest.Text && s.Start == delta.Start && s.End == delta.End,
                lastInput, token, 220);
            EnsureStill(verified, lastInput, token);
            guard.Check();
            Native.ReplaceBrowserSelection(delta.Insert);
        }
        catch
        {
            if (!guard.Interrupted && Native.GetForegroundWindow() == latest.Window && Native.LastInput() == lastInput)
            {
                var current = Capture(latest.Window);
                if (latest.SameField(current) && current.Text == latest.Text) originalSelection.Select();
            }
            throw;
        }
        Snapshot result;
        try
        {
            result = WaitFor(latest, s => s.Text == expected, null, token, 290);
        }
        catch (SafeStop)
        {
            if (TryRecoverBrowser(latest, delta, token, guard))
                throw new SafeStop("O aplicativo recusou a correção. O texto original foi preservado.");
            throw;
        }
        if (result.Start == result.End && Native.GetForegroundWindow() == latest.Window &&
            !guard.Interrupted && !Native.ModifiersDown() && caret != result.Start)
        {
            try { RangeAt(result.Pattern!, expected, caret, caret).Select(); }
            catch (Exception e) when (e is SafeStop or InvalidOperationException or COMException) { }
        }
    }

    private static int InsertedPrefix(string before, string actual, EditDelta delta)
    {
        var suffix = before.AsSpan(delta.End);
        int inserted = actual.Length - delta.Start - suffix.Length;
        if (inserted < 0 || inserted > delta.Insert.Length ||
            !actual.AsSpan(0, delta.Start).SequenceEqual(before.AsSpan(0, delta.Start)) ||
            !actual.AsSpan(actual.Length - suffix.Length).SequenceEqual(suffix) ||
            !actual.AsSpan(delta.Start, inserted).SequenceEqual(delta.Insert.AsSpan(0, inserted)))
            return -1;
        return inserted;
    }

    private static bool TryRecoverBrowser(Snapshot original, EditDelta delta, CancellationToken token,
        Native.InputGuard guard)
    {
        guard.Check();
        token.ThrowIfCancellationRequested();
        var current = Capture(original.Window);
        if (!original.SameField(current)) return false;
        if (current.Text != original.Text)
        {
            int inserted = InsertedPrefix(original.Text, current.Text, delta);
            if (inserted <= 0 || current.Selection || current.Start != delta.Start + inserted) return false;
            var previousText = original.Text[delta.Start..delta.End];
            if (previousText.Length > 1600 || previousText.Any(char.IsControl)) return false;
            var span = RangeAt(current.Pattern!, current.Text, delta.Start, delta.Start + inserted);
            uint lastInput = Native.LastInput();
            EnsureStill(current, lastInput, token);
            guard.Check();
            span.Select();
            current = WaitFor(current,
                s => s.Text == current.Text && s.Start == delta.Start && s.End == delta.Start + inserted,
                lastInput, token, 180);
            EnsureStill(current, lastInput, token);
            guard.Check();
            Native.ReplaceBrowserSelection(previousText);
            current = WaitFor(original, s => s.Text == original.Text, null, token, 290);
        }
        var selection = RangeAt(current.Pattern!, current.Text, original.Start, original.End);
        EnsureStill(current, Native.LastInput(), token);
        guard.Check();
        selection.Select();
        return true;
    }

    private static Snapshot WaitFor(Snapshot target, Func<Snapshot, bool> condition,
        uint? lastInput, CancellationToken token, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            token.ThrowIfCancellationRequested();
            if (lastInput.HasValue) EnsureStill(target, lastInput.Value, token);
            var state = Capture(target.Window);
            if (!target.SameField(state)) throw new SafeStop(Changed);
            if (condition(state)) return state;
            Thread.Sleep(20);
        } while (timer.ElapsedMilliseconds < milliseconds);
        throw new SafeStop(lastInput.HasValue ? Unsupported : "O aplicativo não confirmou a correção. Confira o campo.");
    }

    private static TextPatternRange RangeAt(TextPattern pattern, string text, int start, int end)
    {
        var boundaries = StringInfo.ParseCombiningCharacters(text).Append(text.Length).ToArray();
        var from = Array.BinarySearch(boundaries, start);
        var to = Array.BinarySearch(boundaries, end);
        if (from < 0 || to < 0) throw new SafeStop(Unsupported);
        var range = pattern.DocumentRange;
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        if (from > 0 && range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, from) != from)
            throw new SafeStop(Unsupported);
        if (range.GetText(Engine.MaxText + 1) != text[..start]) throw new SafeStop(Unsupported);
        range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
        if (to > from && range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, to - from) != to - from)
            throw new SafeStop(Unsupported);
        if (range.GetText(Engine.MaxText + 1) != text[start..end]) throw new SafeStop(Unsupported);
        return range;
    }

    private static void EnsureStill(Snapshot target, uint lastInput, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var window = Native.GetForegroundWindow();
        if (window != target.Window || Native.LastInput() != lastInput || Native.ModifiersDown() ||
            Native.Focused(Native.GetWindowThreadProcessId(window, out _)).Focus != target.Focus)
            throw new SafeStop(Changed);
        if (target.Element != null && !target.Element.Equals(AutomationElement.FocusedElement))
            throw new SafeStop(Changed);
        token.ThrowIfCancellationRequested();
        if (Native.GetForegroundWindow() != target.Window || Native.LastInput() != lastInput)
            throw new SafeStop(Changed);
    }

    private static void CheckBounds(string text, int start, int end)
    {
        if (text.Length > Engine.MaxText) throw new SafeStop("O campo excede o limite seguro de 12 mil caracteres.");
        if (start < 0 || end < start || end > text.Length || text.Contains('\0')) throw new SafeStop(Unsupported);
    }

    private static int FromRichOffset(string text, int offset)
    {
        int index = 0;
        while (offset > 0 && index < text.Length)
        {
            if (text[index++] == '\r' && index < text.Length && text[index] == '\n') index++;
            offset--;
        }
        return offset == 0 ? index : -1;
    }

    private static int ToNativeOffset(Snapshot snapshot, int offset)
    {
        if (!snapshot.Rich) return offset;
        int count = offset;
        for (int i = 1; i < offset; i++)
            if (snapshot.Text[i] == '\n' && snapshot.Text[i - 1] == '\r') count--;
        return count;
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThread
    {
        internal int Size, Flags;
        internal IntPtr Active, Focus, Capture, Menu, Move, Caret;
        private int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { internal uint Size, Tick; }

    // Layout of INPUT on x64, with space for the union's largest member (MOUSEINPUT).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal ushort Key;
        [FieldOffset(10)] internal ushort Scan;
        [FieldOffset(12)] internal uint Flags;
        [FieldOffset(24)] internal IntPtr Extra;
    }

    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern bool PeekMessage(IntPtr message, IntPtr window, uint min, uint max, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThread state);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo state);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, KeyInput[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr handle, int code, IntPtr message, IntPtr data);
    private static readonly IntPtr OurInput = (IntPtr)(0x43524C0000000000L | (uint)Environment.ProcessId);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint mods, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint msg, IntPtr w, IntPtr l, uint flags, uint wait, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint msg, IntPtr w, string value, uint flags, uint wait, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint msg, IntPtr w, StringBuilder value, uint flags, uint wait, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint msg, out int start, out int end, uint flags, uint wait, out IntPtr result);

    internal static GuiThread Focused(uint thread)
    {
        var state = new GuiThread { Size = Marshal.SizeOf<GuiThread>() };
        if (thread == 0 || !GetGUIThreadInfo(thread, ref state)) throw new SafeStop("O foco não pôde ser confirmado.");
        return state;
    }

    internal static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    internal static uint LastInput()
    {
        var state = new LastInputInfo { Size = 8 };
        if (!GetLastInputInfo(ref state)) throw new SafeStop("Não foi possível confirmar o estado da entrada.");
        return state.Tick;
    }

    internal static bool KeyDown(int key) => GetAsyncKeyState(key) < 0;
    internal static bool ModifiersDown() =>
        KeyDown(0x10) || KeyDown(0x11) || KeyDown(0x12) || KeyDown(0x5B) || KeyDown(0x5C);

    internal static void Refocus(IntPtr window, IntPtr editor, uint lastInput, CancellationToken token)
    {
        // Make a queue for this short-lived worker, then attach only to the verified editor's thread.
        IntPtr buffer = Marshal.AllocHGlobal(48);
        try { PeekMessage(buffer, IntPtr.Zero, 0, 0, 0); }
        finally { Marshal.FreeHGlobal(buffer); }
        uint worker = GetCurrentThreadId();
        uint target = GetWindowThreadProcessId(editor, out _);
        if (target == 0 || !AttachThreadInput(worker, target, true))
            throw new SafeStop("O foco do Bloco de Notas não pôde ser recuperado.");
        try
        {
            token.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != window || LastInput() != lastInput)
                throw new SafeStop("O foco mudou. Correção cancelada.");
            SetFocus(editor);
        }
        finally { AttachThreadInput(worker, target, false); }
    }

    private static void Check(IntPtr status)
    {
        if (status == IntPtr.Zero) throw new SafeStop("O aplicativo não respondeu à edição com segurança.");
    }

    internal static IntPtr Message(IntPtr window, uint message, int w = 0, int l = 0)
    {
        Check(SendMessageTimeout(window, message, (IntPtr)w, (IntPtr)l, 0x23, 260, out var result));
        return result;
    }

    internal static string ReadText(IntPtr window, int capacity)
    {
        var text = new StringBuilder(capacity);
        Check(SendMessageTimeout(window, 0x000D, (IntPtr)capacity, text, 0x23, 260, out _));
        return text.ToString();
    }

    internal static (int, int) GetSelection(IntPtr window)
    {
        Check(SendMessageTimeout(window, 0x00B0, out int start, out int end, 0x23, 260, out _));
        return (start, end);
    }

    internal static void Select(IntPtr window, int start, int end) => Message(window, 0x00B1, start, end);
    internal static void Replace(IntPtr window, string value) =>
        Check(SendMessageTimeout(window, 0x00C2, (IntPtr)1, value, 0x23, 260, out _));

    internal static void ReplaceBrowserSelection(string value)
    {
        if (value.Length == 0)
        {
            Dispatch(new KeyInput[]
            {
                new() { Type = 1, Key = 0x08, Extra = OurInput },
                new() { Type = 1, Key = 0x08, Flags = 2, Extra = OurInput }
            });
            return;
        }
        var keys = new KeyInput[value.Length * 2];
        for (int i = 0; i < value.Length; i++)
        {
            keys[i * 2] = new() { Type = 1, Scan = value[i], Flags = 4, Extra = OurInput };
            keys[i * 2 + 1] = new() { Type = 1, Scan = value[i], Flags = 6, Extra = OurInput };
        }
        Dispatch(keys);
    }

    private static void Dispatch(KeyInput[] keys)
    {
        if (SendInput((uint)keys.Length, keys, 40) != keys.Length)
            throw new SafeStop("A entrada foi bloqueada pelo aplicativo. Confira o campo.");
    }

    // These hooks exist only while a verified browser replacement is in progress.
    // They inspect the injection marker, never the key code, mouse coordinates or text.
    internal sealed class InputGuard : IDisposable
    {
        private readonly HookProc onKey;
        private readonly HookProc onMouse;
        private IntPtr keyboard, mouse;
        private volatile bool interrupted;

        internal bool Interrupted => interrupted;

        internal InputGuard()
        {
            onKey = Keyboard;
            onMouse = Mouse;
            var module = GetModuleHandle(null);
            keyboard = SetWindowsHookEx(13, onKey, module, 0);
            mouse = SetWindowsHookEx(14, onMouse, module, 0);
            if (keyboard != IntPtr.Zero && mouse != IntPtr.Zero) return;
            Dispose();
            throw new SafeStop("O Windows não permitiu proteger a substituição neste navegador.");
        }

        internal void Check()
        {
            if (Interrupted) throw new SafeStop("Houve nova entrada durante a edição. Confira o campo.");
        }

        private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && Marshal.ReadIntPtr(data, 16) != OurInput) interrupted = true;
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        private IntPtr Mouse(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0) interrupted = true;
            return CallNextHookEx(IntPtr.Zero, code, message, data);
        }

        public void Dispose()
        {
            interrupted = true;
            if (keyboard != IntPtr.Zero) UnhookWindowsHookEx(keyboard);
            if (mouse != IntPtr.Zero) UnhookWindowsHookEx(mouse);
            keyboard = mouse = IntPtr.Zero;
        }
    }
}
