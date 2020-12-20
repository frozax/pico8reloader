using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Permissions;
using System.Threading;
using System.Windows.Forms;

[assembly : AssemblyTitle("pico8reloader")]
[assembly : AssemblyConfiguration("")]
[assembly : AssemblyCompany("rostok - https://github.com/rostok/")]
[assembly : AssemblyCopyright("Copyright � 2020")]
[assembly : AssemblyTrademark("")]
[assembly : AssemblyCulture("")]
[assembly : AssemblyVersion("1.0.1.0")]
[assembly : AssemblyFileVersion("1.0.1.0")]

public class Pico8Reloader {
    // https://www.codeproject.com/Questions/1228092/Simulate-this-keys-to-inactive-application-with-Cs
    [DllImport("user32.dll")]
    public static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    const uint WM_KEYDOWN = 0x0100;
    const uint WM_KEYUP = 0x0101;

    [DllImport("User32.dll")]
    static extern int SetForegroundWindow(IntPtr point);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, ref Rect rectangle);

    public struct Rect {
        public int Left { get; set; }
        public int Top { get; set; }
        public int Right { get; set; }
        public int Bottom { get; set; }
    }

    // http://csharphelper.com/blog/2016/12/set-another-applications-size-and-position-in-c/
    [DllImport("user32.dll")]
    [
        return :MarshalAs(UnmanagedType.Bool)
    ]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, SetWindowPosFlags uFlags);

    [Flags()]
    private enum SetWindowPosFlags : uint {
        SynchronousWindowPosition = 0x4000,
        DeferErase = 0x2000,
        DrawFrame = 0x0020,
        FrameChanged = 0x0020,
        HideWindow = 0x0080,
        DoNotActivate = 0x0010,
        DoNotCopyBits = 0x0100,
        IgnoreMove = 0x0002,
        DoNotChangeOwnerZOrder = 0x0200,
        DoNotRedraw = 0x0008,
        DoNotReposition = 0x0200,
        DoNotSendChangingEvent = 0x0400,
        IgnoreResize = 0x0001,
        IgnoreZOrder = 0x0004,
        ShowWindow = 0x0040,
    }

    public static void Main() {
        Run();
    }

    public static Rect windowRect = new Rect();
    public static bool windowRectSet = false;
    public static string main_p8 = "";

    [PermissionSet(SecurityAction.Demand, Name = "FullTrust")]
    public static void Run() {
        string[] args = System.Environment.GetCommandLineArgs();

        if (args.Length >= 2 && (args[1] == "-h" || args[1] == "/?" || args[1] == "--help")) {
            Console.WriteLine("pico8reloader will watch folder for any p8 file changes.");
            Console.WriteLine("in case of file write or file rename it will:");
            Console.WriteLine("1) run pico8 with latest p8 file if it is not running");
            Console.WriteLine("2) restart pico8 if lastest p8 file is not in command line");
            Console.WriteLine("3) sent Ctrl+R (reload) keystroke to pico8 process");
            Console.WriteLine("");
            Console.WriteLine("syntax: pico8reloader [path] [--winpos=x,y[,w,h]]");
            Console.WriteLine("default path is .");
            Console.WriteLine("pico8 should be accessible via PATH variable");
            Console.WriteLine("");
            Console.WriteLine("this comes with MIT license from rostok - https://github.com/rostok/");
            Console.WriteLine("Update from Frozax: if we specifiy a p8, we use the folder of this p8, but we reload this p8 only even if other changes (useful when using includes)");
            return;
        }

        string dir = ".";
        if (args.Length >= 2)
        {
            if (!Directory.Exists(args[1]))
            {
                // check if it's a p8
                if (File.Exists(args[1]))
                {
                    main_p8 = args[1];
                    dir = Path.GetDirectoryName(args[1]);
                }
            }
            if(Directory.Exists(args[1]))
                dir = args[1];
        }

        if (args[args.Length-1].StartsWith("--winpos=")) {
            string s = args[args.Length-1].Replace("--winpos=","");
            string[] sa = s.Split(',');
            int x = 0;
            int y = 0;
            int w = 256;
            int h = 256;
            if (sa.Length>0) x = Int16.Parse(sa[0]);
            if (sa.Length>1) y = Int16.Parse(sa[1]);
            if (sa.Length>2) w = Int16.Parse(sa[2]);
            if (sa.Length>3) h = Int16.Parse(sa[3]);
            windowRect.Left = x;
            windowRect.Top = y;
            windowRect.Right = x+w;
            windowRect.Bottom = y+h;
            windowRectSet = true;
        }

        // Create a new FileSystemWatcher and set its properties.
        FileSystemWatcher watcher = new FileSystemWatcher();
        watcher.Path = dir;
        watcher.NotifyFilter = NotifyFilters.LastAccess | NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;
        watcher.Filter = "*.p8";

        watcher.Changed += new FileSystemEventHandler(OnChanged);
        watcher.Created += new FileSystemEventHandler(OnChanged);
        //watcher.Deleted += new FileSystemEventHandler(OnChanged);
        watcher.Renamed += new RenamedEventHandler(OnRenamed);

        watcher.EnableRaisingEvents = true;

        Console.WriteLine("Starting to monitor " + dir + " folder.");
        Console.WriteLine("Press \'q\' or Ctrl+C to quit.");
        while (Console.Read() != 'q');
    }

    // https://stackoverflow.com/questions/2633628/can-i-get-command-line-arguments-of-other-processes-from-net-c/2633674#2633674
    private static string GetCommandLine(Process process) {
        using(ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + process.Id))
        using(ManagementObjectCollection objects = searcher.Get()) {
            return objects.Cast<ManagementBaseObject>().SingleOrDefault() ? ["CommandLine"]?.ToString();
        }
    }

    private static void Action(string FullPath) {
        IntPtr focusedWindow = GetForegroundWindow();
        Process p = Process.GetProcessesByName("pico8").FirstOrDefault();

        string full_file_to_find = main_p8.Length == 0 ? FullPath : main_p8;
        if (p != null) {
            string args = GetCommandLine(p);
            string fn = Path.GetFileName(full_file_to_find);

            if (!fn.Contains(".p8")) {
                Console.WriteLine("	not a p8 file: " + fn);
                return;
            }

            //Console.WriteLine("args:"+args);
            //Console.WriteLine("fn:"+fn);
            if (args.ToLower().Contains(fn.ToLower())) {
                // just reload
                Console.WriteLine("	sending Ctrl+R");
                SetForegroundWindow(p.MainWindowHandle);
                p.WaitForInputIdle();
                //SendKeys.SendWait("^(r)"); // doesn't work
                //p.WaitForInputIdle();

                // https://docs.microsoft.com/en-us/windows/win32/inputdev/wm-keydown
                // https://www.win.tue.nl/~aeb/linux/kbd/scancodes-1.html
                PostMessage(p.MainWindowHandle, WM_KEYDOWN, (IntPtr) (Keys.LControlKey), (IntPtr) (1 | 0x1D << 16));
                PostMessage(p.MainWindowHandle, WM_KEYDOWN, (IntPtr) (Keys.R), (IntPtr) (1 | 0x13 << 16));
                Thread.Sleep(50);
                PostMessage(p.MainWindowHandle, WM_KEYUP, (IntPtr) (Keys.R), (IntPtr) (1 | 0x13 << 16));
                PostMessage(p.MainWindowHandle, WM_KEYUP, (IntPtr) (Keys.LControlKey), (IntPtr) (1 | 0x1D << 16));
                Thread.Sleep(50);

                SetForegroundWindow(focusedWindow);
                return;
            } else {
                GetWindowRect(p.MainWindowHandle, ref windowRect);
                windowRectSet = true;

                Console.WriteLine("	killing");
                p.Kill();
            }
        }

        Console.WriteLine("	running: pico8 -run " + full_file_to_find);
        p = Process.Start("pico8", " -run " + full_file_to_find);
        Thread.Sleep(1000);
		SetForegroundWindow(p.MainWindowHandle);

        if (windowRectSet) SetWindowPos(p.MainWindowHandle, IntPtr.Zero, windowRect.Left, windowRect.Top, windowRect.Right - windowRect.Left, windowRect.Bottom - windowRect.Top, 0);
        

        SetForegroundWindow(focusedWindow);
    }

    private static void OnChanged(object source, FileSystemEventArgs e) {
        Console.WriteLine("File: " + e.FullPath + " " + e.ChangeType);
        Action(e.FullPath);
    }

    private static void OnRenamed(object source, RenamedEventArgs e) {
        Console.WriteLine("File: {0} renamed to {1}", e.OldFullPath, e.FullPath);
        Action(e.FullPath);
    }
}