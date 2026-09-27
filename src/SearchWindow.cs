using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Seek
{
    /// <summary>The translucent search bar with up to five results underneath.</summary>
    internal sealed class SearchWindow : Window
    {
        const int MaxResults = 5;
        const double BarHeight = 60;
        const double RowHeight = 50;
        const int HotkeyId = 0x5EE;

        static readonly Brush Ink = Solid(0xFF, 0xF3, 0xF3, 0xF5);
        static readonly Brush Muted = Solid(0x9C, 0xFF, 0xFF, 0xFF);
        static readonly Brush Faint = Solid(0x6E, 0xFF, 0xFF, 0xFF);
        static readonly Brush Glass = Solid(0xB4, 0x1A, 0x1B, 0x20);
        static readonly Brush Edge = Solid(0x3A, 0xFF, 0xFF, 0xFF);
        static readonly Brush Hairline = Solid(0x1E, 0xFF, 0xFF, 0xFF);
        static readonly Brush Highlight = Solid(0x22, 0xFF, 0xFF, 0xFF);
        static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        /// <summary>A folder you've stepped into, and the search you stepped in from.</summary>
        sealed class Scope
        {
            public string Folder;
            public string Name;
            public string Query;
        }

        readonly FileIndex index;
        readonly Brush accent;
        readonly TextBox box;
        readonly TextBlock placeholder;
        readonly Run placeholderMain;
        readonly Run placeholderHint;
        readonly Border chip;
        readonly TextBlock chipName;
        readonly TextBlock status;
        readonly Border divider;
        readonly StackPanel list;
        readonly TextBlock message;
        readonly DispatcherTimer ticker;
        readonly DispatcherTimer trimLater;
        readonly List<Border> rows = new List<Border>();
        readonly List<Border> markers = new List<Border>();
        readonly IntPtr hwnd;

        readonly List<Scope> scopes = new List<Scope>();
        List<Hit> hits = new List<Hit>();
        int selected;
        string preferPath; // select this result on the next render (e.g. the folder we just left)
        string renderedScope;
        string renderedQuery;
        int generation;
        CancellationTokenSource running;
        string notice;
        System.Drawing.Point lastCursor;
        Point pressedAt;
        int pressedRow = -1;

        public bool HotkeyRegistered { get; private set; }

        public SearchWindow(FileIndex index)
        {
            this.index = index;
            accent = Solid(0xFF, AccentColor());

            Title = "Seek";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Width = 680;
            SizeToContent = SizeToContent.Height;
            UseLayoutRounding = true;
            FontFamily = UiFont;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            var glyph = new TextBlock
            {
                Text = "",
                FontFamily = IconFont,
                FontSize = 18,
                Foreground = Muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            box = new TextBox
            {
                FontSize = 21,
                Foreground = Ink,
                CaretBrush = Ink,
                SelectionBrush = accent,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                FocusVisualStyle = null,
            };
            placeholderMain = new Run();
            placeholderHint = new Run { FontSize = 13, Foreground = Solid(0x55, 0xFF, 0xFF, 0xFF) };
            placeholder = new TextBlock { FontSize = 21, Foreground = Faint, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
            placeholder.Inlines.Add(placeholderMain);
            placeholder.Inlines.Add(placeholderHint);
            status = new TextBlock { FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 20, 0) };

            // The folder you're searching inside, shown as a chip before the text.
            chipName = new TextBlock { FontSize = 14, Foreground = Ink, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var chipContent = new StackPanel { Orientation = Orientation.Horizontal };
            var chipIcon = new Image { Source = ShellIcons.For(new Hit { IsFolder = true, Name = "", Path = "" }), Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            RenderOptions.SetBitmapScalingMode(chipIcon, BitmapScalingMode.HighQuality);
            chipContent.Children.Add(chipIcon);
            chipContent.Children.Add(chipName);
            chip = new Border
            {
                Background = Solid(0x2A, 0xFF, 0xFF, 0xFF),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(9, 4, 10, 5),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Child = chipContent,
                Cursor = Cursors.Hand,
            };
            chip.MouseLeftButtonUp += delegate
            {
                if (scopes.Count > 0) ExitScope();
            };

            var bar = new Grid { Height = BarHeight };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(chip, 1);
            Grid.SetColumn(placeholder, 2);
            Grid.SetColumn(box, 2);
            Grid.SetColumn(status, 3);
            bar.Children.Add(glyph);
            bar.Children.Add(chip);
            bar.Children.Add(placeholder);
            bar.Children.Add(box);
            bar.Children.Add(status);
            UpdateScopeChrome();

            divider = new Border { Height = 1, Background = Hairline, Visibility = Visibility.Collapsed };
            list = new StackPanel { Margin = new Thickness(6), Visibility = Visibility.Collapsed };
            message = new TextBlock { FontSize = 13, Foreground = Muted, Margin = new Thickness(20, 14, 20, 16), Visibility = Visibility.Collapsed };

            var stack = new StackPanel();
            stack.Children.Add(bar);
            stack.Children.Add(divider);
            stack.Children.Add(list);
            stack.Children.Add(message);
            Content = new Border { Background = Glass, BorderBrush = Edge, BorderThickness = new Thickness(1), Child = stack };

            box.TextChanged += delegate
            {
                notice = null;
                Search();
            };
            PreviewKeyDown += OnKey;
            Deactivated += delegate { HideBar(); };
            ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            ticker.Tick += delegate { UpdateChrome(); };
            // Once the bar has been hidden for a moment, give its memory back to Windows.
            trimLater = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            trimLater.Tick += delegate
            {
                trimLater.Stop();
                if (!IsVisible) Memory.Trim();
            };
            index.Updated += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible) Search();
            }));

            hwnd = new WindowInteropHelper(this).EnsureHandle();
            User32.EnableBlur(hwnd);
            User32.MakeToolWindow(hwnd);
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);
            HotkeyRegistered = User32.RegisterHotKey(hwnd, HotkeyId, User32.MOD_ALT | User32.MOD_NOREPEAT, User32.VK_SPACE);
        }

        // ---- Showing and hiding -----------------------------------------------------------

        public void ShowBar()
        {
            PlaceOnActiveScreen();
            if (!IsVisible) Show();
            Activate();
            User32.ForceForeground(hwnd);
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
            notice = null; // "Path copied" etc. belong to the previous visit
            lastCursor = System.Windows.Forms.Cursor.Position;
            ticker.Start();
            Search();
            // Windows may refuse focus (another app is in active use). A bar you can't type
            // into shouldn't hover over your work, so give up quietly.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible && !IsActive) HideBar();
            }), DispatcherPriority.ApplicationIdle);
        }

        public void HideBar()
        {
            if (!IsVisible) return;
            Hide();
            ticker.Stop();
            pressedRow = -1;
            trimLater.Stop();
            trimLater.Start();
            // Next time, start from the original search rather than deep inside some folder.
            if (scopes.Count > 0)
            {
                var root = scopes[0];
                scopes.Clear();
                UpdateScopeChrome();
                preferPath = root.Folder;
                SetQuery(root.Query);
            }
        }

        // ---- Searching inside a folder ----------------------------------------------------

        Hit SelectedFolder()
        {
            return selected < hits.Count && hits[selected].IsFolder ? hits[selected] : null;
        }

        void EnterScope(Hit folder)
        {
            scopes.Add(new Scope { Folder = folder.Path, Name = folder.Name, Query = box.Text });
            preferPath = null;
            UpdateScopeChrome();
            SetQuery("");
        }

        void ExitScope()
        {
            var left = scopes[scopes.Count - 1];
            scopes.RemoveAt(scopes.Count - 1);
            preferPath = left.Folder; // land back on the folder we were in
            UpdateScopeChrome();
            SetQuery(left.Query);
        }

        void SetQuery(string text)
        {
            if (box.Text == text) Search();
            else box.Text = text;
            box.CaretIndex = text.Length;
        }

        void UpdateScopeChrome()
        {
            if (scopes.Count == 0)
            {
                chip.Visibility = Visibility.Collapsed;
                placeholderMain.Text = "Search files";
                placeholderHint.Text = "      .pdf     .video     report*2026.pdf";
                return;
            }
            var scope = scopes[scopes.Count - 1];
            chipName.Text = scope.Name;
            chip.ToolTip = scope.Folder + "\nClick or press Esc to go back";
            chip.Visibility = Visibility.Visible;
            placeholderMain.Text = "Search in here";
            placeholderHint.Text = "      Esc to go back";
        }

        void Toggle()
        {
            if (IsVisible && IsActive) HideBar();
            else ShowBar();
        }

        void PlaceOnActiveScreen()
        {
            var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            var source = HwndSource.FromHwnd(hwnd);
            double scale = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformToDevice.M11 : 1.0;
            double left = area.Left / scale, top = area.Top / scale, width = area.Width / scale, height = area.Height / scale;
            Left = Math.Round(left + (width - Width) / 2);
            Top = Math.Round(top + height * 0.36 - BarHeight / 2);
        }

        IntPtr WndProc(IntPtr window, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == User32.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                Toggle();
                handled = true;
            }
            // Letting go of Alt (after Alt+Space) would put the window into "menu mode", and
            // the next key typed would be swallowed looking for a menu. The bar has no menu.
            else if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU)
            {
                handled = true;
            }
            return IntPtr.Zero;
        }

        const int WM_SYSCOMMAND = 0x0112;
        const int SC_KEYMENU = 0xF100;

        // ---- Searching --------------------------------------------------------------------

        async void Search()
        {
            string text = box.Text;
            placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Hidden;
            int ticket = ++generation;
            if (running != null) running.Cancel();
            running = null;
            string scope = scopes.Count > 0 ? scopes[scopes.Count - 1].Folder : null;
            if (Query.Parse(text) == null && scope == null)
            {
                Render(new List<Hit>());
                return;
            }

            var cancel = running = new CancellationTokenSource();
            List<Hit> found;
            try
            {
                found = await Task.Run(() => index.Search(text, scope, MaxResults, cancel.Token));
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return;
            }
            if (ticket != generation || found == null) return;
            Render(found);
        }

        void Render(List<Hit> found)
        {
            string scope = scopes.Count > 0 ? scopes[scopes.Count - 1].Folder : null;
            // Typing something new selects the new top result; a refresh of the same search
            // (files changed on disk) keeps whatever was selected.
            string query = box.Text.Trim() + "\n" + scope;
            bool sameQuery = query == renderedQuery;
            renderedQuery = query;
            string keep = preferPath ?? (sameQuery && selected < hits.Count ? hits[selected].Path : null);
            preferPath = null;
            if (!SameHits(found, hits) || scope != renderedScope)
            {
                hits = found;
                renderedScope = scope; // row paths are shown relative to it
                pressedRow = -1;       // a click that started on an old row must not open a new one
                list.Children.Clear();
                rows.Clear();
                markers.Clear();
                for (int i = 0; i < hits.Count; i++) list.Children.Add(BuildRow(hits[i], i));
            }
            selected = Math.Max(0, hits.FindIndex(h => string.Equals(h.Path, keep, StringComparison.OrdinalIgnoreCase)));
            UpdateSelection();
            UpdateChrome();
        }

        static bool SameHits(List<Hit> a, List<Hit> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i].Path != b[i].Path || a[i].Time != b[i].Time) return false;
            return true;
        }

        /// <summary>Status text, empty-state message and which parts of the window are showing.</summary>
        void UpdateChrome()
        {
            var query = Query.Parse(box.Text);
            bool typed = query != null;
            bool hasQuery = typed || scopes.Count > 0;
            int progress = index.ScanProgress;
            bool indexing = !index.Complete && progress >= 0;

            if (notice != null) status.Text = notice;
            else if (indexing) status.Text = "Indexing… " + progress.ToString("N0");
            else if (query != null && query.Shortcut != null) status.Text = query.Shortcut; // shows the shortcut was understood
            else status.Text = "";

            string empty = null;
            if (hasQuery && hits.Count == 0)
                empty = indexing ? "Nothing yet — still indexing your drives (" + progress.ToString("N0") + " items so far)"
                    : scopes.Count == 0 ? "No matches"
                    : typed ? "No matches in " + scopes[scopes.Count - 1].Name
                    : "This folder is empty";
            message.Text = empty ?? "";
            message.Visibility = empty != null ? Visibility.Visible : Visibility.Collapsed;
            list.Visibility = hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            divider.Visibility = hits.Count > 0 || empty != null ? Visibility.Visible : Visibility.Collapsed;
        }

        Border BuildRow(Hit hit, int i)
        {
            var icon = new Image { Source = ShellIcons.For(hit), Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

            var name = new TextBlock { Text = hit.Name, FontSize = 15, Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis };
            var folder = new TextBlock { Text = WhereIs(hit), FontSize = 12, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(name);
            texts.Children.Add(folder);
            var when = new TextBlock { Text = Friendly.When(hit.Time), FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 4, 0) };

            // Folders get a › : Tab, → or clicking it steps inside to search just that folder.
            var enter = new Border
            {
                Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = hit.IsFolder ? "" : "",
                    FontFamily = IconFont,
                    FontSize = 11,
                    Foreground = Muted,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            if (hit.IsFolder)
            {
                enter.Cursor = Cursors.Hand;
                enter.ToolTip = "Search inside this folder (Tab)";
                enter.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true; // don't also open the folder
                    pressedRow = -1;
                    EnterScope(hit);
                };
            }

            var content = new Grid { Margin = new Thickness(14, 0, 6, 0) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            Grid.SetColumn(texts, 1);
            Grid.SetColumn(when, 2);
            Grid.SetColumn(enter, 3);
            content.Children.Add(icon);
            content.Children.Add(texts);
            content.Children.Add(when);
            content.Children.Add(enter);

            var marker = new Border
            {
                Width = 3,
                Height = 20,
                CornerRadius = new CornerRadius(1.5),
                Background = accent,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(4, 0, 0, 0),
                Visibility = Visibility.Hidden,
            };
            var cell = new Grid();
            cell.Children.Add(marker);
            cell.Children.Add(content);

            var row = new Border
            {
                Height = RowHeight,
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Child = cell,
                Tag = i,
                ToolTip = hit.Path + "\n" + Friendly.Full(hit.Time),
            };
            ToolTipService.SetInitialShowDelay(row, 900);
            row.MouseMove += OnRowMouseMove;
            row.MouseLeftButtonDown += OnRowMouseDown;
            row.MouseLeftButtonUp += OnRowMouseUp;
            row.MouseRightButtonUp += OnRowRightClick;
            rows.Add(row);
            markers.Add(marker);
            return row;
        }

        /// <summary>
        /// The row's second line. Inside a folder, show where the hit sits relative to it
        /// ("…\thcrap\logs") instead of repeating the whole path on every row.
        /// </summary>
        string WhereIs(Hit hit)
        {
            if (scopes.Count == 0) return hit.Folder;
            var scope = scopes[scopes.Count - 1];
            string prefix = scope.Folder.TrimEnd('\\') + "\\";
            if (!hit.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return hit.Folder;
            string relative = hit.Path.Substring(prefix.Length);
            int cut = relative.LastIndexOf('\\');
            return cut < 0 ? "This folder" : "…\\" + relative.Substring(0, cut);
        }

        void UpdateSelection()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].Background = i == selected ? Highlight : Brushes.Transparent;
                markers[i].Visibility = i == selected ? Visibility.Visible : Visibility.Hidden;
            }
        }

        // ---- Input ------------------------------------------------------------------------

        void OnKey(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            switch (e.Key)
            {
                case Key.Escape:
                    // Inside a folder, Esc steps back out; at the top it hides the bar.
                    if (scopes.Count > 0) ExitScope();
                    else HideBar();
                    e.Handled = true;
                    break;
                case Key.Down:
                case Key.Up:
                    if (hits.Count > 0)
                    {
                        selected = Math.Max(0, Math.Min(hits.Count - 1, selected + (e.Key == Key.Down ? 1 : -1)));
                        UpdateSelection();
                    }
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if (hits.Count > 0)
                    {
                        if (ctrl) Reveal(hits[selected]);
                        else Open(hits[selected]);
                    }
                    e.Handled = true;
                    break;
                case Key.C:
                    if (ctrl && shift && hits.Count > 0)
                    {
                        CopyPath(hits[selected]);
                        e.Handled = true;
                    }
                    break;
                case Key.Tab:
                    if (shift)
                    {
                        if (scopes.Count > 0) ExitScope();
                    }
                    else if (SelectedFolder() != null)
                    {
                        EnterScope(SelectedFolder());
                    }
                    else if (hits.Count > 0)
                    {
                        Reveal(hits[selected]); // a file: open the folder it's in, with it selected
                    }
                    e.Handled = true;
                    break;
                case Key.Right:
                    // Only at the end of the text (and not Shift+→ selecting), so → still edits normally.
                    if (!shift && SelectedFolder() != null && box.SelectionStart + box.SelectionLength == box.Text.Length)
                    {
                        EnterScope(SelectedFolder());
                        e.Handled = true;
                    }
                    break;
                case Key.Left:
                case Key.Back:
                    // Only at the very start of the text, so both still edit normally otherwise.
                    if (scopes.Count > 0 && box.SelectionStart == 0 && box.SelectionLength == 0 && !(shift && e.Key == Key.Left))
                    {
                        ExitScope();
                        e.Handled = true;
                    }
                    break;
                case Key.System:
                    if (e.SystemKey == Key.F4)
                    {
                        HideBar(); // Alt+F4 hides; the app keeps running in the tray
                        e.Handled = true;
                    }
                    break;
            }
        }

        void OnRowMouseMove(object sender, MouseEventArgs e)
        {
            int i = (int)((FrameworkElement)sender).Tag;
            Point at = e.GetPosition(this);
            // Only real mouse movement selects, so results appearing under a resting pointer
            // don't steal the keyboard selection. Screen position is reliable even right after
            // the bar appears, when WPF's own idea of where the mouse is can be stale.
            var cursor = System.Windows.Forms.Cursor.Position;
            if (cursor != lastCursor)
            {
                lastCursor = cursor;
                if (selected != i)
                {
                    selected = i;
                    UpdateSelection();
                }
            }
            if (pressedRow == i && e.LeftButton == MouseButtonState.Pressed &&
                (Math.Abs(at.X - pressedAt.X) > SystemParameters.MinimumHorizontalDragDistance ||
                 Math.Abs(at.Y - pressedAt.Y) > SystemParameters.MinimumVerticalDragDistance))
            {
                pressedRow = -1;
                // Drag the file out: into Explorer, an email, a chat... Copy only, never move.
                var data = new DataObject(DataFormats.FileDrop, new[] { hits[i].Path });
                DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Link);
            }
        }

        void OnRowMouseDown(object sender, MouseButtonEventArgs e)
        {
            pressedRow = (int)((FrameworkElement)sender).Tag;
            pressedAt = e.GetPosition(this);
        }

        void OnRowMouseUp(object sender, MouseButtonEventArgs e)
        {
            int i = (int)((FrameworkElement)sender).Tag;
            if (pressedRow != i) return;
            pressedRow = -1;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) Reveal(hits[i]);
            else Open(hits[i]);
        }

        void OnRowRightClick(object sender, MouseButtonEventArgs e)
        {
            Reveal(hits[(int)((FrameworkElement)sender).Tag]);
            e.Handled = true;
        }

        // ---- Actions ----------------------------------------------------------------------

        void Open(Hit hit)
        {
            if (!StillThere(hit)) return;
            try
            {
                try
                {
                    Process.Start(new ProcessStartInfo(hit.Path) { UseShellExecute = true, WorkingDirectory = hit.IsFolder ? hit.Path : hit.Folder });
                }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode == 1155) // no app associated: let the user pick one
                        Process.Start("rundll32.exe", "shell32.dll,OpenAs_RunDLL " + hit.Path);
                    else if (ex.NativeErrorCode != 1223) // 1223: the user cancelled a prompt
                        throw;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                notice = "Couldn't open that " + (hit.IsFolder ? "folder" : "file");
                UpdateChrome();
                return;
            }
            HideBar();
        }

        void Reveal(Hit hit)
        {
            if (!StillThere(hit)) return;
            try
            {
                Process.Start("explorer.exe", "/select,\"" + hit.Path + "\"");
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                notice = "Couldn't open Explorer";
                UpdateChrome();
                return;
            }
            HideBar();
        }

        void CopyPath(Hit hit)
        {
            try
            {
                Clipboard.SetText(hit.Path);
                notice = "Path copied";
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                notice = "Couldn't copy — clipboard busy";
            }
            UpdateChrome();
        }

        /// <summary>The index can briefly trail reality; if the item vanished, say so and refresh.</summary>
        bool StillThere(Hit hit)
        {
            // Kernel.Stat rather than File.Exists: it copes with paths longer than 260 characters.
            bool isFolder;
            long time;
            if (Kernel.Stat(hit.Path, Kernel.FutureLimit(), out isFolder, out time) != StatResult.Missing) return true;
            notice = "That " + (hit.IsFolder ? "folder" : "file") + " was moved or deleted";
            index.Recheck(hit.Path);
            UpdateChrome();
            return false;
        }

        // ---- Helpers ----------------------------------------------------------------------

        static Color AccentColor()
        {
            try
            {
                object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null);
                if (value is int)
                {
                    int abgr = (int)value;
                    var c = Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
                    // Lift dark accents a little so the marker reads on dark glass.
                    return Color.FromRgb((byte)(c.R + (255 - c.R) / 4), (byte)(c.G + (255 - c.G) / 4), (byte)(c.B + (255 - c.B) / 4));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
            return Color.FromRgb(0x4C, 0xA2, 0xFF);
        }

        static Brush Solid(byte alpha, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
            brush.Freeze();
            return brush;
        }

        static Brush Solid(byte alpha, Color c)
        {
            return Solid(alpha, c.R, c.G, c.B);
        }
    }
}
