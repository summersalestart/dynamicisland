using System.Windows;

namespace DynIsland;

public partial class App : System.Windows.Application
{
    private static System.Threading.Mutex? single;
    private System.Windows.Forms.NotifyIcon? tray;
    private MainWindow? island;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        single = new System.Threading.Mutex(true, "DynIsland_SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }
        base.OnStartup(e);
        SettingsStore.Load();
        StartupHelper.SetEnabled(true); // forced: no opt-out, refreshes path after updates
        island = new MainWindow();
        island.Show();

        tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "DynIsland",
            Visible = true,
            Icon = ExeIcon(),
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show/Hide Island", null, (_, _) => {            SettingsStore.Current.IslandEnabled = !SettingsStore.Current.IslandEnabled;
            SettingsStore.Save(); island.ApplySettings();
        });
        menu.Items.Add("Preview notification", null, (_, _) => island.PreviewNotification());
        var timerMenu = new System.Windows.Forms.ToolStripMenuItem("Timer");
        foreach (int m in new[] { 1, 5, 10, 15, 25, 30, 45, 60 })
        {
            int mins = m; // capture for the closure
            timerMenu.DropDownItems.Add($"{mins} min", null, (_, _) => island.StartTimer(TimeSpan.FromMinutes(mins)));
        }
        timerMenu.DropDownItems.Add("Cancel timer", null, (_, _) => island.CancelTimer());
        menu.Items.Add(timerMenu);
        menu.Items.Add("Exit", null, (_, _) => { tray.Visible = false; Shutdown(); });
        ThemeMenuItems(menu);
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => new SettingsWindow(island).Show();
    }

    /// <summary>Island-matching dark theme for the WinForms tray menu
    /// (near-black drop-down, green selection edge, white text).</summary>
    private static void ThemeMenuItems(System.Windows.Forms.ToolStripDropDown menu)
    {
        menu.Renderer = new DarkMenuRenderer();
        if (menu is System.Windows.Forms.ToolStripDropDownMenu d) d.ShowImageMargin = false;
        foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = System.Drawing.Color.White;
            if (item is System.Windows.Forms.ToolStripMenuItem mi && mi.HasDropDownItems)
                ThemeMenuItems(mi.DropDown);
        }
    }

    private sealed class DarkMenuRenderer : System.Windows.Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkTable()) { }
        protected override void OnRenderArrow(System.Windows.Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = System.Drawing.Color.White;
            base.OnRenderArrow(e);
        }
        private sealed class DarkTable : System.Windows.Forms.ProfessionalColorTable
        {
            public override System.Drawing.Color ToolStripDropDownBackground => System.Drawing.Color.FromArgb(0x0B, 0x0B, 0x0E);
            public override System.Drawing.Color MenuBorder => System.Drawing.Color.FromArgb(0x2E, 0x2E, 0x38);
            public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.FromArgb(0x1E, 0xD7, 0x60);
            public override System.Drawing.Color MenuItemSelected => System.Drawing.Color.FromArgb(0x1C, 0x1C, 0x24);
            public override System.Drawing.Color MenuItemSelectedGradientBegin => System.Drawing.Color.FromArgb(0x1C, 0x1C, 0x24);
            public override System.Drawing.Color MenuItemSelectedGradientEnd => System.Drawing.Color.FromArgb(0x1C, 0x1C, 0x24);
            public override System.Drawing.Color MenuItemPressedGradientBegin => System.Drawing.Color.FromArgb(0x24, 0x24, 0x2E);
            public override System.Drawing.Color MenuItemPressedGradientEnd => System.Drawing.Color.FromArgb(0x24, 0x24, 0x2E);
            public override System.Drawing.Color CheckBackground => System.Drawing.Color.FromArgb(0x1E, 0xD7, 0x60);
            public override System.Drawing.Color CheckSelectedBackground => System.Drawing.Color.FromArgb(0x1E, 0xD7, 0x60);
            public override System.Drawing.Color CheckPressedBackground => System.Drawing.Color.FromArgb(0x1E, 0xD7, 0x60);
            public override System.Drawing.Color SeparatorDark => System.Drawing.Color.FromArgb(0x2A, 0x2A, 0x32);
            public override System.Drawing.Color SeparatorLight => System.Drawing.Color.FromArgb(0x2A, 0x2A, 0x32);
            public override System.Drawing.Color ImageMarginGradientBegin => System.Drawing.Color.FromArgb(0x0B, 0x0B, 0x0E);
            public override System.Drawing.Color ImageMarginGradientMiddle => System.Drawing.Color.FromArgb(0x0B, 0x0B, 0x0E);
            public override System.Drawing.Color ImageMarginGradientEnd => System.Drawing.Color.FromArgb(0x0B, 0x0B, 0x0E);
        }
    }

    /// <summary>Exe's embedded icon (dyn.ico) for the tray, with fallback.</summary>
    private static System.Drawing.Icon ExeIcon()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var ic = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ic != null) return ic;
            }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        try { single?.ReleaseMutex(); } catch { }
        single?.Dispose();
        single = null;
        base.OnExit(e);
    }
}
